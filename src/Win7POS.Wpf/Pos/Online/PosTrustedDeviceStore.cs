using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using Win7POS.Core;
using Win7POS.Core.Online;
using Win7POS.Core.Receipt;

namespace Win7POS.Wpf.Pos.Online
{
    public sealed class PosTrustedDeviceStore
    {
        private const int CurrentFormatVersion = 2;
        private const int LegacyFormatVersion = 1;
        private static readonly object StateGate = new object();
        public string TrustedDeviceFilePath => Path.Combine(AppPaths.DataDirectory, "pos-trusted-device.json");

        public bool HasStoredState()
        {
            lock (StateGate)
            {
                return File.Exists(TrustedDeviceFilePath);
            }
        }

        public bool TryRead(out PosTrustedDeviceSession session)
        {
            lock (StateGate)
            {
                if (!TryReadState(out var state, out session))
                {
                    return false;
                }

                if (string.IsNullOrWhiteSpace(state.GenerationId))
                {
                    state.GenerationId = OnlineSyncGeneration.CreateGenerationId();
                    state.FormatVersion = CurrentFormatVersion;
                    SaveState(state);
                    session.GenerationId = state.GenerationId;
                }

                return !string.IsNullOrWhiteSpace(session.DeviceToken) &&
                       !string.IsNullOrWhiteSpace(session.SessionToken) &&
                       !string.IsNullOrWhiteSpace(session.GenerationId);
            }
        }

