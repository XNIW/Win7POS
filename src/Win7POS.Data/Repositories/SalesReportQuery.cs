using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Reports;
using Win7POS.Data.Reports;

namespace Win7POS.Data.Repositories
{
    /// <summary>Shop-scoped analytic reads. Native checkout, cash close and reversal queries remain separate.</summary>
    public sealed class SalesReportQuery : ISalesReportQuery
    {
        private readonly SqliteConnectionFactory _factory;
        private readonly Func<SalesReportAuthorityStamp> _authority;
        private readonly SalesReportSnapshotCoordinator _coordinator;
        public SalesReportQuery(SqliteConnectionFactory factory, Func<SalesReportAuthorityStamp> authority, SalesReportSnapshotCoordinator coordinator)
        { _factory = factory ?? throw new ArgumentNullException(nameof(factory)); _authority = authority ?? throw new ArgumentNullException(nameof(authority)); _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator)); }

        public async Task<SalesReportSnapshot> OpenAsync(SalesReportFilter filter, SalesReportTimePolicy nativeTimePolicy, int pageSize, CancellationToken cancellationToken)
        {
            CheckPageSize(pageSize);
            if (filter == null) throw new ArgumentNullException(nameof(filter));
            using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: true);
            var token = await ReadTokenAsync(connection, transaction, filter, cancellationToken).ConfigureAwait(false);
            var plan = CreatePlan(filter, nativeTimePolicy);
            if (plan == null) return FinishUnavailable(token, SalesReportCoverageStatus.PolicyRequired, transaction);
            var coverage = await GetCoverageAsync(connection, transaction, filter, plan, cancellationToken).ConfigureAwait(false);
            if (!new SalesReportCoverage(coverage).CanPublish) return FinishUnavailable(token, coverage, transaction);
            var groups = (await connection.QueryAsync<GroupRow>(Command(plan.Sql.TrimEnd() + DayHourAndTotalSql, plan.Parameters, transaction, cancellationToken)).ConfigureAwait(false)).ToArray();
            var total = groups.Single(row => row.RollupKind == 2);
            var summary = total.ToSummary();
            var days = groups.Where(row => row.RollupKind == 0).Select(row => new SalesReportDaySummary(new DateTime(checked(row.DayNumber * TimeSpan.TicksPerDay), DateTimeKind.Unspecified), row.ToSummary())).ToArray();
            var hourly = new long[24];
            foreach (var row in groups.Where(row => row.RollupKind == 1)) hourly[row.Hour] = row.BalanceClp;
            var page = await ReadPageAsync(connection, transaction, token, plan, null, pageSize, null, cancellationToken).ConfigureAwait(false);
            if (coverage == SalesReportCoverageStatus.VerifiedForScope && total.RowCount == 0) coverage = SalesReportCoverageStatus.VerifiedEmpty;
            transaction.Commit();
            _coordinator.ObserveRead(token);
            return new SalesReportSnapshot(token, new SalesReportCoverage(coverage), summary, days, hourly, page);
        }

        public async Task<SalesReportPage> GetPageAsync(SalesReportReadToken token, SalesReportFilter filter, SalesReportTimePolicy nativeTimePolicy, SalesReportCursor cursor, int pageSize, CancellationToken cancellationToken)
        {
            CheckPageSize(pageSize);
            using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: true);
            await DemandTokenAsync(connection, transaction, token, filter, cancellationToken).ConfigureAwait(false);
            var plan = CreatePlan(filter, nativeTimePolicy) ?? throw new InvalidOperationException("report_time_policy_required");
            if (!new SalesReportCoverage(await GetCoverageAsync(connection, transaction, filter, plan, cancellationToken).ConfigureAwait(false)).CanPublish) throw new ReportSnapshotStaleException();
            var page = await ReadPageAsync(connection, transaction, token, plan, cursor, pageSize, null, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return page;
        }

        public async Task<SalesReportEntry> GetEntryAsync(SalesReportReadToken token, SalesReportFilter filter, SalesReportTimePolicy nativeTimePolicy, SalesReportEntryKey key, CancellationToken cancellationToken)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: true);
            await DemandTokenAsync(connection, transaction, token, filter, cancellationToken).ConfigureAwait(false);
            var plan = CreatePlan(filter, nativeTimePolicy) ?? throw new InvalidOperationException("report_time_policy_required");
            if(!new SalesReportCoverage(await GetCoverageAsync(connection,transaction,filter,plan,cancellationToken).ConfigureAwait(false)).CanPublish)throw new ReportSnapshotStaleException();
            var page = await ReadPageAsync(connection, transaction, token, plan, null, 1, key, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return page.Entries.SingleOrDefault();
        }

        public async Task ValidateAsync(SalesReportReadToken token, SalesReportFilter filter, SalesReportTimePolicy nativeTimePolicy, CancellationToken cancellationToken)
        {
            using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: true);
            await DemandTokenAsync(connection, transaction, token, filter, cancellationToken).ConfigureAwait(false);
            var plan=CreatePlan(filter,nativeTimePolicy);
            if (plan==null||!new SalesReportCoverage(await GetCoverageAsync(connection,transaction,filter,plan,cancellationToken).ConfigureAwait(false)).CanPublish) throw new ReportSnapshotStaleException();
            transaction.Commit();
        }

        private SalesReportSnapshot FinishUnavailable(SalesReportReadToken token, SalesReportCoverageStatus coverage, SqliteTransaction transaction)
        {
            transaction.Commit(); _coordinator.ObserveRead(token);
            return new SalesReportSnapshot(token, new SalesReportCoverage(coverage), SalesReportSummary.Empty,
                Array.Empty<SalesReportDaySummary>(), new long[24], new SalesReportPage(token, Array.Empty<SalesReportEntry>(), false));
        }

        private async Task<SalesReportReadToken> ReadTokenAsync(SqliteConnection connection, SqliteTransaction transaction, SalesReportFilter filter, CancellationToken cancellationToken)
        {
            if (filter == null) throw new ArgumentNullException(nameof(filter));
            var authority = _authority() ?? throw new InvalidOperationException("report_authority_unavailable");
            var state = await connection.QuerySingleAsync<StateRow>(Command("SELECT dataset_id AS DatasetId,data_revision+(SELECT revision FROM historical_cloud_cache_revision WHERE singleton_id=1) AS DataRevision FROM historical_import_dataset WHERE singleton_id=1", null, transaction, cancellationToken)).ConfigureAwait(false);
            return new SalesReportReadToken(state.DatasetId, authority.RestoreEpoch, authority.AuthorityInstanceEpoch, state.DataRevision, filter.Fingerprint, filter.OwnershipPolicyHash, filter.NativeTimePolicyHash);
        }
        private async Task DemandTokenAsync(SqliteConnection connection, SqliteTransaction transaction, SalesReportReadToken token, SalesReportFilter filter, CancellationToken cancellationToken)
        {
            if (token == null || !(await ReadTokenAsync(connection, transaction, filter, cancellationToken).ConfigureAwait(false)).Equals(token)) throw new ReportSnapshotStaleException();
        }

        private static QueryPlan CreatePlan(SalesReportFilter filter, SalesReportTimePolicy timePolicy)
        {
            if (filter == null) throw new ArgumentNullException(nameof(filter));
            var parameters = new DynamicParameters();
            parameters.Add("shop", filter.ShopId); parameters.Add("fromTicks", filter.FromInclusive.Ticks); parameters.Add("toTicks", filter.ToExclusive.Ticks);
            parameters.Add("ownershipPolicy", filter.OwnershipPolicyHash); parameters.Add("operatorId", filter.OperatorId);
            parameters.Add("search", filter.ReferenceSearch);
            var nativeWhere = filter.OperatorId.HasValue ? " AND s.operator_id=@operatorId" : string.Empty;
            if (filter.ReferenceSearch.Length > 0) nativeWhere += " AND instr(COALESCE(s.code,''),@search)>0";
            var branches = new List<string>(); var candidates = new List<string>();
            if ((filter.Origins & SalesReportOrigins.Native) != 0)
            {
                if (timePolicy == null || timePolicy.Hash != filter.NativeTimePolicyHash || filter.OwnershipPolicyHash != NativeSalesOwnershipRepository.PolicyHash) return null;
                IReadOnlyList<SalesReportUtcSegment> intervals;
                try
                {
                    if (filter.SelectedDates.Count == 0) intervals = timePolicy.GetUtcIntervals(filter.FromInclusive, filter.ToExclusive);
                    else
                    {
                        var ranges = new List<SalesReportUtcSegment>();
                        var start = filter.SelectedDates[0]; var end = start.AddDays(1);
                        for (var day = 1; day < filter.SelectedDates.Count; day++)
                        {
                            if (filter.SelectedDates[day] == end) { end = end.AddDays(1); continue; }
                            ranges.AddRange(timePolicy.GetUtcIntervals(start, end)); start = filter.SelectedDates[day]; end = start.AddDays(1);
                        }
                        ranges.AddRange(timePolicy.GetUtcIntervals(start, end)); intervals = ranges;
                    }
                }
                catch (InvalidOperationException) { return null; }
                if (intervals.Count > 128) throw new InvalidOperationException("report_time_segment_limit");
                for (var index = 0; index < intervals.Count; index++)
                {
                    parameters.Add("utcFrom" + index, intervals[index].FromUtcMs); parameters.Add("utcTo" + index, intervals[index].ToUtcMs);
                    parameters.Add("offsetTicks" + index, checked((long)intervals[index].OffsetMinutes * TimeSpan.TicksPerMinute));
                    var range = "s.createdAt>=@utcFrom" + index + " AND s.createdAt<@utcTo" + index + nativeWhere;
                    candidates.Add("SELECT s.id,p.state,p.policy_hash FROM sales s LEFT JOIN sales_report_native_ownership p ON p.sale_id=s.id WHERE " + range);
                    branches.Add(@"SELECT 0 AS Origin,s.id AS InternalId,(621355968000000000+s.createdAt*10000+@offsetTicks" + index + @") AS BusinessTicks,
s.kind AS MovementKind,s.total AS SignedAmountClp,s.paidCash AS CashTenderedClp,s.change AS ChangeClp,(s.paidCash-s.change) AS CashClp,s.paidCard AS CardClp,
s.operator_id AS OriginalCashierId,COALESCE(s.code,'') AS Reference,'' AS Provider,'' AS ExternalNamespace,'' AS ExternalId,'' AS BatchId,'' AS SourceSheet,NULL AS SourceRow,
CASE WHEN s.voided_by_sale_id IS NULL THEN 0 ELSE 1 END AS IsVoided,s.related_sale_id AS RelatedNativeSaleId,'' AS RawTimestamp,NULL AS OffsetMinutes,'' AS CardSubtype
FROM sales s JOIN sales_report_native_ownership p ON p.sale_id=s.id AND p.state='Verified' AND p.policy_hash=@ownershipPolicy AND p.shop_id=@shop COLLATE NOCASE
WHERE " + range);
                }
            }
            if ((filter.Origins & SalesReportOrigins.Historical) != 0)
            {
                var where = "h.is_active=1 AND h.shop_id=@shop COLLATE NOCASE AND h.business_ticks>=@fromTicks AND h.business_ticks<@toTicks";
                if (filter.SelectedDates.Count > 0) { where += " AND CAST(h.business_ticks/864000000000 AS INTEGER) IN @selectedDays"; parameters.Add("selectedDays", filter.SelectedDates.Select(date => date.Ticks / TimeSpan.TicksPerDay).ToArray()); }
                if (filter.OperatorId.HasValue) where += " AND h.original_cashier_id=@operatorId";
                if (filter.ReferenceSearch.Length > 0) where += " AND instr(COALESCE(h.external_id,''),@search)>0";
                branches.Add(@"SELECT 1 AS Origin,h.id AS InternalId,h.business_ticks AS BusinessTicks,CASE WHEN h.signed_amount_clp<0 THEN 3 ELSE 0 END AS MovementKind,
h.signed_amount_clp AS SignedAmountClp,0 AS CashTenderedClp,0 AS ChangeClp,0 AS CashClp,h.signed_amount_clp AS CardClp,h.original_cashier_id AS OriginalCashierId,
h.external_id AS Reference,h.provider AS Provider,h.external_namespace AS ExternalNamespace,h.external_id AS ExternalId,h.batch_id AS BatchId,h.source_sheet AS SourceSheet,h.source_row AS SourceRow,
0 AS IsVoided,NULL AS RelatedNativeSaleId,h.timestamp_raw AS RawTimestamp,h.offset_minutes AS OffsetMinutes,h.card_subtype AS CardSubtype
FROM historical_cloud_report_movements h WHERE " + where);
            }
            if (branches.Count == 0) branches.Add(EmptyBranch);
            return new QueryPlan { Sql = "WITH report_entries AS (" + string.Join(" UNION ALL ", branches) + ") ", Parameters = parameters,
                CandidateSql = candidates.Count == 0 ? null : "WITH candidates AS (" + string.Join(" UNION ALL ", candidates) + ") ", Branches = branches };
        }

        private static async Task<SalesReportCoverageStatus> GetCoverageAsync(SqliteConnection connection, SqliteTransaction transaction, SalesReportFilter filter, QueryPlan plan, CancellationToken cancellationToken)
        {
            var cloud = (filter.Origins & SalesReportOrigins.Historical) != 0 ? await connection.QuerySingleOrDefaultAsync<CloudCoverageRow>(Command("SELECT generation_id GenerationId FROM historical_cloud_state WHERE shop_id=@shop COLLATE NOCASE AND ever_web=1",new{shop=filter.ShopId},transaction,cancellationToken)).ConfigureAwait(false) : null;
            if(cloud!=null&&cloud.GenerationId==null)return SalesReportCoverageStatus.CloudReplicaRequired;
            if (plan.CandidateSql == null) return cloud!=null?SalesReportCoverageStatus.CloudReplicaCached:SalesReportCoverageStatus.VerifiedForScope;
            var row = await connection.QuerySingleAsync<CoverageRow>(Command(plan.CandidateSql + @"SELECT
COALESCE(MAX(CASE WHEN state IS NULL OR state='Dirty' OR policy_hash IS NOT @ownershipPolicy THEN 1 ELSE 0 END),0) AS Pending,
COALESCE(MAX(CASE WHEN state IN ('Unknown','CodeOnly','Conflict') THEN 1 ELSE 0 END),0) AS Subset FROM candidates", plan.Parameters, transaction, cancellationToken)).ConfigureAwait(false);
            return row.Pending != 0 ? SalesReportCoverageStatus.PendingOwnershipVerification : row.Subset != 0 ? SalesReportCoverageStatus.VerifiedSubset : cloud!=null?SalesReportCoverageStatus.CloudReplicaCached:SalesReportCoverageStatus.VerifiedForScope;
        }

        private static async Task<SalesReportPage> ReadPageAsync(SqliteConnection connection, SqliteTransaction transaction, SalesReportReadToken token, QueryPlan plan,
            SalesReportCursor cursor, int pageSize, SalesReportEntryKey key, CancellationToken cancellationToken)
        {
            var parameters = new DynamicParameters(plan.Parameters); parameters.Add("take", pageSize + 1);
            var where = new List<string>();
            if (cursor != null)
            {
                parameters.Add("lastTicks", cursor.BusinessTicks); parameters.Add("lastOrigin", (int)cursor.Key.Origin); parameters.Add("lastId", cursor.Key.InternalId);
                where.Add("(BusinessTicks<@lastTicks OR (BusinessTicks=@lastTicks AND Origin>@lastOrigin) OR (BusinessTicks=@lastTicks AND Origin=@lastOrigin AND InternalId<@lastId))");
            }
            if (key != null) { parameters.Add("keyOrigin", (int)key.Origin); parameters.Add("keyId", key.InternalId); where.Add("Origin=@keyOrigin AND InternalId=@keyId"); }
            var predicate = where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where);
            var limitedBranches = plan.Branches.Select(branch => "SELECT * FROM (SELECT * FROM (" + branch + ")" + predicate + " ORDER BY BusinessTicks DESC,Origin ASC,InternalId DESC LIMIT @take)");
            var sql = "WITH report_entries AS (" + string.Join(" UNION ALL ", limitedBranches) + ") SELECT * FROM report_entries ORDER BY BusinessTicks DESC,Origin ASC,InternalId DESC LIMIT @take";
            var rows = (await connection.QueryAsync<EntryRow>(Command(sql, parameters, transaction, cancellationToken)).ConfigureAwait(false)).ToArray();
            return new SalesReportPage(token, rows.Take(pageSize).Select(row => row.ToEntry()), rows.Length > pageSize);
        }

        private static void CheckPageSize(int pageSize) { if (pageSize < 1 || pageSize > 200) throw new ArgumentOutOfRangeException(nameof(pageSize)); }
        private static CommandDefinition Command(string sql, object parameters, SqliteTransaction transaction, CancellationToken cancellationToken) { return new CommandDefinition(sql, parameters, transaction, cancellationToken: cancellationToken); }
        private const string AggregateColumns = @"COUNT(*) AS RowCount,
COALESCE(SUM(CASE WHEN MovementKind=0 AND SignedAmountClp>0 THEN SignedAmountClp ELSE 0 END),0) AS GrossPositiveClp,
COALESCE(-SUM(CASE WHEN SignedAmountClp<0 THEN SignedAmountClp ELSE 0 END),0) AS ReversalsClp,
COALESCE(SUM(SignedAmountClp),0) AS BalanceClp,COALESCE(SUM(CashTenderedClp),0) AS CashTenderedClp,
COALESCE(SUM(ChangeClp),0) AS ChangeClp,COALESCE(SUM(CashClp),0) AS CashClp,COALESCE(SUM(CardClp),0) AS CardClp,
COALESCE(SUM(CASE WHEN Origin=0 AND MovementKind=0 AND SignedAmountClp>0 THEN SignedAmountClp ELSE 0 END),0) AS NativePositiveSalesClp,
COUNT(CASE WHEN Origin=0 AND MovementKind=0 AND SignedAmountClp>0 THEN 1 END) AS NativePositiveReceiptCount,
COUNT(CASE WHEN Origin=0 AND SignedAmountClp<0 THEN 1 END) AS NativeNegativeMovementCount,
COUNT(CASE WHEN Origin=1 THEN 1 END) AS HistoricalMovementCount,
COUNT(CASE WHEN Origin=1 AND SignedAmountClp<0 THEN 1 END) AS HistoricalCancellationCount,
COUNT(CASE WHEN (CashClp+CardClp)<>SignedAmountClp OR typeof(CashClp)<>'integer' OR typeof(CardClp)<>'integer' OR typeof(SignedAmountClp)<>'integer' THEN 1 END) AS ReconciliationAnomalyCount";
        // Scan the source once. The small day/hour cells retain exact integer
        // aggregates for each axis, including reversals and reconciliation counts.
        private const string RollupColumns = @"COALESCE(SUM(RowCount),0) AS RowCount,
COALESCE(SUM(GrossPositiveClp),0) AS GrossPositiveClp,
COALESCE(SUM(ReversalsClp),0) AS ReversalsClp,
COALESCE(SUM(BalanceClp),0) AS BalanceClp,
COALESCE(SUM(CashTenderedClp),0) AS CashTenderedClp,
COALESCE(SUM(ChangeClp),0) AS ChangeClp,
COALESCE(SUM(CashClp),0) AS CashClp,
COALESCE(SUM(CardClp),0) AS CardClp,
COALESCE(SUM(NativePositiveSalesClp),0) AS NativePositiveSalesClp,
COALESCE(SUM(NativePositiveReceiptCount),0) AS NativePositiveReceiptCount,
COALESCE(SUM(NativeNegativeMovementCount),0) AS NativeNegativeMovementCount,
COALESCE(SUM(HistoricalMovementCount),0) AS HistoricalMovementCount,
COALESCE(SUM(HistoricalCancellationCount),0) AS HistoricalCancellationCount,
COALESCE(SUM(ReconciliationAnomalyCount),0) AS ReconciliationAnomalyCount";
        private const string DayHourAndTotalSql = @", report_cells AS MATERIALIZED (
SELECT CAST(BusinessTicks/864000000000 AS INTEGER) AS DayNumber,
CAST((BusinessTicks/36000000000)%24 AS INTEGER) AS Hour," + AggregateColumns + @"
FROM report_entries GROUP BY DayNumber,Hour)
SELECT 0 AS RollupKind,DayNumber,-1 AS Hour," + RollupColumns + @" FROM report_cells GROUP BY DayNumber
UNION ALL SELECT 1 AS RollupKind,-1 AS DayNumber,Hour," + RollupColumns + @" FROM report_cells GROUP BY Hour
UNION ALL SELECT 2 AS RollupKind,-1 AS DayNumber,-1 AS Hour," + RollupColumns + @" FROM report_cells
ORDER BY RollupKind,DayNumber,Hour";
        private const string EmptyBranch = @"SELECT 0 AS Origin,0 AS InternalId,0 AS BusinessTicks,0 AS MovementKind,0 AS SignedAmountClp,0 AS CashTenderedClp,0 AS ChangeClp,0 AS CashClp,0 AS CardClp,NULL AS OriginalCashierId,'' AS Reference,'' AS Provider,'' AS ExternalNamespace,'' AS ExternalId,'' AS BatchId,'' AS SourceSheet,NULL AS SourceRow,0 AS IsVoided,NULL AS RelatedNativeSaleId,'' AS RawTimestamp,NULL AS OffsetMinutes,'' AS CardSubtype WHERE 0";
        private sealed class QueryPlan { internal string Sql { get; set; } internal string CandidateSql { get; set; } internal DynamicParameters Parameters { get; set; } internal IReadOnlyList<string> Branches { get; set; } }
        private sealed class StateRow { public string DatasetId { get; set; } public long DataRevision { get; set; } }
        private sealed class CoverageRow { public int Pending { get; set; } public int Subset { get; set; } }
        private sealed class CloudCoverageRow { public string GenerationId { get; set; } }
        private class AggregateRow
        {
            public long RowCount { get; set; } public long GrossPositiveClp { get; set; } public long ReversalsClp { get; set; } public long BalanceClp { get; set; }
            public long CashTenderedClp { get; set; } public long ChangeClp { get; set; } public long CashClp { get; set; } public long CardClp { get; set; }
            public long NativePositiveSalesClp { get; set; } public long NativePositiveReceiptCount { get; set; } public long NativeNegativeMovementCount { get; set; }
            public long HistoricalMovementCount { get; set; } public long HistoricalCancellationCount { get; set; } public long ReconciliationAnomalyCount { get; set; }
            internal SalesReportSummary ToSummary() { return new SalesReportSummary(GrossPositiveClp, ReversalsClp, BalanceClp, CashTenderedClp, ChangeClp, CashClp, CardClp, NativePositiveSalesClp, NativePositiveReceiptCount, NativeNegativeMovementCount, HistoricalMovementCount, HistoricalCancellationCount, ReconciliationAnomalyCount); }
        }
        private sealed class GroupRow : AggregateRow { public int RollupKind { get; set; } public long DayNumber { get; set; } public int Hour { get; set; } }
        private sealed class EntryRow
        {
            public int Origin { get; set; } public long InternalId { get; set; } public long BusinessTicks { get; set; } public int MovementKind { get; set; }
            public long SignedAmountClp { get; set; } public long CashTenderedClp { get; set; } public long ChangeClp { get; set; } public long CardClp { get; set; }
            public int? OriginalCashierId { get; set; } public string Reference { get; set; } public string Provider { get; set; } public string ExternalNamespace { get; set; }
            public string ExternalId { get; set; } public string BatchId { get; set; } public string SourceSheet { get; set; } public int? SourceRow { get; set; }
            public bool IsVoided { get; set; } public long? RelatedNativeSaleId { get; set; } public string RawTimestamp { get; set; } public int? OffsetMinutes { get; set; } public string CardSubtype { get; set; }
            internal SalesReportEntry ToEntry() { return new SalesReportEntry(new SalesReportEntryKey((SalesReportOrigin)Origin, InternalId), BusinessTicks, (SalesReportMovementKind)MovementKind, SignedAmountClp,
                CashTenderedClp, ChangeClp, CardClp, OriginalCashierId, Reference, Provider, ExternalNamespace, ExternalId, BatchId, SourceSheet, SourceRow, IsVoided, RelatedNativeSaleId, RawTimestamp, OffsetMinutes, CardSubtype); }
        }
    }
}
