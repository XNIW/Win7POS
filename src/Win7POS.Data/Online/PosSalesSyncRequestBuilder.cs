using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Win7POS.Core.Models;
using Win7POS.Core.Online;
using Win7POS.Core.Pos;
using Win7POS.Data.Repositories;

namespace Win7POS.Data.Online
{
    public static class PosSalesSyncRequestBuilder
    {
        private const string SchemaVersion = PosOnlineContract.SalesSchemaVersion;

        public static async Task<PosSalesSyncRequest> BuildAsync(
            PosTrustedDeviceSession trustedSession,
            SalesSyncOutboxItem item,
            Sale sale,
            IReadOnlyList<SaleLine> lines,
            SaleRepository sales,
            string appVersion)
        {
            if (trustedSession == null) throw new ArgumentNullException(nameof(trustedSession));
            if (item == null) throw new ArgumentNullException(nameof(item));
            if (sale == null) throw new ArgumentNullException(nameof(sale));
            if (lines == null) throw new ArgumentNullException(nameof(lines));
            if (sales == null) throw new ArgumentNullException(nameof(sales));

            var bindingError = OutboxShopBinding.GetMismatchCode(
                item.OriginShopId,
                item.OriginShopCode,
                trustedSession.ShopId,
                trustedSession.ShopCode);
            if (!string.IsNullOrWhiteSpace(bindingError))
            {
                throw new InvalidOperationException(bindingError);
            }

            var remoteProductIds = await sales
                .GetRemoteProductIdsAsync(
                    lines.Where(line => line.ProductId.HasValue).Select(line => line.ProductId.Value))
                .ConfigureAwait(false);
            var request = BuildCanonical(item, sale, lines, remoteProductIds);
            request.AppVersion = appVersion;
            request.DeviceToken = trustedSession.DeviceToken;
            request.PosSessionId = trustedSession.PosSessionId;
            request.SessionToken = trustedSession.SessionToken;
            request.ShopDeviceId = trustedSession.ShopDeviceId;
            return request;
        }

        public static PosSalesSyncRequest BuildCanonical(
            SalesSyncOutboxItem item,
            Sale sale,
            IReadOnlyList<SaleLine> lines,
            IReadOnlyDictionary<long, string> remoteProductIds)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            if (sale == null) throw new ArgumentNullException(nameof(sale));
            if (lines == null) throw new ArgumentNullException(nameof(lines));
            if (remoteProductIds == null) throw new ArgumentNullException(nameof(remoteProductIds));

            var saleKind = sale.Kind == (int)SaleKind.Void
                ? "void"
                : sale.Kind == (int)SaleKind.Refund
                    ? "refund"
                    : "sale";
            if (!string.Equals(item.SchemaVersion, SchemaVersion, StringComparison.Ordinal) ||
                !string.Equals(item.OperationType, saleKind, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("outbox_operation_mismatch");
            }
            var businessDate = DateTimeOffset
                .FromUnixTimeMilliseconds(sale.CreatedAt)
                .LocalDateTime
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var occurredAt = DateTimeOffset
                .FromUnixTimeMilliseconds(sale.CreatedAt)
                .ToUniversalTime()
                .ToString("O", CultureInfo.InvariantCulture);
            var syncLines = lines
                .Select((line, index) => BuildLine(line, index, saleKind, remoteProductIds))
                .ToArray();
            var grossClp = syncLines
                .Where(line => string.Equals(line.LineType, "item", StringComparison.Ordinal))
                .Sum(line => Math.Abs(line.AmountClp));
            var discountClp = syncLines
                .Where(line => string.Equals(line.LineType, "discount", StringComparison.Ordinal))
                .Sum(line => Math.Abs(line.AmountClp));
            var payments = BuildPayments(sale);
            var clientSaleId = string.IsNullOrWhiteSpace(sale.ClientSaleId)
                ? item.ClientSaleId
                : sale.ClientSaleId;
            var clientBatchId = "win7pos-" + clientSaleId + "-batch-v2";

            return new PosSalesSyncRequest
            {
                Batch = new PosSalesSyncBatchRequest
                {
                    ClientBatchId = clientBatchId,
                    IdempotencyKey = clientBatchId,
                },
                Sales = new[]
                {
                    new PosSalesSyncSaleRequest
                    {
                        Amounts = new PosSalesSyncAmounts
                        {
                            ChangeClp = Math.Abs(sale.Change),
                            DiscountClp = discountClp,
                            GrossClp = grossClp,
                            NetClp = sale.Total,
                            PaidClp = payments.Sum(payment => payment.AmountClp),
                            TaxClp = 0,
                        },
                        BusinessDate = businessDate,
                        ClientOriginalSaleId =
                            (sale.Kind == (int)SaleKind.Refund || sale.Kind == (int)SaleKind.Void) &&
                            sale.RelatedSaleId.HasValue
                                ? "win7pos-sale-" + sale.RelatedSaleId.Value.ToString(CultureInfo.InvariantCulture)
                                : null,
                        ClientSaleId = clientSaleId,
                        Currency = "CLP",
                        Fiscal = new PosSalesSyncFiscal
                        {
                            DocumentNumber = sale.Code,
                            DocumentType = "boleta",
                            PrintedAt = occurredAt,
                            Status = sale.PdfPrinted
                                ? "printed_local_pdf"
                                : sale.PaidCard > 0 && sale.PaidCash == 0
                                    ? "not_printed_card_policy"
                                    : "not_reported",
                        },
                        IdempotencyKey = item.IdempotencyKey,
                        Kind = saleKind,
                        Lines = syncLines,
                        OccurredAt = occurredAt,
                        Payments = payments,
                        ReversalReason = sale.Kind == (int)SaleKind.Refund || sale.Kind == (int)SaleKind.Void
                            ? TrimOrNull(sale.Reason, 240)
                            : null,
                        SaleNumber = sale.Code,
                    }
                },
                SchemaVersion = SchemaVersion,
                ShopCode = item.OriginShopCode,
            };
        }

