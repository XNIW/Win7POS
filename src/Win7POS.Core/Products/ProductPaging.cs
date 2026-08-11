using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Win7POS.Core.Models;

namespace Win7POS.Core.Products
{
    public enum ProductPageQueryKind
    {
        First,
        Forward,
        Reverse,
        OffsetFallback
    }

    public sealed class ProductPageFilter
    {
        private const string OrderingVersion = "exact-barcode-sqlite-binary-id-v2";

        public ProductPageFilter(
            string query,
            int? categoryId,
            int? supplierId,
            int pageSize,
            long catalogRevision)
        {
            if (pageSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(pageSize));
            if (catalogRevision < 0)
                throw new ArgumentOutOfRangeException(nameof(catalogRevision));

            Query = (query ?? string.Empty).Trim();
            CategoryId = NormalizeFilterId(categoryId);
            SupplierId = NormalizeFilterId(supplierId);
            PageSize = pageSize;
            CatalogRevision = catalogRevision;
            Fingerprint = ComputeFingerprint(
                Query,
                CategoryId,
                SupplierId,
                PageSize,
                CatalogRevision);
        }

        public string Query { get; }
        public int? CategoryId { get; }
        public int? SupplierId { get; }
        public int PageSize { get; }
        public long CatalogRevision { get; }
        public string Fingerprint { get; }

        public int ExactRank(string barcode)
        {
            return Query.Length > 0 &&
                   string.Equals(barcode ?? string.Empty, Query, StringComparison.Ordinal)
                ? 0
                : 1;
        }

        public ProductPageCursor CreateCursor(string barcode, long id)
        {
            return new ProductPageCursor(
                ExactRank(barcode),
                barcode ?? string.Empty,
                id,
                Fingerprint,
                CatalogRevision);
        }

        private static int? NormalizeFilterId(int? value)
        {
            return value.HasValue && value.Value > 0 ? value : null;
        }

        private static string ComputeFingerprint(
            string query,
            int? categoryId,
            int? supplierId,
            int pageSize,
            long catalogRevision)
        {
            var canonical = string.Join("\n", new[]
            {
                OrderingVersion,
                Encode(query),
                categoryId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-",
                supplierId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-",
                pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
                catalogRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });

            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
                var builder = new StringBuilder(bytes.Length * 2);
                foreach (var value in bytes)
                    builder.Append(value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                return builder.ToString();
            }
        }

        private static string Encode(string value)
        {
            var normalized = value ?? string.Empty;
            return normalized.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + normalized;
        }
    }

    public sealed class ProductPageCursor : IComparable<ProductPageCursor>
    {
        public ProductPageCursor(
            int exactRank,
            string barcode,
            long id,
            string filterFingerprint,
            long catalogRevision)
        {
            if (exactRank != 0 && exactRank != 1)
                throw new ArgumentOutOfRangeException(nameof(exactRank));
            if (id <= 0)
                throw new ArgumentOutOfRangeException(nameof(id));
            if (string.IsNullOrWhiteSpace(filterFingerprint))
                throw new ArgumentException("A filter fingerprint is required.", nameof(filterFingerprint));
            if (catalogRevision < 0)
                throw new ArgumentOutOfRangeException(nameof(catalogRevision));

            ExactRank = exactRank;
            Barcode = barcode ?? string.Empty;
            Id = id;
            FilterFingerprint = filterFingerprint;
            CatalogRevision = catalogRevision;
        }

        public int ExactRank { get; }
        public string Barcode { get; }
        public long Id { get; }
        public string FilterFingerprint { get; }
        public long CatalogRevision { get; }

        public bool Matches(ProductPageFilter filter)
        {
            return filter != null &&
                   CatalogRevision == filter.CatalogRevision &&
                   string.Equals(FilterFingerprint, filter.Fingerprint, StringComparison.Ordinal);
        }

        public int CompareTo(ProductPageCursor other)
        {
            if (other == null)
                return 1;

            var rank = ExactRank.CompareTo(other.ExactRank);
            if (rank != 0)
                return rank;

            var barcode = CompareSqliteBinary(Barcode, other.Barcode);
            return barcode != 0 ? barcode : Id.CompareTo(other.Id);
        }

        private static int CompareSqliteBinary(string left, string right)
        {
            // SQLite BINARY compares the database's UTF-8 bytes, while .NET ordinal
            // compares UTF-16 code units. They differ for supplementary characters.
            var leftBytes = Encoding.UTF8.GetBytes(left ?? string.Empty);
            var rightBytes = Encoding.UTF8.GetBytes(right ?? string.Empty);
            var length = Math.Min(leftBytes.Length, rightBytes.Length);
            for (var index = 0; index < length; index++)
            {
                var comparison = leftBytes[index].CompareTo(rightBytes[index]);
                if (comparison != 0)
                    return comparison;
            }

            return leftBytes.Length.CompareTo(rightBytes.Length);
        }
    }

