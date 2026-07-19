using System;
using System.Collections.Generic;

namespace Win7POS.Core.Online
{
    /// <summary>
    /// Tracks distinct full-snapshot lane evidence across pages. Category and supplier
    /// rows may be repeated by the server on each product page, but the repeated
    /// representation must remain identical for the pinned snapshot.
    /// </summary>
    public sealed class CatalogFullLaneEvidenceTracker
    {
        public const string CategoryConflictCode = "catalog_category_snapshot_conflict";
        public const string CategoryActiveTombstoneConflictCode =
            "catalog_category_active_tombstone_conflict";
        public const string CategoryTombstoneConflictCode =
            "catalog_category_tombstone_snapshot_conflict";
        public const string ProductActiveTombstoneConflictCode =
            "catalog_product_active_tombstone_conflict";
        public const string ProductTombstoneConflictCode =
            "catalog_product_tombstone_snapshot_conflict";
        public const string SupplierConflictCode = "catalog_supplier_snapshot_conflict";
        public const string SupplierActiveTombstoneConflictCode =
            "catalog_supplier_active_tombstone_conflict";
        public const string SupplierTombstoneConflictCode =
            "catalog_supplier_tombstone_snapshot_conflict";

        private readonly Dictionary<string, ReferenceSnapshot> _categories =
            new Dictionary<string, ReferenceSnapshot>(StringComparer.Ordinal);
        private readonly Dictionary<string, TombstoneSnapshot> _categoryTombstones =
            new Dictionary<string, TombstoneSnapshot>(StringComparer.Ordinal);
        private readonly HashSet<string> _prices =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _products =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, TombstoneSnapshot> _productTombstones =
            new Dictionary<string, TombstoneSnapshot>(StringComparer.Ordinal);
        private readonly Dictionary<string, ReferenceSnapshot> _suppliers =
            new Dictionary<string, ReferenceSnapshot>(StringComparer.Ordinal);
        private readonly Dictionary<string, TombstoneSnapshot> _supplierTombstones =
            new Dictionary<string, TombstoneSnapshot>(StringComparer.Ordinal);

        public IReadOnlyCollection<string> CategoryIds => _categories.Keys;
        public string ConflictCode { get; private set; } = string.Empty;
        public IReadOnlyCollection<string> SupplierIds => _suppliers.Keys;

        public CatalogPaginationLaneCounts Add(PosCatalogPayload payload)
        {
            payload = payload ?? new PosCatalogPayload();
            AddIds(_products, payload.Products, value => value?.ProductId);
            AddReferences(
                _categories,
                payload.Categories,
                value => value?.CategoryId,
                value => value?.Name,
                value => value?.UpdatedAt,
                CategoryConflictCode);
            AddReferences(
                _suppliers,
                payload.Suppliers,
                value => value?.SupplierId,
                value => value?.Name,
                value => value?.UpdatedAt,
                SupplierConflictCode);
            AddIds(_prices, payload.Prices, value => value?.PriceId);
            AddTombstones(
                _productTombstones,
                payload.Tombstones?.Products,
                value => value?.ProductId,
                value => value?.DeletedAt,
                value => value?.UpdatedAt,
                ProductTombstoneConflictCode);
            AddTombstones(
                _categoryTombstones,
                payload.Tombstones?.Categories,
                value => value?.CategoryId,
                value => value?.DeletedAt,
                value => value?.UpdatedAt,
                CategoryTombstoneConflictCode);
            AddTombstones(
                _supplierTombstones,
                payload.Tombstones?.Suppliers,
                value => value?.SupplierId,
                value => value?.DeletedAt,
                value => value?.UpdatedAt,
                SupplierTombstoneConflictCode);
            DetectActiveTombstoneOverlap(
                _products,
                _productTombstones,
                ProductActiveTombstoneConflictCode);
            DetectActiveTombstoneOverlap(
                _categories.Keys,
                _categoryTombstones,
                CategoryActiveTombstoneConflictCode);
            DetectActiveTombstoneOverlap(
                _suppliers.Keys,
                _supplierTombstones,
                SupplierActiveTombstoneConflictCode);
            return new CatalogPaginationLaneCounts(
                _products.Count,
                _categories.Count,
                _suppliers.Count,
                _prices.Count,
                _productTombstones.Count,
                _categoryTombstones.Count,
                _supplierTombstones.Count);
        }

