using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Win7POS.Core.Import;
using Win7POS.Core.Online;

namespace Win7POS.Data.Online
{
    public sealed class CatalogImportOutboxPlan
    {
        public string PlanId { get; internal set; }
        public IReadOnlyList<CatalogImportOutboxEntry> Entries { get; internal set; }
        public int TotalRows { get; internal set; }
        internal string RemotePlanJson { get; set; }
        internal string RecoveryRowsJson { get; set; }
        internal string PreparedDocumentJson { get; set; }
    }

    public static class CatalogImportPlanBuilder
    {
        public const int MaximumRowsPerRequest = 1000;
        public const int MaximumTransportBytes = 512 * 1024;

        // The complete serialized envelope is measured, including a worst-case
        // JSON-escaped credential reserve and the full retry counter width.
        internal static PosTrustedDeviceSession MaximumSession() => new PosTrustedDeviceSession
        {
            DeviceToken = new string('\u0001', 256), SessionToken = new string('\u0001', 256),
            PosSessionId = "10000000-0000-4000-8000-000000000001", ShopDeviceId = "10000000-0000-4000-8000-000000000002", ShopCode = new string('\u0001', 80)
        };

        internal static CatalogImportOutboxPlan Split(CatalogImportOutboxEntry entry)
        {
            if (entry == null) return null;
            if (entry.OperationType == "catalog_import_correction") return SplitCorrection(entry);
            var source = CatalogImportRecoveryService.Deserialize<PosCatalogImportRequest>(entry.PayloadJson);
            if (source.Items.Length <= MaximumRowsPerRequest && Measure(entry) <= MaximumTransportBytes)
                return Plan(new[] { entry });
            var entries = new List<CatalogImportOutboxEntry>();
            // Item IDs have a fixed-width operation prefix. Measure each row
            // once with that width; only row-number digits vary. The additional
            // eight bytes reserve the two four-digit summary counters.
            var envelopeBytes = Measure(Part(entry, source, 0, 0)) + 8;
            for (var offset = 0; offset < source.Items.Length;)
            {
                var bytes = envelopeBytes; var length = 0;
                while (length < MaximumRowsPerRequest && offset + length < source.Items.Length)
                {
                    var item = CloneItem(source.Items[offset + length]);
                    item.ClientItemId = "win7pos-part-" + new string('a', 32) + "-row-" + item.RowNumber;
                    var rowBytes = Encoding.UTF8.GetByteCount(CatalogImportRecoveryService.Serialize(item)) + (length == 0 ? 0 : 1);
                    if (bytes + rowBytes > MaximumTransportBytes) break;
                    bytes += rowBytes; length++;
                }
                if (length == 0) throw new CatalogImportRecoveryException("recovery_row_too_large|"+source.Items[offset].RowNumber+"|"+source.Items[offset].Barcode);
                var candidate = Part(entry, source, offset, length);
                if (Measure(candidate) > MaximumTransportBytes) throw new CatalogImportRecoveryException("recovery_row_too_large");
                entries.Add(candidate); offset += length;
            }
            return Plan(entries);
        }

        private static CatalogImportOutboxEntry Part(CatalogImportOutboxEntry entry, PosCatalogImportRequest source, int offset, int count)
        {
            var request = new PosCatalogImportRequest
            {
                AppVersion = source.AppVersion, SchemaVersion = source.SchemaVersion, Source = source.Source,
                Batch = new PosCatalogImportBatchRequest { CreatedAt = source.Batch.CreatedAt, SourceFileName = source.Batch.SourceFileName,
                    PreviewFingerprint = source.Batch.PreviewFingerprint },
                Items = source.Items.Skip(offset).Take(count).Select(CloneItem).ToArray()
            };
            var hash = CatalogImportOutboxPayloadBuilder.Sha256Hex(entry.PayloadHash + "|" + offset + "|" + count);
            request.Batch.ClientImportId = "win7pos-part-" + hash.Substring(0, 32);
            request.Batch.IdempotencyKey = request.Batch.ClientImportId + ":" + request.SchemaVersion;
            foreach (var item in request.Items) item.ClientItemId = request.Batch.ClientImportId + "-row-" + item.RowNumber;
            request.Summary = new PosCatalogImportSummaryRequest { NewProducts = request.Items.Count(i => i.ChangeKind == "new"), UpdatedProducts = request.Items.Count(i => i.ChangeKind == "updated") };
            return Entry(entry, request.Batch.ClientImportId, request.Batch.IdempotencyKey, CatalogImportRecoveryService.Serialize(request));
        }

        private static PosCatalogImportItemRequest CloneItem(PosCatalogImportItemRequest item) => new PosCatalogImportItemRequest
        {
            Barcode = item.Barcode, Category = item.Category, ChangeKind = item.ChangeKind, ClientItemId = item.ClientItemId,
            DiffSummary = item.DiffSummary, ItemNumber = item.ItemNumber, Operation = item.Operation, ProductName = item.ProductName,
            PurchasePrice = item.PurchasePrice, Quantity = item.Quantity, RetailPrice = item.RetailPrice, RowNumber = item.RowNumber,
            SecondProductName = item.SecondProductName, Supplier = item.Supplier
        };

