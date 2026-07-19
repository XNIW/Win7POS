using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Win7POS.Core.Models;

namespace Win7POS.Core.Receipt
{
    /// <summary>
    /// Immutable snapshot consumed by every normal sales-receipt surface.
    /// It freezes persisted sale economics and shop data before formatting so
    /// preview and print cannot diverge if mutable entities change afterwards.
    /// </summary>
    public sealed class SalesReceiptRenderModel
    {
        private SalesReceiptRenderModel(
            SaleSnapshot sale,
            IReadOnlyList<LineSnapshot> lines,
            ShopSnapshot shop)
        {
            Sale = sale ?? throw new ArgumentNullException(nameof(sale));
            Lines = lines ?? throw new ArgumentNullException(nameof(lines));
            Shop = shop ?? throw new ArgumentNullException(nameof(shop));
        }

        public SaleSnapshot Sale { get; }
        public IReadOnlyList<LineSnapshot> Lines { get; }
        public ShopSnapshot Shop { get; }

        public static SalesReceiptRenderModel Create(
            Sale sale,
            IReadOnlyList<SaleLine> lines,
            ReceiptShopInfo shop = null)
        {
            if (sale == null) throw new ArgumentNullException(nameof(sale));

            var frozenLines = new List<LineSnapshot>();
            if (lines != null)
            {
                foreach (var line in lines)
                    if (line != null) frozenLines.Add(new LineSnapshot(line));
            }

            return new SalesReceiptRenderModel(
                new SaleSnapshot(sale),
                new ReadOnlyCollection<LineSnapshot>(frozenLines),
                new ShopSnapshot(shop ?? new ReceiptShopInfo()));
        }

        public sealed class SaleSnapshot
        {
            internal SaleSnapshot(Sale source)
            {
                Id = source.Id;
                ClientSaleId = source.ClientSaleId ?? string.Empty;
                Code = source.Code ?? string.Empty;
                CreatedAt = source.CreatedAt;
                Kind = source.Kind;
                RelatedSaleId = source.RelatedSaleId;
                VoidedBySaleId = source.VoidedBySaleId;
                VoidedAt = source.VoidedAt;
                Reason = source.Reason ?? string.Empty;
                Total = source.Total;
                PaidCash = source.PaidCash;
                PaidCard = source.PaidCard;
                Change = source.Change;
                OperatorId = source.OperatorId;
                ReceiptShopSnapshotJson = source.ReceiptShopSnapshotJson ?? string.Empty;
            }

            public long Id { get; }
            public string ClientSaleId { get; }
            public string Code { get; }
            public long CreatedAt { get; }
            public int Kind { get; }
            public long? RelatedSaleId { get; }
            public long? VoidedBySaleId { get; }
            public long? VoidedAt { get; }
            public string Reason { get; }
            public long Total { get; }
            public long PaidCash { get; }
            public long PaidCard { get; }
            public long Change { get; }
            public int? OperatorId { get; }
            public string ReceiptShopSnapshotJson { get; }
        }

        public sealed class LineSnapshot
        {
            internal LineSnapshot(SaleLine source)
            {
                Id = source.Id;
                SaleId = source.SaleId;
                ProductId = source.ProductId;
                Barcode = source.Barcode ?? string.Empty;
                Name = source.Name ?? string.Empty;
                Quantity = source.Quantity;
                UnitPrice = source.UnitPrice;
                LineTotal = source.LineTotal;
                RelatedOriginalLineId = source.RelatedOriginalLineId;
            }

            public long Id { get; }
            public long SaleId { get; }
            public long? ProductId { get; }
            public string Barcode { get; }
            public string Name { get; }
            public int Quantity { get; }
            public long UnitPrice { get; }
            public long LineTotal { get; }
            public long? RelatedOriginalLineId { get; }
        }

        public sealed class ShopSnapshot
        {
            internal ShopSnapshot(ReceiptShopInfo source)
            {
                Name = source.Name ?? string.Empty;
                Address = source.Address ?? string.Empty;
                BusinessGiro = source.BusinessGiro ?? string.Empty;
                City = source.City ?? string.Empty;
                LegalRepresentativeRut = source.LegalRepresentativeRut ?? string.Empty;
                Rut = source.Rut ?? string.Empty;
                Phone = source.Phone ?? string.Empty;
                Footer = source.Footer ?? string.Empty;
                ShopCode = source.ShopCode ?? string.Empty;
                ShopStatus = source.ShopStatus ?? string.Empty;
                Source = source.Source ?? string.Empty;
                SyncedAtUtc = source.SyncedAtUtc ?? string.Empty;
            }

            public string Name { get; }
            public string Address { get; }
            public string BusinessGiro { get; }
            public string City { get; }
            public string LegalRepresentativeRut { get; }
            public string Rut { get; }
            public string Phone { get; }
            public string Footer { get; }
            public string ShopCode { get; }
            public string ShopStatus { get; }
            public string Source { get; }
            public string SyncedAtUtc { get; }
        }
    }
}