    public sealed class ProductPagePlan
    {
        internal ProductPagePlan(
            ProductPageQueryKind kind,
            int targetPage,
            int offset,
            ProductPageCursor cursor,
            ProductPageFilter filter,
            long stateVersion)
        {
            Kind = kind;
            TargetPage = targetPage;
            Offset = offset;
            Cursor = cursor;
            FilterFingerprint = filter.Fingerprint;
            CatalogRevision = filter.CatalogRevision;
            PageSize = filter.PageSize;
            StateVersion = stateVersion;
        }

        public ProductPageQueryKind Kind { get; }
        public int TargetPage { get; }
        public int Offset { get; }
        public ProductPageCursor Cursor { get; }
        public string FilterFingerprint { get; }
        public long CatalogRevision { get; }
        public int PageSize { get; }
        public long StateVersion { get; }
        public bool UsedOffsetFallback => Kind == ProductPageQueryKind.OffsetFallback;

        public bool Matches(ProductPageFilter filter)
        {
            return filter != null &&
                   CatalogRevision == filter.CatalogRevision &&
                   PageSize == filter.PageSize &&
                   string.Equals(FilterFingerprint, filter.Fingerprint, StringComparison.Ordinal);
        }
    }

    public sealed class ProductPageResult
    {
        public ProductPageResult(
            IReadOnlyList<ProductDetailsRow> items,
            int pageIndex,
            int totalCount,
            bool usedOffsetFallback,
            string filterFingerprint,
            long catalogRevision)
        {
            Items = items ?? throw new ArgumentNullException(nameof(items));
            PageIndex = pageIndex;
            TotalCount = totalCount;
            UsedOffsetFallback = usedOffsetFallback;
            FilterFingerprint = filterFingerprint ?? string.Empty;
            CatalogRevision = catalogRevision;
        }

        public IReadOnlyList<ProductDetailsRow> Items { get; }
        public int PageIndex { get; }
        public int TotalCount { get; }
        public bool UsedOffsetFallback { get; }
        public string FilterFingerprint { get; }
        public long CatalogRevision { get; }
    }

    public sealed class ProductPagingCoordinator
    {
        private readonly int _maximumAnchors;
        private readonly object _sync = new object();
        private readonly Dictionary<int, Anchor> _anchors = new Dictionary<int, Anchor>();
        private readonly LinkedList<int> _anchorRecency = new LinkedList<int>();
        private ProductPageFilter _acceptedFilter;
        private ProductPageCursor _currentFirst;
        private ProductPageCursor _currentLast;
        private int _currentPage;
        private long _stateVersion;

        public ProductPagingCoordinator(int maximumAnchors = 32)
        {
            if (maximumAnchors <= 0)
                throw new ArgumentOutOfRangeException(nameof(maximumAnchors));
            _maximumAnchors = maximumAnchors;
        }

        public int CurrentPage
        {
            get { lock (_sync) return _currentPage; }
        }

        public int AnchorCount
        {
            get { lock (_sync) return _anchors.Count; }
        }

        public long StateVersion
        {
            get { lock (_sync) return _stateVersion; }
        }

        public ProductPagePlan Plan(ProductPageFilter filter, int targetPage)
        {
            if (filter == null)
                throw new ArgumentNullException(nameof(filter));
            if (targetPage <= 0)
                throw new ArgumentOutOfRangeException(nameof(targetPage));

            lock (_sync)
            {
                if (!IsAcceptedFilter(filter))
                    return NewPlan(ProductPageQueryKind.First, 1, 0, null, filter);

                if (targetPage == 1)
                    return NewPlan(ProductPageQueryKind.First, 1, 0, null, filter);

                if (targetPage == _currentPage + 1 && CursorMatches(_currentLast, filter))
                    return NewPlan(ProductPageQueryKind.Forward, targetPage, 0, _currentLast, filter);

                if (TryGetAnchor(targetPage, filter, out var anchor))
                    return NewPlan(ProductPageQueryKind.Forward, targetPage, 0, anchor, filter);

                if (targetPage == _currentPage - 1 && CursorMatches(_currentFirst, filter))
                    return NewPlan(ProductPageQueryKind.Reverse, targetPage, 0, _currentFirst, filter);

                var offsetValue = checked(((long)targetPage - 1L) * filter.PageSize);
                if (offsetValue > int.MaxValue)
                    throw new ArgumentOutOfRangeException(nameof(targetPage), "Product page offset exceeds SQLite parameter bounds.");
                return NewPlan(ProductPageQueryKind.OffsetFallback, targetPage, (int)offsetValue, null, filter);
            }
        }

