using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Online;

namespace Win7POS.Data.Online
{
    // One immutable proof object per recovery plan, shared by every child.
    // Its exact JSON is persisted once; every child binds its SHA-256.
    [DataContract]
    internal sealed class CatalogImportCorrectionSharedProof
    {
        [DataMember(Name="originalRequest")] internal PosCatalogImportRequest OriginalRequest { get; set; }
        [DataMember(Name="originalReceipt")] internal PosCatalogImportReceiptResponse OriginalReceipt { get; set; }
        internal string Json { get; private set; }
        internal string Hash { get; private set; }

        internal static CatalogImportCorrectionSharedProof Create(PosCatalogImportRequest original, PosCatalogImportReceiptResponse receipt)
        {
            var proof=new CatalogImportCorrectionSharedProof { OriginalRequest=original,OriginalReceipt=receipt };
            proof.Json=CatalogImportRecoveryService.Serialize(proof);
            proof.Hash=CatalogImportOutboxPayloadBuilder.Sha256Hex(proof.Json);
            return proof;
        }
        internal static CatalogImportCorrectionSharedProof Parse(string json, string hash)
        {
            if (CatalogImportOutboxPayloadBuilder.Sha256Hex(json)!=hash) throw new CatalogImportRecoveryException("payload_hash_mismatch");
            var proof=CatalogImportRecoveryService.Deserialize<CatalogImportCorrectionSharedProof>(json);
            if (proof?.OriginalRequest?.Batch==null || proof.OriginalReceipt==null) throw new CatalogImportRecoveryException("receipt_required");
            proof.Json=json;proof.Hash=hash;return proof;
        }
        internal static async Task<CatalogImportCorrectionSharedProof> LoadAsync(SqliteConnection conn, SqliteTransaction tx, string payload,
            IDictionary<string,CatalogImportCorrectionSharedProof> cache=null)
        {
            var hash=CatalogImportCorrectionTransport.SharedProofHash(payload);
            if(hash==null) return null;
            if(cache!=null && cache.TryGetValue(hash,out var cached)) return cached;
            var row=await conn.QuerySingleOrDefaultAsync<StoredProof>(@"SELECT p.proof_json AS Json,o.payload_json AS OriginalJson,
o.payload_hash AS OriginalHash,o.client_import_id AS ClientImportId,o.idempotency_key AS IdempotencyKey,o.origin_shop_id AS ShopId
FROM catalog_import_correction_proof p JOIN catalog_import_outbox o ON o.id=p.original_id
WHERE p.proof_hash=@hash AND o.operation_type='catalog_import'",new { hash },tx).ConfigureAwait(false);
            if(row==null) throw new CatalogImportRecoveryException("receipt_required");
            var proof=Parse(row.Json,hash);
            if(CatalogImportOutboxPayloadBuilder.Sha256Hex(row.OriginalJson)!=row.OriginalHash ||
                proof.OriginalReceipt.PayloadHash!=row.OriginalHash || proof.OriginalReceipt.ShopId!=row.ShopId ||
                proof.OriginalRequest.Batch.ClientImportId!=row.ClientImportId || proof.OriginalRequest.Batch.IdempotencyKey!=row.IdempotencyKey)
                throw new CatalogImportRecoveryException("receipt_conflict");
            var original=CatalogImportRecoveryService.Deserialize<PosCatalogImportRequest>(row.OriginalJson);
            if(CatalogImportRecoveryService.Serialize(original)!=CatalogImportRecoveryService.Serialize(proof.OriginalRequest))
                throw new CatalogImportRecoveryException("receipt_conflict");
            if(cache!=null) cache.Add(hash,proof);
            return proof;
        }
        private sealed class StoredProof
        {
            public string Json { get; set; } public string OriginalJson { get; set; } public string OriginalHash { get; set; }
            public string ClientImportId { get; set; } public string IdempotencyKey { get; set; } public string ShopId { get; set; }
        }
        internal static async Task SaveAsync(SqliteConnection conn, SqliteTransaction tx, CatalogImportOutboxEntry entry)
        {
            var proof=entry.SharedProof;
            if(proof==null) return;
            if(CatalogImportCorrectionTransport.SharedProofHash(entry.PayloadJson)!=proof.Hash) throw new CatalogImportRecoveryException("payload_hash_mismatch");
            var found=await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM catalog_import_correction_proof WHERE proof_hash=@Hash",new { proof.Hash },tx).ConfigureAwait(false);
            if(found!=0) return;
            var root=proof.OriginalRequest.Batch;
            var rows=await conn.ExecuteAsync(@"INSERT INTO catalog_import_correction_proof(proof_hash,original_id,proof_json)
SELECT @hash,id,@json FROM catalog_import_outbox WHERE client_import_id=@ClientImportId AND idempotency_key=@IdempotencyKey AND payload_hash=@PayloadHash AND operation_type='catalog_import'",
                new { hash=proof.Hash,json=proof.Json,root.ClientImportId,root.IdempotencyKey,proof.OriginalReceipt.PayloadHash },tx).ConfigureAwait(false);
            if(rows!=1) throw new CatalogImportRecoveryException("receipt_conflict");
        }
    }
}
