using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
        private const int CurrentFormatVersion = 4;
        private const int PreviousFormatVersion = 3;
        private const int OlderFormatVersion = 2;
        private const int LegacyFormatVersion = 1;
        private const int MaximumTrustedDeviceStateBytes = 64 * 1024;
        private const int MaximumBindingValueCharacters = 16 * 1024;
        private const int MaximumBindingMaterialCharacters = 128 * 1024;
        private const int MaximumProcessClockScopes = 16;
        private const int MaximumMonotonicClockDomainCharacters = 128;
        private const int MaximumProfileNameCharacters = 80;
        private const int MinimumProfileNameCharacters = 8;
        private static readonly object StateGate = new object();
        private static readonly string ProcessAuthorizationScope =
            CreateProcessAuthorizationScope();
        private static readonly string DefaultMonotonicClockDomain =
            "system-stopwatch";
        private static ProcessAuthorizationClockState
            _processAuthorizationClockState =
                ProcessAuthorizationClockState.CreateEmpty();
        private readonly Func<long> _monotonicTimestamp;
        private readonly long _monotonicFrequency;
        private readonly string _monotonicClockDomain;
        private readonly string _profileName;

        public PosTrustedDeviceStore()
            : this(
                Stopwatch.GetTimestamp,
                Stopwatch.Frequency,
                DefaultMonotonicClockDomain,
                null)
        {
        }

        public PosTrustedDeviceStore(string profileName)
            : this(
                Stopwatch.GetTimestamp,
                Stopwatch.Frequency,
                DefaultMonotonicClockDomain,
                NormalizeProfileName(profileName))
        {
        }

        internal PosTrustedDeviceStore(
            Func<long> monotonicTimestamp,
            long monotonicFrequency,
            string monotonicClockDomain)
            : this(
                monotonicTimestamp,
                monotonicFrequency,
                monotonicClockDomain,
                null)
        {
        }

        private PosTrustedDeviceStore(
            Func<long> monotonicTimestamp,
            long monotonicFrequency,
            string monotonicClockDomain,
            string profileName)
        {
            _monotonicTimestamp = monotonicTimestamp ??
                throw new ArgumentNullException(nameof(monotonicTimestamp));
            _monotonicFrequency = monotonicFrequency;
            var rawMonotonicClockDomain =
                monotonicClockDomain ?? string.Empty;
            _monotonicClockDomain =
                rawMonotonicClockDomain.Length <=
                    MaximumMonotonicClockDomainCharacters
                    ? rawMonotonicClockDomain.Trim()
                    : string.Empty;
            _profileName = profileName;
        }

        public string TrustedDeviceFilePath => string.IsNullOrEmpty(_profileName)
            ? Path.Combine(AppPaths.DataDirectory, "pos-trusted-device.json")
            : Path.Combine(
                AppPaths.DataDirectory,
                "trusted-profiles",
                _profileName + ".json");

        public string ProfileName => _profileName ?? string.Empty;

        public static bool IsValidProfileName(string profileName)
        {
            if (string.IsNullOrWhiteSpace(profileName))
                return false;
            var candidate = profileName.Trim();
            if (candidate.Length < MinimumProfileNameCharacters ||
                candidate.Length > MaximumProfileNameCharacters)
            {
                return false;
            }

            for (var index = 0; index < candidate.Length; index++)
            {
                var character = candidate[index];
                if ((character < 'a' || character > 'z') &&
                    (character < '0' || character > '9') &&
                    character != '-')
                {
                    return false;
                }
            }

            return candidate[0] != '-' &&
                candidate[candidate.Length - 1] != '-';
        }

        public PosTrustedDeviceStore CreateIsolatedProfileFromCurrentTrust(
            string profileName)
        {
            var normalizedProfileName = NormalizeProfileName(profileName);
            if (!string.IsNullOrEmpty(_profileName))
            {
                throw new InvalidOperationException(
                    "Only the shared trusted-device profile can bootstrap an isolated profile.");
            }

            lock (StateGate)
            {
                if (!TryReadState(out _, out var currentSession))
                {
                    throw new InvalidOperationException(
                        "The shared trusted-device profile is unavailable or invalid.");
                }

                var isolatedStore = new PosTrustedDeviceStore(
                    Stopwatch.GetTimestamp,
                    Stopwatch.Frequency,
                    DefaultMonotonicClockDomain,
                    normalizedProfileName);
                isolatedStore.EnsureProfilePathSafe(createDirectory: false);
                if (File.Exists(isolatedStore.TrustedDeviceFilePath))
                {
                    throw new IOException(
                        "The isolated trusted-device profile already exists.");
                }

                var isolatedSession = CloneForIsolatedProfile(currentSession);
                isolatedStore.SaveState(CreateStoredState(isolatedSession));
                return isolatedStore;
            }
        }

        public bool HasStoredState()
        {
            lock (StateGate)
            {
                EnsureProfilePathSafe(createDirectory: false);
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
                    session.GenerationId = state.GenerationId;
                    if (state.FormatVersion == CurrentFormatVersion &&
                        session.OfflineAuthorizationAttested)
                    {
                        state.ProtectedOfflineAuthorizationBinding =
                            ProtectString(
                                ComputeOfflineAuthorizationBinding(session));
                    }
                    SaveState(state);
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

        internal bool TryGetReusableGenerationId(
            PosFirstLoginResponse response,
            string expectedCurrentFingerprint,
            out string generationId)
        {
            generationId = string.Empty;
            if (response?.Device == null ||
                response.Session == null ||
                response.Shop == null ||
                response.Staff == null ||
                string.IsNullOrWhiteSpace(expectedCurrentFingerprint))
            {
                return false;
            }

            lock (StateGate)
            {
                if (!TryReadState(out var state, out var session) ||
                    state.FormatVersion != CurrentFormatVersion ||
                    !PosOnlineSyncSupervisorHost.TryCreateGeneration(
                        session,
                        out var generation) ||
                    !string.Equals(
                        generation.Fingerprint,
                        expectedCurrentFingerprint.Trim(),
                        StringComparison.Ordinal) ||
                    !IsExactFirstLoginResponse(session, response))
                {
                    return false;
                }

                generationId = generation.GenerationId;
                return true;
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
            var receiptClock = CaptureOnlineReceiptClock(
                !string.IsNullOrWhiteSpace(
                    response.EffectiveOfflineAuthorizationExpiresAt));
            SaveFirstLogin(response, generationId, receiptClock);
        }

        internal PosAuthoritativeReceiptClock CaptureOnlineReceiptClock(
            bool requireMonotonicClock)
        {
            var receiptTimestamp = 0L;
            if (requireMonotonicClock &&
                !TryCaptureMonotonicTimestamp(out receiptTimestamp))
            {
                throw new InvalidDataException(
                    "Invalid POS authorization lease: trusted_time_continuity_lost");
            }

            return new PosAuthoritativeReceiptClock(
                DateTimeOffset.UtcNow,
                receiptTimestamp,
                _monotonicFrequency,
                _monotonicClockDomain);
        }

        internal void SaveFirstLogin(
            PosFirstLoginResponse response,
            string generationId,
            PosAuthoritativeReceiptClock receiptClock)
        {
            if (response == null || response.Device == null || response.Session == null)
            {
                throw new ArgumentNullException(nameof(response));
            }
            if (receiptClock == null)
                throw new ArgumentNullException(nameof(receiptClock));

            var requiresOfflineClock =
                !string.IsNullOrWhiteSpace(
                    response.EffectiveOfflineAuthorizationExpiresAt);
            if (requiresOfflineClock &&
                (receiptClock.Frequency != _monotonicFrequency ||
                 !string.Equals(
                     receiptClock.ClockDomain,
                     _monotonicClockDomain,
                     StringComparison.Ordinal)))
            {
                throw new InvalidDataException(
                    "Invalid POS authorization lease: trusted_time_continuity_lost");
            }
            ReceiptShopMetadataPolicy.EnsureValidRemoteShop(response.Shop);

            var localReceiptAt = receiptClock.LocalReceiptAt;
            var receiptTimestamp = receiptClock.MonotonicTimestamp;
            generationId = (generationId ?? string.Empty).Trim();
            if (generationId.Length == 0 || generationId.Length > 64)
                throw new ArgumentException("A bounded generation ID is required.", nameof(generationId));
            var candidate = new PosTrustedDeviceSession
            {
                DeviceToken = response.TrustedDeviceToken,
                EffectiveOfflineAuthorizationExpiresAt =
                    response.EffectiveOfflineAuthorizationExpiresAt,
                GenerationId = generationId,
                LastOkLocalAt = localReceiptAt.ToString("O"),
                LastOkServerAt = response.ServerTime,
                OfflineAuthorizationAttested =
                    !string.IsNullOrWhiteSpace(
                        response.EffectiveOfflineAuthorizationExpiresAt),
                PosSessionId = response.Session.PosSessionId,
                SessionExpiresAt = response.Session.ExpiresAt,
                SessionToken = response.Session.SessionToken,
                ShopCode = response.Shop?.ShopCode,
                ShopId = response.Shop?.ShopId,
                ShopName = response.Shop?.ShopName,
                ShopDeviceId = response.Device.ShopDeviceId,
                StaffCode = response.Staff?.StaffCode,
                StaffCredentialVersion = response.Staff?.CredentialVersion ?? 0,
                StaffDisplayName = response.Staff?.DisplayName,
                StaffId = response.Staff?.StaffId,
                StaffRoleKey = response.Staff?.RoleKey,
                TrustedStateFormatVersion = CurrentFormatVersion
            };
            EnsureFreshOnlineReceipt(candidate, localReceiptAt);
            var state = CreateStoredState(candidate);

            lock (StateGate)
            {
                var preparedClockState =
                    _processAuthorizationClockState.WithoutActiveAnchor();
                if (candidate.OfflineAuthorizationAttested &&
                    !TryPrepareProcessAuthorizationClockState(
                        candidate,
                        receiptTimestamp,
                        out preparedClockState,
                        out var clockFailureCode))
                {
                    throw new InvalidDataException(
                        "Invalid POS authorization lease: " +
                        clockFailureCode);
                }
                SaveState(state);
                _processAuthorizationClockState = preparedClockState;
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
            var receiptTimestamp = 0L;
            if (expectedSession.OfflineAuthorizationAttested &&
                !TryCaptureMonotonicTimestamp(out receiptTimestamp))
            {
                return false;
            }
            var candidate = new PosTrustedDeviceSession
            {
                EffectiveOfflineAuthorizationExpiresAt =
                    expectedSession.EffectiveOfflineAuthorizationExpiresAt,
                LastOkLocalAt = localReceiptAt.ToString("O"),
                LastOkServerAt = response.ServerTime,
                OfflineAuthorizationAttested =
                    expectedSession.OfflineAuthorizationAttested,
                PosSessionId = responsePosSessionId,
                SessionExpiresAt = response.Session.ExpiresAt,
                ShopDeviceId = expectedSession.ShopDeviceId
            };
            EnsureFreshOnlineReceipt(candidate, localReceiptAt);

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

                var refreshedCandidate = new PosTrustedDeviceSession
                {
                    DeviceToken = currentSession.DeviceToken,
                    EffectiveOfflineAuthorizationExpiresAt =
                        currentSession.EffectiveOfflineAuthorizationExpiresAt,
                    GenerationId = currentState.GenerationId,
                    LastOkLocalAt = candidate.LastOkLocalAt,
                    LastOkServerAt = candidate.LastOkServerAt,
                    OfflineAuthorizationAttested =
                        currentSession.OfflineAuthorizationAttested,
                    PosSessionId = responsePosSessionId,
                    SessionToken = sessionToken,
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
                    TrustedStateFormatVersion = CurrentFormatVersion
                };

                EnsureFreshOnlineReceipt(refreshedCandidate, localReceiptAt);
                var preparedClockState =
                    _processAuthorizationClockState.WithoutActiveAnchor();
                if (refreshedCandidate.OfflineAuthorizationAttested &&
                    !TryPrepareProcessAuthorizationClockState(
                        refreshedCandidate,
                        receiptTimestamp,
                        out preparedClockState,
                        out _))
                {
                    return false;
                }
                SaveState(CreateStoredState(refreshedCandidate));
                _processAuthorizationClockState = preparedClockState;
                return TryReadState(out _, out refreshedSession);
            }
        }

        private static void EnsureFreshOnlineReceipt(
            PosTrustedDeviceSession session,
            DateTimeOffset localReceiptAt)
        {
            var decision =
                PosOfflineAuthorizationLeasePolicy.ValidateOnlineReceipt(
                    session,
                    localReceiptAt);
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
                _processAuthorizationClockState =
                    _processAuthorizationClockState.WithoutActiveAnchor();
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

                var cleared = DeleteStateBestEffort();
                _processAuthorizationClockState =
                    _processAuthorizationClockState.WithoutActiveAnchor();
                return cleared;
            }
        }

        internal bool TryGetProcessAuthorizationTrustedNow(
            OnlineSyncGeneration expectedGeneration,
            out DateTimeOffset trustedServerNow)
        {
            trustedServerNow = default;
            if (expectedGeneration == null)
                return false;

            lock (StateGate)
            {
                var anchor =
                    _processAuthorizationClockState.ActiveAnchor;
                if (anchor == null ||
                    !string.Equals(
                        anchor.GenerationFingerprint,
                        expectedGeneration.Fingerprint,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        anchor.ClockDomain,
                        _monotonicClockDomain,
                        StringComparison.Ordinal) ||
                    anchor.Frequency != _monotonicFrequency ||
                    !TryCaptureMonotonicTimestamp(out var currentTimestamp))
                {
                    return false;
                }

                return TryAdvanceServerAnchor(
                    anchor.ServerAnchor,
                    anchor.MonotonicTimestamp,
                    currentTimestamp,
                    anchor.Frequency,
                    out trustedServerNow);
            }
        }

        private bool TryPrepareProcessAuthorizationClockState(
            PosTrustedDeviceSession session,
            long receiptTimestamp,
            out ProcessAuthorizationClockState prepared,
            out string failureCode)
        {
            prepared = null;
            failureCode = "trusted_time_continuity_lost";
            if (session == null ||
                !session.OfflineAuthorizationAttested ||
                _monotonicFrequency <= 0 ||
                string.IsNullOrWhiteSpace(_monotonicClockDomain) ||
                _monotonicClockDomain.Length >
                    MaximumMonotonicClockDomainCharacters ||
                !TryParseUtc(session.LastOkServerAt, out var serverAnchor) ||
                !TryParseUtc(session.LastOkLocalAt, out var localReceiptAt) ||
                !TryCreateGenerationFingerprint(
                    session,
                    out var generationFingerprint) ||
                !TryCaptureMonotonicTimestamp(out var currentTimestamp) ||
                !TryAdvanceServerAnchor(
                    serverAnchor,
                    receiptTimestamp,
                    currentTimestamp,
                    _monotonicFrequency,
                    out serverAnchor))
            {
                return false;
            }

            var currentState = _processAuthorizationClockState;
            var activeAnchor = currentState.ActiveAnchor;
            if (activeAnchor != null &&
                (!string.Equals(
                    activeAnchor.ClockDomain,
                    _monotonicClockDomain,
                    StringComparison.Ordinal) ||
                 activeAnchor.Frequency != _monotonicFrequency))
            {
                return false;
            }

            var clockKey = new ProcessClockKey(
                _monotonicClockDomain,
                _monotonicFrequency);
            var nextHighWaters =
                new Dictionary<
                    ProcessClockKey,
                    ProcessTrustedTimeHighWater>(
                        currentState.HighWaters);
            if (nextHighWaters.TryGetValue(
                    clockKey,
                    out var existingHighWater))
            {
                if (!string.Equals(
                        existingHighWater.ClockDomain,
                        _monotonicClockDomain,
                        StringComparison.Ordinal) ||
                    existingHighWater.Frequency != _monotonicFrequency ||
                    !TryAdvanceServerAnchor(
                        existingHighWater.ServerAnchor,
                        existingHighWater.MonotonicTimestamp,
                        currentTimestamp,
                        existingHighWater.Frequency,
                        out var existingTrustedNow))
                {
                    return false;
                }

                if (existingTrustedNow > serverAnchor)
                    serverAnchor = existingTrustedNow;
            }
            else if (nextHighWaters.Count >= MaximumProcessClockScopes)
            {
                return false;
            }

            var decision = PosOfflineAuthorizationLeasePolicy.Evaluate(
                session,
                localReceiptAt,
                null,
                serverAnchor);
            if (!decision.Allowed)
            {
                failureCode = decision.Code;
                return false;
            }

            if (!decision.EstimatedServerNow.HasValue)
                return false;

            var trustedServerNow = decision.EstimatedServerNow.Value;
            nextHighWaters[clockKey] =
                new ProcessTrustedTimeHighWater(
                    trustedServerNow,
                    currentTimestamp,
                    _monotonicFrequency,
                    _monotonicClockDomain);
            prepared = new ProcessAuthorizationClockState(
                nextHighWaters,
                new ProcessAuthorizationClockAnchor(
                    generationFingerprint,
                    trustedServerNow,
                    currentTimestamp,
                    _monotonicFrequency,
                    _monotonicClockDomain));
            return true;
        }

        private bool TryCaptureMonotonicTimestamp(out long timestamp)
        {
            timestamp = 0;
            if (_monotonicFrequency <= 0)
                return false;

            try
            {
                timestamp = _monotonicTimestamp();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryAdvanceServerAnchor(
            DateTimeOffset serverAnchor,
            long anchorTimestamp,
            long currentTimestamp,
            long frequency,
            out DateTimeOffset trustedServerNow)
        {
            trustedServerNow = default;
            if (frequency <= 0 || currentTimestamp < anchorTimestamp)
                return false;

            try
            {
                var elapsedCounterTicks =
                    (decimal)currentTimestamp - anchorTimestamp;
                var elapsedTimeSpanTicks = decimal.Truncate(
                    elapsedCounterTicks *
                    TimeSpan.TicksPerSecond /
                    frequency);
                if (elapsedTimeSpanTicks < 0 ||
                    elapsedTimeSpanTicks > long.MaxValue)
                {
                    return false;
                }

                trustedServerNow = serverAnchor.AddTicks(
                    decimal.ToInt64(elapsedTimeSpanTicks));
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        private static bool TryCreateGenerationFingerprint(
            PosTrustedDeviceSession session,
            out string fingerprint)
        {
            fingerprint = string.Empty;
            try
            {
                fingerprint = new OnlineSyncGeneration(
                    session.GenerationId,
                    session.PosSessionId,
                    session.ShopDeviceId,
                    session.ShopId,
                    session.ShopCode,
                    session.StaffId,
                    session.StaffCredentialVersion).Fingerprint;
                return !string.IsNullOrWhiteSpace(fingerprint);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static bool TryParseUtc(
            string value,
            out DateTimeOffset parsed)
        {
            return DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces |
                    DateTimeStyles.AssumeUniversal |
                    DateTimeStyles.AdjustToUniversal,
                out parsed);
        }

        private bool DeleteStateBestEffort()
        {
            try
            {
                EnsureProfilePathSafe(createDirectory: false);
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
                EnsureProfilePathSafe(createDirectory: false);
                if (!File.Exists(TrustedDeviceFilePath))
                    return false;

                if (!TryReadBoundedUtf8(
                        TrustedDeviceFilePath,
                        out var stateJson))
                {
                    return false;
                }
                state = Deserialize<StoredTrustedDeviceState>(stateJson);
                if (state == null ||
                    (state.FormatVersion != CurrentFormatVersion &&
                     state.FormatVersion != PreviousFormatVersion &&
                     state.FormatVersion != OlderFormatVersion &&
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
                    EffectiveOfflineAuthorizationExpiresAt =
                        state.EffectiveOfflineAuthorizationExpiresAt,
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
                    TrustedStateFormatVersion = state.FormatVersion,
                };
                session.OfflineAuthorizationAttested =
                    HasValidOfflineAuthorizationBinding(state, session);
                if (!session.OfflineAuthorizationAttested)
                    session.EffectiveOfflineAuthorizationExpiresAt = null;
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
                string.Equals(expected.SessionToken, current.SessionToken, StringComparison.Ordinal) &&
                expected.OfflineAuthorizationAttested ==
                    current.OfflineAuthorizationAttested &&
                string.Equals(
                    expected.EffectiveOfflineAuthorizationExpiresAt,
                    current.EffectiveOfflineAuthorizationExpiresAt,
                    StringComparison.Ordinal) &&
                expected.TrustedStateFormatVersion ==
                    current.TrustedStateFormatVersion;
        }

        private static bool IsExactFirstLoginResponse(
            PosTrustedDeviceSession session,
            PosFirstLoginResponse response)
        {
            return session != null &&
                response?.Device != null &&
                response.Session != null &&
                response.Shop != null &&
                response.Staff != null &&
                FixedTimeEquals(
                    session.DeviceToken,
                    response.TrustedDeviceToken) &&
                FixedTimeEquals(
                    session.SessionToken,
                    response.Session.SessionToken) &&
                string.Equals(
                    session.EffectiveOfflineAuthorizationExpiresAt,
                    response.EffectiveOfflineAuthorizationExpiresAt,
                    StringComparison.Ordinal) &&
                string.Equals(
                    session.LastOkServerAt,
                    response.ServerTime,
                    StringComparison.Ordinal) &&
                string.Equals(
                    session.PosSessionId,
                    response.Session.PosSessionId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    session.SessionExpiresAt,
                    response.Session.ExpiresAt,
                    StringComparison.Ordinal) &&
                string.Equals(
                    session.ShopCode,
                    response.Shop.ShopCode,
                    StringComparison.Ordinal) &&
                string.Equals(
                    session.ShopId,
                    response.Shop.ShopId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    session.ShopName,
                    response.Shop.ShopName,
                    StringComparison.Ordinal) &&
                string.Equals(
                    session.ShopDeviceId,
                    response.Device.ShopDeviceId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    session.StaffCode,
                    response.Staff.StaffCode,
                    StringComparison.Ordinal) &&
                session.StaffCredentialVersion ==
                    response.Staff.CredentialVersion &&
                string.Equals(
                    session.StaffDisplayName,
                    response.Staff.DisplayName,
                    StringComparison.Ordinal) &&
                string.Equals(
                    session.StaffId,
                    response.Staff.StaffId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    session.StaffRoleKey,
                    response.Staff.RoleKey,
                    StringComparison.Ordinal);
        }

        private static string ComputeCredentialStamp(PosTrustedDeviceSession session)
        {
            var material = new StringBuilder();
            AppendBoundedValue(material, ComputeSecretStamp(session));
            AppendBoundedValue(
                material,
                session.OfflineAuthorizationAttested ? "1" : "0");
            AppendBoundedValue(
                material,
                session.EffectiveOfflineAuthorizationExpiresAt);
            AppendBoundedValue(
                material,
                session.TrustedStateFormatVersion.ToString());
            return ComputeSha256(material.ToString());
        }

        private static StoredTrustedDeviceState CreateStoredState(
            PosTrustedDeviceSession session)
        {
            var state = new StoredTrustedDeviceState
            {
                FormatVersion = CurrentFormatVersion,
                GenerationId = session.GenerationId,
                LastOkLocalAt = session.LastOkLocalAt,
                LastOkServerAt = session.LastOkServerAt,
                PosSessionId = session.PosSessionId,
                ProtectedDeviceSecret = ProtectString(session.DeviceToken),
                ProtectedSessionSecret = ProtectString(session.SessionToken),
                SessionExpiresAt = session.SessionExpiresAt,
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

            if (session.OfflineAuthorizationAttested)
            {
                state.EffectiveOfflineAuthorizationExpiresAt =
                    session.EffectiveOfflineAuthorizationExpiresAt;
                state.ProtectedOfflineAuthorizationBinding = ProtectString(
                    ComputeOfflineAuthorizationBinding(session));
            }
            return state;
        }

        private static PosTrustedDeviceSession CloneForIsolatedProfile(
            PosTrustedDeviceSession source)
        {
            return new PosTrustedDeviceSession
            {
                DeviceToken = source.DeviceToken,
                EffectiveOfflineAuthorizationExpiresAt = null,
                GenerationId = OnlineSyncGeneration.CreateGenerationId(),
                LastOkLocalAt = source.LastOkLocalAt,
                LastOkServerAt = source.LastOkServerAt,
                OfflineAuthorizationAttested = false,
                PosSessionId = source.PosSessionId,
                SessionExpiresAt = source.SessionExpiresAt,
                SessionToken = source.SessionToken,
                ShopCode = source.ShopCode,
                ShopId = source.ShopId,
                ShopName = source.ShopName,
                ShopDeviceId = source.ShopDeviceId,
                StaffCode = source.StaffCode,
                StaffCredentialVersion = source.StaffCredentialVersion,
                StaffDisplayName = source.StaffDisplayName,
                StaffId = source.StaffId,
                StaffRoleKey = source.StaffRoleKey,
                TrustedStateFormatVersion = CurrentFormatVersion,
            };
        }

        private static string NormalizeProfileName(string profileName)
        {
            var candidate = (profileName ?? string.Empty).Trim();
            if (!IsValidProfileName(candidate))
            {
                throw new ArgumentException(
                    "Profile names must contain 8-80 lowercase ASCII letters, digits, or hyphens and cannot start or end with a hyphen.",
                    nameof(profileName));
            }
            return candidate;
        }

        private static bool HasValidOfflineAuthorizationBinding(
            StoredTrustedDeviceState state,
            PosTrustedDeviceSession session)
        {
            if (state.FormatVersion != CurrentFormatVersion ||
                string.IsNullOrWhiteSpace(
                    state.EffectiveOfflineAuthorizationExpiresAt) ||
                string.IsNullOrWhiteSpace(
                    state.ProtectedOfflineAuthorizationBinding))
            {
                return false;
            }

            var expected = ComputeOfflineAuthorizationBinding(session);
            var actual = UnprotectToString(
                state.ProtectedOfflineAuthorizationBinding);
            return FixedTimeEquals(expected, actual);
        }

        private static string ComputeOfflineAuthorizationBinding(
            PosTrustedDeviceSession session)
        {
            var material = new StringBuilder();
            AppendBoundedValue(material, CurrentFormatVersion.ToString());
            AppendBoundedValue(material, session.GenerationId);
            AppendBoundedValue(material, session.LastOkServerAt);
            AppendBoundedValue(material, session.LastOkLocalAt);
            AppendBoundedValue(material, session.PosSessionId);
            AppendBoundedValue(material, session.SessionExpiresAt);
            AppendBoundedValue(material, session.ShopId);
            AppendBoundedValue(material, session.ShopCode);
            AppendBoundedValue(material, session.ShopDeviceId);
            AppendBoundedValue(material, session.StaffId);
            AppendBoundedValue(material, session.StaffCode);
            AppendBoundedValue(
                material,
                session.StaffCredentialVersion.ToString());
            AppendBoundedValue(
                material,
                session.EffectiveOfflineAuthorizationExpiresAt);
            AppendBoundedValue(material, ComputeSecretStamp(session));
            // The repository has no trustworthy persisted monotonic boot clock on
            // Windows 7. Keep offline authority process-scoped so a restart cannot
            // reset the in-memory server-time high-water and replay this binding.
            AppendBoundedValue(material, ProcessAuthorizationScope);
            return ComputeSha256(material.ToString());
        }

        private static string CreateProcessAuthorizationScope()
        {
            var bytes = new byte[32];
            try
            {
                using (var random = RandomNumberGenerator.Create())
                {
                    random.GetBytes(bytes);
                }
                return Convert.ToBase64String(bytes);
            }
            finally
            {
                Array.Clear(bytes, 0, bytes.Length);
            }
        }

        private sealed class ProcessClockKey :
            IEquatable<ProcessClockKey>
        {
            public ProcessClockKey(
                string clockDomain,
                long frequency)
            {
                ClockDomain = clockDomain ?? string.Empty;
                Frequency = frequency;
            }

            public string ClockDomain { get; }
            public long Frequency { get; }

            public bool Equals(ProcessClockKey other)
            {
                return other != null &&
                    Frequency == other.Frequency &&
                    string.Equals(
                        ClockDomain,
                        other.ClockDomain,
                        StringComparison.Ordinal);
            }

            public override bool Equals(object obj)
            {
                return Equals(obj as ProcessClockKey);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    var hash = 17;
                    hash = (hash * 31) +
                        StringComparer.Ordinal.GetHashCode(ClockDomain);
                    hash = (hash * 31) + Frequency.GetHashCode();
                    return hash;
                }
            }
        }

        private sealed class ProcessTrustedTimeHighWater
        {
            public ProcessTrustedTimeHighWater(
                DateTimeOffset serverAnchor,
                long monotonicTimestamp,
                long frequency,
                string clockDomain)
            {
                ServerAnchor = serverAnchor;
                MonotonicTimestamp = monotonicTimestamp;
                Frequency = frequency;
                ClockDomain = clockDomain ?? string.Empty;
            }

            public DateTimeOffset ServerAnchor { get; }
            public long MonotonicTimestamp { get; }
            public long Frequency { get; }
            public string ClockDomain { get; }
        }

        private sealed class ProcessAuthorizationClockState
        {
            public ProcessAuthorizationClockState(
                Dictionary<
                    ProcessClockKey,
                    ProcessTrustedTimeHighWater> highWaters,
                ProcessAuthorizationClockAnchor activeAnchor)
            {
                HighWaters = highWaters ??
                    throw new ArgumentNullException(nameof(highWaters));
                ActiveAnchor = activeAnchor;
            }

            public Dictionary<
                ProcessClockKey,
                ProcessTrustedTimeHighWater> HighWaters { get; }
            public ProcessAuthorizationClockAnchor ActiveAnchor { get; }

            public static ProcessAuthorizationClockState CreateEmpty()
            {
                return new ProcessAuthorizationClockState(
                    new Dictionary<
                        ProcessClockKey,
                        ProcessTrustedTimeHighWater>(),
                    null);
            }

            public ProcessAuthorizationClockState WithoutActiveAnchor()
            {
                return ActiveAnchor == null
                    ? this
                    : new ProcessAuthorizationClockState(
                        HighWaters,
                        null);
            }
        }

        private sealed class ProcessAuthorizationClockAnchor
        {
            public ProcessAuthorizationClockAnchor(
                string generationFingerprint,
                DateTimeOffset serverAnchor,
                long monotonicTimestamp,
                long frequency,
                string clockDomain)
            {
                GenerationFingerprint = generationFingerprint ?? string.Empty;
                ServerAnchor = serverAnchor;
                MonotonicTimestamp = monotonicTimestamp;
                Frequency = frequency;
                ClockDomain = clockDomain ?? string.Empty;
            }

            public string GenerationFingerprint { get; }
            public DateTimeOffset ServerAnchor { get; }
            public long MonotonicTimestamp { get; }
            public long Frequency { get; }
            public string ClockDomain { get; }
        }

        private static string ComputeSecretStamp(PosTrustedDeviceSession session)
        {
            var material = new StringBuilder();
            AppendBoundedValue(material, session.DeviceToken);
            AppendBoundedValue(material, session.SessionToken);
            return ComputeSha256(material.ToString());
        }

        private static void AppendBoundedValue(
            StringBuilder builder,
            string value)
        {
            value = value ?? string.Empty;
            if (value.Length > MaximumBindingValueCharacters ||
                builder.Length >
                    MaximumBindingMaterialCharacters - value.Length - 32)
            {
                throw new InvalidDataException(
                    "Trusted authorization binding exceeds the supported size.");
            }
            builder.Append(value.Length);
            builder.Append(':');
            builder.Append(value);
            builder.Append('|');
        }

        private static string ComputeSha256(string material)
        {
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
            EnsureProfilePathSafe(createDirectory: true);
            var serialized = Serialize(state);
            if (Encoding.UTF8.GetByteCount(serialized) >
                MaximumTrustedDeviceStateBytes)
            {
                throw new InvalidDataException(
                    "Trusted-device state exceeds the supported size.");
            }
            WriteAllTextAtomic(TrustedDeviceFilePath, serialized);
        }

        private void EnsureProfilePathSafe(bool createDirectory)
        {
            if (string.IsNullOrEmpty(_profileName)) return;
            var dataRoot = Path.GetFullPath(AppPaths.DataDirectory).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            var profileDirectory = Path.GetFullPath(Path.Combine(
                dataRoot,
                "trusted-profiles"));
            if (!profileDirectory.StartsWith(
                    dataRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("trusted_profile_path_escape");
            }
            var dataInfo = new DirectoryInfo(dataRoot);
            if (dataInfo.Exists &&
                (dataInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("trusted_profile_data_root_reparse");
            }
            var profileInfo = new DirectoryInfo(profileDirectory);
            if (profileInfo.Exists &&
                (profileInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("trusted_profile_directory_reparse");
            }
            if (createDirectory)
            {
                Directory.CreateDirectory(profileDirectory);
                profileInfo.Refresh();
                if ((profileInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("trusted_profile_directory_reparse");
                }
            }
            var fileInfo = new FileInfo(TrustedDeviceFilePath);
            if (fileInfo.Exists &&
                (fileInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("trusted_profile_file_reparse");
            }
        }

        private static bool TryReadBoundedUtf8(
            string path,
            out string text)
        {
            text = null;
            var bytes = new byte[MaximumTrustedDeviceStateBytes + 1];
            using (var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            {
                var count = 0;
                while (count < bytes.Length)
                {
                    var read = stream.Read(
                        bytes,
                        count,
                        bytes.Length - count);
                    if (read <= 0)
                        break;
                    count += read;
                }
                if (count > MaximumTrustedDeviceStateBytes ||
                    stream.ReadByte() >= 0)
                {
                    return false;
                }

                text = new UTF8Encoding(
                        encoderShouldEmitUTF8Identifier: false,
                        throwOnInvalidBytes: true)
                    .GetString(bytes, 0, count);
                if (text.Length > 0 && text[0] == '\uFEFF')
                    text = text.Substring(1);
                return true;
            }
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

            [DataMember(
                Name = "effectiveOfflineAuthorizationExpiresAt",
                EmitDefaultValue = false)]
            public string EffectiveOfflineAuthorizationExpiresAt { get; set; }

            [DataMember(
                Name = "protectedOfflineAuthorizationBinding",
                EmitDefaultValue = false)]
            public string ProtectedOfflineAuthorizationBinding { get; set; }

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

    internal sealed class PosAuthoritativeReceiptClock
    {
        public PosAuthoritativeReceiptClock(
            DateTimeOffset localReceiptAt,
            long monotonicTimestamp,
            long frequency,
            string clockDomain)
        {
            LocalReceiptAt = localReceiptAt;
            MonotonicTimestamp = monotonicTimestamp;
            Frequency = frequency;
            ClockDomain = clockDomain ?? string.Empty;
        }

        public DateTimeOffset LocalReceiptAt { get; }
        public long MonotonicTimestamp { get; }
        public long Frequency { get; }
        public string ClockDomain { get; }
    }
}
