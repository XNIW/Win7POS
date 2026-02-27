using System;
using System.Threading.Tasks;

namespace Win7POS.Core.Reports
{
    public sealed class DailyTakingsService
    {
        private readonly IDateRangeSalesQuery _query;

        public DailyTakingsService(IDateRangeSalesQuery query)
        {
            _query = query ?? throw new ArgumentNullException(nameof(query));
        }

        public async Task<DailyTakings> GetForDateAsync(DateTime localDate)
        {
            var day = localDate.Date;
            var fromMs = new DateTimeOffset(day).ToUnixTimeMilliseconds();
            var toMs = new DateTimeOffset(day.AddDays(1)).ToUnixTimeMilliseconds();
            var sales = await _query.GetSalesBetweenAsync(fromMs, toMs);

            var result = new DailyTakings();
            foreach (var s in sales)
            {
                result.TotalSalesCount += 1;
                result.GrossTotal += s.Total;
                result.CashTotal += s.PaidCash;
                result.CardTotal += s.PaidCard;
                result.ChangeTotal += s.Change;
            }

            return result;
        }
    }
}