        private static CatalogImportOutboxPlan SplitCorrection(CatalogImportOutboxEntry entry)
        {
            var source = CatalogImportCorrectionTransport.ReadSavedRequest(entry.PayloadJson, entry.SharedProof);
            if (source.Correction.Items.Length <= MaximumRowsPerRequest && Measure(entry) <= MaximumTransportBytes)
                return Plan(new[] { entry });
            // A large original makes an embedded correction unrepresentable even
            // when every changed row is small. Use the verified server plan and
            // bind one shared local proof instead of duplicating it per child.
            var receipt=CatalogImportCorrectionTransport.ReadSavedReceipt(entry.PayloadJson,entry.SharedProof);
            var proof=entry.SharedProof ?? CatalogImportCorrectionSharedProof.Create(source.RecoveryOf.OriginalRequest,receipt);
            var entries=new List<CatalogImportOutboxEntry>();
            for(var offset=0;offset<source.Correction.Items.Length;)
            {
                var low=1;var high=Math.Min(MaximumRowsPerRequest,source.Correction.Items.Length-offset);
                CatalogImportOutboxEntry best=null;var length=0;
                while(low<=high)
                {
                    var count=low+(high-low)/2;
                    var hash=CatalogImportOutboxPayloadBuilder.Sha256Hex(entry.PayloadHash+"|"+offset+"|"+count);
                    var request=new PosCatalogImportCorrectionRequest {
                        SchemaVersion=source.SchemaVersion,ShopCode=source.ShopCode,
                        RecoveryOf=new PosCatalogImportRecoveryOf { ClientImportId=source.RecoveryOf.ClientImportId,
                            IdempotencyKey=source.RecoveryOf.IdempotencyKey,PayloadHash=source.RecoveryOf.PayloadHash,
                            OriginalRequest=source.RecoveryOf.OriginalRequest },
                        Correction=new PosCatalogImportCorrectionOperation { ClientImportId="win7pos-correction-"+hash.Substring(0,32),
                            CreatedAt=source.Correction.CreatedAt,Items=source.Correction.Items.Skip(offset).Take(count).ToArray() } };
                    request.Correction.IdempotencyKey=request.Correction.ClientImportId+":"+request.SchemaVersion;
                    var candidate=Entry(entry,request.Correction.ClientImportId,request.Correction.IdempotencyKey,
                        CatalogImportCorrectionTransport.SerializeSaved(request,receipt,proof));
                    candidate.SharedProof=proof;
                    if(Measure(candidate)<=MaximumTransportBytes) { best=candidate;length=count;low=count+1; } else high=count-1;
                }
                if(best==null) throw new CatalogImportRecoveryException("recovery_row_too_large|"+source.Correction.Items[offset].ClientItemId);
                entries.Add(best);offset+=length;
            }
            return Plan(entries);
        }

        private static CatalogImportOutboxEntry Entry(CatalogImportOutboxEntry source, string id, string key, string json) => new CatalogImportOutboxEntry
        { OperationType = source.OperationType, ClientImportId = id, IdempotencyKey = key, PayloadJson = json,
            PayloadHash = CatalogImportOutboxPayloadBuilder.Sha256Hex(json), CreatedAt = source.CreatedAt, SchemaVersion = source.SchemaVersion, Source = source.Source };

        internal static CatalogImportOutboxPlan Plan(IEnumerable<CatalogImportOutboxEntry> entries)
        {
            var parts = entries.ToArray();
            return new CatalogImportOutboxPlan { Entries = parts, PlanId = "catalog-plan-" + CatalogImportOutboxPayloadBuilder.Sha256Hex(string.Join("|", parts.Select(p => p.PayloadHash))),
                TotalRows = parts.Sum(CountRows) };
        }
        internal static int CountRows(CatalogImportOutboxEntry entry) => entry.OperationType == "catalog_import_correction"
            ? CatalogImportCorrectionTransport.ReadSavedRequest(entry.PayloadJson, entry.SharedProof).Correction.Items.Length
            : CatalogImportRecoveryService.Deserialize<PosCatalogImportRequest>(entry.PayloadJson).Items.Length;

        internal static int Measure(CatalogImportOutboxEntry entry)
        {
            var session = MaximumSession();
            if (entry.OperationType == "catalog_import_correction")
            {
                var correction = CatalogImportCorrectionTransport.ReadSavedRequest(entry.PayloadJson, entry.SharedProof);
                if (entry.SharedProof != null || CatalogImportRecoveryService.RequiresMultipartProof(correction.RecoveryOf.OriginalRequest))
                {
                    correction.Correction.PayloadHash=entry.PayloadHash;
                    return Encoding.UTF8.GetByteCount(CatalogImportRecoveryService.Serialize(CatalogImportRecoveryProofTransport.ProjectionForTransport(correction,
                        "10000000-0000-4000-8000-000000000001")));
                }
                correction.DeviceToken = session.DeviceToken; correction.SessionToken = session.SessionToken;
                correction.PosSessionId = session.PosSessionId; correction.ShopDeviceId = session.ShopDeviceId; correction.ShopCode = session.ShopCode;
                correction.Correction.PayloadHash = entry.PayloadHash;
                correction.RecoveryOf.OriginalRequest.PayloadHash = correction.RecoveryOf.PayloadHash;
                return Encoding.UTF8.GetByteCount(CatalogImportRecoveryService.Serialize(correction));
            }
            var request = CatalogImportRecoveryService.Deserialize<PosCatalogImportRequest>(entry.PayloadJson);
            request.Batch.AttemptCount = int.MaxValue; request.PayloadHash = entry.PayloadHash;
            request.DeviceToken = session.DeviceToken; request.SessionToken = session.SessionToken; request.PosSessionId = session.PosSessionId;
            request.ShopDeviceId = session.ShopDeviceId; request.ShopCode = session.ShopCode;
            return Encoding.UTF8.GetByteCount(CatalogImportRecoveryService.Serialize(request));
        }
    }
}
