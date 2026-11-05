using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Win7POS.Core.Security;
using Win7POS.Data.Reports;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Infrastructure.Security;
using Win7POS.Wpf.Localization;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public sealed class SalesReportTimePolicyViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly PosWorkflowService _service;
        private readonly OperatorSession _session;
        private readonly IPermissionService _permissions;
        private ApprovedSalesReportTimePolicy _approved;
        private HistoricalImportContext _reviewedContext;
        private CancellationTokenSource _operation;
        private bool _busy;
        private bool _disposed;
        private int _sessionVersion;
        private string _evidence = string.Empty;
        private string _preview = string.Empty;
        private string _status = string.Empty;
        private string _fileName = string.Empty;
        private string _shop = string.Empty;
        public SalesReportTimePolicyViewModel(PosWorkflowService service, OperatorSession session, IPermissionService permissions)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service)); _session = session ?? throw new ArgumentNullException(nameof(session)); _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
            SelectCommand = new Command(SelectAsync, () => !IsBusy && CanConfigure);
            ActivateCommand = new Command(ActivateAsync, () => CanActivate);
            CancelCommand = new Command(() => { _operation?.Cancel(); return Task.CompletedTask; }, () => IsBusy);
            _session.SessionChanged += OnSessionChanged;
        }
        public Window OwnerWindow { get; set; }
        public ICommand SelectCommand { get; }
        public ICommand ActivateCommand { get; }
        public ICommand CancelCommand { get; }
        public bool IsBusy { get => _busy; private set { _busy = value; Changed(); Changed(nameof(CanEdit)); UpdateCommands(); } }
        public bool CanEdit => !IsBusy;
        public bool CanConfigure => _permissions.Has(PermissionCodes.RegisterImportHistory);
        public bool CanActivate => !_disposed && !IsBusy && CanConfigure && _approved != null && _reviewedContext != null && !IsActivated && !string.IsNullOrWhiteSpace(EvidenceReference) && EvidenceReference.Length <= 500;
        public bool IsActivated { get; private set; }
        public string EvidenceReference { get => _evidence; set { _evidence = value ?? string.Empty; Changed(); UpdateCommands(); } }
        public string Preview { get => _preview; private set { _preview = value; Changed(); } }
        public string Status { get => _status; private set { _status = value; Changed(); } }
        public string SourceFileName { get => _fileName; private set { _fileName = value; Changed(); } }
        public string DestinationShop { get => _shop; private set { _shop = value; Changed(); } }
        private static string T(string key) => PosLocalization.T("reportTimePolicy." + key);
        private Task SelectAsync()
        {
            var picker = new OpenFileDialog { Filter = T("fileFilter"), CheckFileExists = true, Multiselect = false };
            return picker.ShowDialog(OwnerWindow ?? DialogOwnerHelper.GetSafeOwner()) == true ? LoadApprovedPolicyAsync(picker.FileName) : Task.CompletedTask;
        }
        public Task LoadApprovedPolicyAsync(string filePath) => ExecuteAsync(async (ct, version) =>
        {
            _permissions.Demand(PermissionCodes.RegisterImportHistory, T("title"));
            var approved = await SalesReportTimePolicyRepository.ReadApprovedPolicyFileAsync(filePath, ct).ConfigureAwait(true);
            _permissions.Demand(PermissionCodes.RegisterImportHistory, T("title"));
            if (version != _sessionVersion || _disposed) return;
            _approved = approved; _reviewedContext = null; IsActivated = false;
            SourceFileName = approved.SourceFileName;
            var policy = approved.Policy; var first = policy.Segments[0]; var last = policy.Segments[policy.Segments.Count - 1];
            Preview = string.Format(CultureInfo.CurrentCulture, T("preview"), policy.PolicyId, policy.Version, policy.Provenance,
                DateTimeOffset.FromUnixTimeMilliseconds(first.FromUtcMs).ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture),
                DateTimeOffset.FromUnixTimeMilliseconds(last.ToUtcMs).ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture), policy.Segments.Count, policy.Hash, approved.SourceSha256);
            try
            {
                var context = await _service.GetHistoricalImportContextAsync(_session, ct).ConfigureAwait(true);
                if (version != _sessionVersion || _disposed) return;
                _reviewedContext = context; DestinationShop = context.ShopCode; Status = T("ready");
            }
            catch (InvalidOperationException exception) when (exception.Message == "historical_shop_binding_required")
            { DestinationShop = T("shopMissing"); Status = T("shopMissing"); }
        });
        public Task ActivateAsync() => ExecuteAsync(async (ct, version) =>
        {
            _permissions.Demand(PermissionCodes.RegisterImportHistory, T("title"));
            var approved = _approved ?? throw new InvalidOperationException("report_time_policy_not_reviewed");
            var context = _reviewedContext ?? throw new InvalidOperationException("report_time_policy_shop_required");
            if (string.IsNullOrWhiteSpace(EvidenceReference)) throw new InvalidOperationException("report_time_policy_evidence_required");
            var result = await _service.ConfigureSalesReportTimePolicyAsync(approved, EvidenceReference.Trim(), context, _session, ct).ConfigureAwait(true);
            if (version != _sessionVersion || _disposed) return;
            IsActivated = true; Changed(nameof(IsActivated)); Status = string.Format(CultureInfo.CurrentCulture, T("activated"), result.PolicyHash);
        });
        private async Task ExecuteAsync(Func<CancellationToken, int, Task> action)
        {
            if (_disposed || IsBusy) return;
            using var cancellation = new CancellationTokenSource(); _operation = cancellation; var version = _sessionVersion; IsBusy = true; Status = T("working");
            try { await action(cancellation.Token, version).ConfigureAwait(true); }
            catch (OperationCanceledException) { if (version == _sessionVersion) Status = T("cancelled"); }
            catch { if (version == _sessionVersion) { Status = T("failed"); _reviewedContext = null; } }
            finally { if (ReferenceEquals(_operation, cancellation)) _operation = null; IsBusy = false; UpdateCommands(); }
        }
        private void OnSessionChanged()
        {
            Interlocked.Increment(ref _sessionVersion); _operation?.Cancel();
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess()) { if (!dispatcher.HasShutdownStarted) dispatcher.Invoke(new Action(ClearReview)); }
            else ClearReview();
        }
        private void ClearReview() { _approved = null; _reviewedContext = null; Preview = string.Empty; SourceFileName = string.Empty; DestinationShop = string.Empty; EvidenceReference = string.Empty; Status = T("authorizationChanged"); UpdateCommands(); }
        private void UpdateCommands() { (SelectCommand as Command)?.Raise(); (ActivateCommand as Command)?.Raise(); (CancelCommand as Command)?.Raise(); Changed(nameof(CanActivate)); }
        public event PropertyChangedEventHandler PropertyChanged;
        private void Changed([CallerMemberName] string property = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        public void Dispose() { if (_disposed) return; _disposed = true; _session.SessionChanged -= OnSessionChanged; _operation?.Cancel(); _approved = null; _reviewedContext = null; }
        private sealed class Command : ICommand
        {
            private readonly Func<Task> _execute; private readonly Func<bool> _can;
            internal Command(Func<Task> execute, Func<bool> can) { _execute = execute; _can = can; }
            public bool CanExecute(object parameter) => _can();
            public async void Execute(object parameter) { if (CanExecute(parameter)) await _execute().ConfigureAwait(true); }
            public event EventHandler CanExecuteChanged;
            internal void Raise() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
