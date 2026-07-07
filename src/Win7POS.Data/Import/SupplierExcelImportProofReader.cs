using System;
using Dapper;

namespace Win7POS.Data.Import
{
    public sealed class SupplierExcelImportProofReader
    {
        private readonly SqliteConnectionFactory _factory;

        public SupplierExcelImportProofReader(SqliteConnectionFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public SupplierExcelImportProof Read(string barcode)
        {
            using (var conn = _factory.Open())
            {
                return new SupplierExcelImportProof
                {
                    ProductRows = conn.ExecuteScalar<long>(
                        "SELECT COUNT(1) FROM products WHERE barcode = @barcode",
                        new { barcode }),
                    ImportPriceHistoryRows = conn.ExecuteScalar<long>(
                        "SELECT COUNT(1) FROM product_price_history WHERE barcode = @barcode AND source = 'IMPORT'",
                        new { barcode }),
                    OutboxRows = conn.ExecuteScalar<long>(
                        "SELECT COUNT(1) FROM catalog_import_outbox WHERE status IN ('pending', 'acked')")
                };
            }
        }
    }

    public sealed class SupplierExcelImportProof
    {
        public long ImportPriceHistoryRows { get; set; }
        public long OutboxRows { get; set; }
        public long ProductRows { get; set; }
    }
}
