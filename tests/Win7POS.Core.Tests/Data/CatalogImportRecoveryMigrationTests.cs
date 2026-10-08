using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Data;
using Win7POS.Data.Migrations;
using Win7POS.Data.Online;
using Win7POS.Core.Online;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class CatalogImportRecoveryMigrationTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void UpgradeFrom0012_PreservesLegacyImportsWithoutInventingDeliveryProof(bool ledgerless)
    {
        using var database = MigrationDatabase.Create();
        new SchemaMigrationRunner(database.Factory, SchemaMigrationRegistry.All.Take(12)).Run();
        string[] beforeImports;
        string[] beforeLedger;
        using (var connection = database.Factory.Open())
        {
            InsertOutbox(connection, 1, 0);
            InsertOutbox(connection, 2, 4);
            beforeImports = ReadImports(connection);
            beforeLedger = ReadLedger(connection);
            Assert.IsFalse(new LegacySchemaDetector(connection).TableExists("catalog_import_recovery"));
            Assert.IsFalse(new LegacySchemaDetector(connection).TableExists("catalog_import_recovery_contributions"));
            if (ledgerless)
                connection.Execute("DROP TABLE schema_migrations;");
        }

        var result = new SchemaMigrationRunner(database.Factory).Run();

        CollectionAssert.AreEqual(
            new[] { "0013-catalog-import-recovery" },
            result.AppliedMigrationIds.ToArray());
        Assert.AreEqual(ledgerless ? 12 : 0, result.BootstrappedMigrationIds.Count);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.BackupFileName));
        using (var verify = database.Factory.Open())
        {
            CollectionAssert.AreEqual(beforeImports, ReadImports(verify));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(*) FROM catalog_import_recovery;"),
                "A legacy attempt count of zero can follow a released dispatch and is not never-sent proof.");
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(*) FROM catalog_import_recovery_contributions;"));
            if (!ledgerless)
                CollectionAssert.AreEqual(beforeLedger, ReadLedger(verify).Take(12).ToArray());
            Assert.IsTrue(new LegacySchemaDetector(verify).HasCatalogImportRecoverySchema());
            Assert.IsTrue(SchemaMigrationRegistry.IsCurrentSchemaStructurallyValid(new LegacySchemaDetector(verify)));
        }
        Assert.IsTrue(new SchemaMigrationRunner(database.Factory).Run().WasNoOp);
    }

    [TestMethod]
    public void NewDatabaseAndCurrentLedgerlessBaseline_UseCanonical0013()
    {
        using var database = MigrationDatabase.Create();
        var first = new SchemaMigrationRunner(database.Factory).Run();
        Assert.AreEqual(13, first.AppliedMigrationIds.Count);
        Assert.AreEqual(0, first.BootstrappedMigrationIds.Count);
        using (var connection = database.Factory.Open())
        {
            var detector = new LegacySchemaDetector(connection);
            Assert.IsTrue(detector.HasCatalogImportRecoverySchema());
            Assert.IsTrue(SchemaMigrationRegistry.IsCurrentSchemaStructurallyValid(detector));
            Assert.AreEqual(0L, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM catalog_import_recovery;"));
            Assert.AreEqual(0L, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM catalog_import_recovery_contributions;"));
            connection.Execute("DROP TABLE schema_migrations;");
        }

        var bootstrapped = new SchemaMigrationRunner(database.Factory).Run();

        Assert.AreEqual(13, bootstrapped.BootstrappedMigrationIds.Count);
        Assert.AreEqual(0, bootstrapped.AppliedMigrationIds.Count);
        using var verify = database.Factory.Open();
        CollectionAssert.AreEqual(
            SchemaMigrationRegistry.All.Select(item => item.Checksum).ToArray(),
            verify.Query<string>("SELECT checksum FROM schema_migrations ORDER BY migration_id;").ToArray());
    }

    [TestMethod]
    public void EvidenceConstraints_RejectInvalidStatesDuplicateReplacementsAndDeletedOutboxRows()
    {
        using var database = MigrationDatabase.Create();
        DbInitializer.EnsureCreated(database.Options);
        using var connection = database.Factory.Open();
        for (var id = 1; id <= 4; id++)
            InsertOutbox(connection, id, 0);
        connection.Execute(@"
INSERT INTO catalog_import_recovery(
  original_id, delivery_known, dispatch_count, receipt_status,
  receipt_json, replacement_id, resolved_at, created_at, updated_at)
VALUES(1, 1, 2, 'accepted', '{""accepted"":true}', 2, 10, 1, 10);");

        foreach (var invalid in new[]
        {
            "INSERT INTO catalog_import_recovery(original_id, delivery_known, created_at, updated_at) VALUES(3, 2, 1, 1);",
            "INSERT INTO catalog_import_recovery(original_id, delivery_known, dispatch_count, created_at, updated_at) VALUES(3, 1, -1, 1, 1);",
            "INSERT INTO catalog_import_recovery(original_id, delivery_known, receipt_status, created_at, updated_at) VALUES(3, 1, 'unknown', 1, 1);",
            "INSERT INTO catalog_import_recovery(original_id, delivery_known, created_at, updated_at) VALUES(99, 0, 1, 1);",
            "INSERT INTO catalog_import_recovery(original_id, delivery_known, replacement_id, created_at, updated_at) VALUES(3, 0, 99, 1, 1);",
            "INSERT INTO catalog_import_recovery(original_id, delivery_known, replacement_id, created_at, updated_at) VALUES(3, 1, 2, 1, 1);",
            "INSERT INTO catalog_import_recovery(original_id, delivery_known, created_at, updated_at) VALUES(1, 0, 1, 1);",
            "INSERT INTO catalog_import_recovery(original_id, delivery_known, created_at, updated_at) VALUES(3, NULL, 1, 1);",
            "DELETE FROM catalog_import_outbox WHERE id = 1;",
            "DELETE FROM catalog_import_outbox WHERE id = 2;"
        })
        {
            var error = Assert.ThrowsExactly<SqliteException>(() => connection.Execute(invalid), invalid);
            Assert.AreEqual(19, error.SqliteErrorCode, invalid);
        }

        connection.Execute(@"
INSERT INTO catalog_import_recovery(original_id, delivery_known, created_at, updated_at)
VALUES(3, 0, 1, 1), (4, 0, 1, 1);
UPDATE catalog_import_recovery SET receipt_status = 'not_found' WHERE original_id = 3;");
        connection.Execute("UPDATE catalog_import_recovery SET receipt_status = 'retired' WHERE original_id = 3;");
        Assert.AreEqual("retired", connection.ExecuteScalar<string>(
            "SELECT receipt_status FROM catalog_import_recovery WHERE original_id = 3;"));
        Assert.AreEqual("0|unverified", connection.ExecuteScalar<string>(@"
SELECT dispatch_count || '|' || receipt_status
FROM catalog_import_recovery WHERE original_id = 4;"));
        Assert.AreEqual(3L, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM catalog_import_recovery;"));
        Assert.AreEqual(4L, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM catalog_import_outbox;"));
    }

    [TestMethod]
    public void ContributionConstraints_PreserveDistinctAcceptedPayloadsAndRestrictTheirOutboxOwners()
    {
        using var database = MigrationDatabase.Create();
        DbInitializer.EnsureCreated(database.Options);
        using var connection = database.Factory.Open();
        for (var id = 1; id <= 4; id++)
            InsertOutbox(connection, id, 0);
        connection.Execute(@"
INSERT INTO catalog_import_recovery_contributions(original_id, contributor_id, payload_hash, receipt_json, created_at)
VALUES(1, 2, 'hash-two', '{""accepted"":true}', 10),
      (1, 3, 'hash-three', '{""accepted"":true}', 11),
      (4, 2, 'hash-two', '{""accepted"":true}', 12);");

        foreach (var invalid in new[]
        {
            "INSERT INTO catalog_import_recovery_contributions VALUES(1, 2, 'other', '{}', 13);",
            "INSERT INTO catalog_import_recovery_contributions VALUES(99, 3, 'hash', '{}', 13);",
            "INSERT INTO catalog_import_recovery_contributions VALUES(1, 99, 'hash', '{}', 13);",
            "INSERT INTO catalog_import_recovery_contributions VALUES(NULL, 4, 'hash', '{}', 13);",
            "INSERT INTO catalog_import_recovery_contributions VALUES(1, NULL, 'hash', '{}', 13);",
            "INSERT INTO catalog_import_recovery_contributions VALUES(1, 4, NULL, '{}', 13);",
            "INSERT INTO catalog_import_recovery_contributions VALUES(1, 4, 'hash', NULL, 13);",
            "INSERT INTO catalog_import_recovery_contributions VALUES(1, 4, 'hash', '{}', NULL);",
            "DELETE FROM catalog_import_outbox WHERE id = 1;",
            "DELETE FROM catalog_import_outbox WHERE id = 2;"
        })
        {
            var error = Assert.ThrowsExactly<SqliteException>(() => connection.Execute(invalid), invalid);
            Assert.AreEqual(19, error.SqliteErrorCode, invalid);
        }

        CollectionAssert.AreEqual(new[] { "1|2|hash-two|10", "1|3|hash-three|11", "4|2|hash-two|12" },
            connection.Query<string>(@"
SELECT original_id || '|' || contributor_id || '|' || payload_hash || '|' || created_at
FROM catalog_import_recovery_contributions ORDER BY original_id, contributor_id;").ToArray());
        Assert.AreEqual(0L, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM catalog_import_recovery;"));
    }

    [TestMethod]
    [DataRow("CHECK(delivery_known IN (0,1))", "CHECK(delivery_known IN (0,1,2))")]
    [DataRow("CHECK(dispatch_count>=0)", "CHECK(dispatch_count>=-1)")]
    [DataRow("'not_found','accepted'", "'not_found','accepted','unknown'")]
    [DataRow("replacement_id INTEGER NULL UNIQUE", "replacement_id INTEGER NULL")]
    [DataRow("ON DELETE RESTRICT", "ON DELETE CASCADE")]
    [DataRow("DEFAULT 'unverified'", "DEFAULT 'accepted'")]
    [DataRow("'not_found','accepted'", "'not_found','ACCEPTED'")]
    [DataRow("PRIMARY KEY(original_id,contributor_id)", "PRIMARY KEY(contributor_id)")]
    [DataRow("contributor_id INTEGER NOT NULL REFERENCES", "contributor_id INTEGER NULL REFERENCES")]
    [DataRow("payload_hash TEXT NOT NULL", "payload_hash TEXT NULL")]
    [DataRow("receipt_json TEXT NOT NULL", "receipt_json TEXT NULL")]
    public void CurrentSchema_RejectsChangedEvidenceGuards(string original, string replacement)
    {
        using var database = MigrationDatabase.Create();
        DbInitializer.EnsureCreated(database.Options);
        using (var connection = database.Factory.Open())
        {
            connection.Execute("DROP TABLE catalog_import_recovery_contributions; DROP TABLE catalog_import_recovery;");
            connection.Execute(DbInitializer.CatalogImportRecoverySchemaSql.Replace(original, replacement));
            var detector = new LegacySchemaDetector(connection);
            Assert.IsFalse(detector.HasCatalogImportRecoverySchema());
            Assert.IsFalse(SchemaMigrationRegistry.IsCurrentSchemaStructurallyValid(detector));
        }

        Assert.ThrowsExactly<InvalidDataException>(() => DbInitializer.EnsureCreated(database.Options));
    }

    [TestMethod]
    public void CurrentSchema_RequiresContributionTableEvenWhenRecoveryTableAndLedgerExist()
    {
        using var database = MigrationDatabase.Create();
        DbInitializer.EnsureCreated(database.Options);
        using (var connection = database.Factory.Open())
        {
            connection.Execute("DROP TABLE catalog_import_recovery_contributions;");
            var detector = new LegacySchemaDetector(connection);
            Assert.IsFalse(detector.HasCatalogImportRecoverySchema());
            Assert.IsFalse(SchemaMigrationRegistry.IsCurrentSchemaStructurallyValid(detector));
        }

        Assert.ThrowsExactly<InvalidDataException>(() => DbInitializer.EnsureCreated(database.Options));
    }

    [TestMethod]
    [DataRow("valid")]
    [DataRow("payload")]
    [DataRow("hash")]
    [DataRow("link")]
    [DataRow("chain")]
    [DataRow("chain_accepted")]
    [DataRow("chain_fence")]
    [DataRow("chain_schema")]
    [DataRow("chain_fence_hash")]
    [DataRow("chain_root")]
    [DataRow("chain_cycle")]
    [DataRow("chain_depth")]
    public void CurrentLedgerlessCorrection_PreservesValidPendingAndRejectsCorruptProofBeforeLegacyBackfill(string condition)
    {
        using var database = MigrationDatabase.Create();
        DbInitializer.EnsureCreated(database.Options);
        string before;
        using (var connection = database.Factory.Open())
        {
            var correction = CatalogImportRecoveryService.Deserialize<PosCatalogImportCorrectionRequest>(FixtureText("correction.request.json"));
            var proof = CatalogImportRecoveryService.Deserialize<PosCatalogImportReceiptResponse>(FixtureText("accepted.response.json"));
            proof.OriginalSchemaVersion = PosOnlineContract.CatalogImportSchemaVersion;
            var original = correction.RecoveryOf.OriginalRequest;
            original.PayloadHash = null;
            var originalJson = CatalogImportRecoveryService.Serialize(original);
            var originalHash = CatalogImportOutboxPayloadBuilder.Sha256Hex(originalJson);
            correction.RecoveryOf.PayloadHash = originalHash;
            proof.PayloadHash = originalHash;
            var correctionJson = CatalogImportCorrectionTransport.SerializeSaved(correction, proof);
            connection.Execute(@"
INSERT INTO catalog_import_outbox(id,client_import_id,idempotency_key,schema_version,operation_type,origin_shop_id,origin_shop_code,
payload_json,payload_hash,status,last_error_code,created_at,updated_at)
VALUES(1,@originalId,@originalKey,'pos-catalog-import-v1','catalog_import',@shopId,'FIXTURE',@originalJson,@originalHash,'failed_blocked','invalid_payload',1,2),
(2,@correctionId,@correctionKey,'pos-catalog-import-correction-v1','catalog_import_correction',@shopId,'FIXTURE',@correctionJson,@correctionHash,'pending',NULL,3,4);
INSERT INTO catalog_import_recovery(original_id,delivery_known,dispatch_count,receipt_status,receipt_json,replacement_id,created_at,updated_at)
VALUES(1,0,1,'accepted',@proofJson,2,1,4);",
                new { originalId=original.Batch.ClientImportId,originalKey=original.Batch.IdempotencyKey,shopId=proof.ShopId,
                    originalJson,originalHash,correctionId=correction.Correction.ClientImportId,correctionKey=correction.Correction.IdempotencyKey,
                    correctionJson,correctionHash=CatalogImportOutboxPayloadBuilder.Sha256Hex(correctionJson),proofJson=CatalogImportRecoveryService.Serialize(proof) });
            if (condition.StartsWith("chain", StringComparison.Ordinal))
            {
                var parentHash = CatalogImportOutboxPayloadBuilder.Sha256Hex(correctionJson);
                var retirement = CatalogImportRecoveryService.Deserialize<PosCatalogImportReceiptResponse>(FixtureText("retired.response.json"));
                retirement.OriginalSchemaVersion = PosCatalogImportCorrectionContract.SchemaVersion;
                retirement.ClientImportId = correction.Correction.ClientImportId;
                retirement.IdempotencyKey = correction.Correction.IdempotencyKey;
                retirement.PayloadHash = parentHash;
                correction.Correction.ClientImportId += "-next";
                correction.Correction.IdempotencyKey += "-next";
                if (condition == "chain_root") correction.RecoveryOf.OriginalRequest.Items[0].ProductName += "tampered";
                var childJson = CatalogImportCorrectionTransport.SerializeSaved(correction, proof);
                var childHash = CatalogImportOutboxPayloadBuilder.Sha256Hex(childJson);
                if (condition == "chain_fence") retirement.OldIdentityBlocked = false;
                if (condition == "chain_schema") retirement.OriginalSchemaVersion = PosOnlineContract.CatalogImportSchemaVersion;
                if (condition == "chain_fence_hash") retirement.PayloadHash = new string('f', 64);
                connection.Execute(@"
UPDATE catalog_import_outbox SET status='failed_blocked',last_error_code='revision_conflict' WHERE id=2;
INSERT INTO catalog_import_outbox(id,client_import_id,idempotency_key,schema_version,operation_type,origin_shop_id,origin_shop_code,
payload_json,payload_hash,status,last_error_code,created_at,updated_at)
VALUES(3,@childId,@childKey,'pos-catalog-import-correction-v1','catalog_import_correction',@shopId,'FIXTURE',@childJson,@childHash,'pending',NULL,5,6);
INSERT INTO catalog_import_recovery(original_id,delivery_known,dispatch_count,receipt_status,receipt_json,replacement_id,created_at,updated_at)
VALUES(2,0,1,'retired',@retirement,3,3,6);",
                    new { childId=correction.Correction.ClientImportId,childKey=correction.Correction.IdempotencyKey,
                        shopId=proof.ShopId,childJson,childHash,retirement=CatalogImportRecoveryService.Serialize(retirement) });
                if (condition == "chain_accepted")
                {
                    // A fetched accepted child remains blocked until explicit
                    // local reconciliation; its own receipt is not a new link.
                    proof.ClientImportId = correction.Correction.ClientImportId;
                    proof.IdempotencyKey = correction.Correction.IdempotencyKey;
                    proof.PayloadHash = childHash;
                    connection.Execute(@"UPDATE catalog_import_outbox SET status='failed_blocked',last_error_code='transport_error' WHERE id=3;
INSERT INTO catalog_import_recovery(original_id,delivery_known,dispatch_count,receipt_status,receipt_json,created_at,updated_at)
VALUES(3,0,1,'accepted',@proof,5,6);", new { proof=CatalogImportRecoveryService.Serialize(proof) });
                }
                if (condition == "chain_cycle")
                    connection.Execute("UPDATE catalog_import_recovery SET replacement_id=NULL WHERE original_id=1; UPDATE catalog_import_recovery SET replacement_id=2 WHERE original_id=2;");
                if (condition == "chain_depth")
                {
                    for (var id = 4; id <= 130; id++)
                    {
                        var priorId = id - 1;
                        retirement.ClientImportId = correction.Correction.ClientImportId;
                        retirement.IdempotencyKey = correction.Correction.IdempotencyKey;
                        retirement.PayloadHash = childHash;
                        correction.Correction.ClientImportId = "chain-correction-" + id;
                        correction.Correction.IdempotencyKey = "chain-correction-key-" + id;
                        childJson = CatalogImportCorrectionTransport.SerializeSaved(correction, proof);
                        childHash = CatalogImportOutboxPayloadBuilder.Sha256Hex(childJson);
                        connection.Execute(@"
UPDATE catalog_import_outbox SET status='failed_blocked',last_error_code='revision_conflict' WHERE id=@priorId;
INSERT INTO catalog_import_outbox(id,client_import_id,idempotency_key,schema_version,operation_type,origin_shop_id,origin_shop_code,
payload_json,payload_hash,status,created_at,updated_at)
VALUES(@id,@childId,@childKey,'pos-catalog-import-correction-v1','catalog_import_correction',@shopId,'FIXTURE',@childJson,@childHash,'pending',5,6);
INSERT INTO catalog_import_recovery(original_id,delivery_known,dispatch_count,receipt_status,receipt_json,replacement_id,created_at,updated_at)
VALUES(@priorId,0,1,'retired',@retirement,@id,3,6);",
                            new { id,priorId,childId=correction.Correction.ClientImportId,childKey=correction.Correction.IdempotencyKey,
                                shopId=proof.ShopId,childJson,childHash,retirement=CatalogImportRecoveryService.Serialize(retirement) });
                    }
                }
            }
            if (condition == "payload") connection.Execute("UPDATE catalog_import_outbox SET payload_json=payload_json || ' ' WHERE id=2;");
            if (condition == "hash") connection.Execute("UPDATE catalog_import_outbox SET payload_hash='wrong' WHERE id=2;");
            if (condition == "link") connection.Execute("DELETE FROM catalog_import_recovery;");
            connection.Execute("DROP TABLE schema_migrations;");
            before = CorrectionRows(connection);
        }

        var valid = condition == "valid" || condition == "chain" || condition == "chain_accepted";
        if (valid)
        {
            var result = new SchemaMigrationRunner(database.Factory).Run();
            Assert.AreEqual(13, result.BootstrappedMigrationIds.Count);
            Assert.AreEqual(0, result.AppliedMigrationIds.Count);
        }
        else
        {
            Assert.ThrowsExactly<InvalidDataException>(() => new SchemaMigrationRunner(database.Factory).Run());
        }
        using var verify = database.Factory.Open();
        Assert.AreEqual(before, CorrectionRows(verify), "No current correction may be reclassified by historical 0004 backfill.");
        Assert.AreEqual(valid, new LegacySchemaDetector(verify).TableExists("schema_migrations"));
    }

    private static string CorrectionRows(SqliteConnection connection) => string.Join("\n", connection.Query<string>(@"
SELECT id || '|' || client_import_id || '|' || idempotency_key || '|' || payload_json || '|' || payload_hash || '|' || status || '|' ||
COALESCE(last_error_code,'') || '|' || created_at || '|' || updated_at FROM catalog_import_outbox ORDER BY id;").ToArray());

    private static string FixtureText(string name)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "Win7POS.slnx"))) root = root.Parent;
        if (root == null) throw new DirectoryNotFoundException();
        return File.ReadAllText(Path.Combine(root.FullName, "tests", "fixtures", "pos-catalog-import-receipt-v1", name));
    }

    [TestMethod]
    public void MalformedLedgerless0013_CannotBootstrapOrCommitDeliveryEvidenceMigration()
    {
        using var database = MigrationDatabase.Create();
        DbInitializer.EnsureCreated(database.Options);
        using (var connection = database.Factory.Open())
        {
            connection.Execute("DROP TABLE schema_migrations; DROP TABLE catalog_import_recovery_contributions; DROP TABLE catalog_import_recovery;");
            connection.Execute(DbInitializer.CatalogImportRecoverySchemaSql.Replace(
                "CHECK(delivery_known IN (0,1))", "CHECK(delivery_known IN (0,1,2))"));
        }

        Assert.ThrowsExactly<InvalidDataException>(() => new SchemaMigrationRunner(database.Factory).Run());

        using var verify = database.Factory.Open();
        Assert.AreEqual(0L, verify.ExecuteScalar<long>(@"
SELECT COUNT(*) FROM schema_migrations WHERE migration_id = '0013-catalog-import-recovery';"));
        Assert.AreEqual(12L, verify.ExecuteScalar<long>("SELECT COUNT(*) FROM schema_migrations;"));
    }

    private static void InsertOutbox(SqliteConnection connection, int id, int attemptCount)
    {
        connection.Execute(@"
INSERT INTO catalog_import_outbox(
  id, client_import_id, idempotency_key, origin_shop_id, origin_shop_code,
  payload_json, payload_hash, status, attempt_count, last_error_code,
  created_at, updated_at)
VALUES(@id, @clientId, @key, 'migration-shop', 'TEST',
  @payload, @hash, 'failed_blocked', @attemptCount, 'invalid_payload', 10, 20);",
            new
            {
                id,
                clientId = "migration-import-" + id,
                key = "migration-idempotency-" + id,
                payload = "{\"legacy\":" + id + "}",
                hash = "legacy-hash-" + id,
                attemptCount
            });
    }

    private static string[] ReadImports(SqliteConnection connection) => connection.Query<string>(@"
SELECT id || '|' || client_import_id || '|' || idempotency_key || '|' || origin_shop_code || '|' ||
       payload_json || '|' || payload_hash || '|' || status || '|' || attempt_count || '|' ||
       last_error_code || '|' || created_at || '|' || updated_at
FROM catalog_import_outbox ORDER BY id;").ToArray();

    private static string[] ReadLedger(SqliteConnection connection) => connection.Query<string>(@"
SELECT migration_id || '|' || checksum || '|' || description || '|' || applied_at || '|' || COALESCE(app_version, '')
FROM schema_migrations ORDER BY migration_id;").ToArray();

    private sealed class MigrationDatabase : IDisposable
    {
        private readonly string _root;

        private MigrationDatabase(string root)
        {
            _root = root;
            Options = PosDbOptions.ForPath(Path.Combine(root, "pos.db"));
            Factory = new SqliteConnectionFactory(Options);
        }

        public PosDbOptions Options { get; }
        public SqliteConnectionFactory Factory { get; }

        public static MigrationDatabase Create()
        {
            SQLitePCL.Batteries_V2.Init();
            var root = Path.Combine(Path.GetTempPath(), "win7pos-catalog-recovery-migration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new MigrationDatabase(root);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_root, true); } catch { }
        }
    }
}
