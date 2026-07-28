using System;
using System.Text;
using Win7POS.Core.Logging;

namespace Win7POS.Core.Online
{
    /// <summary>
    /// A bounded, secret-free description of a POS runtime operation. It is safe to
    /// persist in settings, write through the normal logger, and copy to support.
    /// </summary>
    public sealed class PosRuntimeDiagnostic
    {
        public PosRuntimeDiagnostic(
            string operation,
            string stage,
            string code,
            int? httpStatus,
            bool retryable,
            bool authenticationDenied,
            int attemptNumber,
            long? pageNumber,
            long pagesProcessed,
            long rowsReceived,
            long rowsApplied,
            bool hasMore,
            bool catalogSaleSafe,
            string clientRequestId,
            string serverRequestId,
            string cfRay,
            string localIncidentId,
            DateTimeOffset occurredAtUtc,
            long elapsedMilliseconds,
            string exceptionType,
            string safeSummary)
        {
            Operation = NormalizeOperation(operation);
            Stage = NormalizeStage(stage);
            Code = NormalizeCode(code);
            HttpStatus = httpStatus.HasValue && httpStatus.Value >= 100 && httpStatus.Value <= 599
                ? httpStatus
                : null;
            Retryable = retryable && !authenticationDenied;
            AuthenticationDenied = authenticationDenied;
            AttemptNumber = Math.Max(0, attemptNumber);
            PageNumber = pageNumber.HasValue && pageNumber.Value > 0 ? pageNumber : null;
            PagesProcessed = Math.Max(0, pagesProcessed);
            RowsReceived = Math.Max(0, rowsReceived);
            RowsApplied = Math.Max(0, rowsApplied);
            HasMore = hasMore;
            CatalogSaleSafe = catalogSaleSafe;
            ClientRequestId = NormalizeId(clientRequestId);
            ServerRequestId = NormalizeId(serverRequestId);
            CfRay = NormalizeId(cfRay);
            LocalIncidentId = NormalizeId(localIncidentId);
            OccurredAtUtc = occurredAtUtc == default(DateTimeOffset)
                ? DateTimeOffset.UtcNow
                : occurredAtUtc.ToUniversalTime();
            ElapsedMilliseconds = Math.Max(0L, elapsedMilliseconds);
            ExceptionType = NormalizeExceptionType(exceptionType);
            SafeSummary = LogSanitizer.Sanitize(safeSummary ?? string.Empty, 320);
        }

        public bool AuthenticationDenied { get; }
        public int AttemptNumber { get; }
        public string CfRay { get; }
        public string ClientRequestId { get; }
        public string Code { get; }
        public bool CatalogSaleSafe { get; }
        public long ElapsedMilliseconds { get; }
        public string ExceptionType { get; }
        public bool HasMore { get; }
        public int? HttpStatus { get; }
        public string LocalIncidentId { get; }
        public DateTimeOffset OccurredAtUtc { get; }
        public string Operation { get; }
        public long? PageNumber { get; }
        public long PagesProcessed { get; }
        public long RowsApplied { get; }
        public long RowsReceived { get; }
        public bool Retryable { get; }
        public string SafeSummary { get; }
        public string ServerRequestId { get; }
        public string Stage { get; }

        public string SupportId
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(ServerRequestId)) return ServerRequestId;
                if (!string.IsNullOrWhiteSpace(ClientRequestId)) return ClientRequestId;
                if (!string.IsNullOrWhiteSpace(CfRay)) return CfRay;
                return LocalIncidentId;
            }
        }

        public static string CreateLocalIncidentId()
        {
            return "inc-" + Guid.NewGuid().ToString("N").Substring(0, 16);
        }

        public string ToSafeSupportText()
        {
            var text = new StringBuilder();
            text.AppendLine("Win7POS diagnostic");
            text.AppendLine("Operation: " + Operation);
            text.AppendLine("Stage: " + Stage);
            text.AppendLine("Code: " + Code);
            if (HttpStatus.HasValue)
            {
                text.AppendLine("HTTP: " + HttpStatus.Value);
            }

            if (PageNumber.HasValue)
            {
                text.AppendLine("Page: " + PageNumber.Value);
            }

            text.AppendLine("Pages processed: " + PagesProcessed);
            text.AppendLine("Rows received: " + RowsReceived);
            text.AppendLine("Rows applied: " + RowsApplied);
            text.AppendLine("Sale safe: " + (CatalogSaleSafe ? "yes" : "no"));
            text.AppendLine("Retryable: " + (Retryable ? "yes" : "no"));
            text.AppendLine("Support ID: " + SupportId);
            text.Append("UTC: " + OccurredAtUtc.ToString("o"));
            return text.ToString();
        }

        private static string NormalizeCode(string value)
        {
            var normalized = NormalizeAscii(value, 80, allowDot: true, allowColon: false);
            return normalized.Length == 0 ? "failure" : normalized;
        }

        private static string NormalizeExceptionType(string value)
        {
            return NormalizeAscii(value, 160, allowDot: true, allowColon: false);
        }

        private static string NormalizeId(string value)
        {
            return PosTechnicalIdentifier.Redact(value);
        }

        private static string NormalizeAscii(string value, int maximumLength, bool allowDot, bool allowColon)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var source = value.Trim();
            var builder = new StringBuilder(Math.Min(source.Length, maximumLength));
            for (var index = 0; index < source.Length && builder.Length < maximumLength; index++)
            {
                var character = source[index];
                if (char.IsLetterOrDigit(character) || character == '_' || character == '-' ||
                    (allowDot && character == '.') || (allowColon && character == ':'))
                {
                    builder.Append(character);
                }
            }

            return builder.ToString();
        }

        private static string NormalizeOperation(string operation)
        {
            switch ((operation ?? string.Empty).Trim())
            {
                case "online.bootstrap":
                case "device.link":
                case "session.heartbeat":
                case "catalog.pull":
                case "catalog.apply":
                case "sales.sync":
                case "offline.reconnect":
                    return operation.Trim();
                default:
                    return "unknown";
            }
        }

        private static string NormalizeStage(string stage)
        {
            switch ((stage ?? string.Empty).Trim())
            {
                case "validation":
                case "profile_validation":
                case "request_build":
                case "dns":
                case "tls":
                case "network":
                case "timeout":
                case "request":
                case "server_response":
                case "invalid_response":
                case "deserialization":
                case "authentication":
                case "first_login_contract":
                case "device_pending_approval":
                case "device_denied":
                case "staff_denied":
                case "session_creation":
                case "trusted_session_persistence":
                case "shop_transition":
                case "operator_mirror":
                case "catalog_start":
                case "catalog_pull":
                case "local_operator_login":
                case "lease":
                case "catalog_manifest":
                case "catalog_page":
                case "compatibility":
                case "local_stage":
                case "local_apply":
                case "local_persistence":
                case "audit":
                    return stage.Trim();
                default:
                    return "unknown";
            }
        }
    }
}
