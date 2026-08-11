using System;
using System.Collections.Generic;
using Win7POS.Data.Online;

namespace Win7POS.Data.Repositories
{
    public sealed class LocalArticleCreateRequest
    {
        public string Barcode { get; set; }
        public string PrimaryName { get; set; }
        public string ItemNumber { get; set; }
        public string SecondaryName { get; set; }
        public long RetailPrice { get; set; }
        public int PurchasePrice { get; set; }
        public int? SupplierId { get; set; }
        public string SupplierName { get; set; }
        public int? CategoryId { get; set; }
        public string CategoryName { get; set; }
        public int InitialStock { get; set; }
        public long? DuplicateSourceProductId { get; set; }
        public DateTimeOffset OccurredAt { get; set; }
    }

    public sealed class LocalArticleUpdateRequest
    {
        public long ProductId { get; set; }
        public string Barcode { get; set; }
        public string PrimaryName { get; set; }
        public string ItemNumber { get; set; }
        public string SecondaryName { get; set; }
        public long RetailPrice { get; set; }
        public int PurchasePrice { get; set; }
        public int? SupplierId { get; set; }
        public string SupplierName { get; set; }
        public int? CategoryId { get; set; }
        public string CategoryName { get; set; }
        public int StockQuantity { get; set; }
        public string StockReason { get; set; } = "count_correction";
        public DateTimeOffset OccurredAt { get; set; }
    }

    public sealed class LocalArticleWriteResult
    {
        public IReadOnlyList<ProductImageCancelledStaging> CancelledProductImages { get; internal set; } =
            Array.Empty<ProductImageCancelledStaging>();
        public long ProductId { get; internal set; }
        public string ClientProductId { get; internal set; }
        public IReadOnlyList<ArticleMutationEnqueueResult> Mutations { get; internal set; } =
            Array.Empty<ArticleMutationEnqueueResult>();
    }
}
