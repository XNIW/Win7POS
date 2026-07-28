using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Online;
using Win7POS.Core.Receipt;
using Win7POS.Data.Online;

namespace Win7POS.Data.Repositories
{
    /// <summary>
    /// Owns explicit operator article saves. The local projection, local
    /// history/adjustment evidence and every outbound intent share one SQLite
    /// transaction guarded against an authoritative catalog apply.
    /// </summary>
    internal sealed class LocalArticleMutationWriter
    {
        private readonly SqliteConnectionFactory _factory;

        internal LocalArticleMutationWriter(SqliteConnectionFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        internal async Task<LocalArticleWriteResult> CreateAsync(
            LocalArticleCreateRequest request,
            ProductWriteOrigin origin)
        {
            EnsureLocalUserOrigin(origin);
            ValidateCreate(request);
            await CatalogMutationGate.Instance.WaitAsync().ConfigureAwait(false);
            try
            {
                using (var connection = _factory.Open())
                using (var transaction = connection.BeginTransaction())
                {
                    try
                    {
                        var now = EffectiveTime(request.OccurredAt);
                        var source = request.DuplicateSourceProductId.HasValue
                            ? await LoadProductStateAsync(
                                connection,
                                transaction,
                                request.DuplicateSourceProductId.Value)
                                .ConfigureAwait(false)
                            : null;
                        if (request.DuplicateSourceProductId.HasValue &&
                            (source == null ||
                             !Guid.TryParse(source.RemoteProductId, out _) ||
                             !PosArticleMutationIntentPolicy.IsProductRevision(
                                 source.RemoteBaseRevision)))
                        {
                            throw new InvalidOperationException(
                                "The source article has no verified remote identity.");
                        }

                        var barcode = request.Barcode.Trim();
                        var productId = await connection.ExecuteScalarAsync<long>(@"
INSERT INTO products(
  barcode,
  name,
  unitPrice,
  is_active,
  remote_deleted_at,
  remote_product_id,
  client_product_id,
  remote_base_revision)
VALUES(
  @barcode,
  @name,
  @unitPrice,
  1,
  NULL,
  NULL,
  NULL,
  NULL);
SELECT last_insert_rowid();",
                            new
                            {
                                barcode,
                                name = NormalizeRequired(request.PrimaryName),
                                unitPrice = request.RetailPrice
                            },
                            transaction).ConfigureAwait(false);

                        var references = await ResolveReferencesAsync(
                            connection,
                            transaction,
                            request.SupplierId,
                            request.SupplierName,
                            request.CategoryId,
                            request.CategoryName).ConfigureAwait(false);
                        await InsertMetaAsync(
                            connection,
                            transaction,
                            barcode,
                            request.ItemNumber,
                            request.SecondaryName,
                            request.PurchasePrice,
                            references,
                            request.InitialStock).ConfigureAwait(false);

                        var mutations = new List<ArticleMutationEnqueueResult>();
                        if (source == null)
                        {
                            var changes = BuildCreateChanges(request, references);
                            mutations.Add(await EnqueueAsync(
                                connection,
                                transaction,
                                productId,
                                PosArticleMutationKinds.ProductCreate,
                                changes,
                                Array.Empty<string>(),
                                now,
                                references.DependencyCode).ConfigureAwait(false));
                            await AddInitialPriceHistoryAsync(
                                connection,
                                transaction,
                                barcode,
                                request.PurchasePrice,
                                checked((int)request.RetailPrice),
                                now).ConfigureAwait(false);
                        }
                        else
                        {
                            var changes = BuildDuplicateChanges(request, references);
                            mutations.Add(await EnqueueAsync(
                                connection,
                                transaction,
                                productId,
                                PosArticleMutationKinds.ProductDuplicate,
                                changes,
                                Array.Empty<string>(),
                                now,
                                references.DependencyCode,
                                source.RemoteProductId,
                                source.RemoteBaseRevision).ConfigureAwait(false));

                            if (source.PurchasePrice != request.PurchasePrice)
                            {
                                await AddPriceMutationAsync(
                                    connection,
                                    transaction,
                                    productId,
                                    barcode,
                                    "purchase",
                                    source.PurchasePrice,
                                    request.PurchasePrice,
                                    now,
                                    mutations).ConfigureAwait(false);
                            }
                            if (source.UnitPrice != request.RetailPrice)
                            {
                                await AddPriceMutationAsync(
                                    connection,
                                    transaction,
                                    productId,
                                    barcode,
                                    "retail",
                                    checked((int)source.UnitPrice),
                                    checked((int)request.RetailPrice),
                                    now,
                                    mutations).ConfigureAwait(false);
                            }
                            var stockDelta = checked(
                                request.InitialStock - source.StockQuantity);
                            if (stockDelta != 0)
                            {
                                await AddStockMutationAsync(
                                    connection,
                                    transaction,
                                    productId,
                                    barcode,
                                    stockDelta,
                                    "count_correction",
                                    now,
                                    mutations).ConfigureAwait(false);
                            }
                        }

                        transaction.Commit();
                        return new LocalArticleWriteResult
                        {
                            ProductId = productId,
                            ClientProductId = mutations[0].ClientProductId,
                            Mutations = new ReadOnlyCollection<ArticleMutationEnqueueResult>(
                                mutations)
                        };
                    }
                    catch
                    {
                        transaction.Rollback();
                        throw;
                    }
                }
            }
            finally
            {
                CatalogMutationGate.Instance.Release();
            }
        }

        internal async Task<LocalArticleWriteResult> UpdateAsync(
            LocalArticleUpdateRequest request,
            ProductWriteOrigin origin)
        {
            EnsureLocalUserOrigin(origin);
            ValidateUpdate(request);
            await CatalogMutationGate.Instance.WaitAsync().ConfigureAwait(false);
            try
            {
                using (var connection = _factory.Open())
                using (var transaction = connection.BeginTransaction())
                {
                    try
                    {
                        var current = await LoadProductStateAsync(
                            connection,
                            transaction,
                            request.ProductId).ConfigureAwait(false);
                        if (current == null)
                            throw new InvalidOperationException("Product not found.");

                        var now = EffectiveTime(request.OccurredAt);
                        var barcode = request.Barcode.Trim();
                        var primaryName = NormalizeRequired(request.PrimaryName);
                        var itemNumber = NormalizeOptional(request.ItemNumber);
                        var secondaryName = NormalizeOptional(request.SecondaryName);
                        var references = await ResolveReferencesAsync(
                            connection,
                            transaction,
                            request.SupplierId,
                            request.SupplierName,
                            request.CategoryId,
                            request.CategoryName).ConfigureAwait(false);

                        await connection.ExecuteAsync(@"
UPDATE products
SET barcode = @barcode,
    name = @primaryName,
    unitPrice = @retailPrice
WHERE id = @productId;",
                            new
                            {
                                productId = request.ProductId,
                                barcode = barcode,
                                primaryName = primaryName,
                                retailPrice = request.RetailPrice
                            },
                            transaction).ConfigureAwait(false);

                        var metaRows = await connection.ExecuteAsync(@"
UPDATE product_meta
SET barcode = @barcode,
    article_code = @itemNumber,
    name2 = @secondaryName,
    purchase_price = @purchasePrice,
    supplier_id = @supplierId,
    supplier_name = @supplierName,
    category_id = @categoryId,
    category_name = @categoryName,
    stock_qty = @stockQuantity
WHERE barcode = @oldBarcode;",
                            new
                            {
                                barcode = barcode,
                                itemNumber = itemNumber ?? string.Empty,
                                secondaryName = secondaryName ?? string.Empty,
                                purchasePrice = request.PurchasePrice,
                                supplierId = references.Supplier.Id,
                                supplierName = references.Supplier.Name,
                                categoryId = references.Category.Id,
                                categoryName = references.Category.Name,
                                stockQuantity = request.StockQuantity,
                                oldBarcode = current.Barcode
                            },
                            transaction).ConfigureAwait(false);
                        if (metaRows == 0)
                        {
                            await InsertMetaAsync(
                                connection,
                                transaction,
                                barcode,
                                itemNumber,
                                secondaryName,
                                request.PurchasePrice,
                                references,
                                request.StockQuantity).ConfigureAwait(false);
                        }

                        var mutations = new List<ArticleMutationEnqueueResult>();
                        var updateChanges = new Dictionary<string, object>(
                            StringComparer.Ordinal);
                        if (!string.Equals(current.Barcode, barcode, StringComparison.Ordinal))
                            updateChanges.Add(PosArticleMutationFields.Barcode, barcode);
                        if (!string.Equals(
                                NormalizeOptional(current.ItemNumber),
                                itemNumber,
                                StringComparison.Ordinal))
                        {
                            updateChanges.Add(
                                PosArticleMutationFields.ItemNumber,
                                itemNumber);
                        }
                        if (!string.Equals(
                                current.PrimaryName,
                                primaryName,
                                StringComparison.Ordinal))
                        {
                            updateChanges.Add(
                                PosArticleMutationFields.PrimaryName,
                                primaryName);
                        }
                        if (!string.Equals(
                                NormalizeOptional(current.SecondaryName),
                                secondaryName,
                                StringComparison.Ordinal))
                        {
                            updateChanges.Add(
                                PosArticleMutationFields.SecondaryName,
                                secondaryName);
                        }
                        if (current.CategoryId != references.Category.Id)
                        {
                            updateChanges.Add(
                                PosArticleMutationFields.CategoryId,
                                references.Category.Id.HasValue
                                    ? ReferenceIntentValue(
                                        references.Category,
                                        category: true)
                                    : null);
                        }
                        if (current.SupplierId != references.Supplier.Id)
                        {
                            updateChanges.Add(
                                PosArticleMutationFields.SupplierId,
                                references.Supplier.Id.HasValue
                                    ? ReferenceIntentValue(
                                        references.Supplier,
                                        category: false)
                                    : null);
                        }

                        if (updateChanges.Count > 0)
                        {
                            mutations.Add(await EnqueueAsync(
                                connection,
                                transaction,
                                request.ProductId,
                                PosArticleMutationKinds.ProductUpdate,
                                updateChanges,
                                updateChanges.Keys.ToArray(),
                                now,
                                MissingReferenceForChanges(
                                    updateChanges,
                                    references)).ConfigureAwait(false));
                        }

                        if (current.PurchasePrice != request.PurchasePrice)
                        {
                            await AddPriceMutationAsync(
                                connection,
                                transaction,
                                request.ProductId,
                                barcode,
                                "purchase",
                                current.PurchasePrice,
                                request.PurchasePrice,
                                now,
                                mutations).ConfigureAwait(false);
                        }
                        if (current.UnitPrice != request.RetailPrice)
                        {
                            await AddPriceMutationAsync(
                                connection,
                                transaction,
                                request.ProductId,
                                barcode,
                                "retail",
                                checked((int)current.UnitPrice),
                                checked((int)request.RetailPrice),
                                now,
                                mutations).ConfigureAwait(false);
                        }
                        var stockDelta = checked(
                            request.StockQuantity - current.StockQuantity);
                        if (stockDelta != 0)
                        {
                            await AddStockMutationAsync(
                                connection,
                                transaction,
                                request.ProductId,
                                barcode,
                                stockDelta,
                                NormalizeStockReason(request.StockReason),
                                now,
                                mutations).ConfigureAwait(false);
                        }

                        transaction.Commit();
                        return new LocalArticleWriteResult
                        {
                            ProductId = request.ProductId,
                            ClientProductId = mutations.Count == 0
                                ? current.ClientProductId
                                : mutations[0].ClientProductId,
                            Mutations = new ReadOnlyCollection<ArticleMutationEnqueueResult>(
                                mutations)
                        };
                    }
                    catch
                    {
                        transaction.Rollback();
                        throw;
                    }
                }
            }
            finally
            {
                CatalogMutationGate.Instance.Release();
            }
        }

        internal async Task<LocalArticleWriteResult> SetActiveAsync(
            long productId,
            bool active,
            ProductWriteOrigin origin)
        {
            EnsureLocalUserOrigin(origin);
            if (productId <= 0) throw new ArgumentOutOfRangeException(nameof(productId));
            await CatalogMutationGate.Instance.WaitAsync().ConfigureAwait(false);
            try
            {
                using (var connection = _factory.Open())
                using (var transaction = connection.BeginTransaction())
                {
                    try
                    {
                        var current = await LoadProductStateAsync(
                            connection,
                            transaction,
                            productId).ConfigureAwait(false);
                        if (current == null)
                            throw new InvalidOperationException("Product not found.");
                        if (current.IsActive == active)
                        {
                            transaction.Commit();
                            return new LocalArticleWriteResult
                            {
                                ProductId = productId,
                                ClientProductId = current.ClientProductId
                            };
                        }

                        var occurredAt = DateTimeOffset.UtcNow;
                        await connection.ExecuteAsync(@"
UPDATE products
SET is_active = @active,
    remote_deleted_at = CASE WHEN @active = 1 THEN NULL ELSE @deletedAt END
WHERE id = @productId;",
                            new
                            {
                                productId,
                                active = active ? 1 : 0,
                                deletedAt = FormatTimestamp(occurredAt)
                            },
                            transaction).ConfigureAwait(false);
                        var mutation = await EnqueueAsync(
                            connection,
                            transaction,
                            productId,
                            active
                                ? PosArticleMutationKinds.ProductActivate
                                : PosArticleMutationKinds.ProductDeactivate,
                            new Dictionary<string, object>(StringComparer.Ordinal),
                            Array.Empty<string>(),
                            occurredAt,
                            null).ConfigureAwait(false);
                        transaction.Commit();
                        return new LocalArticleWriteResult
                        {
                            ProductId = productId,
                            ClientProductId = mutation.ClientProductId,
                            Mutations = new[] { mutation }
                        };
                    }
                    catch
                    {
                        transaction.Rollback();
                        throw;
                    }
                }
            }
            finally
            {
                CatalogMutationGate.Instance.Release();
            }
        }

        private static async Task AddPriceMutationAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long productId,
            string barcode,
            string priceType,
            int oldPrice,
            int newPrice,
            DateTimeOffset occurredAt,
            ICollection<ArticleMutationEnqueueResult> mutations)
        {
            var mutation = await EnqueueAsync(
                connection,
                transaction,
                productId,
                string.Equals(priceType, "retail", StringComparison.Ordinal)
                    ? PosArticleMutationKinds.ProductRetailPriceChange
                    : PosArticleMutationKinds.ProductPurchasePriceChange,
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    [PosArticleMutationFields.Price] = newPrice
                },
                Array.Empty<string>(),
                occurredAt,
                null).ConfigureAwait(false);
            var historyId = await connection.ExecuteScalarAsync<long>(@"
INSERT INTO product_price_history(
  barcode,
  timestamp,
  type,
  old_price,
  new_price,
  source,
  article_mutation_id)
VALUES(
  @barcode,
  @timestamp,
  @priceType,
  @oldPrice,
  @newPrice,
  'MANUAL_EDIT',
  @mutationId);
SELECT last_insert_rowid();",
                new
                {
                    barcode,
                    timestamp = FormatTimestamp(occurredAt),
                    priceType,
                    oldPrice,
                    newPrice,
                    mutationId = mutation.MutationId
                },
                transaction).ConfigureAwait(false);
            await connection.ExecuteAsync(@"
UPDATE article_mutation_outbox
SET local_price_history_id = @historyId
WHERE mutation_id = @mutationId;",
                new { historyId, mutationId = mutation.MutationId },
                transaction).ConfigureAwait(false);
            mutations.Add(mutation);
        }

        private static async Task AddInitialPriceHistoryAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string barcode,
            int purchasePrice,
            int retailPrice,
            DateTimeOffset occurredAt)
        {
            await connection.ExecuteAsync(@"
INSERT INTO product_price_history(
  barcode,
  timestamp,
  type,
  old_price,
  new_price,
  source,
  article_mutation_id)
VALUES(
  @barcode,
  @timestamp,
  'retail',
  NULL,
  @retailPrice,
  'MANUAL_CREATE',
  NULL);",
                new
                {
                    barcode,
                    timestamp = FormatTimestamp(occurredAt),
                    retailPrice
                },
                transaction).ConfigureAwait(false);
            if (purchasePrice > 0)
            {
                await connection.ExecuteAsync(@"
INSERT INTO product_price_history(
  barcode,
  timestamp,
  type,
  old_price,
  new_price,
  source,
  article_mutation_id)
VALUES(
  @barcode,
  @timestamp,
  'purchase',
  NULL,
  @purchasePrice,
  'MANUAL_CREATE',
  NULL);",
                    new
                    {
                        barcode,
                        timestamp = FormatTimestamp(occurredAt),
                        purchasePrice
                    },
                    transaction).ConfigureAwait(false);
            }
        }

        private static async Task AddStockMutationAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long productId,
            string barcode,
            int quantityDelta,
            string reason,
            DateTimeOffset occurredAt,
            ICollection<ArticleMutationEnqueueResult> mutations)
        {
            var mutation = await EnqueueAsync(
                connection,
                transaction,
                productId,
                PosArticleMutationKinds.ProductManualStockAdjustment,
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    [PosArticleMutationFields.QuantityDelta] = quantityDelta,
                    [PosArticleMutationFields.Reason] = reason
                },
                Array.Empty<string>(),
                occurredAt,
                null).ConfigureAwait(false);
            var adjustmentId = await connection.ExecuteScalarAsync<long>(@"
INSERT INTO article_manual_stock_adjustments(
  local_product_id,
  mutation_id,
  barcode,
  quantity_delta,
  reason,
  occurred_at,
  created_at)
VALUES(
  @productId,
  @mutationId,
  @barcode,
  @quantityDelta,
  @reason,
  @occurredAt,
  @createdAt);
SELECT last_insert_rowid();",
                new
                {
                    productId,
                    mutationId = mutation.MutationId,
                    barcode,
                    quantityDelta,
                    reason,
                    occurredAt = FormatTimestamp(occurredAt),
                    createdAt = FormatTimestamp(DateTimeOffset.UtcNow)
                },
                transaction).ConfigureAwait(false);
            await connection.ExecuteAsync(@"
UPDATE article_mutation_outbox
SET local_stock_adjustment_id = @adjustmentId
WHERE mutation_id = @mutationId;",
                new { adjustmentId, mutationId = mutation.MutationId },
                transaction).ConfigureAwait(false);
            mutations.Add(mutation);
        }

        private static Task<ArticleMutationEnqueueResult> EnqueueAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long productId,
            string mutationKind,
            IDictionary<string, object> changes,
            IReadOnlyList<string> fieldMask,
            DateTimeOffset occurredAt,
            string dependencyCode,
            string targetRemoteProductId = null,
            string targetBaseRevision = null)
        {
            return ArticleMutationOutboxRepository.EnqueueInTransactionAsync(
                connection,
                transaction,
                new ArticleMutationEnqueueRequest
                {
                    LocalProductId = productId,
                    MutationKind = mutationKind,
                    Changes = changes,
                    FieldMask = fieldMask,
                    CreatedAt = DateTimeOffset.UtcNow,
                    OccurredAt = occurredAt,
                    DependencyCode = dependencyCode,
                    TargetRemoteProductId = targetRemoteProductId,
                    TargetBaseRevision = targetBaseRevision
                });
        }

        private static IDictionary<string, object> BuildCreateChanges(
            LocalArticleCreateRequest request,
            ResolvedArticleReferences references)
        {
            var changes = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [PosArticleMutationFields.Barcode] = request.Barcode.Trim(),
                [PosArticleMutationFields.PrimaryName] =
                    NormalizeRequired(request.PrimaryName),
                [PosArticleMutationFields.PurchasePrice] = request.PurchasePrice,
                [PosArticleMutationFields.RetailPrice] = request.RetailPrice,
                [PosArticleMutationFields.StockQuantity] = request.InitialStock
            };
            AddOptionalText(changes, PosArticleMutationFields.ItemNumber, request.ItemNumber);
            AddOptionalText(
                changes,
                PosArticleMutationFields.SecondaryName,
                request.SecondaryName);
            AddVerifiedReferences(changes, references);
            return changes;
        }

        private static IDictionary<string, object> BuildDuplicateChanges(
            LocalArticleCreateRequest request,
            ResolvedArticleReferences references)
        {
            var changes = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [PosArticleMutationFields.Barcode] = request.Barcode.Trim(),
                [PosArticleMutationFields.PrimaryName] =
                    NormalizeRequired(request.PrimaryName)
            };
            AddOptionalText(changes, PosArticleMutationFields.ItemNumber, request.ItemNumber);
            AddOptionalText(
                changes,
                PosArticleMutationFields.SecondaryName,
                request.SecondaryName);
            AddVerifiedReferences(changes, references);
            return changes;
        }

        private static void AddVerifiedReferences(
            IDictionary<string, object> changes,
            ResolvedArticleReferences references)
        {
            if (references.Category.Requested &&
                references.Category.Id.HasValue)
            {
                changes[PosArticleMutationFields.CategoryId] =
                    ReferenceIntentValue(references.Category, category: true);
            }
            if (references.Supplier.Requested &&
                references.Supplier.Id.HasValue)
            {
                changes[PosArticleMutationFields.SupplierId] =
                    ReferenceIntentValue(references.Supplier, category: false);
            }
        }

        private static string ReferenceIntentValue(
            ResolvedArticleReference reference,
            bool category)
        {
            if (reference == null || !reference.Id.HasValue)
                return null;
            if (!string.IsNullOrWhiteSpace(reference.RemoteId))
                return reference.RemoteId.Trim();
            return category
                ? ArticleMutationReferenceDependency.Category(
                    reference.Id.Value)
                : ArticleMutationReferenceDependency.Supplier(
                    reference.Id.Value);
        }

        private static string MissingReferenceForChanges(
            IDictionary<string, object> changes,
            ResolvedArticleReferences references)
        {
            var missingCategory =
                changes.ContainsKey(PosArticleMutationFields.CategoryId) &&
                references.Category.Id.HasValue &&
                string.IsNullOrWhiteSpace(references.Category.RemoteId);
            var missingSupplier =
                changes.ContainsKey(PosArticleMutationFields.SupplierId) &&
                references.Supplier.Id.HasValue &&
                string.IsNullOrWhiteSpace(references.Supplier.RemoteId);
            return missingCategory || missingSupplier
                ? ArticleMutationReferenceDependency.Code
                : null;
        }

        private static async Task<ResolvedArticleReferences> ResolveReferencesAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int? supplierId,
            string supplierName,
            int? categoryId,
            string categoryName)
        {
            var supplier = await ProductMetaResolver.ResolveSupplierReferenceAsync(
                connection,
                transaction,
                supplierId,
                supplierName).ConfigureAwait(false);
            var category = await ProductMetaResolver.ResolveCategoryReferenceAsync(
                connection,
                transaction,
                categoryId,
                categoryName).ConfigureAwait(false);
            var result = new ResolvedArticleReferences
            {
                Supplier = await LoadReferenceAsync(
                    connection,
                    transaction,
                    "suppliers",
                    "remote_supplier_id",
                    supplier).ConfigureAwait(false),
                Category = await LoadReferenceAsync(
                    connection,
                    transaction,
                    "categories",
                    "remote_category_id",
                    category).ConfigureAwait(false)
            };
            result.Supplier.Requested =
                (supplierId.HasValue && supplierId.Value > 0) ||
                !string.IsNullOrWhiteSpace(supplierName);
            result.Category.Requested =
                (categoryId.HasValue && categoryId.Value > 0) ||
                !string.IsNullOrWhiteSpace(categoryName);
            if ((result.Supplier.Requested &&
                 result.Supplier.Id.HasValue &&
                 string.IsNullOrWhiteSpace(result.Supplier.RemoteId)) ||
                (result.Category.Requested &&
                 result.Category.Id.HasValue &&
                 string.IsNullOrWhiteSpace(result.Category.RemoteId)))
            {
                result.DependencyCode = ArticleMutationReferenceDependency.Code;
            }
            return result;
        }

        private static async Task<ResolvedArticleReference> LoadReferenceAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string tableName,
            string remoteColumn,
            ProductMetaReference reference)
        {
            if (reference == null || !reference.Id.HasValue)
                return new ResolvedArticleReference();
            if ((tableName != "suppliers" && tableName != "categories") ||
                (remoteColumn != "remote_supplier_id" &&
                 remoteColumn != "remote_category_id"))
            {
                throw new InvalidDataException("Unsupported article reference table.");
            }
            var sql = "SELECT id AS Id, name AS Name, " + remoteColumn +
                " AS RemoteId FROM " + tableName +
                " WHERE id = @id LIMIT 1;";
            return await connection.QueryFirstOrDefaultAsync<ResolvedArticleReference>(
                       sql,
                       new { id = reference.Id.Value },
                       transaction).ConfigureAwait(false) ??
                   new ResolvedArticleReference();
        }

        private static Task<ProductStateRow> LoadProductStateAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long productId)
        {
            return connection.QueryFirstOrDefaultAsync<ProductStateRow>(@"
SELECT p.id AS Id,
       p.barcode AS Barcode,
       p.name AS PrimaryName,
       p.unitPrice AS UnitPrice,
       p.is_active AS IsActive,
       p.remote_product_id AS RemoteProductId,
       p.client_product_id AS ClientProductId,
       p.remote_base_revision AS RemoteBaseRevision,
       m.article_code AS ItemNumber,
       m.name2 AS SecondaryName,
       COALESCE(m.purchase_price, 0) AS PurchasePrice,
       m.supplier_id AS SupplierId,
       m.category_id AS CategoryId,
       COALESCE(m.stock_qty, 0) AS StockQuantity
FROM products p
LEFT JOIN product_meta m ON m.barcode = p.barcode
WHERE p.id = @productId
LIMIT 1;",
                new { productId },
                transaction);
        }

        private static Task InsertMetaAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string barcode,
            string itemNumber,
            string secondaryName,
            int purchasePrice,
            ResolvedArticleReferences references,
            int stockQuantity)
        {
            return connection.ExecuteAsync(@"
INSERT INTO product_meta(
  barcode,
  article_code,
  name2,
  purchase_price,
  purchase_old,
  retail_old,
  supplier_id,
  supplier_name,
  category_id,
  category_name,
  stock_qty)
VALUES(
  @barcode,
  @itemNumber,
  @secondaryName,
  @purchasePrice,
  0,
  0,
  @supplierId,
  @supplierName,
  @categoryId,
  @categoryName,
  @stockQuantity);",
                new
                {
                    barcode,
                    itemNumber = NormalizeOptional(itemNumber) ?? string.Empty,
                    secondaryName =
                        NormalizeOptional(secondaryName) ?? string.Empty,
                    purchasePrice,
                    supplierId = references.Supplier.Id,
                    supplierName = references.Supplier.Name ?? string.Empty,
                    categoryId = references.Category.Id,
                    categoryName = references.Category.Name ?? string.Empty,
                    stockQuantity
                },
                transaction);
        }

        private static void ValidateCreate(LocalArticleCreateRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            SalesReceiptContentPolicy.EnsureValidProductIdentity(
                request.Barcode,
                request.PrimaryName);
            if (ProductIdentityPolicy.IsReservedBarcode(request.Barcode))
                throw new InvalidOperationException("Reserved product barcode.");
            if (request.RetailPrice < 0 ||
                request.RetailPrice > int.MaxValue ||
                request.PurchasePrice < 0 ||
                request.InitialStock < 0)
            {
                throw new ArgumentException("Article price or stock is invalid.");
            }
        }

        private static void ValidateUpdate(LocalArticleUpdateRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.ProductId <= 0)
                throw new ArgumentOutOfRangeException(nameof(request.ProductId));
            SalesReceiptContentPolicy.EnsureValidProductIdentity(
                request.Barcode,
                request.PrimaryName);
            if (ProductIdentityPolicy.IsReservedBarcode(request.Barcode))
                throw new InvalidOperationException("Reserved product barcode.");
            if (request.RetailPrice < 0 ||
                request.RetailPrice > int.MaxValue ||
                request.PurchasePrice < 0 ||
                request.StockQuantity < 0)
            {
                throw new ArgumentException("Article price or stock is invalid.");
            }
            NormalizeStockReason(request.StockReason);
        }

        private static void EnsureLocalUserOrigin(ProductWriteOrigin origin)
        {
            if (origin != ProductWriteOrigin.LocalUserSave)
                throw new InvalidOperationException(
                    "Only an explicit local operator Save may enqueue article mutations.");
        }

        private static string NormalizeStockReason(string reason)
        {
            var value = (reason ?? string.Empty).Trim();
            switch (value)
            {
                case "count_correction":
                case "damage":
                case "found":
                case "loss":
                case "other":
                case "return_to_stock":
                case "transfer":
                    return value;
                default:
                    throw new ArgumentException("Manual stock reason is invalid.");
            }
        }

        private static DateTimeOffset EffectiveTime(DateTimeOffset value)
        {
            return value == default(DateTimeOffset)
                ? DateTimeOffset.UtcNow
                : value;
        }

        private static void AddOptionalText(
            IDictionary<string, object> changes,
            string field,
            string value)
        {
            var normalized = NormalizeOptional(value);
            if (normalized != null) changes[field] = normalized;
        }

        private static string NormalizeRequired(string value)
        {
            return (value ?? string.Empty).Trim();
        }

        private static string NormalizeOptional(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return normalized.Length == 0 ? null : normalized;
        }

        private static string FormatTimestamp(DateTimeOffset value)
        {
            return value.ToUniversalTime().ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                CultureInfo.InvariantCulture);
        }

        private sealed class ResolvedArticleReferences
        {
            public ResolvedArticleReference Supplier { get; set; } =
                new ResolvedArticleReference();
            public ResolvedArticleReference Category { get; set; } =
                new ResolvedArticleReference();
            public string DependencyCode { get; set; }
        }

        private sealed class ResolvedArticleReference
        {
            public int? Id { get; set; }
            public string Name { get; set; }
            public string RemoteId { get; set; }
            public bool Requested { get; set; }
        }

        private sealed class ProductStateRow
        {
            public long Id { get; set; }
            public string Barcode { get; set; }
            public string PrimaryName { get; set; }
            public long UnitPrice { get; set; }
            public bool IsActive { get; set; }
            public string RemoteProductId { get; set; }
            public string ClientProductId { get; set; }
            public string RemoteBaseRevision { get; set; }
            public string ItemNumber { get; set; }
            public string SecondaryName { get; set; }
            public int PurchasePrice { get; set; }
            public int? SupplierId { get; set; }
            public int? CategoryId { get; set; }
            public int StockQuantity { get; set; }
        }
    }
}