        public static string SerializeRedacted(PosSalesSyncRequest request)
        {
            if (request == null)
            {
                return "{}";
            }

            return SerializeCanonical(request);
        }

        public static string SerializeCanonical(PosSalesSyncRequest request)
        {
            if (request == null)
            {
                return "{}";
            }

            var canonical = new PosSalesSyncRequest
            {
                Batch = request.Batch,
                DeviceToken = null,
                PosSessionId = null,
                Sales = request.Sales,
                SchemaVersion = request.SchemaVersion,
                SessionToken = null,
                ShopCode = request.ShopCode,
                ShopDeviceId = null,
            };

            return Serialize(canonical);
        }

        public static string Sha256Hex(string value)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes)
                {
                    sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                }

                return sb.ToString();
            }
        }

        public static PosSalesSyncRequest DeserializeCanonical(string payloadJson)
        {
            if (string.IsNullOrWhiteSpace(payloadJson))
            {
                return null;
            }

            try
            {
                var serializer = new DataContractJsonSerializer(typeof(PosSalesSyncRequest));
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(payloadJson)))
                {
                    return serializer.ReadObject(stream) as PosSalesSyncRequest;
                }
            }
            catch
            {
                return null;
            }
        }

        private static PosSalesSyncLine BuildLine(
            SaleLine line,
            int index,
            string saleKind,
            IReadOnlyDictionary<long, string> remoteProductIds)
        {
            var barcode = line.Barcode ?? string.Empty;
            var isDiscount = DiscountKeys.IsDiscount(barcode);
            var isManual = barcode.StartsWith(DiscountKeys.ManualPrefix, StringComparison.Ordinal);
            var lineType = isDiscount ? "discount" : "item";
            var signedAmount = line.LineTotal;

            if ((string.Equals(saleKind, "refund", StringComparison.Ordinal) ||
                 string.Equals(saleKind, "void", StringComparison.Ordinal)) &&
                signedAmount > 0)
            {
                signedAmount = -Math.Abs(signedAmount);
            }
            else if (isDiscount)
            {
                signedAmount = -Math.Abs(signedAmount);
            }
            else if (string.Equals(saleKind, "sale", StringComparison.Ordinal))
            {
                signedAmount = Math.Abs(signedAmount);
            }

            var stockQuantityDelta = 0;
            if (!isDiscount && !isManual && line.ProductId.HasValue)
            {
                stockQuantityDelta =
                    string.Equals(saleKind, "refund", StringComparison.Ordinal) ||
                    string.Equals(saleKind, "void", StringComparison.Ordinal)
                        ? Math.Abs(line.Quantity)
                        : -Math.Abs(line.Quantity);
            }

            string remoteProductId = null;
            if (line.ProductId.HasValue)
            {
                remoteProductIds.TryGetValue(line.ProductId.Value, out remoteProductId);
            }

            return new PosSalesSyncLine
            {
                AmountClp = signedAmount,
                Barcode = TrimOrNull(barcode, 80),
                ClientLineId = "line-" + line.Id.ToString(CultureInfo.InvariantCulture),
                LinePosition = index + 1,
                LineType = lineType,
                LocalProductId = line.ProductId.HasValue
                    ? line.ProductId.Value.ToString(CultureInfo.InvariantCulture)
                    : null,
                ProductId = TrimOrNull(remoteProductId, 80),
                ProductName = TrimOrNull(line.Name, 160),
                Quantity = Math.Abs(line.Quantity),
                StockQuantityDelta = stockQuantityDelta,
                UnitAmountClp = Math.Abs(line.UnitPrice),
            };
        }

        private static PosSalesSyncPayment[] BuildPayments(Sale sale)
        {
            var rows = new List<PosSalesSyncPayment>();

            if (sale.PaidCash != 0)
            {
                rows.Add(new PosSalesSyncPayment
                {
                    AmountClp = sale.PaidCash,
                    ChangeClp = Math.Abs(sale.Change),
                    ClientPaymentId = PosOnlineContract.PaymentCash,
                    Method = PosOnlineContract.PaymentCash,
                });
            }

            if (sale.PaidCard != 0)
            {
                rows.Add(new PosSalesSyncPayment
                {
                    AmountClp = sale.PaidCard,
                    ChangeClp = 0,
                    ClientPaymentId = PosOnlineContract.PaymentCard,
                    Method = PosOnlineContract.PaymentCard,
                });
            }

            if (rows.Count == 0)
            {
                rows.Add(new PosSalesSyncPayment
                {
                    AmountClp = sale.Total,
                    ChangeClp = 0,
                    ClientPaymentId = PosOnlineContract.PaymentOther,
                    Method = PosOnlineContract.PaymentOther,
                });
            }

            return rows.ToArray();
        }

        private static string TrimOrNull(string value, int maxLength)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (normalized.Length == 0)
            {
                return null;
            }

            return normalized.Length > maxLength
                ? normalized.Substring(0, maxLength)
                : normalized;
        }

        private static string Serialize<T>(T value)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
    }
}
