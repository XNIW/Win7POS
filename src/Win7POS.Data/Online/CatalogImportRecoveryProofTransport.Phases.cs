using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Win7POS.Core.Online;

namespace Win7POS.Data.Online
{
    internal static partial class CatalogImportRecoveryProofTransport
    {
        private static bool RequiresPhasedUpload(string rawJson) => rawJson != null &&
            Encoding.UTF8.GetByteCount(rawJson) > PosCatalogImportRecoveryMultipartContract.MaximumCompatibilityBytes;

        internal static async Task<PosOnlineResult<PosCatalogImportRecoveryMultipartResponse>> RegisterPlanAsync(
            PosAdminWebClient client, string rawJson, string uploadId, string verifiedOriginalId,
            Func<PosTrustedDeviceSession> freshSession, CancellationToken token, PosCatalogImportRecoveryPlanDocument planDocument = null)
        {
            if (!IsUuid(verifiedOriginalId)) throw new CatalogImportRecoveryException("payload_invalid");
            if (planDocument != null && (planDocument.VerifiedOriginalId != verifiedOriginalId || planDocument.Parts == null ||
                planDocument.Parts.Length > PosCatalogImportRecoveryMultipartContract.MaximumPlanChildren ||
                (planDocument.Supersedes?.RetiredChildren?.Length ?? 0) > PosCatalogImportRecoveryMultipartContract.MaximumPlanChildren))
                throw new CatalogImportRecoveryException("payload_invalid");
            var binding = new SessionBinding(freshSession);
            var phased = RequiresPhasedUpload(rawJson) || planDocument != null &&
                (planDocument.Parts.Length > PosCatalogImportRecoveryMultipartContract.MaximumUploadParts ||
                 (planDocument.Supersedes?.RetiredChildren?.Length ?? 0) > PosCatalogImportRecoveryMultipartContract.MaximumUploadParts);
            var manifest = BuildManifest(rawJson, token, phased);
            PosOnlineResult<PosCatalogImportRecoveryMultipartResponse> uploaded;
            if (!phased)
            {
                uploaded = await UploadCoreAsync(client, rawJson, manifest, uploadId, "plan", binding.Read, token, null, null).ConfigureAwait(false);
                // This specific server refusal precedes admission. Retry only
                // the transport envelope with the SAME immutable bytes and ID.
                phased = !uploaded.Success && uploaded.HttpStatus == 409 && uploaded.Code == "phased_upload_required";
                if (!phased && !uploaded.Success) return uploaded;
            }
            else uploaded = null;
            if (phased)
            {
                uploaded = await UploadPhasedAsync(client, rawJson, manifest, uploadId, "plan", binding.Read, token,
                    null, null, verifiedOriginalId).ConfigureAwait(false);
                if (!uploaded.Success) return uploaded;
                return await CompletePhasedAsync(client, uploadId, manifest.RawSha256, true, binding.Read, token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            return await client.CatalogImportRecoveryPlanAsync(Authenticate(new PosCatalogImportRecoveryHandleRequest
                { UploadId = uploadId }, binding.Read()), token).ConfigureAwait(false);
        }

        private static async Task<PosOnlineResult<PosCatalogImportRecoveryMultipartResponse>> UploadPhasedAsync(
            PosAdminWebClient client, string rawJson, UploadManifest manifest, string uploadId, string mode,
            Func<PosTrustedDeviceSession> freshSession, CancellationToken token,
            string originalKind, string declaredPayloadHash, string verifiedOriginalId)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (!IsUuid(uploadId) || manifest.Parts.Length < 1 || manifest.Parts.Length > PosCatalogImportRecoveryMultipartContract.MaximumPhasedUploadParts)
                throw new CatalogImportRecoveryException("payload_invalid");
            var manifestHash = PhasedManifestHash(mode, originalKind, declaredPayloadHash, verifiedOriginalId,
                manifest.TotalByteLength, manifest.RawSha256, manifest.Parts);
            var binding = new SessionBinding(freshSession);
            PosOnlineResult<PosCatalogImportRecoveryMultipartResponse> result;
            for (var offset = 0; offset < manifest.Parts.Length; offset += PosCatalogImportRecoveryMultipartContract.MaximumManifestPageParts)
            {
                token.ThrowIfCancellationRequested();
                var parts = manifest.Parts.Skip(offset).Take(PosCatalogImportRecoveryMultipartContract.MaximumManifestPageParts).ToArray();
                result = await client.CatalogImportRecoveryManifestAsync(Authenticate(new PosCatalogImportRecoveryManifestRequest
                {
                    UploadId = uploadId, Mode = mode, OriginalKind = originalKind, DeclaredPayloadHash = declaredPayloadHash,
                    VerifiedOriginalId = verifiedOriginalId, TotalByteLength = manifest.TotalByteLength, RawSha256 = manifest.RawSha256,
                    PartCount = manifest.Parts.Length, ManifestSha256 = manifestHash, Offset = offset, Parts = parts
                }, binding.Read()), token).ConfigureAwait(false);
                if (!result.Success) return result;
                var response = result.Value; ValidateCommon(response, binding);
                if (response.Status != "registering" || response.UploadId != uploadId || response.ManifestSha256 != manifestHash ||
                    response.NextOffset != offset + parts.Length || response.PartCount != manifest.Parts.Length ||
                    response.Complete != (offset + parts.Length == manifest.Parts.Length))
                    throw new CatalogImportRecoveryException("receipt_conflict");
            }
            token.ThrowIfCancellationRequested();
            result = await client.CatalogImportRecoverySealAsync(Authenticate(new PosCatalogImportRecoverySealRequest
                { UploadId = uploadId, ManifestSha256 = manifestHash }, binding.Read()), token).ConfigureAwait(false);
            if (!result.Success) return result;
            var sealedManifest = result.Value; ValidateCommon(sealedManifest, binding);
            if (sealedManifest.Status != "registered" || sealedManifest.UploadId != uploadId || sealedManifest.ManifestSha256 != manifestHash ||
                sealedManifest.PartCount != manifest.Parts.Length || sealedManifest.TotalByteLength != manifest.TotalByteLength || sealedManifest.RawSha256 != manifest.RawSha256)
                throw new CatalogImportRecoveryException("receipt_conflict");
            var index = 0;
            foreach (var bytes in ReadUtf8Chunks(rawJson))
            {
                token.ThrowIfCancellationRequested();
                result = await client.CatalogImportRecoveryBytesAsync(Authenticate(new PosCatalogImportRecoveryBytesRequest
                {
                    UploadId = uploadId, ManifestSha256 = manifestHash, PartIndex = index,
                    Sha256 = manifest.Parts[index].Sha256, ContentBase64 = Convert.ToBase64String(bytes)
                }, binding.Read()), token).ConfigureAwait(false);
                if (!result.Success) return result;
                var response = result.Value; ValidateCommon(response, binding);
                if (response.Status != "uploaded" || response.UploadId != uploadId || response.ManifestSha256 != manifestHash || response.PartIndex != index)
                    throw new CatalogImportRecoveryException("receipt_conflict");
                index++;
            }
            return result;
        }

        internal static string PhasedManifestHash(string mode, string originalKind, string declaredPayloadHash,
            string verifiedOriginalId, int totalByteLength, string rawHash, PosCatalogImportRecoveryUploadPart[] parts)
        {
            if ((mode != "original" && mode != "plan") || !IsHash(rawHash) || totalByteLength < 1 ||
                totalByteLength > PosCatalogImportRecoveryMultipartContract.MaximumPhasedRawBytes || parts == null ||
                parts.Length < 1 || parts.Length > PosCatalogImportRecoveryMultipartContract.MaximumPhasedUploadParts ||
                (mode == "original" ? (originalKind != "ordinary" && originalKind != "correction") ||
                    declaredPayloadHash == null || !DeclaredHash.IsMatch(declaredPayloadHash) || verifiedOriginalId != null :
                    !IsUuid(verifiedOriginalId) || originalKind != null || declaredPayloadHash != null))
                throw new CatalogImportRecoveryException("payload_invalid");
            // All interpolated strings are bounded ASCII identifiers/hashes.
            // This order is the published JSON.stringify manifest canonical form,
            // independent of the DataContract HTTP serializer's property order.
            var json = new StringBuilder("{\"mode\":\"").Append(mode).Append('"');
            if (mode == "original") json.Append(",\"originalKind\":\"").Append(originalKind).Append("\",\"declaredPayloadHash\":\"").Append(declaredPayloadHash).Append('"');
            else json.Append(",\"verifiedOriginalId\":\"").Append(verifiedOriginalId).Append('"');
            json.Append(",\"totalByteLength\":").Append(totalByteLength.ToString(CultureInfo.InvariantCulture))
                .Append(",\"rawSha256\":\"").Append(rawHash).Append("\",\"parts\":[");
            long total = 0;
            for (var index = 0; index < parts.Length; index++)
            {
                var part = parts[index];
                if (part == null || part.Index != index || part.ByteLength < 1 ||
                    part.ByteLength > PosCatalogImportRecoveryMultipartContract.MaximumRawPartBytes || !IsHash(part.Sha256))
                    throw new CatalogImportRecoveryException("payload_invalid");
                total += part.ByteLength;
                if (index != 0) json.Append(',');
                json.Append("{\"index\":").Append(index.ToString(CultureInfo.InvariantCulture)).Append(",\"byteLength\":")
                    .Append(part.ByteLength.ToString(CultureInfo.InvariantCulture)).Append(",\"sha256\":\"").Append(part.Sha256).Append("\"}");
            }
            if (total != totalByteLength) throw new CatalogImportRecoveryException("payload_invalid");
            return "sha256:" + CatalogImportOutboxPayloadBuilder.Sha256Hex(json.Append("]}").ToString());
        }

        private static async Task<PosOnlineResult<PosCatalogImportRecoveryMultipartResponse>> CompletePhasedAsync(
            PosAdminWebClient client, string uploadId, string rawHash, bool plan,
            Func<PosTrustedDeviceSession> freshSession, CancellationToken token)
        {
            var binding = new SessionBinding(freshSession);
            Func<PosCatalogImportRecoveryPhaseRequest, Task<PosOnlineResult<PosCatalogImportRecoveryMultipartResponse>>> send = request =>
                plan ? client.CatalogImportRecoveryPlanPhaseAsync(Authenticate(request, binding.Read()), token)
                     : client.CatalogImportRecoveryFinalizePhaseAsync(Authenticate(request, binding.Read()), token);
            token.ThrowIfCancellationRequested();
            var result = await send(new PosCatalogImportRecoveryPhaseRequest { Phase = "prepare", UploadId = uploadId }).ConfigureAwait(false);
            string previousStage = null;
            int? previousCursor = null;
            int? children = null, coverage = null;
            for (var call = 0; call <= 2 * PosCatalogImportRecoveryMultipartContract.MaximumOriginalItems + 2; call++)
            {
                token.ThrowIfCancellationRequested();
                if (!result.Success) return result;
                var response = result.Value; ValidateCommon(response, binding);
                if (response.Status == (plan ? "planned" : "verified")) return result;
                if (response.Status != "normalizing" || response.UploadId != uploadId || response.RawSha256 != rawHash ||
                    response.TotalItemCount < 1 || response.TotalItemCount > PosCatalogImportRecoveryMultipartContract.MaximumOriginalItems ||
                    !response.TotalItemCount.HasValue || !response.PartCount.HasValue || !response.TotalCoverageCount.HasValue)
                    throw new CatalogImportRecoveryException("receipt_conflict");
                if (plan)
                {
                    if (response.Phase != null || response.PartCount < 0 || response.PartCount > PosCatalogImportRecoveryMultipartContract.MaximumPlanChildren ||
                        response.TotalCoverageCount < 1 || response.TotalCoverageCount > PosCatalogImportRecoveryMultipartContract.MaximumOriginalItems ||
                        children.HasValue && response.PartCount != children || coverage.HasValue && response.TotalCoverageCount != coverage)
                        throw new CatalogImportRecoveryException("receipt_conflict");
                    children = response.PartCount; coverage = response.TotalCoverageCount;
                }
                else if (response.Stage != null || response.PartCount != 0 || response.TotalCoverageCount != 0)
                    throw new CatalogImportRecoveryException("receipt_conflict");
                var stage = plan ? response.Stage : response.Phase;
                if (stage == "complete")
                {
                    if (response.NextCursor.HasValue) throw new CatalogImportRecoveryException("receipt_conflict");
                    result = await send(new PosCatalogImportRecoveryPhaseRequest { Phase = "complete", UploadId = uploadId, RawSha256 = rawHash }).ConfigureAwait(false);
                    if (result.Success)
                    {
                        ValidateCommon(result.Value, binding);
                        if (result.Value.Status != (plan ? "planned" : "verified")) throw new CatalogImportRecoveryException("receipt_conflict");
                    }
                    return result;
                }
                var rank = PhaseRank(stage, plan);
                var cursor = response.NextCursor;
                if (rank < 0 || !cursor.HasValue || cursor < 0 ||
                    (stage == "items" || stage == "coverage") && cursor >= response.TotalItemCount ||
                    stage == "children" && (cursor / 1001 >= response.PartCount || cursor % 1001 >= 1000) ||
                    stage == "correction" && (cursor < 1000000 || cursor >= 1001000) ||
                    previousStage != null && (rank < PhaseRank(previousStage, plan) || stage == previousStage && cursor <= previousCursor))
                    throw new CatalogImportRecoveryException("receipt_conflict");
                previousStage = stage; previousCursor = cursor;
                result = await send(new PosCatalogImportRecoveryPhaseRequest
                    { Phase = "normalize", UploadId = uploadId, RawSha256 = rawHash, Cursor = cursor, Stage = plan ? stage : null }).ConfigureAwait(false);
            }
            throw new CatalogImportRecoveryException("receipt_incomplete");
        }

        private static int PhaseRank(string phase, bool plan) => phase == (plan ? "children" : "items") ? 0 :
            phase == (plan ? "coverage" : "correction") ? 1 : -1;

        // This reader is reserved for the phased path. The legacy serializer and
        // its established wire corpus remain unchanged. Only transport identity
        // and row count are projected here; Admin still parses every business
        // field and independently derives the complete canonical hash.
        internal static PhasedOriginalIdentity ReadPhasedIdentity(CatalogImportOutboxItem original,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (original == null || string.IsNullOrEmpty(original.PayloadJson) ||
                original.OperationType != "catalog_import" && original.OperationType != CatalogImportCorrectionTransport.OperationType)
                throw new CatalogImportRecoveryException("payload_invalid");
            ValidateSingleJsonDocument(original.PayloadJson, cancellationToken);
            try
            {
                using (var input = new Utf8ChunkReadStream(ReadUtf8Chunks(original.PayloadJson),
                    original.PayloadJson[0] == '\uFEFF' ? 3 : 0, cancellationToken))
                using (var reader = JsonReaderWriterFactory.CreateJsonReader(input, new XmlDictionaryReaderQuotas
                    { MaxDepth = 64, MaxStringContentLength = 4096, MaxArrayLength = 4096, MaxBytesPerRead = 4096, MaxNameTableCharCount = 16384 }))
                {
                    var correction = original.OperationType == CatalogImportCorrectionTransport.OperationType;
                    reader.MoveToContent();
                    var identity = ReadIdentityObject(reader, correction, true, cancellationToken);
                    if (!reader.EOF && reader.MoveToContent() != XmlNodeType.None)
                        throw new CatalogImportRecoveryException("payload_invalid");
                    var expectedSchema = correction ? PosCatalogImportCorrectionContract.SchemaVersion : PosOnlineContract.CatalogImportSchemaVersion;
                    if (original.SchemaVersion != expectedSchema || identity.SchemaVersion != expectedSchema || identity.ClientImportId != original.ClientImportId ||
                        identity.IdempotencyKey != original.IdempotencyKey || identity.ItemCount < 1 ||
                        identity.ItemCount > PosCatalogImportRecoveryMultipartContract.MaximumOriginalItems)
                        throw new CatalogImportRecoveryException("payload_invalid");
                    return identity;
                }
            }
            catch (XmlException) { throw new CatalogImportRecoveryException("payload_invalid"); }
            catch (System.Runtime.Serialization.SerializationException) { throw new CatalogImportRecoveryException("payload_invalid"); }
        }

        // XmlJsonReader stops at the first root even when another JSON value
        // follows it. Check the single-document boundary without buffering a
        // second copy; the JSON reader still validates the object's grammar.
        private static void ValidateSingleJsonDocument(string json, CancellationToken token)
        {
            var at = json[0] == '\uFEFF' ? 1 : 0;
            while (at < json.Length && JsonWhitespace(json[at])) at++;
            if (at == json.Length || json[at] != '{') throw new CatalogImportRecoveryException("payload_invalid");
            var depth = 0; var quoted = false; var escaped = false;
            for (; at < json.Length; at++)
            {
                if ((at & 4095) == 0) token.ThrowIfCancellationRequested();
                var value = json[at];
                if (quoted)
                {
                    if (escaped) escaped = false;
                    else if (value == '\\') escaped = true;
                    else if (value == '"') quoted = false;
                }
                else if (value == '"') quoted = true;
                else if (value == '{' || value == '[') depth++;
                else if ((value == '}' || value == ']') && --depth == 0)
                {
                    for (at++; at < json.Length; at++)
                    {
                        if ((at & 4095) == 0) token.ThrowIfCancellationRequested();
                        if (!JsonWhitespace(json[at])) throw new CatalogImportRecoveryException("payload_invalid");
                    }
                    return;
                }
            }
            throw new CatalogImportRecoveryException("payload_invalid");
        }

        private static bool JsonWhitespace(char value) => value == ' ' || value == '\t' || value == '\r' || value == '\n';

        private static PhasedOriginalIdentity ReadIdentityObject(XmlDictionaryReader reader, bool correction,
            bool allowWrapper, CancellationToken token)
        {
            DemandJsonType(reader, "object");
            var depth = reader.Depth;
            var identity = new PhasedOriginalIdentity();
            var fields = new HashSet<string>(StringComparer.Ordinal);
            PhasedOriginalIdentity wrapped = null;
            reader.Read();
            while (reader.MoveToContent() == XmlNodeType.Element && reader.Depth == depth + 1)
            {
                token.ThrowIfCancellationRequested();
                var name = reader.LocalName;
                if (name == "schemaVersion" || name == "batch" || name == "items" || name == "correction" || name == "request")
                    if (!fields.Add(name)) throw new CatalogImportRecoveryException("payload_invalid");
                if (name == "schemaVersion") identity.SchemaVersion = ReadIdentityString(reader);
                else if (name == (correction ? "correction" : "batch")) ReadOperationIdentity(reader, identity, correction, token);
                else if (!correction && name == "items") identity.ItemCount = CountIdentityItems(reader, token);
                else if (correction && allowWrapper && name == "request") wrapped = ReadIdentityObject(reader, true, false, token);
                else reader.Skip();
            }
            reader.ReadEndElement();
            if (wrapped == null) return identity;
            if (identity.SchemaVersion != null || identity.ClientImportId != null || identity.IdempotencyKey != null)
                throw new CatalogImportRecoveryException("payload_invalid");
            return wrapped;
        }

        private static void ReadOperationIdentity(XmlDictionaryReader reader, PhasedOriginalIdentity identity,
            bool correction, CancellationToken token)
        {
            DemandJsonType(reader, "object");
            var depth = reader.Depth;
            var fields = new HashSet<string>(StringComparer.Ordinal);
            reader.Read();
            while (reader.MoveToContent() == XmlNodeType.Element && reader.Depth == depth + 1)
            {
                token.ThrowIfCancellationRequested();
                var name = reader.LocalName;
                if (name == "clientImportId" || name == "idempotencyKey" || name == "items")
                    if (!fields.Add(name)) throw new CatalogImportRecoveryException("payload_invalid");
                if (name == "clientImportId") identity.ClientImportId = ReadIdentityString(reader);
                else if (name == "idempotencyKey") identity.IdempotencyKey = ReadIdentityString(reader);
                else if (correction && name == "items") identity.ItemCount = CountIdentityItems(reader, token);
                else reader.Skip();
            }
            reader.ReadEndElement();
        }

        private static int CountIdentityItems(XmlDictionaryReader reader, CancellationToken token)
        {
            DemandJsonType(reader, "array");
            var depth = reader.Depth;
            var count = 0;
            reader.Read();
            while (reader.MoveToContent() == XmlNodeType.Element && reader.Depth == depth + 1)
            {
                token.ThrowIfCancellationRequested();
                DemandJsonType(reader, "object");
                if (++count > PosCatalogImportRecoveryMultipartContract.MaximumOriginalItems)
                    throw new CatalogImportRecoveryException("payload_invalid");
                reader.Skip();
            }
            reader.ReadEndElement();
            return count;
        }

        private static string ReadIdentityString(XmlDictionaryReader reader)
        {
            DemandJsonType(reader, "string");
            return reader.ReadElementContentAsString();
        }

        private static void DemandJsonType(XmlDictionaryReader reader, string type)
        {
            if (reader.NodeType != XmlNodeType.Element || reader.GetAttribute("type") != type)
                throw new CatalogImportRecoveryException("payload_invalid");
        }

        internal sealed class PhasedOriginalIdentity
        {
            internal string SchemaVersion;
            internal string ClientImportId;
            internal string IdempotencyKey;
            internal int ItemCount;
        }

        // Feeds the XML-backed JSON reader with the same bounded UTF-8 encoder
        // used by upload. Stripping one BOM affects only the parser's view, never
        // the immutable raw JSON, upload stream, or raw hash.
        private sealed class Utf8ChunkReadStream : Stream
        {
            private readonly IEnumerator<byte[]> _chunks;
            private readonly CancellationToken _token;
            private byte[] _current;
            private int _offset;
            private int _leadingBytes;
            internal Utf8ChunkReadStream(IEnumerable<byte[]> chunks, int leadingBytes, CancellationToken token)
            { _chunks = chunks.GetEnumerator(); _leadingBytes = leadingBytes; _token = token; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (buffer == null) throw new ArgumentNullException(nameof(buffer));
                if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException();
                if (count == 0) return 0;
                _token.ThrowIfCancellationRequested();
                while (_current == null || _offset == _current.Length)
                {
                    if (!_chunks.MoveNext()) return 0;
                    _current = _chunks.Current; _offset = _leadingBytes; _leadingBytes = 0;
                }
                var copied = Math.Min(count, _current.Length - _offset);
                Buffer.BlockCopy(_current, _offset, buffer, offset, copied); _offset += copied;
                return copied;
            }
            protected override void Dispose(bool disposing) { if (disposing) _chunks.Dispose(); base.Dispose(disposing); }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
