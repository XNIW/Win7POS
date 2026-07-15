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
            ReversalEconomicsResult reversalEconomics = null;
            if (sale.Kind == (int)SaleKind.Refund || sale.Kind == (int)SaleKind.Void)
            {
                if (!sale.RelatedSaleId.HasValue)
                {
                    throw new InvalidOperationException(ReversalEconomicsPolicy.InvalidOriginalCode);
                }

                var snapshot = await sales
                    .GetReversalEconomicsSnapshotExcludingAsync(sale.RelatedSaleId.Value, sale.Id)
                    .ConfigureAwait(false);
                reversalEconomics = ReversalEconomicsPolicy.Calculate(
                    snapshot,
                    ReversalEconomicsPolicy.CalculateItemGross(lines));
            }

            var request = BuildCanonical(item, sale, lines, remoteProductIds, reversalEconomics);
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
            IReadOnlyDictionary<long, string> remoteProductIds,
            ReversalEconomicsResult reversalEconomics = null)
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
            var isReversal = !string.Equals(saleKind, "sale", StringComparison.Ordinal);
            if (!string.Equals(item.SchemaVersion, SchemaVersion, StringComparison.Ordinal) ||
                !string.Equals(item.OperationType, saleKind, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("outbox_operation_mismatch");
            }
            if (isReversal && lines.Any(line =>
                line == null || DiscountKeys.IsEconomicAdjustment(line.Barcode)))
            {
                throw new InvalidOperationException(ReversalEconomicsPolicy.MismatchCode);
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
            var taxClp = syncLines
                .Where(line => string.Equals(line.LineType, "tax", StringComparison.Ordinal))
                .Sum(line => Math.Abs(line.AmountClp));
            var payments = BuildPayments(sale);
            if (!isReversal)
            {
                try
                {
                    if (sale.Total != checked(checked(grossClp - discountClp) + taxClp))
                    {
                        throw new InvalidOperationException("sale_economics_mismatch");
                    }
                }
                catch (OverflowException)
                {
                    throw new InvalidOperationException("sale_economics_mismatch");
                }
            }
            else
            {
                if (reversalEconomics == null ||
                    reversalEconomics.GrossClp != grossClp ||
                    reversalEconomics.NetClp != sale.Total ||
                    sale.Change != 0 ||
                    payments.Sum(payment => payment.AmountClp) != sale.Total)
                {
                    throw new InvalidOperationException(ReversalEconomicsPolicy.MismatchCode);
                }

                discountClp = reversalEconomics.DiscountClp;
                taxClp = reversalEconomics.TaxClp;
            }
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
                            TaxClp = taxClp,
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
            var isReversal =
                string.Equals(saleKind, "refund", StringComparison.Ordinal) ||
                string.Equals(saleKind, "void", StringComparison.Ordinal);
            if (isReversal &&
                (!line.RelatedOriginalLineId.HasValue || line.RelatedOriginalLineId.Value <= 0))
            {
                throw new InvalidOperationException("reversal_original_line_missing");
            }

            var barcode = line.Barcode ?? string.Empty;
            var isDiscount = DiscountKeys.IsDiscount(barcode);
            var isTax = DiscountKeys.IsTax(barcode);
            var isManual = barcode.StartsWith(DiscountKeys.ManualPrefix, StringComparison.Ordinal);
            var lineType = isDiscount ? "discount" : isTax ? "tax" : "item";
            var signedAmount = line.LineTotal;

            if (isReversal && signedAmount > 0)
            {
                signedAmount = -Math.Abs(signedAmount);
            }
            else if (isDiscount)
            {
                signedAmount = -Math.Abs(signedAmount);
            }
            else if (isTax)
            {
                signedAmount = Math.Abs(signedAmount);
            }
            else if (string.Equals(saleKind, "sale", StringComparison.Ordinal))
            {
                signedAmount = Math.Abs(signedAmount);
            }

            var stockQuantityDelta = 0;
            if (!isDiscount && !isTax && !isManual && line.ProductId.HasValue)
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
                ClientOriginalLineId = isReversal
                    ? "line-" + line.RelatedOriginalLineId.Value.ToString(CultureInfo.InvariantCulture)
                    : null,
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

        public static bool HasCompleteReversalBindings(PosSalesSyncRequest request)
        {
            var sales = request?.Sales;
            if (sales == null || sales.Length != 1 || sales[0] == null)
            {
                return false;
            }

            var sale = sales[0];
            if (!string.Equals(sale.Kind, "refund", StringComparison.Ordinal) &&
                !string.Equals(sale.Kind, "void", StringComparison.Ordinal))
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(sale.ClientOriginalSaleId) &&
                sale.Lines != null &&
                sale.Lines.Length > 0 &&
                sale.Lines.All(line =>
                    line != null &&
                    string.Equals(line.LineType, "item", StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(line.ClientOriginalLineId));
        }

        public static bool HasExpectedReversalEconomics(
            PosSalesSyncRequest request,
            ReversalEconomicsResult expected)
        {
            var sales = request?.Sales;
            if (expected == null || sales == null || sales.Length != 1 || sales[0] == null)
            {
                return false;
            }

            var sale = sales[0];
            if ((!string.Equals(sale.Kind, "refund", StringComparison.Ordinal) &&
                 !string.Equals(sale.Kind, "void", StringComparison.Ordinal)) ||
                sale.Amounts == null ||
                sale.Lines == null ||
                sale.Lines.Length == 0 ||
                sale.Payments == null ||
                !HasCompleteReversalBindings(request))
            {
                return false;
            }

            try
            {
                var gross = sale.Lines.Aggregate(0L, (total, line) =>
                {
                    if (line == null ||
                        !string.Equals(line.LineType, "item", StringComparison.Ordinal) ||
                        line.Quantity <= 0 ||
                        line.UnitAmountClp < 0 ||
                        line.AmountClp >= 0 ||
                        line.AmountClp != -checked((long)line.Quantity * line.UnitAmountClp))
                    {
                        throw new InvalidOperationException(ReversalEconomicsPolicy.MismatchCode);
                    }

                    return checked(total + Math.Abs(line.AmountClp));
                });
                var paid = sale.Payments.Aggregate(
                    0L,
                    (total, payment) => checked(total + (payment?.AmountClp ?? long.MaxValue)));

                return gross == expected.GrossClp &&
                    sale.Amounts.GrossClp == expected.GrossClp &&
                    sale.Amounts.DiscountClp == expected.DiscountClp &&
                    sale.Amounts.TaxClp == expected.TaxClp &&
                    sale.Amounts.NetClp == expected.NetClp &&
                    sale.Amounts.PaidClp == expected.NetClp &&
                    sale.Amounts.ChangeClp == 0 &&
                    paid == expected.NetClp &&
                    expected.NetClp == -checked(
                        checked(expected.GrossClp - expected.DiscountClp) + expected.TaxClp) &&
                    sale.Payments.All(payment => payment != null && payment.ChangeClp == 0);
            }
            catch (Exception ex) when (ex is OverflowException || ex is ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        public static bool TryGetReversalGross(PosSalesSyncRequest request, out long grossClp)
        {
            grossClp = 0;
            var sales = request?.Sales;
            if (sales == null || sales.Length != 1 || sales[0] == null ||
                sales[0].Lines == null || sales[0].Lines.Length == 0 ||
                (!string.Equals(sales[0].Kind, "refund", StringComparison.Ordinal) &&
                 !string.Equals(sales[0].Kind, "void", StringComparison.Ordinal)))
            {
                return false;
            }

            try
            {
                foreach (var line in sales[0].Lines)
                {
                    if (line == null ||
                        !string.Equals(line.LineType, "item", StringComparison.Ordinal) ||
                        line.AmountClp >= 0)
                    {
                        grossClp = 0;
                        return false;
                    }

                    grossClp = checked(grossClp + Math.Abs(line.AmountClp));
                }

                return grossClp > 0;
            }
            catch (OverflowException)
            {
                grossClp = 0;
                return false;
            }
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
