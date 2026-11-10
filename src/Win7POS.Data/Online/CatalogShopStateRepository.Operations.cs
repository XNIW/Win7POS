using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Models;
using Win7POS.Core.Online;
using Win7POS.Core.Pos;
using Win7POS.Data.Repositories;

namespace Win7POS.Data.Online
{
    public sealed partial class CatalogShopStateRepository
    {
        public const string LocalCreationScopePrefix = "pos.catalog.local_creation_scope.";
        internal static bool AllowsLocalCatalogOperation(string reason) =>
            PosCatalogOperationPolicy.AllowsVerifiedLocalLine(reason);

        internal static async Task<bool> HasLocalCreateEvidenceAsync(SqliteConnection conn,
            SqliteTransaction tx, long productId)
        {
            var proof = await conn.QuerySingleOrDefaultAsync<LocalCreateProof>(@"
SELECT m.intent_json AS IntentJson, m.intent_hash AS IntentHash
FROM products p JOIN article_mutation_outbox m
  ON m.local_product_id = p.id AND m.client_product_id = p.client_product_id
WHERE p.id = @productId
  AND (SELECT value FROM app_settings WHERE key=@scopePrefix || p.client_product_id) =
      (SELECT value FROM app_settings WHERE key=@officialShopKey)
  AND COALESCE(p.is_active,1) = 1 AND p.remote_deleted_at IS NULL
  AND (NULLIF(TRIM(p.remote_product_id),'') IS NULL OR
       (m.state='completed' AND m.remote_assigned_product_id=p.remote_product_id
        AND m.ack_terminal=1 AND LENGTH(m.authoritative_revision)>0))
  AND NULLIF(p.client_product_id,'') IS NOT NULL
  AND m.mutation_kind IN ('product_create','product_duplicate')
  AND LENGTH(m.intent_json) BETWEEN 1 AND 262144 AND m.state <> 'failed_blocked'
ORDER BY m.local_sequence LIMIT 1;", new { productId, scopePrefix=LocalCreationScopePrefix,
                officialShopKey=OutboxShopBinding.OfficialShopIdKey }, tx).ConfigureAwait(false);
            return proof != null && string.Equals(proof.IntentHash,
                PosArticleMutationPayloadHash.Compute(new UTF8Encoding(false,true).GetBytes(proof.IntentJson)), StringComparison.Ordinal);
        }

        private sealed class LocalCreateProof
        {
            public string IntentJson { get; set; }
            public string IntentHash { get; set; }
        }

        internal static async Task RequireSaleSafeForOrdinarySaleAsync(SqliteConnection conn,
            SqliteTransaction tx, Sale sale, IReadOnlyList<SaleLine> lines, SaleAuthorizationCommitGuard guard)
        {
            var safety = await EvaluateSaleSafetyAsync(conn, tx, true).ConfigureAwait(false);
            if (safety.IsSaleSafe && !safety.IsCatalogBound) return; // Existing unlinked/local contract.
            OrdinarySaleEconomicsPolicy.EnsureValid(sale, lines);
            if (!safety.IsSaleSafe && (guard == null || !AllowsLocalCatalogOperation(safety.ReasonCode)))
                throw new InvalidOperationException(safety.ReasonCode);
            if (guard == null) throw new InvalidOperationException("sale_authorization_required");
            if (guard != null)
            {
                // Commit-time binding, in the same transaction as lines/stock/outbox. A
                // delayed response or shop switch cannot authorize a different generation.
                var current = await conn.ExecuteScalarAsync<long>(@"
SELECT COUNT(*) WHERE
 (SELECT value FROM app_settings WHERE key=@officialId)=@ShopId
 AND (SELECT value FROM app_settings WHERE key=@officialCode)=@ShopCode
 AND (EXISTS (SELECT 1 FROM pos_sync_session_generation g WHERE g.singleton_id=1 AND g.active=1
   AND g.generation_id=@GenerationId AND g.fingerprint=@GenerationFingerprint
   AND g.shop_id=@ShopId AND g.shop_code=@ShopCode)
 OR (@publishedSafe=1 AND NOT EXISTS (SELECT 1 FROM pos_sync_session_generation)));
-- A pre-supervisor linked installation can use its already verified remote
-- snapshot with the existing live authorization-use guard. Empty/local exemptions
-- require the durable generation; an existing stopped/mismatched row never falls back.",
                    new { guard.GenerationId, guard.GenerationFingerprint, guard.ShopId, guard.ShopCode,
                        publishedSafe = safety.IsSaleSafe ? 1 : 0,
                        officialId = OutboxShopBinding.OfficialShopIdKey, officialCode = OutboxShopBinding.OfficialShopCodeKey }, tx).ConfigureAwait(false);
                if (current != 1) throw new InvalidOperationException("sale_generation_changed");
            }
            var restore = await conn.ExecuteScalarAsync<string>(
                "SELECT value FROM app_settings WHERE key='pos.restore.needs_sync_review';", transaction: tx).ConfigureAwait(false);
            if (restore == "1") throw new InvalidOperationException("restore_needs_review");
            foreach (var line in lines)
            {
                if (DiscountKeys.IsEconomicAdjustment(line.Barcode)) continue;
                if (!line.ProductId.HasValue)
                {
                    if (line.ManualPriceEvidence?.Matches(line.ProductId, line.Barcode, line.UnitPrice) != true)
                        throw new InvalidOperationException("sale_manual_evidence_missing");
                    continue;
                }
                if (!safety.IsSaleSafe && !await HasLocalCreateEvidenceAsync(conn, tx, line.ProductId.Value).ConfigureAwait(false))
                    throw new InvalidOperationException(safety.ReasonCode);
                var product = await conn.QuerySingleOrDefaultAsync<Product>(@"
SELECT id,barcode,name,unitPrice FROM products
WHERE id=@id AND barcode=@barcode AND COALESCE(is_active,1)=1 AND remote_deleted_at IS NULL;",
                    new { id = line.ProductId.Value, barcode = line.Barcode }, tx).ConfigureAwait(false);
                if (product == null) throw new InvalidOperationException("sale_product_no_longer_available");
                // Never silently replace an operator's cart totals during price publication.
                if (product.UnitPrice != line.UnitPrice) throw new InvalidOperationException("sale_product_price_changed");
            }
        }
    }
}