        public bool TryReadGeneration(
            OnlineSyncGeneration expectedGeneration,
            out PosTrustedDeviceSession session,
            out string credentialStamp)
        {
            session = null;
            credentialStamp = string.Empty;
            if (expectedGeneration == null || !TryRead(out var currentSession))
                return false;

            try
            {
                var currentGeneration = new OnlineSyncGeneration(
                    currentSession.GenerationId,
                    currentSession.PosSessionId,
                    currentSession.ShopDeviceId,
                    currentSession.ShopId,
                    currentSession.ShopCode,
                    currentSession.StaffId,
                    currentSession.StaffCredentialVersion);
                if (!string.Equals(
                    currentGeneration.Fingerprint,
                    expectedGeneration.Fingerprint,
                    StringComparison.Ordinal))
                {
                    return false;
                }

                session = currentSession;
                credentialStamp = ComputeCredentialStamp(currentSession);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        public bool HasCredentialStamp(
            OnlineSyncGeneration expectedGeneration,
            string expectedCredentialStamp)
        {
            return TryReadGeneration(
                    expectedGeneration,
                    out _,
                    out var currentCredentialStamp) &&
                FixedTimeEquals(currentCredentialStamp, expectedCredentialStamp);
        }

        public void SaveFirstLogin(PosFirstLoginResponse response)
        {
            SaveFirstLogin(response, OnlineSyncGeneration.CreateGenerationId());
        }

        public void SaveFirstLogin(PosFirstLoginResponse response, string generationId)
        {
            if (response == null || response.Device == null || response.Session == null)
            {
                throw new ArgumentNullException(nameof(response));
            }
            ReceiptShopMetadataPolicy.EnsureValidRemoteShop(response.Shop);

            var localReceiptAt = DateTimeOffset.UtcNow;
            generationId = (generationId ?? string.Empty).Trim();
            if (generationId.Length == 0 || generationId.Length > 64)
                throw new ArgumentException("A bounded generation ID is required.", nameof(generationId));
            var candidate = new PosTrustedDeviceSession
            {
                GenerationId = generationId,
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
                GenerationId = generationId,
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

            lock (StateGate)
            {
                SaveState(state);
            }
        }

        public void SaveHeartbeat(PosTrustedDeviceSession session, PosHeartbeatResponse response)
        {
            if (session == null || response == null || response.Session == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            if (!TrySaveHeartbeat(
                session.GenerationId,
                session,
                response,
                out _))
            {
                throw new InvalidOperationException("The trusted-session generation changed before the heartbeat commit.");
            }
        }

        public bool TrySaveHeartbeat(
            string expectedGenerationId,
            PosTrustedDeviceSession expectedSession,
            PosHeartbeatResponse response,
            out PosTrustedDeviceSession refreshedSession)
        {
            refreshedSession = null;
            if (expectedSession == null || response == null || response.Session == null)
                return false;

            var responsePosSessionId = string.IsNullOrWhiteSpace(response.Session.PosSessionId)
                ? expectedSession.PosSessionId
                : response.Session.PosSessionId.Trim();
            if (!string.Equals(
                responsePosSessionId,
                expectedSession.PosSessionId,
                StringComparison.Ordinal))
            {
                return false;
            }

            var sessionToken = string.IsNullOrWhiteSpace(response.Session.SessionToken)
                ? expectedSession.SessionToken
                : response.Session.SessionToken;

            var localReceiptAt = DateTimeOffset.UtcNow;
            var candidate = new PosTrustedDeviceSession
            {
                LastOkLocalAt = localReceiptAt.ToString("O"),
                LastOkServerAt = response.ServerTime,
                PosSessionId = responsePosSessionId,
                SessionExpiresAt = response.Session.ExpiresAt,
                ShopDeviceId = expectedSession.ShopDeviceId
            };
            EnsureFreshLease(candidate, localReceiptAt);

            lock (StateGate)
            {
                if (!TryReadState(out var currentState, out var currentSession) ||
                    !string.Equals(
                        currentState.GenerationId,
                        (expectedGenerationId ?? string.Empty).Trim(),
                        StringComparison.Ordinal) ||
                    !IsSameImmutableSession(expectedSession, currentSession))
                {
                    return false;
                }

                var state = new StoredTrustedDeviceState
                {
                    FormatVersion = CurrentFormatVersion,
                    GenerationId = currentState.GenerationId,
                    LastOkLocalAt = candidate.LastOkLocalAt,
                    LastOkServerAt = candidate.LastOkServerAt,
                    PosSessionId = responsePosSessionId,
                    ProtectedDeviceSecret = ProtectString(currentSession.DeviceToken),
                    ProtectedSessionSecret = ProtectString(sessionToken),
                    SessionExpiresAt = response.Session.ExpiresAt,
                    ShopCode = currentSession.ShopCode,
                    ShopId = currentSession.ShopId,
                    ShopName = currentSession.ShopName,
                    ShopDeviceId = currentSession.ShopDeviceId,
                    StaffCode = currentSession.StaffCode,
                    StaffCredentialVersion = currentSession.StaffCredentialVersion,
                    StaffDisplayName = currentSession.StaffDisplayName,
                    StaffId = currentSession.StaffId,
                    StaffRoleKey = currentSession.StaffRoleKey,
                };

                SaveState(state);
                return TryReadState(out _, out refreshedSession);
            }
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
            lock (StateGate)
            {
                DeleteStateBestEffort();
            }
        }

        public bool TryClear(string expectedGenerationId)
        {
            lock (StateGate)
            {
                if (!TryReadState(out var state, out _) ||
                    !string.Equals(
                        state.GenerationId,
                        (expectedGenerationId ?? string.Empty).Trim(),
                        StringComparison.Ordinal))
                {
                    return false;
                }

                return DeleteStateBestEffort();
            }
        }

        private bool DeleteStateBestEffort()
        {
            try
            {
                if (File.Exists(TrustedDeviceFilePath))
                    File.Delete(TrustedDeviceFilePath);
                return !File.Exists(TrustedDeviceFilePath);
            }
            catch
            {
                // Removing local trust is best-effort; caller will deny online state if read fails.
                return false;
            }
        }

        private bool TryReadState(
            out StoredTrustedDeviceState state,
            out PosTrustedDeviceSession session)
        {
            state = null;
            session = null;
            try
            {
                if (!File.Exists(TrustedDeviceFilePath))
                    return false;

                state = Deserialize<StoredTrustedDeviceState>(
                    File.ReadAllText(TrustedDeviceFilePath, Encoding.UTF8));
                if (state == null ||
                    (state.FormatVersion != CurrentFormatVersion &&
                     state.FormatVersion != LegacyFormatVersion) ||
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
                    GenerationId = state.GenerationId,
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
                return true;
            }
            catch
            {
                state = null;
                session = null;
                return false;
            }
        }

        private static bool IsSameImmutableSession(
            PosTrustedDeviceSession expected,
            PosTrustedDeviceSession current)
        {
            return expected != null && current != null &&
                string.Equals(expected.GenerationId, current.GenerationId, StringComparison.Ordinal) &&
                string.Equals(expected.PosSessionId, current.PosSessionId, StringComparison.Ordinal) &&
                string.Equals(expected.ShopDeviceId, current.ShopDeviceId, StringComparison.Ordinal) &&
                string.Equals(expected.ShopId, current.ShopId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(expected.ShopCode, current.ShopCode, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(expected.StaffId, current.StaffId, StringComparison.Ordinal) &&
                expected.StaffCredentialVersion == current.StaffCredentialVersion &&
                string.Equals(expected.DeviceToken, current.DeviceToken, StringComparison.Ordinal) &&
                string.Equals(expected.SessionToken, current.SessionToken, StringComparison.Ordinal);
        }

        private static string ComputeCredentialStamp(PosTrustedDeviceSession session)
        {
            var material = string.Concat(
                (session.DeviceToken ?? string.Empty).Length,
                ":",
                session.DeviceToken ?? string.Empty,
                "|",
                (session.SessionToken ?? string.Empty).Length,
                ":",
                session.SessionToken ?? string.Empty);
            using (var sha = SHA256.Create())
            {
                return Convert.ToBase64String(
                    sha.ComputeHash(Encoding.UTF8.GetBytes(material)));
            }
        }

        private static bool FixedTimeEquals(string left, string right)
        {
            if (left == null || right == null || left.Length != right.Length)
                return false;
            var difference = 0;
            for (var index = 0; index < left.Length; index++)
                difference |= left[index] ^ right[index];
            return difference == 0;
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

            [DataMember(Name = "generationId", EmitDefaultValue = false)]
            public string GenerationId { get; set; }

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
