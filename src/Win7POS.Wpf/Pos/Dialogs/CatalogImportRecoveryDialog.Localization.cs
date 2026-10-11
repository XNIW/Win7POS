using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Win7POS.Wpf.Localization;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class CatalogImportRecoveryDialog
    {
        private readonly Dictionary<TextBlock, Func<string>> _localizedText =
            new Dictionary<TextBlock, Func<string>>();

        private void InitializeLocalization() => WeakEventManager<PosLocalization, EventArgs>.AddHandler(
            PosLocalization.Current, nameof(PosLocalization.LanguageChanged), OnLanguageChanged);

        private void StopLocalization()
        {
            WeakEventManager<PosLocalization, EventArgs>.RemoveHandler(
                PosLocalization.Current, nameof(PosLocalization.LanguageChanged), OnLanguageChanged);
            _localizedText.Clear();
        }

        private void SetLocalizedText(TextBlock target, string key, params object[] args) =>
            SetLocalizedText(target, () => PosLocalization.F(key, args));

        private void SetLocalizedText(TextBlock target, Func<string> format)
        {
            // Retain the last presentation inputs, never rerun an operation to translate it.
            _localizedText[target] = format;
            target.Text = format();
        }

        private void OnLanguageChanged(object sender, EventArgs e)
        {
            if (_closed) return;
            if (Dispatcher.CheckAccess()) RefreshLocalizedText();
            else Dispatcher.BeginInvoke(new Action(RefreshLocalizedText), DispatcherPriority.DataBind);
        }

        private void RefreshLocalizedText()
        {
            if (_closed) return;
            foreach (var text in _localizedText) text.Key.Text = text.Value();
        }
    }
}
