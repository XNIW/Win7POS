using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Win7POS.Core.Reports;
using Win7POS.Data.Repositories;
using Win7POS.Data.Reports;

namespace Win7POS.Wpf.Pos
{
    public sealed class SalesReportContext
    {
        public SalesReportContext(SalesReportFilter filter, SalesReportTimePolicy policy, SalesReportSnapshot snapshot, ISalesReportQuery query, SalesReportSnapshotCoordinator coordinator,bool nativeComponentUnavailable=false)
        { Filter = filter; NativeTimePolicy = policy; Snapshot = snapshot; Query = query; Coordinator = coordinator;NativeComponentUnavailable=nativeComponentUnavailable; }
        public SalesReportFilter Filter { get; }
        public SalesReportTimePolicy NativeTimePolicy { get; }
        public SalesReportSnapshot Snapshot { get; }
        public ISalesReportQuery Query { get; }
        public SalesReportSnapshotCoordinator Coordinator { get; }
        public bool NativeComponentUnavailable { get; }
    }

    public sealed partial class PosWorkflowService
    {
        internal SalesReportSnapshotCoordinator ReportSnapshotCoordinator => SalesReportSnapshotCoordinator.ForDatabase(_factory.DbPath);

        public async Task<SalesReportContext> OpenAvailableAnalyticalReportAsync(DateTime fromInclusive,DateTime toExclusive,
            SalesReportOrigins origins,bool canViewAllOperators,int? operatorId,CancellationToken cancellationToken)
        {
            var context=await OpenAnalyticalReportAsync(fromInclusive,toExclusive,origins,canViewAllOperators,operatorId,null,cancellationToken).ConfigureAwait(false);
            if(origins!=SalesReportOrigins.All||context.Snapshot.Coverage.Status!=SalesReportCoverageStatus.PolicyRequired)return context;
            // A missing native conversion rule cannot hide already qualified card history.
            // The effective filter and token remain historical-only, including exports.
            var available=await OpenAnalyticalReportAsync(fromInclusive,toExclusive,SalesReportOrigins.Historical,canViewAllOperators,operatorId,null,cancellationToken).ConfigureAwait(false);
            return new SalesReportContext(available.Filter,available.NativeTimePolicy,available.Snapshot,available.Query,available.Coordinator,true);
        }

        public Task<SalesReportContext> OpenAnalyticalReportAsync(DateTime fromInclusive, DateTime toExclusive,
            SalesReportOrigins origins, bool canViewAllOperators, int? operatorId, string referenceSearch, CancellationToken cancellationToken, IEnumerable<DateTime> selectedDates = null)
        {
            return Task.Run(async () =>
            {
                var shop = await _officialShopSnapshots.GetAsync().ConfigureAwait(false);
                if (shop == null || string.IsNullOrWhiteSpace(shop.ShopId)) throw new InvalidOperationException("report_shop_required");
                var coordinator = SalesReportSnapshotCoordinator.ForDatabase(_factory.DbPath);
                var policy = (origins & SalesReportOrigins.Native) != 0 ? await new SalesReportTimePolicyRepository(_factory).GetActiveAsync(shop.ShopId, cancellationToken).ConfigureAwait(false) : null;
                var filter = new SalesReportFilter(shop.ShopId, DateTime.SpecifyKind(fromInclusive, DateTimeKind.Unspecified), DateTime.SpecifyKind(toExclusive, DateTimeKind.Unspecified),
                    origins, canViewAllOperators, operatorId, NativeSalesOwnershipRepository.PolicyHash, policy?.Hash, referenceSearch, selectedDates);
                var query = new SalesReportQuery(_factory, () => _factory.AuthorityStamp, coordinator);
                var snapshot = await query.OpenAsync(filter, policy, 200, cancellationToken).ConfigureAwait(false);
                if (snapshot.Coverage.Status == SalesReportCoverageStatus.PendingOwnershipVerification && policy != null)
                {
                    var refresher = new NativeSalesOwnershipRepository(_factory, coordinator, () => _factory.AuthorityStamp);
                    foreach (var interval in policy.GetUtcIntervals(filter.FromInclusive, filter.ToExclusive))
                        while (!await refresher.RefreshRangeAsync(interval.FromUtcMs, interval.ToUtcMs, 50, cancellationToken).ConfigureAwait(false)) cancellationToken.ThrowIfCancellationRequested();
                    snapshot = await query.OpenAsync(filter, policy, 200, cancellationToken).ConfigureAwait(false);
                }
                var current = await _officialShopSnapshots.GetAsync().ConfigureAwait(false);
                if (current?.ShopId != filter.ShopId) throw new ReportSnapshotStaleException();
                return new SalesReportContext(filter, policy, snapshot, query, coordinator);
            }, cancellationToken);
        }

        public async Task DemandAnalyticalReportCurrentAsync(SalesReportContext context, CancellationToken cancellationToken)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            var shop = await _officialShopSnapshots.GetAsync().ConfigureAwait(false);
            if (shop?.ShopId != context.Filter.ShopId) throw new ReportSnapshotStaleException();
            await context.Query.ValidateAsync(context.Snapshot.Token, context.Filter, context.NativeTimePolicy, cancellationToken).ConfigureAwait(false);
        }
    }
}
