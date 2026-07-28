using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Win7POS.Core.Online
{
    public static class PosArticleMutationResponseValidator
    {
        private static readonly Regex PayloadHash = new Regex(
            "^sha256:[0-9a-f]{64}$",
            RegexOptions.CultureInvariant);
        private static readonly Regex CatalogRevision = new Regex(
            "^(0|[1-9][0-9]{0,18})$",
            RegexOptions.CultureInvariant);

        public static PosArticleMutationResponseValidation Validate(
            PosArticleMutationResponse response,
            IReadOnlyList<PosArticleMutationRequest> sent,
            Func<string, string, bool> isKnownAttemptToken)
        {
            if (response == null ||
                sent == null ||
                sent.Count == 0 ||
                !string.Equals(
                    response.SchemaVersion,
                    PosArticleMutationContract.SchemaVersion,
                    StringComparison.Ordinal) ||
                !string.Equals(response.Code, "success", StringComparison.Ordinal) ||
                !TryParseUtc(response.ServerTime) ||
                response.Results == null ||
                response.Results.Length != sent.Count)
            {
                return PosArticleMutationResponseValidation.Invalid(
                    "article_mutation_invalid_response");
            }

            var sentByMutation = new Dictionary<string, PosArticleMutationRequest>(
                StringComparer.Ordinal);
            foreach (var request in sent)
            {
                if (request?.Intent == null ||
                    sentByMutation.ContainsKey(request.Intent.MutationId))
                {
                    return PosArticleMutationResponseValidation.Invalid(
                        "article_mutation_invalid_sent_batch");
                }
                sentByMutation.Add(request.Intent.MutationId, request);
            }

            var validated = new Dictionary<string, PosArticleMutationResult>(
                StringComparer.Ordinal);
            var allSuccessful = true;
            foreach (var result in response.Results)
            {
                PosArticleMutationRequest request;
                if (result?.Ack == null ||
                    !sentByMutation.TryGetValue(result.Ack.MutationId ?? string.Empty, out request) ||
                    validated.ContainsKey(result.Ack.MutationId))
                {
                    return PosArticleMutationResponseValidation.Invalid(
                        "article_mutation_result_identity_mismatch");
                }

                var resultValidation = ValidateResult(
                    result,
                    request,
                    isKnownAttemptToken);
                if (resultValidation != null)
                {
                    return PosArticleMutationResponseValidation.Invalid(
                        resultValidation);
                }
                validated.Add(result.Ack.MutationId, result);
                allSuccessful &=
                    string.Equals(
                        result.DeliveryStatus,
                        PosArticleMutationStatusPolicy.Applied,
                        StringComparison.Ordinal) ||
                    string.Equals(
                        result.DeliveryStatus,
                        PosArticleMutationStatusPolicy.DuplicateReplay,
                        StringComparison.Ordinal);
            }

            if (validated.Count != sentByMutation.Count ||
                response.Ok != allSuccessful)
            {
                return PosArticleMutationResponseValidation.Invalid(
                    "article_mutation_response_completeness_mismatch");
            }
            return PosArticleMutationResponseValidation.Valid(validated);
        }

        private static string ValidateResult(
            PosArticleMutationResult result,
            PosArticleMutationRequest request,
            Func<string, string, bool> isKnownAttemptToken)
        {
            var ack = result.Ack;
            var intent = request.Intent;
            if (!string.Equals(
                    ack.SchemaVersion,
                    PosArticleMutationContract.SchemaVersion,
                    StringComparison.Ordinal) ||
                !string.Equals(ack.MutationId, intent.MutationId, StringComparison.Ordinal) ||
                !string.Equals(
                    ack.IdempotencyKey,
                    intent.IdempotencyKey,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    ack.PayloadHash,
                    request.PayloadHash,
                    StringComparison.Ordinal) ||
                !PayloadHash.IsMatch(ack.PayloadHash ?? string.Empty) ||
                !string.Equals(ack.Status, ack.Code, StringComparison.Ordinal) ||
                !CatalogRevision.IsMatch(ack.CatalogRevision ?? string.Empty) ||
                !PosArticleMutationIntentPolicy.IsProductRevision(
                    ack.ServerTimestamp) ||
                !IsNullableUuid(ack.RemoteProductId) ||
                !IsNullableUuid(ack.PriceHistoryId) ||
                !IsNullableUuid(ack.StockMovementId))
            {
                return "article_mutation_ack_shape_invalid";
            }
            var assignsNewRemoteProduct =
                string.Equals(
                    intent.MutationKind,
                    PosArticleMutationKinds.ProductCreate,
                    StringComparison.Ordinal) ||
                string.Equals(
                    intent.MutationKind,
                    PosArticleMutationKinds.ProductDuplicate,
                    StringComparison.Ordinal);
            if (!assignsNewRemoteProduct &&
                ack.RemoteProductId != null &&
                !string.Equals(
                    ack.RemoteProductId,
                    intent.RemoteProductId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return "article_mutation_remote_identity_mismatch";
            }

            var duplicate = string.Equals(
                result.DeliveryStatus,
                PosArticleMutationStatusPolicy.DuplicateReplay,
                StringComparison.Ordinal);
            if (duplicate)
            {
                if (!string.Equals(
                        ack.Code,
                        PosArticleMutationStatusPolicy.Applied,
                        StringComparison.Ordinal) ||
                    isKnownAttemptToken == null ||
                    !isKnownAttemptToken(intent.MutationId, ack.AttemptToken))
                {
                    return "article_mutation_unknown_replay_attempt";
                }
            }
            else if (!string.Equals(
                ack.AttemptToken,
                request.AttemptToken,
                StringComparison.Ordinal))
            {
                return "article_mutation_attempt_mismatch";
            }

            if (!duplicate &&
                !string.Equals(
                    result.DeliveryStatus,
                    ack.Code,
                    StringComparison.Ordinal))
            {
                return "article_mutation_delivery_status_mismatch";
            }

            PosArticleMutationLocalDisposition disposition;
            try
            {
                disposition = PosArticleMutationStatusPolicy.Classify(
                    result.DeliveryStatus);
            }
            catch (ArgumentException)
            {
                return "article_mutation_unknown_status";
            }

            if (disposition == PosArticleMutationLocalDisposition.Completed)
            {
                if (!ack.Terminal ||
                    ack.Retryable ||
                    !Guid.TryParse(ack.RemoteProductId, out _) ||
                    !PosArticleMutationIntentPolicy.IsProductRevision(
                        ack.AuthoritativeRevision))
                {
                    return "article_mutation_success_ack_invalid";
                }

                if ((string.Equals(
                         intent.MutationKind,
                         PosArticleMutationKinds.ProductRetailPriceChange,
                         StringComparison.Ordinal) ||
                     string.Equals(
                         intent.MutationKind,
                         PosArticleMutationKinds.ProductPurchasePriceChange,
                         StringComparison.Ordinal)) &&
                    !Guid.TryParse(ack.PriceHistoryId, out _))
                {
                    return "article_mutation_price_ack_invalid";
                }
                if (string.Equals(
                        intent.MutationKind,
                        PosArticleMutationKinds.ProductManualStockAdjustment,
                        StringComparison.Ordinal) &&
                    !Guid.TryParse(ack.StockMovementId, out _))
                {
                    return "article_mutation_stock_ack_invalid";
                }
            }
            else
            {
                if (ack.AuthoritativeRevision != null)
                    return "article_mutation_failure_revision_invalid";
                if (disposition == PosArticleMutationLocalDisposition.RetryWait)
                {
                    if (ack.Terminal || !ack.Retryable)
                        return "article_mutation_retry_ack_invalid";
                }
                else if (!ack.Terminal || ack.Retryable)
                {
                    return "article_mutation_terminal_ack_invalid";
                }
            }
            return null;
        }

        private static bool IsNullableUuid(string value)
        {
            return value == null || Guid.TryParse(value, out _);
        }

        private static bool TryParseUtc(string value)
        {
            DateTimeOffset parsed;
            return DateTimeOffset.TryParse(
                       value,
                       CultureInfo.InvariantCulture,
                       DateTimeStyles.RoundtripKind,
                       out parsed) &&
                   parsed.Offset == TimeSpan.Zero;
        }
    }
}
