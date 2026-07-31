using System;
using System.Globalization;
using System.IO;
using Win7POS.Core.Online;
using Win7POS.Wpf.Pos.Online;

namespace Win7POS.Wpf.UiSmokeHarness
{
    internal static class ProductImageProfileWpfSmoke
    {
        internal static string Run()
        {
            const string profileName =
                "asus-staging-image-phase-b-0123456789abcdef01234567";
            const string deviceToken = "qa-profile-device-secret";
            const string sessionToken = "qa-profile-session-secret";
            var shared = new PosTrustedDeviceStore();
            PosTrustedDeviceStore isolated = null;
            try
            {
                shared.Clear();
                shared.SaveFirstLogin(BuildResponse(deviceToken, sessionToken));
                var sharedBefore = File.ReadAllBytes(shared.TrustedDeviceFilePath);

                isolated = shared.CreateIsolatedProfileFromCurrentTrust(
                    profileName);
                if (!isolated.TryRead(out var isolatedSession) ||
                    !shared.TryRead(out var sharedSession))
                {
                    throw new InvalidOperationException(
                        "profile_read_failed");
                }
                var sharedAfter = File.ReadAllBytes(shared.TrustedDeviceFilePath);
                var isolatedBytes = File.ReadAllBytes(
                    isolated.TrustedDeviceFilePath);
                if (!FixedBytesEqual(sharedBefore, sharedAfter))
                    throw new InvalidOperationException("shared_profile_changed");
                if (FixedBytesEqual(sharedBefore, isolatedBytes))
                    throw new InvalidOperationException("encrypted_file_was_copied");
                if (ContainsPlaintext(isolatedBytes, deviceToken) ||
                    ContainsPlaintext(isolatedBytes, sessionToken))
                {
                    throw new InvalidOperationException(
                        "profile_contains_plaintext_secret");
                }
                if (string.Equals(
                    sharedSession.GenerationId,
                    isolatedSession.GenerationId,
                    StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "profile_generation_not_isolated");
                }
                if (isolatedSession.OfflineAuthorizationAttested ||
                    isolatedSession.EffectiveOfflineAuthorizationExpiresAt != null)
                {
                    throw new InvalidOperationException(
                        "offline_authority_was_cloned");
                }
                if (!string.Equals(
                        isolated.ProfileName,
                        profileName,
                        StringComparison.Ordinal) ||
                    string.Equals(
                        isolated.TrustedDeviceFilePath,
                        shared.TrustedDeviceFilePath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "profile_path_not_isolated");
                }
                if (PosTrustedDeviceStore.IsValidProfileName("../escape") ||
                    PosTrustedDeviceStore.IsValidProfileName("UPPERCASE-PROFILE"))
                {
                    throw new InvalidOperationException(
                        "unsafe_profile_name_accepted");
                }

                isolated.Clear();
                if (File.Exists(isolated.TrustedDeviceFilePath) ||
                    !File.Exists(shared.TrustedDeviceFilePath))
                {
                    throw new InvalidOperationException(
                        "profile_cleanup_scope_invalid");
                }
                return "PASS product_image_profile_dpapi_isolated=true " +
                    "shared_unchanged=true plaintext_secrets=false " +
                    "offline_authority_cloned=false cleanup_exact=true";
            }
            finally
            {
                try { isolated?.Clear(); } catch { }
                try { shared.Clear(); } catch { }
            }
        }

        private static PosFirstLoginResponse BuildResponse(
            string deviceToken,
            string sessionToken)
        {
            var now = DateTimeOffset.UtcNow;
            return new PosFirstLoginResponse
            {
                Ok = true,
                ServerTime = now.ToString("O", CultureInfo.InvariantCulture),
                TrustedDeviceToken = deviceToken,
                Device = new PosTrustedDeviceResponse
                {
                    ShopDeviceId =
                        "50000000-0000-4000-8000-000000000152",
                    Status = "active",
                    Trusted = true
                },
                Session = new PosSessionResponse
                {
                    ExpiresAt = now.AddHours(1).ToString(
                        "O",
                        CultureInfo.InvariantCulture),
                    HeartbeatAfterSeconds = 300,
                    PosSessionId =
                        "70000000-0000-4000-8000-000000000152",
                    SessionToken = sessionToken
                },
                Shop = new PosShopResponse
                {
                    ShopCode = "QA-IMAGE",
                    ShopId = "10000000-0000-4000-8000-000000000152",
                    ShopName = "QA Product Image Shop",
                    ShopStatus = "active",
                    Source = "qa_harness"
                },
                Staff = new PosStaffResponse
                {
                    CredentialVersion = 1,
                    DisplayName = "QA Image Operator",
                    RoleKey = "cashier",
                    StaffCode = "QA-IMAGE",
                    StaffId = "60000000-0000-4000-8000-000000000152"
                }
            };
        }

        private static bool ContainsPlaintext(byte[] bytes, string value)
        {
            var text = System.Text.Encoding.UTF8.GetString(bytes);
            return text.IndexOf(value, StringComparison.Ordinal) >= 0;
        }

        private static bool FixedBytesEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
                return false;
            var difference = 0;
            for (var index = 0; index < left.Length; index++)
                difference |= left[index] ^ right[index];
            return difference == 0;
        }
    }
}
