using System;
using System.Threading.Tasks;
using Win7POS.Core.Security;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Infrastructure.Security;
using Win7POS.Wpf.Localization;
using Win7POS.Wpf.Pos.Dialogs;

namespace Win7POS.Wpf.Pos
{
    internal static class DailyReportComposition
    {
        internal static DailyReportViewModel Create(PosWorkflowService service, IOverrideAuthService overrides,
            IPermissionService permissions, OperatorSession session, Action restoreFocus = null, bool historicalOnly = false,
            Func<PosViewModel> commercialView = null)
        {
            var report = new DailyReportViewModel(service, overrides, permissions, session,
                () => OpenImportAsync(service, permissions, session, restoreFocus, commercialView));
            if (historicalOnly) report.ReportOriginIndex = 2;
            return report;
        }

        private static Task OpenImportAsync(PosWorkflowService service, IPermissionService permissions,
            OperatorSession session, Action restoreFocus, Func<PosViewModel> commercialView)
        {
            permissions.Demand(permissions.Has(PermissionCodes.RegisterImportHistory)
                ? PermissionCodes.RegisterImportHistory : PermissionCodes.RegisterCancelHistory, PosLocalization.T("history.title"));
            if (session == null || !ReferenceEquals(OperatorSessionHolder.Current, session) || !session.EnsureAuthorizationValid())
                throw new InvalidOperationException("historical_authorization_unavailable");
            var pos = commercialView?.Invoke();
            var previousBusy = pos?.IsBusy ?? false;
            if (pos != null) pos.IsBusy = true;
            try
            {
                var dialog = new HistoricalSalesImportDialog(new HistoricalSalesImportViewModel(service, session))
                    { Owner = DialogOwnerHelper.GetSafeOwner() };
                dialog.ShowDialog();
            }
            finally
            {
                if (pos != null) pos.IsBusy = previousBusy;
                restoreFocus?.Invoke();
            }
            return Task.CompletedTask;
        }
    }
}
