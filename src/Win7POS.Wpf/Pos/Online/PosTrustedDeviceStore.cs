using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using Win7POS.Core;
using Win7POS.Core.Online;

namespace Win7POS.Wpf.Pos.Online
{
    public sealed class PosTrustedDeviceStore
    {
        private const int CurrentFormatVersion = 1;
        public string TrustedDeviceFilePath => Path.Combine(AppPaths.DataDirectory, "pos-trusted-device.json");

        public bool HasStoredState()
        {
            return File.Exists(TrustedDeviceFilePath);
        }

        public bool TryRead(out PosTrustedDeviceSession session)
        {
            session = null;

            try
            {
                if (!File.Exists(TrustedDeviceFilePath))
                {
                    return false;
                }

                var state = Deserialize<StoredTrustedDeviceState>(
                    File.ReadAllText(TrustedDeviceFilePath, Encoding.UTF8));

                if (state == null ||
                    state.FormatVersion != CurrentFormatVersion ||
                    string.IsNullOrWhiteSpace(state.ShopDeviceId) ||
                    string.IsNullOrWhiteSpace(state.PosSessionId) ||
                    string.IsNullOrWhiteSpace(state.ProtectedDeviceSecret) ||
                    string.IsNullOrWhiteSpace(state.ProtectedSessionSecret))
                {
                    return false;
                }

                session = new PosTrustedDeviceSession
                {
                    DeviceToken = UnprotectToString(state.ProtectedDeviceSecret),
                    LastOkLocalAt = state.LastOkLocalAt,
                    LastOkServerAt = state.LastOkServerAt,
                    PosSessionId = state.PosSessionId,
                    SessionExpiresAt = state.SessionExpiresAt,
                    SessionToken = UnprotectToString(state.ProtectedSessionSecret),
                    ShopCode = state.ShopCode,
                    ShopId = state.ShopId,
                    ShopName = state.ShopName,
                    ShopDeviceId = state.ShopDeviceId,
                    StaffCode = state.StaffCode,
                    StaffCredentialVersion = state.StaffCredentialVersion,
                    StaffDisplayName = state.StaffDisplayName,
                    StaffId = state.StaffId,
                    StaffRoleKey = state.StaffRoleKey,
                };

                return !string.IsNullOrWhiteSpace(session.DeviceToken) &&
                       !string.IsNullOrWhiteSpace(session.SessionToken);
            }
            catch
            {
                session = null;
                return false;
            }
        }

