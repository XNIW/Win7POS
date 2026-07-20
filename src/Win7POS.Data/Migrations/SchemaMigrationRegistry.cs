using System;
using System.Collections.Generic;

namespace Win7POS.Data.Migrations
{
    public static class SchemaMigrationRegistry
    {
        private static readonly IReadOnlyList<SchemaMigration> Registered =
            Array.AsReadOnly(new[]
            {
                new SchemaMigration(
                    "0001-core-pos-schema",
                    "Create the additive core POS, catalog, cart and security tables.",
                    @"0001-core-pos-schema/v1
tables=products,sales,sale_lines,app_settings,audit_log,suppliers,categories,product_meta,product_price_history,held_carts,held_cart_lines,roles,users,role_permissions,security_events
foreign-keys=sale_lines.saleId->sales.id:cascade;held_cart_lines.holdId->held_carts.holdId:cascade;users.role_id->roles.id;role_permissions.role_id->roles.id:cascade
operation=DbInitializer.CreateBaseTables
fingerprint=" + DbInitializer.CoreSchemaFingerprintSql + @"
allowed-schema=" + DbInitializer.PrBKnownSchemaSql + @"
sql=" + DbInitializer.CoreSchemaSql,
                    "bd7f3e733cdf867b40816757687e34a654ceee39a2d60ea6923dda6cb98591c6",
                    "1.0.0",
                    "Additive schema; older readers ignore tables and nullable/defaulted columns they do not use.",
                    true,
                    DbInitializer.CreateBaseTables,
                    HasBaseSchema),

                new SchemaMigration(
                    "0002-supported-legacy-columns",
                    "Add supported security, reversal, sync and remote-catalog columns.",
                    @"0002-supported-legacy-columns/v1
users=require_pin_change,max_discount_percent,last_login_at,failed_attempts,lockout_until,remote_staff_id,remote_staff_code,remote_shop_id,remote_shop_code,remote_role_key,remote_credential_version,remote_synced_at
sales=kind,related_sale_id,voided_by_sale_id,voided_at,reason,operator_id,pdf_printed,client_sale_id,sync_status
sale_lines=related_original_line_id
products=remote_product_id,remote_deleted_at,is_active
categories=remote_category_id,remote_updated_at,remote_deleted_at,is_active
suppliers=remote_supplier_id,remote_updated_at,remote_deleted_at,is_active
product_price_history=old_price,source,remote_price_id,catalog_import_client_item_id,catalog_import_idempotency_key
operation=DbInitializer.EnsureMigrations
definitions=" + DbInitializer.SupportedLegacyColumnMaterial,
                    "93008b229176205ed7c8d9c631739fb78e2166504012b4b9f277e1338d125d47",
                    "1.0.0",
                    "Additive columns only; no automatic downgrade or column removal is supported.",
                    true,
                    DbInitializer.EnsureMigrations,
                    HasSupportedLegacyColumns),

                new SchemaMigration(
                    "0003-outbox-catalog-evidence",
                    "Create sales/catalog outboxes and catalog exactness evidence tables.",
                    @"0003-outbox-catalog-evidence/v1
tables=local_stock_movements,sales_sync_outbox,catalog_import_outbox,remote_catalog_pending_prices,remote_catalog_product_references,remote_catalog_price_ownership,remote_catalog_price_evidence_quarantine
backfill=unambiguous pending remote-price ownership only; history conflicts remain fail-closed
operation=DbInitializer.CreateDependentTables
table-sql=" + DbInitializer.DependentSchemaSql + @"
alter-definitions=" + DbInitializer.DependentLegacyColumnMaterial + @"
ownership-sql=" + DbInitializer.RemoteOwnershipBackfillSql + @"
allowed-schema=" + DbInitializer.PrBKnownSchemaSql,
                    "dbc5dae94d81d82fd9043020712471731cb34c1e1d961e00348fcc5cec29eacd",
                    "1.0.0",
                    "Additive tables and conservative ownership evidence; downgrade requires restoring the verified backup.",
                    true,
                    DbInitializer.CreateDependentTables,
                    HasDependentSchemaAndOwnership),

                new SchemaMigration(
                    "0004-shop-bound-outbox-backfill",
                    "Normalize outbox contracts and fail closed on ambiguous legacy shop binding.",
                    @"0004-shop-bound-outbox-backfill/v1
sales=schema_version->pos-sales-ledger-v2;operation_type derived from immutable sale kind;origin_shop_code accepted only from hash-verified matching payload
catalog=schema_version->pos-catalog-import-v1;operation_type->catalog_import
ambiguous-active-outbox=status:failed_blocked,last_error_code:legacy_origin_ambiguous
payload-json-and-hash=immutable
operation=DbInitializer.BackfillLegacyOutboxBindings
implementation=" + DbInitializer.OutboxBackfillMaterial,
                    "649f49fbe75acf86ecfd354269df305fcece6b81a21e45a5de224f2377992a66",
                    "1.0.0",
                    "Forward-only safety classification; payloads and hashes stay immutable and rollback uses the verified backup.",
                    true,
                    DbInitializer.BackfillLegacyOutboxBindings,
                    HasSafeOutboxBindings),

                new SchemaMigration(
                    "0005-canonical-query-indexes",
                    "Create the canonical uniqueness, lookup and outbox scheduling indexes.",
                    @"0005-canonical-query-indexes/v1
indexes=idx_sale_lines_saleId,idx_sale_lines_barcode,idx_sales_createdAt,idx_sales_client_sale_id,idx_sales_client_sale_id_unique,idx_sales_sync_status,idx_local_stock_movements_sale,idx_local_stock_movements_barcode,idx_sales_sync_outbox_status_next,idx_sales_sync_outbox_sale,idx_sales_sync_outbox_last_attempt,idx_catalog_import_outbox_client_import,idx_catalog_import_outbox_idempotency,idx_catalog_import_outbox_status_next,idx_catalog_import_outbox_last_attempt,idx_audit_log_ts,idx_price_history_unique,idx_price_history_remote_price_id,idx_price_history_catalog_import_item,idx_pending_remote_price_id,idx_pending_remote_price_fallback,idx_pending_remote_price_product,idx_remote_price_ownership_product,idx_remote_price_quarantine_remote_id,idx_remote_product_refs_category,idx_remote_product_refs_supplier,idx_held_cart_lines_holdId,idx_security_events_ts,idx_products_remote_product_id,idx_products_active_barcode,idx_products_active_remote_product_id,idx_categories_remote_category_id,idx_categories_active_name,idx_suppliers_remote_supplier_id,idx_suppliers_active_name,idx_users_remote_staff_id,idx_users_remote_shop_staff
operation=DbInitializer.EnsureIndexes
sql=" + DbInitializer.CanonicalIndexSql,
                    "44afcce1cee8d87f0d68f1de472c18f0b5fb6ca474ee94c592d43cf71234da1a",
                    "1.0.0",
                    "Indexes are additive; older binaries remain readable, but uniqueness guarantees must not be downgraded automatically.",
                    true,
                    DbInitializer.EnsureIndexes,
                    HasCanonicalIndexes),

                new SchemaMigration(
                    "0006-system-role-permissions",
                    "Seed the immutable system roles and required permission grants.",
                    @"0006-system-role-permissions/v1
roles=admin,manager,supervisor,cashier
permissions=DbInitializer canonical role-permission matrix
users=no default administrator
operation=DbInitializer.SeedSecurity
matrix=" + DbInitializer.SecuritySeedMaterial,
                    "ade7405f309f563d6734bf5eaafd36df1f2ef6da8bd42ac9b910d1c51b783b8e",
                    "1.0.0",
                    "Additive system grants; no user or credential is created and downgrade requires administrative review.",
                    true,
                    DbInitializer.SeedSecurity,
                    detector => DbInitializer.IsSecuritySeedSatisfied(
                        detector.Connection,
                        detector.Transaction)),

                new SchemaMigration(
                    "0007-receipt-shop-snapshot",
                    "Add the immutable shop snapshot used by historical receipt reprints.",
                    @"0007-receipt-shop-snapshot/v1
sales=receipt_shop_snapshot:TEXT NULL
operation=DbInitializer.EnsureReceiptShopSnapshot
definition=" + DbInitializer.ReceiptShopSnapshotColumn.ToCanonicalMaterial() + @"
ledgerless-baseline=" + DbInitializer.PostPr7LedgerlessKnownSchemaSql + @"
ledgerless-invariants=supported-columns,dependent-ownership,safe-outbox,canonical-indexes,security-seed
postcondition=current-structural-schema",
                    "a1d12cca8bbfeb57872ee854e18cc32bf98258937d1f7be4be91d925f2ef6462",
                    "1.0.0",
                    "Additive nullable column; older readers ignore it, while downgrade requires restoring the verified backup.",
                    true,
                    DbInitializer.EnsureReceiptShopSnapshot,
                    IsPostPr7SchemaStructurallyValid,
                    IsRecognizedPostPr7LedgerlessBaseline),

                new SchemaMigration(
                    "0008-online-sync-generation",
                    "Fence online sync claims and commits to one trusted-session generation.",
                    @"0008-online-sync-generation/v1
table=pos_sync_session_generation:singleton active generation identity and auth-stop state
outbox-columns=claim_generation_id,claim_token on sales_sync_outbox and catalog_import_outbox
legacy-in-progress=release to retry without consuming an attempt
operation=DbInitializer.EnsureOnlineSyncGenerationSchema
table-sql=" + DbInitializer.OnlineSyncGenerationSchemaSql + @"
column-definitions=" + DbInitializer.OnlineSyncGenerationColumnMaterial + @"
alter-sql=" + DbInitializer.OnlineSyncGenerationAlterSql + @"
ledgerless-baseline=" + DbInitializer.PostSync2LedgerlessKnownSchemaSql + @"
postcondition=current-structural-schema",
                    "a951929521bdb7a73d82fcc308bd2e800ccb4888b6c16c829f51c2b93f49a488",
                    "1.0.0",
                    "Additive table and nullable columns; downgrade requires restoring the verified backup.",
                    true,
                    DbInitializer.EnsureOnlineSyncGenerationSchema,
                    IsCurrentSchemaStructurallyValid,
                    IsRecognizedPostSync2LedgerlessBaseline)
            });