        public void Accept(
            ProductPageFilter filter,
            ProductPagePlan plan,
            ProductPageCursor first,
            ProductPageCursor last,
            int itemCount,
            int totalCount)
        {
            if (filter == null)
                throw new ArgumentNullException(nameof(filter));
            if (plan == null)
                throw new ArgumentNullException(nameof(plan));
            if (!plan.Matches(filter))
                throw new InvalidOperationException("The page plan does not match the active filter.");
            if (itemCount < 0 || totalCount < 0 || itemCount > totalCount)
                throw new ArgumentOutOfRangeException(nameof(itemCount));
            if (itemCount > filter.PageSize)
                throw new InvalidOperationException("A page cannot contain more rows than its requested size.");
            if (itemCount == 0 && (first != null || last != null))
                throw new InvalidOperationException("An empty page cannot publish cursors.");
            if (itemCount == 0 && totalCount > 0)
                throw new InvalidOperationException("A non-empty result set cannot publish an empty page.");
            if (itemCount > 0 && (!CursorMatches(first, filter) || !CursorMatches(last, filter)))
                throw new InvalidOperationException("Page cursors do not match the active filter.");
            if (itemCount > 0 && first.CompareTo(last) > 0)
                throw new InvalidOperationException("Page cursors are not ordered.");

            var totalPages = Math.Max(1, (int)(((long)totalCount + filter.PageSize - 1L) / filter.PageSize));
            if (plan.TargetPage > totalPages)
                throw new InvalidOperationException("The requested product page is beyond the current result set.");

            lock (_sync)
            {
                if (plan.StateVersion != _stateVersion)
                    throw new InvalidOperationException("Product page state changed while the query was running.");

                if (!IsAcceptedFilter(filter))
                    ResetState();

                _acceptedFilter = filter;
                _currentPage = plan.TargetPage;
                _currentFirst = first;
                _currentLast = last;

                if (plan.Kind == ProductPageQueryKind.Forward && CursorMatches(plan.Cursor, filter))
                    StoreAnchor(plan.TargetPage, plan.Cursor);

                if (itemCount > 0 && (long)plan.TargetPage * filter.PageSize < totalCount)
                    StoreAnchor(plan.TargetPage + 1, last);

                _stateVersion++;
            }
        }

        public void Reset()
        {
            lock (_sync)
            {
                ResetState();
                _acceptedFilter = null;
                _stateVersion++;
            }
        }

        private ProductPagePlan NewPlan(
            ProductPageQueryKind kind,
            int targetPage,
            int offset,
            ProductPageCursor cursor,
            ProductPageFilter filter)
        {
            return new ProductPagePlan(kind, targetPage, offset, cursor, filter, _stateVersion);
        }

        private bool IsAcceptedFilter(ProductPageFilter filter)
        {
            return _acceptedFilter != null &&
                   string.Equals(_acceptedFilter.Fingerprint, filter.Fingerprint, StringComparison.Ordinal) &&
                   _acceptedFilter.CatalogRevision == filter.CatalogRevision;
        }

        private static bool CursorMatches(ProductPageCursor cursor, ProductPageFilter filter)
        {
            return cursor != null && cursor.Matches(filter);
        }

        private bool TryGetAnchor(int page, ProductPageFilter filter, out ProductPageCursor cursor)
        {
            if (_anchors.TryGetValue(page, out var anchor) && CursorMatches(anchor.Cursor, filter))
            {
                cursor = anchor.Cursor;
                return true;
            }

            cursor = null;
            return false;
        }

        private void StoreAnchor(int page, ProductPageCursor cursor)
        {
            if (page <= 1 || cursor == null)
                return;

            if (_anchors.TryGetValue(page, out var existing))
            {
                existing.Cursor = cursor;
                Touch(existing);
                return;
            }

            var node = _anchorRecency.AddLast(page);
            _anchors[page] = new Anchor(cursor, node);
            while (_anchors.Count > _maximumAnchors)
            {
                var oldest = _anchorRecency.First;
                if (oldest == null)
                    break;
                _anchorRecency.RemoveFirst();
                _anchors.Remove(oldest.Value);
            }
        }

        private void Touch(Anchor anchor)
        {
            _anchorRecency.Remove(anchor.RecencyNode);
            _anchorRecency.AddLast(anchor.RecencyNode);
        }

        private void ResetState()
        {
            _anchors.Clear();
            _anchorRecency.Clear();
            _currentPage = 0;
            _currentFirst = null;
            _currentLast = null;
        }

        private sealed class Anchor
        {
            internal Anchor(ProductPageCursor cursor, LinkedListNode<int> recencyNode)
            {
                Cursor = cursor;
                RecencyNode = recencyNode;
            }

            internal ProductPageCursor Cursor { get; set; }
            internal LinkedListNode<int> RecencyNode { get; }
        }
    }
}