        public void SaveFirstLogin(PosFirstLoginResponse response)
        {
            if (response == null || response.Device == null || response.Session == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            var localReceiptAt = DateTimeOffset.UtcNow;
            var candidate = new PosTrustedDeviceSession
            {
                LastOkLocalAt = localReceiptAt.ToString("O"),
                LastOkServerAt = response.ServerTime,
                PosSessionId = response.Session.PosSessionId,
                SessionExpiresAt = response.Session.ExpiresAt,
                ShopDeviceId = response.Device.ShopDeviceId
            };
            EnsureFreshLease(candidate, localReceiptAt);

            var state = new StoredTrustedDeviceState
            {
                FormatVersion = CurrentFormatVersion,
                LastOkLocalAt = candidate.LastOkLocalAt,
                LastOkServerAt = candidate.LastOkServerAt,
                PosSessionId = response.Session.PosSessionId,
                ProtectedDeviceSecret = ProtectString(response.TrustedDeviceToken),
                ProtectedSessionSecret = ProtectString(response.Session.SessionToken),
                SessionExpiresAt = response.Session.ExpiresAt,
                ShopCode = response.Shop?.ShopCode,
                ShopId = response.Shop?.ShopId,
                ShopName = response.Shop?.ShopName,
                ShopDeviceId = response.Device.ShopDeviceId,
                StaffCredentialVersion = response.Staff?.CredentialVersion ?? 0,
                StaffCode = response.Staff?.StaffCode,
                StaffDisplayName = response.Staff?.DisplayName,
                StaffId = response.Staff?.StaffId,
                StaffRoleKey = response.Staff?.RoleKey,
            };

            SaveState(state);
        }

        public void SaveHeartbeat(PosTrustedDeviceSession session, PosHeartbeatResponse response)
        {
            if (session == null || response == null || response.Session == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            var sessionToken = string.IsNullOrWhiteSpace(response.Session.SessionToken)
                ? session.SessionToken
                : response.Session.SessionToken;

            var localReceiptAt = DateTimeOffset.UtcNow;
            var candidate = new PosTrustedDeviceSession
            {
                LastOkLocalAt = localReceiptAt.ToString("O"),
                LastOkServerAt = response.ServerTime,
                PosSessionId = response.Session.PosSessionId,
                SessionExpiresAt = response.Session.ExpiresAt,
                ShopDeviceId = session.ShopDeviceId
            };
            EnsureFreshLease(candidate, localReceiptAt);

            var state = new StoredTrustedDeviceState
            {
                FormatVersion = CurrentFormatVersion,
                LastOkLocalAt = candidate.LastOkLocalAt,
                LastOkServerAt = candidate.LastOkServerAt,
                PosSessionId = response.Session.PosSessionId,
                ProtectedDeviceSecret = ProtectString(session.DeviceToken),
                ProtectedSessionSecret = ProtectString(sessionToken),
                SessionExpiresAt = response.Session.ExpiresAt,
                ShopCode = session.ShopCode,
                ShopId = session.ShopId,
                ShopName = session.ShopName,
                ShopDeviceId = session.ShopDeviceId,
                StaffCode = session.StaffCode,
                StaffCredentialVersion = session.StaffCredentialVersion,
                StaffDisplayName = session.StaffDisplayName,
                StaffId = session.StaffId,
                StaffRoleKey = session.StaffRoleKey,
            };

            SaveState(state);
        }

        private static void EnsureFreshLease(
            PosTrustedDeviceSession session,
            DateTimeOffset localReceiptAt)
        {
            var decision = PosOfflineAuthorizationLeasePolicy.Evaluate(session, localReceiptAt);
            if (!decision.Allowed)
            {
                throw new InvalidDataException("Invalid POS authorization lease: " + decision.Code);
            }
        }

        public void Clear()
        {
            try
            {
                if (File.Exists(TrustedDeviceFilePath))
                {
                    File.Delete(TrustedDeviceFilePath);
                }
            }
            catch
            {
                // Removing local trust is best-effort; caller will deny online state if read fails.
            }
        }

        private void SaveState(StoredTrustedDeviceState state)
        {
            AppPaths.EnsureDataDirectories();
            WriteAllTextAtomic(TrustedDeviceFilePath, Serialize(state));
        }

        private static void WriteAllTextAtomic(string path, string text)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tempPath, text ?? string.Empty, Encoding.UTF8);
            try
            {
                if (File.Exists(path))
                {
                    File.Replace(tempPath, path, null);
                }
                else
                {
                    File.Move(tempPath, path);
                }
            }
            catch (IOException)
            {
                File.Copy(tempPath, path, true);
                File.Delete(tempPath);
            }
            catch (UnauthorizedAccessException)
            {
                File.Copy(tempPath, path, true);
                File.Delete(tempPath);
            }
        }

        private static string ProtectString(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                throw new ArgumentException("Secret is empty.");
            }

            var bytes = Encoding.UTF8.GetBytes(value);
            var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }

        private static string UnprotectToString(string protectedValue)
        {
            var protectedBytes = Convert.FromBase64String(protectedValue);
            var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }

        private static string Serialize<T>(T value)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        private static T Deserialize<T>(string json)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json ?? string.Empty)))
            {
                return (T)serializer.ReadObject(stream);
            }
        }

        [DataContract]
        private sealed class StoredTrustedDeviceState
        {
            [DataMember(Name = "formatVersion")]
            public int FormatVersion { get; set; }

            [DataMember(Name = "lastOkServerAt")]
            public string LastOkServerAt { get; set; }

            [DataMember(Name = "lastOkLocalAt", EmitDefaultValue = false)]
            public string LastOkLocalAt { get; set; }

            [DataMember(Name = "posSessionId")]
            public string PosSessionId { get; set; }

            [DataMember(Name = "protectedDeviceSecret")]
            public string ProtectedDeviceSecret { get; set; }

            [DataMember(Name = "protectedSessionSecret")]
            public string ProtectedSessionSecret { get; set; }

            [DataMember(Name = "sessionExpiresAt")]
            public string SessionExpiresAt { get; set; }

            [DataMember(Name = "shopCode")]
            public string ShopCode { get; set; }

            [DataMember(Name = "shopId", EmitDefaultValue = false)]
            public string ShopId { get; set; }

            [DataMember(Name = "shopName", EmitDefaultValue = false)]
            public string ShopName { get; set; }

            [DataMember(Name = "shopDeviceId")]
            public string ShopDeviceId { get; set; }

            [DataMember(Name = "staffCode")]
            public string StaffCode { get; set; }

            [DataMember(Name = "staffCredentialVersion", EmitDefaultValue = false)]
            public int StaffCredentialVersion { get; set; }

            [DataMember(Name = "staffDisplayName", EmitDefaultValue = false)]
            public string StaffDisplayName { get; set; }

            [DataMember(Name = "staffId", EmitDefaultValue = false)]
            public string StaffId { get; set; }

            [DataMember(Name = "staffRoleKey", EmitDefaultValue = false)]
            public string StaffRoleKey { get; set; }
        }
    }

}
