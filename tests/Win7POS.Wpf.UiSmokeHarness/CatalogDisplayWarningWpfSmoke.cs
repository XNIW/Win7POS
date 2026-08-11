using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Win7POS.Core.Online;
using Win7POS.Core.Pos;
using Win7POS.Data;
using Win7POS.Data.Online;
using Win7POS.Wpf.Localization;
using Win7POS.Wpf.Pos;
using Win7POS.Wpf.Pos.Dialogs;
using Win7POS.Wpf.Pos.Online;

namespace Win7POS.Wpf.UiSmokeHarness
{
    internal static class CatalogDisplayWarningWpfSmoke
    {
        public static async Task<string> RunAsync()
        {
            const string rawDisplayFixture = "synthetic\nprivate display name";
            var options = PosDbOptions.Default();
            DbInitializer.EnsureCreated(options);
            var factory = new SqliteConnectionFactory(options);
            var warningSummary = CatalogDisplayRecoveryPolicy.Recover(
                new PosCatalogPullResponse
                {
                    Catalog = new PosCatalogPayload
                    {
                        Products = new[]
                        {
                            new PosCatalogProductResponse
                            {
                                Barcode = "QA-WARNING-BARCODE",
                                ProductId = "qa-warning-product",
                                ProductName = rawDisplayFixture,
                                RetailPrice = 100
                            }
                        }
                    }
                }).WarningSummary;
            var revision = "qa-warning-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var warnings = new CatalogDisplayWarningRepository(factory);
            await warnings.StoreSuccessfulSyncAsync(warningSummary, revision, generation: null)
                .ConfigureAwait(true);

            var snapshot = await new PosSyncStatusReader(factory).ReadAsync().ConfigureAwait(true);
            var syncCenter = new SyncCenterViewModel();
            syncCenter.Apply(snapshot, DateTimeOffset.UtcNow);
            var syncCenterPass = snapshot.CatalogDisplayWarningCount > 0 &&
                                 !string.IsNullOrWhiteSpace(syncCenter.CatalogDisplayWarningText) &&
                                 syncCenter.CatalogDisplayWarningText.IndexOf(rawDisplayFixture, StringComparison.Ordinal) < 0;

            var view = new PosView();
            var viewModel = view.DataContext as PosViewModel;
            if (viewModel == null)
            {
                return "FAIL: toast_view_model_missing";
            }

            var detailsRequests = 0;
            view.CatalogWarningDetailsRequested += () => detailsRequests++;
            viewModel.SetStatus(
                PosLocalization.Current.Format(
                    "pos.status.catalogWarningsAvailable",
                    snapshot.CatalogDisplayWarningCount),
                PosNoticeSeverity.Warning,
                showDetails: true);
            view.Measure(new Size(1024, 768));
            view.Arrange(new Rect(0, 0, 1024, 768));
            view.UpdateLayout();
            var detailsButton = view.FindName("StatusToastDetailsButton") as Button;
            var bannerPass = viewModel.IsStatusToastVisible &&
                             viewModel.IsStatusToastDetailsVisible &&
                             detailsButton != null &&
                             detailsButton.Visibility == Visibility.Visible &&
                             view.ActualWidth <= 1024 &&
                             view.ActualHeight <= 768;
            viewModel.StatusToastDetailsCommand.Execute(null);
            var detailsPass = detailsRequests == 1 &&
                              !viewModel.IsStatusToastVisible &&
                              !viewModel.IsStatusToastDetailsVisible;

            var displayedOnce = await warnings.TryMarkDisplayedAsync(revision).ConfigureAwait(true) &&
                                !await warnings.TryMarkDisplayedAsync(revision).ConfigureAwait(true);
            var localizationPass = VerifyLocalizedDetailsLabel();
            viewModel.Dispose();

            return bannerPass && detailsPass && syncCenterPass && displayedOnce && localizationPass
                ? "PASS: bannerNonModal=True; detailsButton=True; syncCenter=True; rawValues=False; " +
                  "languages=en,es,it,zh; viewport=1024x768; displayedOnce=True; hardwareEffects=None"
                : "FAIL: banner=" + bannerPass +
                  "; details=" + detailsPass +
                  "; syncCenter=" + syncCenterPass +
                  "; displayedOnce=" + displayedOnce +
                  "; localization=" + localizationPass;
        }

        private static bool VerifyLocalizedDetailsLabel()
        {
            var localization = PosLocalization.Current;
            var original = localization.CurrentLanguage;
            try
            {
                foreach (var language in new[] { "en", "es", "it", "zh-CN" })
                {
                    localization.SetLanguage(language);
                    if (string.IsNullOrWhiteSpace(localization.Text("pos.status.viewDetails")))
                    {
                        return false;
                    }
                }

                return true;
            }
            finally
            {
                localization.SetLanguage(original);
            }
        }
    }
}
