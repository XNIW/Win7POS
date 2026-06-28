using System;
using System.Linq;
using System.Threading.Tasks;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Repositories;

namespace Win7POS.Wpf.Pos.Online
{
    internal static class PosOnlinePolicySnapshot
    {
        private const string ContractVersionKey = "pos.policy.contract_version";
        private const string OfflineModeKey = "pos.policy.offline_mode";
        private const string PaymentMethodsKey = "pos.policy.payment_methods";
        private const string RevocationEnforcementKey = "pos.policy.revocation_enforcement";
        private const string StaffOfflineMirrorKey = "pos.policy.staff_offline_mirror";
        private const string TaxStatusKey = "pos.policy.tax_status";
        private const string LimitationsKey = "pos.policy.limitations";
        private const string WarningKey = "pos.policy.warning";

        public static async Task SaveAsync(
            SqliteConnectionFactory factory,
            PosPolicyResponse policy)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            if (policy == null) return;

            var settings = new SettingsRepository(factory);
            await settings.SetStringAsync(ContractVersionKey, Safe(policy.ContractVersion)).ConfigureAwait(false);
            await settings.SetStringAsync(OfflineModeKey, Safe(policy.OfflinePolicy?.Mode)).ConfigureAwait(false);
            await settings.SetStringAsync(PaymentMethodsKey, Join(policy.PaymentPolicy?.SupportedMethods)).ConfigureAwait(false);
            await settings.SetStringAsync(RevocationEnforcementKey, Safe(policy.OfflinePolicy?.RevocationEnforcement)).ConfigureAwait(false);
            await settings.SetStringAsync(StaffOfflineMirrorKey, Safe(policy.StaffPolicy?.OfflineMirror)).ConfigureAwait(false);
            await settings.SetStringAsync(TaxStatusKey, Safe(policy.TaxPolicy?.Status)).ConfigureAwait(false);
            await settings.SetStringAsync(LimitationsKey, Join(policy.Limitations)).ConfigureAwait(false);
            await settings.SetStringAsync(WarningKey, CapabilityWarning(policy)).ConfigureAwait(false);
        }

        private static string CapabilityWarning(PosPolicyResponse policy)
        {
            if (!string.Equals(policy.PaymentPolicy?.Currency, "CLP", StringComparison.OrdinalIgnoreCase))
            {
                return "unsupported_currency";
            }

            if (Contains(policy.PaymentPolicy?.SupportedMethods, PosOnlineContract.PaymentTransfer))
            {
                return "transfer_payment_not_supported_by_win7pos";
            }

            if (!string.IsNullOrWhiteSpace(policy.StaffPolicy?.OfflineMirror) &&
                !string.Equals(policy.StaffPolicy.OfflineMirror, "current_staff_only", StringComparison.OrdinalIgnoreCase))
            {
                return "staff_offline_policy_not_supported";
            }

            if (!string.IsNullOrWhiteSpace(policy.TaxPolicy?.Status) &&
                !string.Equals(policy.TaxPolicy.Status, "not_configured", StringComparison.OrdinalIgnoreCase))
            {
                return "tax_policy_not_supported";
            }

            if (policy.TaxPolicy != null && policy.TaxPolicy.DefaultTaxClp != 0)
            {
                return "tax_amount_not_supported";
            }

            if (!string.IsNullOrWhiteSpace(policy.Capabilities?.SalesSync) &&
                !string.Equals(policy.Capabilities.SalesSync, PosOnlineContract.SalesSchemaVersion, StringComparison.OrdinalIgnoreCase))
            {
                return "sales_sync_contract_not_supported";
            }

            if (policy.Capabilities != null && !policy.Capabilities.OfflineSales)
            {
                return "offline_sales_disabled_by_server_policy";
            }

            return string.Empty;
        }

        private static bool Contains(string[] values, string expected)
        {
            return (values ?? new string[0]).Any(value =>
                string.Equals(value, expected, StringComparison.OrdinalIgnoreCase));
        }

        private static string Join(string[] values)
        {
            return string.Join(",", (values ?? new string[0])
                .Select(Safe)
                .Where(value => value.Length > 0)
                .Take(20));
        }

        private static string Safe(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (normalized.Length > 120)
            {
                normalized = normalized.Substring(0, 120);
            }

            return normalized.Replace("\r", string.Empty).Replace("\n", string.Empty);
        }
    }
}