        private static void AddIds<T>(
            HashSet<string> target,
            IEnumerable<T> values,
            Func<T, string> selector)
        {
            foreach (var value in values ?? Array.Empty<T>())
            {
                target.Add(Normalize(selector(value)));
            }
        }

        private void AddReferences<T>(
            Dictionary<string, ReferenceSnapshot> target,
            IEnumerable<T> values,
            Func<T, string> selectId,
            Func<T, string> selectName,
            Func<T, string> selectUpdatedAt,
            string conflictCode)
        {
            foreach (var value in values ?? Array.Empty<T>())
            {
                var id = Normalize(selectId(value));
                var incoming = new ReferenceSnapshot(
                    Normalize(selectName(value)),
                    Normalize(selectUpdatedAt(value)));
                if (!target.TryGetValue(id, out var existing))
                {
                    target.Add(id, incoming);
                }
                else if (!existing.Equals(incoming) && ConflictCode.Length == 0)
                {
                    ConflictCode = conflictCode;
                }
            }
        }

        private void AddTombstones<T>(
            Dictionary<string, TombstoneSnapshot> target,
            IEnumerable<T> values,
            Func<T, string> selectId,
            Func<T, string> selectDeletedAt,
            Func<T, string> selectUpdatedAt,
            string conflictCode)
        {
            foreach (var value in values ?? Array.Empty<T>())
            {
                var id = Normalize(selectId(value));
                var incoming = new TombstoneSnapshot(
                    Normalize(selectDeletedAt(value)),
                    Normalize(selectUpdatedAt(value)));
                if (!target.TryGetValue(id, out var existing))
                {
                    target.Add(id, incoming);
                }
                else if (!existing.Equals(incoming) && ConflictCode.Length == 0)
                {
                    ConflictCode = conflictCode;
                }
            }
        }

        private void DetectActiveTombstoneOverlap(
            IEnumerable<string> activeIds,
            Dictionary<string, TombstoneSnapshot> tombstones,
            string conflictCode)
        {
            if (ConflictCode.Length > 0) return;

            foreach (var activeId in activeIds)
            {
                if (tombstones.ContainsKey(activeId))
                {
                    ConflictCode = conflictCode;
                    return;
                }
            }
        }

        private static string Normalize(string value)
        {
            return (value ?? string.Empty).Trim();
        }

        private sealed class ReferenceSnapshot
        {
            public ReferenceSnapshot(string name, string updatedAt)
            {
                Name = name ?? string.Empty;
                UpdatedAt = updatedAt ?? string.Empty;
            }

            public string Name { get; }
            public string UpdatedAt { get; }

            public override bool Equals(object obj)
            {
                var other = obj as ReferenceSnapshot;
                return other != null &&
                    string.Equals(Name, other.Name, StringComparison.Ordinal) &&
                    string.Equals(UpdatedAt, other.UpdatedAt, StringComparison.Ordinal);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return ((Name?.GetHashCode() ?? 0) * 397) ^
                        (UpdatedAt?.GetHashCode() ?? 0);
                }
            }
        }

        private sealed class TombstoneSnapshot
        {
            public TombstoneSnapshot(string deletedAt, string updatedAt)
            {
                DeletedAt = deletedAt ?? string.Empty;
                UpdatedAt = updatedAt ?? string.Empty;
            }

            public string DeletedAt { get; }
            public string UpdatedAt { get; }

            public override bool Equals(object obj)
            {
                var other = obj as TombstoneSnapshot;
                return other != null &&
                    string.Equals(DeletedAt, other.DeletedAt, StringComparison.Ordinal) &&
                    string.Equals(UpdatedAt, other.UpdatedAt, StringComparison.Ordinal);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return ((DeletedAt?.GetHashCode() ?? 0) * 397) ^
                        (UpdatedAt?.GetHashCode() ?? 0);
                }
            }
        }
    }
}