        public static IReadOnlyList<SchemaMigration> All => Registered;

        public static SchemaMigration Latest => Registered[Registered.Count - 1];

        internal static bool IsCanonicalRegistry(IReadOnlyList<SchemaMigration> migrations)
        {
            if (migrations == null || migrations.Count != Registered.Count)
                return false;
            for (var index = 0; index < Registered.Count; index++)
            {
                if (!string.Equals(
                        migrations[index].MigrationId,
                        Registered[index].MigrationId,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        migrations[index].Checksum,
                        Registered[index].Checksum,
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
        }

        internal static bool IsCurrentSchemaStructurallyValid(LegacySchemaDetector detector)
        {
            if (detector == null)
                throw new ArgumentNullException(nameof(detector));
            return
                HasBaseSchema(detector, DbInitializer.PostSync2LedgerlessKnownSchemaSql) &&
                HasSupportedLegacyColumns(detector) &&
                HasDependentSchema(detector, DbInitializer.PostSync2LedgerlessKnownSchemaSql) &&
                HasCanonicalIndexes(detector) &&
                detector.ColumnMatchesDefinition(DbInitializer.ReceiptShopSnapshotColumn) &&
                HasOnlineSyncGenerationSchema(detector);
        }

        private static bool IsPostPr7SchemaStructurallyValid(LegacySchemaDetector detector)
        {
            if (detector == null)
                throw new ArgumentNullException(nameof(detector));
            return
                HasBaseSchema(detector, DbInitializer.PostPr7LedgerlessKnownSchemaSql) &&
                HasSupportedLegacyColumns(detector) &&
                HasDependentSchema(detector) &&
                HasCanonicalIndexes(detector) &&
                detector.ColumnMatchesDefinition(DbInitializer.ReceiptShopSnapshotColumn);
        }

        private static bool HasBaseSchema(LegacySchemaDetector detector)
        {
            return HasBaseSchema(detector, DbInitializer.PrBKnownSchemaSql);
        }

        private static bool HasBaseSchema(
            LegacySchemaDetector detector,
            string allowedSchemaSql)
        {
            return
                detector.HasKnownTableDefinitions(
                    DbInitializer.CoreSchemaFingerprintSql,
                    allowedSchemaSql,
                    "products", "sales", "sale_lines", "app_settings", "audit_log",
                    "suppliers", "categories", "product_meta", "product_price_history",
                    "held_carts", "held_cart_lines", "roles", "users", "role_permissions",
                    "security_events") &&
                detector.HasForeignKey("sale_lines", "saleId", "sales", "id", "CASCADE") &&
                detector.HasForeignKey("held_cart_lines", "holdId", "held_carts", "holdId", "CASCADE") &&
                detector.HasForeignKey("users", "role_id", "roles", "id", "NO ACTION") &&
                detector.HasForeignKey("role_permissions", "role_id", "roles", "id", "CASCADE");
        }

        private static bool HasSupportedLegacyColumns(LegacySchemaDetector detector)
        {
            return detector.HasAllColumnDefinitions(DbInitializer.SupportedLegacyColumns);
        }

        private static bool HasDependentSchemaAndOwnership(LegacySchemaDetector detector)
        {
            return
                HasDependentSchema(detector) &&
                HasRemotePriceOwnership(detector);
        }

        private static bool HasRemotePriceOwnership(LegacySchemaDetector detector)
        {
            return detector.NoRows(@"
SELECT 1
FROM (
  SELECT
    TRIM(pending.remote_price_id) AS remote_price_id,
    MIN(TRIM(pending.remote_product_id)) AS remote_product_id
  FROM remote_catalog_pending_prices pending
  WHERE TRIM(COALESCE(pending.remote_price_id, '')) <> ''
    AND TRIM(COALESCE(pending.remote_product_id, '')) <> ''
    AND NOT EXISTS (
      SELECT 1
      FROM product_price_history history
      WHERE TRIM(COALESCE(history.remote_price_id, '')) =
            TRIM(COALESCE(pending.remote_price_id, '')))
  GROUP BY TRIM(pending.remote_price_id)
  HAVING COUNT(DISTINCT TRIM(pending.remote_product_id)) = 1
) evidence
WHERE NOT EXISTS (
  SELECT 1
  FROM remote_catalog_price_ownership ownership
  WHERE ownership.remote_price_id = evidence.remote_price_id
    AND ownership.remote_product_id = evidence.remote_product_id)
LIMIT 1;");
        }

        private static bool HasDependentSchema(LegacySchemaDetector detector)
        {
            return HasDependentSchema(detector, DbInitializer.PrBKnownSchemaSql);
        }

        private static bool HasDependentSchema(
            LegacySchemaDetector detector,
            string allowedSchemaSql)
        {
            return
                detector.HasAllColumnDefinitions(DbInitializer.DependentLegacyColumns) &&
                detector.HasKnownTableDefinitions(
                    DbInitializer.DependentSchemaSql,
                    allowedSchemaSql,
                    "local_stock_movements", "sales_sync_outbox", "catalog_import_outbox",
                    "remote_catalog_pending_prices", "remote_catalog_product_references",
                    "remote_catalog_price_ownership", "remote_catalog_price_evidence_quarantine") &&
                detector.HasForeignKey("sales_sync_outbox", "sale_id", "sales", "id", "CASCADE");
        }

        private static bool HasOnlineSyncGenerationSchema(LegacySchemaDetector detector)
        {
            return
                detector.HasAllColumnDefinitions(DbInitializer.OnlineSyncGenerationColumns) &&
                detector.HasCanonicalTableDefinitions(
                    DbInitializer.OnlineSyncGenerationSchemaSql,
                    "pos_sync_session_generation");
        }

        private static bool HasSafeOutboxBindings(LegacySchemaDetector detector)
        {
            return detector.NoRows(@"
SELECT 1
FROM sales_sync_outbox outbox
WHERE (
    status IN ('pending', 'retry', 'in_progress')
    AND (
      COALESCE(schema_version, '') <> 'pos-sales-ledger-v2'
      OR COALESCE(operation_type, '') <> CASE COALESCE(
           (SELECT kind FROM sales WHERE sales.id = outbox.sale_id), 0)
           WHEN 1 THEN 'refund'
           WHEN 2 THEN 'void'
           ELSE 'sale'
         END
      OR TRIM(COALESCE(origin_shop_code, '')) = ''
    )
  )
   OR (
    status = 'failed_blocked'
    AND (
      (
        (COALESCE(schema_version, '') <> 'pos-sales-ledger-v2'
         OR COALESCE(operation_type, '') <> CASE COALESCE(
              (SELECT kind FROM sales WHERE sales.id = outbox.sale_id), 0)
              WHEN 1 THEN 'refund'
              WHEN 2 THEN 'void'
              ELSE 'sale'
            END)
        AND COALESCE(last_error_code, '') <> 'legacy_contract_mismatch'
      )
      OR (
        TRIM(COALESCE(origin_shop_code, '')) = ''
        AND COALESCE(last_error_code, '') NOT IN (
          'legacy_origin_ambiguous',
          'legacy_contract_mismatch')
      )
    )
  )
LIMIT 1;") &&
                detector.NoRows(@"
SELECT 1
FROM catalog_import_outbox
WHERE (
    status IN ('pending', 'retry', 'in_progress')
    AND (
      COALESCE(schema_version, '') <> 'pos-catalog-import-v1'
      OR COALESCE(operation_type, '') <> 'catalog_import'
      OR TRIM(COALESCE(origin_shop_code, '')) = ''
    )
  )
   OR (
    status = 'failed_blocked'
    AND (
      (
        (COALESCE(schema_version, '') <> 'pos-catalog-import-v1'
         OR COALESCE(operation_type, '') <> 'catalog_import')
        AND COALESCE(last_error_code, '') <> 'legacy_contract_mismatch'
      )
      OR (
        TRIM(COALESCE(origin_shop_code, '')) = ''
        AND COALESCE(last_error_code, '') NOT IN (
          'legacy_origin_ambiguous',
          'legacy_contract_mismatch')
      )
    )
  )
LIMIT 1;");
        }

        private static bool HasCanonicalIndexes(LegacySchemaDetector detector)
        {
            return detector.HasAllIndexDefinitions(DbInitializer.CanonicalIndexSql);
        }

        private static bool IsRecognizedPostPr7LedgerlessBaseline(LegacySchemaDetector detector)
        {
            return
                IsPostPr7SchemaStructurallyValid(detector) &&
                HasSupportedLegacyColumns(detector) &&
                HasDependentSchemaAndOwnership(detector) &&
                HasSafeOutboxBindings(detector) &&
                HasCanonicalIndexes(detector) &&
                DbInitializer.IsSecuritySeedSatisfied(
                    detector.Connection,
                    detector.Transaction);
        }

        private static bool IsRecognizedPostSync2LedgerlessBaseline(LegacySchemaDetector detector)
        {
            return
                IsCurrentSchemaStructurallyValid(detector) &&
                HasRemotePriceOwnership(detector) &&
                HasSafeOutboxBindings(detector) &&
                DbInitializer.IsSecuritySeedSatisfied(
                    detector.Connection,
                    detector.Transaction);
        }
    }
}
