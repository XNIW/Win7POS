using System;
using System.Runtime.Serialization;

namespace Win7POS.Core.Online
{
    // This witness is stored outside profile backups in a volatile Windows-user
    // registry key. It proves continuity between processes, never across boot.
    [DataContract]
    public sealed class PosOfflineRestartReceipt
    {
        [DataMember] public string ProfileScope { get; set; }
        [DataMember] public string StateSha256 { get; set; }
        [DataMember] public string ReceiptId { get; set; }
        [DataMember] public string GenerationFingerprint { get; set; }
        [DataMember] public string ClockDomain { get; set; }
        [DataMember] public long Frequency { get; set; }
        [DataMember] public long Timestamp { get; set; }
        [DataMember] public DateTimeOffset ServerAnchor { get; set; }
        [DataMember] public DateTimeOffset LocalHighWater { get; set; }
    }

    public static class PosOfflineRestartClockPolicy
    {
        public static bool TryAdvance(
            PosOfflineRestartReceipt receipt,
            string profileScope,
            string stateSha256,
            string receiptId,
            string generationFingerprint,
            string clockDomain,
            long frequency,
            long timestamp,
            DateTimeOffset localNow,
            out PosOfflineRestartReceipt advanced,
            out string code)
        {
            advanced = null;
            code = "offline_attestation_required";
            if (receipt == null) return false;
            code = "offline_attestation_invalid";
            if (!Same(receipt.ProfileScope, profileScope) ||
                !Same(receipt.StateSha256, stateSha256) ||
                !Same(receipt.ReceiptId, receiptId) ||
                !Same(receipt.GenerationFingerprint, generationFingerprint))
                return false;
            code = "trusted_time_continuity_lost";
            if (!Same(receipt.ClockDomain, clockDomain) || frequency <= 0 ||
                receipt.Frequency != frequency || receipt.Timestamp < 0 ||
                timestamp < receipt.Timestamp)
                return false;
            code = "clock_rollback";
            if (localNow < receipt.LocalHighWater) return false;
            code = "trusted_time_continuity_lost";
            try
            {
                // Round upward: quantization cannot extend the lease at expiry.
                var elapsedTicks = checked((long)Math.Ceiling(
                    (decimal)(timestamp - receipt.Timestamp) *
                    TimeSpan.TicksPerSecond / frequency));
                advanced = new PosOfflineRestartReceipt
                {
                    ProfileScope = profileScope,
                    StateSha256 = stateSha256,
                    ReceiptId = receiptId,
                    GenerationFingerprint = generationFingerprint,
                    ClockDomain = clockDomain,
                    Frequency = frequency,
                    Timestamp = timestamp,
                    ServerAnchor = receipt.ServerAnchor.AddTicks(elapsedTicks),
                    LocalHighWater = localNow
                };
                code = "ok";
                return true;
            }
            catch (OverflowException) { return false; }
            catch (ArgumentOutOfRangeException) { return false; }
        }

        private static bool Same(string left, string right) =>
            !string.IsNullOrWhiteSpace(left) &&
            string.Equals(left, right, StringComparison.Ordinal);
    }
}
