using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Win7POS.Data.Migrations
{
    public sealed class LegacySchemaDetector
    {
        private static readonly Regex IdentifierPattern = new Regex(
            "^[A-Za-z_][A-Za-z0-9_]*$",
            RegexOptions.CultureInvariant);

        private readonly SqliteConnection _connection;
        private readonly SqliteTransaction _transaction;

        public LegacySchemaDetector(
            SqliteConnection connection,
            SqliteTransaction transaction = null)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _transaction = transaction;
        }

        internal SqliteConnection Connection => _connection;
        internal SqliteTransaction Transaction => _transaction;

        public bool TableExists(string tableName)
        {
            ValidateIdentifier(tableName, nameof(tableName));
            return _connection.ExecuteScalar<long>(@"
SELECT COUNT(1)
FROM sqlite_master
WHERE type = 'table'
  AND name = @tableName;",
                new { tableName },
                _transaction) == 1;
        }

        public bool IndexExists(string indexName)
        {
            ValidateIdentifier(indexName, nameof(indexName));
            return _connection.ExecuteScalar<long>(@"
SELECT COUNT(1)
FROM sqlite_master
WHERE type = 'index'
  AND name = @indexName;",
                new { indexName },
                _transaction) == 1;
        }

        public bool HasAllColumns(string tableName, params string[] columnNames)
        {
            if (!TableExists(tableName))
                return false;
            if (columnNames == null)
                throw new ArgumentNullException(nameof(columnNames));

            var actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var command = _connection.CreateCommand())
            {
                command.Transaction = _transaction;
                command.CommandText = "PRAGMA table_info(" + QuoteIdentifier(tableName) + ");";
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                        actual.Add(Convert.ToString(reader.GetValue(1)) ?? string.Empty);
                }
            }

            foreach (var columnName in columnNames)
            {
                ValidateIdentifier(columnName, nameof(columnNames));
                if (!actual.Contains(columnName))
                    return false;
            }

            return true;
        }

        public bool HasAllColumnDefinitions(IEnumerable<SchemaColumnDefinition> definitions)
        {
            if (definitions == null)
                throw new ArgumentNullException(nameof(definitions));
            foreach (var definition in definitions)
            {
                if (definition == null || !ColumnMatchesDefinition(definition))
                    return false;
            }
            return true;
        }

        public bool HasCanonicalTableDefinitions(
            string canonicalSchemaSql,
            params string[] tableNames)
        {
            return HasKnownTableDefinitions(
                canonicalSchemaSql,
                canonicalSchemaSql,
                tableNames);
        }

        public bool HasKnownTableDefinitions(
            string requiredSchemaSql,
            string allowedSchemaSql,
            params string[] tableNames)
        {
            if (string.IsNullOrWhiteSpace(requiredSchemaSql))
                throw new ArgumentException("Required schema SQL is required.", nameof(requiredSchemaSql));
            if (string.IsNullOrWhiteSpace(allowedSchemaSql))
                throw new ArgumentException("Allowed schema SQL is required.", nameof(allowedSchemaSql));
            if (tableNames == null || tableNames.Length == 0)
                throw new ArgumentException("At least one canonical table is required.", nameof(tableNames));

            using (var required = new SqliteConnection("Data Source=:memory:"))
            using (var allowed = new SqliteConnection("Data Source=:memory:"))
            {
                required.Open();
                required.Execute(requiredSchemaSql);
                allowed.Open();
                allowed.Execute(allowedSchemaSql);

                foreach (var tableName in tableNames)
                {
                    ValidateIdentifier(tableName, nameof(tableNames));
                    if (!TableExists(tableName))
                        return false;

                    var expectedColumns = ReadColumnShapes(required, null, tableName);
                    var allowedColumns = ReadColumnShapes(allowed, null, tableName);
                    var actualColumns = ReadColumnShapes(_connection, _transaction, tableName);
                    foreach (var expected in expectedColumns.Values)
                    {
                        TableColumnShape actual;
                        if (!actualColumns.TryGetValue(expected.Name, out actual) ||
                            !actual.Matches(expected))
                        {
                            return false;
                        }
                    }

                    foreach (var actual in actualColumns.Values)
                    {
                        TableColumnShape allowedColumn;
                        if (!allowedColumns.TryGetValue(actual.Name, out allowedColumn) ||
                            !actual.Matches(allowedColumn))
                        {
                            return false;
                        }
                    }

                    var expectedUniqueKeys = ReadUniqueKeySignatures(required, null, tableName);
                    var allowedUniqueKeys = ReadUniqueKeySignatures(allowed, null, tableName);
                    var actualUniqueKeys = ReadUniqueKeySignatures(_connection, _transaction, tableName);
                    if (expectedUniqueKeys.Any(key => !actualUniqueKeys.Contains(key)) ||
                        actualUniqueKeys.Any(key => !allowedUniqueKeys.Contains(key)))
                    {
                        return false;
                    }

                    var expectedForeignKeys = ReadForeignKeySignatures(required, null, tableName);
                    var allowedForeignKeys = ReadForeignKeySignatures(allowed, null, tableName);
                    var actualForeignKeys = ReadForeignKeySignatures(_connection, _transaction, tableName);
                    if (expectedForeignKeys.Any(key => !actualForeignKeys.Contains(key)) ||
                        actualForeignKeys.Any(key => !allowedForeignKeys.Contains(key)))
                    {
                        return false;
                    }

                    var expectedIndexes = ReadExplicitIndexDefinitions(required, null, tableName);
                    var allowedIndexes = ReadExplicitIndexDefinitions(allowed, null, tableName);
                    var actualIndexes = ReadExplicitIndexDefinitions(_connection, _transaction, tableName);
                    if (expectedIndexes.Any(index => !actualIndexes.Contains(index)) ||
                        actualIndexes.Any(index => !allowedIndexes.Contains(index)))
                    {
                        return false;
                    }

                    var expectedTriggers = ReadTriggerDefinitions(required, null, tableName);
                    var allowedTriggers = ReadTriggerDefinitions(allowed, null, tableName);
                    var actualTriggers = ReadTriggerDefinitions(_connection, _transaction, tableName);
                    if (expectedTriggers.Any(trigger => !actualTriggers.Contains(trigger)) ||
                        actualTriggers.Any(trigger => !allowedTriggers.Contains(trigger)) ||
                        !HasAllowedTableGuards(
                            required,
                            allowed,
                            _connection,
                            _transaction,
                            tableName))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        public bool ColumnMatchesDefinition(SchemaColumnDefinition definition)
        {
            if (definition == null)
                throw new ArgumentNullException(nameof(definition));
            ValidateIdentifier(definition.TableName, nameof(definition));
            ValidateIdentifier(definition.ColumnName, nameof(definition));
            if (!TableExists(definition.TableName))
                return false;

            using (var command = _connection.CreateCommand())
            {
                command.Transaction = _transaction;
                command.CommandText = "PRAGMA table_info(" + QuoteIdentifier(definition.TableName) + ");";
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (!string.Equals(
                                Convert.ToString(reader.GetValue(1)),
                                definition.ColumnName,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var actualType = Convert.ToString(reader.GetValue(2)) ?? string.Empty;
                        var actualNotNull = Convert.ToInt32(reader.GetValue(3)) == 1;
                        var actualDefault = reader.IsDBNull(4)
                            ? string.Empty
                            : Convert.ToString(reader.GetValue(4)) ?? string.Empty;
                        if (!string.Equals(
                                NormalizeDeclaredType(actualType),
                                definition.DeclaredType,
                                StringComparison.Ordinal))
                        {
                            return false;
                        }
                        if (actualNotNull != definition.IsNotNull)
                            return false;
                        if (!string.Equals(
                                NormalizeDefaultValue(actualDefault),
                                NormalizeDefaultValue(definition.DefaultValue),
                                StringComparison.Ordinal))
                        {
                            return false;
                        }
                        return true;
                    }
                }
            }

            return false;
        }

        public bool HasAllIndexes(params string[] indexNames)
        {
            if (indexNames == null)
                throw new ArgumentNullException(nameof(indexNames));
            foreach (var indexName in indexNames)
            {
                if (!IndexExists(indexName))
                    return false;
            }
            return true;
        }

        public bool HasAllIndexDefinitions(string createIndexSql)
        {
            if (string.IsNullOrWhiteSpace(createIndexSql))
                throw new ArgumentException("Index SQL is required.", nameof(createIndexSql));

            var statements = createIndexSql
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(statement => statement.Trim())
                .Where(statement => statement.Length > 0)
                .ToArray();
            if (statements.Length == 0)
                return false;

            foreach (var statement in statements)
            {
                if (!IndexMatchesDefinition(statement + ";"))
                    return false;
            }

            return true;
        }

        public bool IndexMatchesDefinition(string createIndexSql)
        {
            if (string.IsNullOrWhiteSpace(createIndexSql))
                throw new ArgumentException("Index SQL is required.", nameof(createIndexSql));

            var match = Regex.Match(
                createIndexSql,
                @"^\s*CREATE\s+(?:UNIQUE\s+)?INDEX\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<name>[A-Za-z_][A-Za-z0-9_]*)\s+ON\s+",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            if (!match.Success)
                throw new ArgumentException("Unsupported canonical index definition.", nameof(createIndexSql));

            var indexName = match.Groups["name"].Value;
            ValidateIdentifier(indexName, nameof(createIndexSql));
            var actualSql = _connection.ExecuteScalar<string>(@"
SELECT sql
FROM sqlite_master
WHERE type = 'index'
  AND name = @indexName;",
                new { indexName },
                _transaction);
            return !string.IsNullOrWhiteSpace(actualSql) &&
                string.Equals(
                    NormalizeIndexSql(actualSql),
                    NormalizeIndexSql(createIndexSql),
                    StringComparison.Ordinal);
        }

        public bool ColumnIsPrimaryKey(string tableName, string columnName)
        {
            return ReadColumnFlag(tableName, columnName, 5) > 0;
        }

        public bool ColumnIsNotNull(string tableName, string columnName)
        {
            return ReadColumnFlag(tableName, columnName, 3) == 1;
        }

        public bool HasForeignKey(
            string tableName,
            string fromColumn,
            string referencedTable,
            string referencedColumn,
            string onDeleteAction)
        {
            ValidateIdentifier(tableName, nameof(tableName));
            ValidateIdentifier(fromColumn, nameof(fromColumn));
            ValidateIdentifier(referencedTable, nameof(referencedTable));
            ValidateIdentifier(referencedColumn, nameof(referencedColumn));

            if (!TableExists(tableName))
                return false;

            using (var command = _connection.CreateCommand())
            {
                command.Transaction = _transaction;
                command.CommandText = "PRAGMA foreign_key_list(" + QuoteIdentifier(tableName) + ");";
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (string.Equals(Convert.ToString(reader.GetValue(2)), referencedTable, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(Convert.ToString(reader.GetValue(3)), fromColumn, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(Convert.ToString(reader.GetValue(4)), referencedColumn, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(Convert.ToString(reader.GetValue(6)), onDeleteAction, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        public bool NoRows(string sql)
        {
            if (string.IsNullOrWhiteSpace(sql))
                throw new ArgumentException("SQL is required.", nameof(sql));

            using (var command = _connection.CreateCommand())
            {
                command.Transaction = _transaction;
                command.CommandText = sql;
                using (var reader = command.ExecuteReader())
                    return !reader.Read();
            }
        }

        public IReadOnlyList<string> GetUserTableNames()
        {
            return _connection.Query<string>(@"
SELECT name
FROM sqlite_master
WHERE type = 'table'
  AND name NOT LIKE 'sqlite_%'
ORDER BY name;",
                    transaction: _transaction)
                .AsList();
        }

        private static string QuoteIdentifier(string value)
        {
            ValidateIdentifier(value, nameof(value));
            return "\"" + value + "\"";
        }

        private static Dictionary<string, TableColumnShape> ReadColumnShapes(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string tableName)
        {
            var result = new Dictionary<string, TableColumnShape>(StringComparer.OrdinalIgnoreCase);
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "PRAGMA table_info(" + QuoteIdentifier(tableName) + ");";
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var shape = new TableColumnShape(
                            Convert.ToString(reader.GetValue(1)) ?? string.Empty,
                            NormalizeDeclaredType(Convert.ToString(reader.GetValue(2)) ?? string.Empty),
                            Convert.ToInt32(reader.GetValue(3)) == 1,
                            NormalizeDefaultValue(reader.IsDBNull(4)
                                ? string.Empty
                                : Convert.ToString(reader.GetValue(4)) ?? string.Empty),
                            Convert.ToInt32(reader.GetValue(5)));
                        result.Add(shape.Name, shape);
                    }
                }
            }
            return result;
        }

        private static HashSet<string> ReadUniqueKeySignatures(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string tableName)
        {
            var indexes = new List<UniqueIndexShape>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "PRAGMA index_list(" + QuoteIdentifier(tableName) + ");";
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var isUnique = Convert.ToInt32(reader.GetValue(2)) == 1;
                        var origin = Convert.ToString(reader.GetValue(3)) ?? string.Empty;
                        if (isUnique &&
                            !string.Equals(origin, "pk", StringComparison.OrdinalIgnoreCase))
                        {
                            indexes.Add(new UniqueIndexShape(
                                Convert.ToString(reader.GetValue(1)) ?? string.Empty,
                                origin));
                        }
                    }
                }
            }

            var signatures = new HashSet<string>(StringComparer.Ordinal);
            foreach (var index in indexes)
            {
                ValidateIdentifier(index.Name, nameof(index.Name));
                if (string.Equals(index.Origin, "c", StringComparison.OrdinalIgnoreCase))
                {
                    var sql = connection.ExecuteScalar<string>(@"
SELECT sql
FROM sqlite_master
WHERE type = 'index'
  AND name = @indexName;",
                        new { indexName = index.Name },
                        transaction);
                    if (string.IsNullOrWhiteSpace(sql))
                        return new HashSet<string>(StringComparer.Ordinal);
                    signatures.Add("create|" + NormalizeIndexSql(sql));
                    continue;
                }

                var columns = new List<string>();
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "PRAGMA index_xinfo(" + QuoteIdentifier(index.Name) + ");";
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            if (Convert.ToInt32(reader.GetValue(5)) != 1)
                                continue;
                            var columnName = reader.IsDBNull(2)
                                ? string.Empty
                                : Convert.ToString(reader.GetValue(2)) ?? string.Empty;
                            var collation = reader.IsDBNull(4)
                                ? string.Empty
                                : Convert.ToString(reader.GetValue(4)) ?? string.Empty;
                            columns.Add(
                                columnName.ToLowerInvariant() + ":" +
                                collation.ToLowerInvariant() + ":" +
                                Convert.ToInt32(reader.GetValue(3)));
                        }
                    }
                }
                if (columns.Count == 0 || columns.Any(column => column[0] == ':'))
                    return new HashSet<string>(StringComparer.Ordinal);
                signatures.Add("constraint|" + string.Join("\u001f", columns));
            }
            return signatures;
        }

        private static HashSet<string> ReadForeignKeySignatures(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string tableName)
        {
            var signatures = new HashSet<string>(StringComparer.Ordinal);
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "PRAGMA foreign_key_list(" + QuoteIdentifier(tableName) + ");";
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        signatures.Add(string.Join("|", new[]
                        {
                            Convert.ToString(reader.GetValue(2)) ?? string.Empty,
                            Convert.ToString(reader.GetValue(3)) ?? string.Empty,
                            Convert.ToString(reader.GetValue(4)) ?? string.Empty,
                            Convert.ToString(reader.GetValue(5)) ?? string.Empty,
                            Convert.ToString(reader.GetValue(6)) ?? string.Empty,
                            Convert.ToString(reader.GetValue(7)) ?? string.Empty
                        }).ToLowerInvariant());
                    }
                }
            }
            return signatures;
        }

        private static HashSet<string> ReadExplicitIndexDefinitions(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string tableName)
        {
            var definitions = new HashSet<string>(StringComparer.Ordinal);
            var rows = connection.Query<SqlDefinitionRow>(@"
SELECT name AS Name, sql AS Sql
FROM sqlite_master
WHERE type = 'index'
  AND tbl_name = @tableName
  AND sql IS NOT NULL;",
                    new { tableName },
                    transaction)
                .ToList();
            foreach (var row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.Name) || string.IsNullOrWhiteSpace(row.Sql))
                    return new HashSet<string>(StringComparer.Ordinal);
                ValidateIdentifier(row.Name, nameof(row.Name));
                definitions.Add(NormalizeIndexSql(row.Sql));
            }
            return definitions;
        }

        private static HashSet<string> ReadTriggerDefinitions(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string tableName)
        {
            var definitions = new HashSet<string>(StringComparer.Ordinal);
            var rows = connection.Query<SqlDefinitionRow>(@"
SELECT name AS Name, sql AS Sql
FROM sqlite_master
WHERE type = 'trigger'
  AND tbl_name = @tableName;",
                    new { tableName },
                    transaction)
                .ToList();
            foreach (var row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.Name) || string.IsNullOrWhiteSpace(row.Sql))
                    return new HashSet<string>(StringComparer.Ordinal);
                ValidateIdentifier(row.Name, nameof(row.Name));
                definitions.Add(NormalizeSql(row.Sql));
            }
            return definitions;
        }

        private static bool HasAllowedTableGuards(
            SqliteConnection required,
            SqliteConnection allowed,
            SqliteConnection actual,
            SqliteTransaction actualTransaction,
            string tableName)
        {
            var requiredSql = ReadTableSql(required, null, tableName);
            var allowedSql = ReadTableSql(allowed, null, tableName);
            var actualSql = ReadTableSql(actual, actualTransaction, tableName);

            foreach (var pattern in new[]
            {
                @"\bAUTOINCREMENT\b",
                @"\bCHECK\s*\(",
                @"\bCOLLATE\b",
                @"\bON\s+CONFLICT\b",
                @"\bDEFERRABLE\b",
                @"\bGENERATED\b",
                @"\bWITHOUT\s+ROWID\b",
                @"\bSTRICT\b"
            })
            {
                var requiredHas = ContainsSchemaToken(requiredSql, pattern);
                var allowedHas = ContainsSchemaToken(allowedSql, pattern);
                var actualHas = ContainsSchemaToken(actualSql, pattern);
                if ((requiredHas && !actualHas) || (actualHas && !allowedHas))
                    return false;
            }
            return true;
        }

        private static string ReadTableSql(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string tableName)
        {
            return connection.ExecuteScalar<string>(@"
SELECT sql
FROM sqlite_master
WHERE type = 'table'
  AND name = @tableName;",
                    new { tableName },
                    transaction) ?? string.Empty;
        }

        private static bool ContainsSchemaToken(string sql, string pattern)
        {
            var withoutStringLiterals = Regex.Replace(
                sql ?? string.Empty,
                @"'(?:''|[^'])*'",
                "''",
                RegexOptions.CultureInvariant);
            var withoutBlockComments = Regex.Replace(
                withoutStringLiterals,
                @"/\*[\s\S]*?\*/",
                " ",
                RegexOptions.CultureInvariant);
            var withoutComments = Regex.Replace(
                withoutBlockComments,
                @"--[^\r\n]*",
                " ",
                RegexOptions.CultureInvariant);
            return Regex.IsMatch(
                withoutComments,
                pattern,
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        }

        private static string NormalizeIndexSql(string value)
        {
            var withoutCreateGuard = Regex.Replace(
                value ?? string.Empty,
                @"\bIF\s+NOT\s+EXISTS\b",
                string.Empty,
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            var withoutWhitespace = Regex.Replace(
                withoutCreateGuard.Trim().TrimEnd(';'),
                @"\s+",
                string.Empty,
                RegexOptions.CultureInvariant);
            return withoutWhitespace.ToLowerInvariant();
        }

        private static string NormalizeSql(string value)
        {
            return Regex.Replace(
                    (value ?? string.Empty).Trim().TrimEnd(';'),
                    @"\s+",
                    string.Empty,
                    RegexOptions.CultureInvariant)
                .ToLowerInvariant();
        }

        private static string NormalizeDeclaredType(string value)
        {
            return (value ?? string.Empty).Trim().ToUpperInvariant();
        }

        private static string NormalizeDefaultValue(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            while (normalized.Length >= 2 && normalized[0] == '(' && normalized[normalized.Length - 1] == ')')
                normalized = normalized.Substring(1, normalized.Length - 2).Trim();
            return normalized;
        }

        private int ReadColumnFlag(string tableName, string columnName, int ordinal)
        {
            ValidateIdentifier(tableName, nameof(tableName));
            ValidateIdentifier(columnName, nameof(columnName));
            if (!TableExists(tableName))
                return -1;

            using (var command = _connection.CreateCommand())
            {
                command.Transaction = _transaction;
                command.CommandText = "PRAGMA table_info(" + QuoteIdentifier(tableName) + ");";
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (string.Equals(
                            Convert.ToString(reader.GetValue(1)),
                            columnName,
                            StringComparison.OrdinalIgnoreCase))
                        {
                            return Convert.ToInt32(reader.GetValue(ordinal));
                        }
                    }
                }
            }

            return -1;
        }

        private static void ValidateIdentifier(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value) || !IdentifierPattern.IsMatch(value))
                throw new ArgumentException("Unsafe SQLite identifier.", parameterName);
        }

        private sealed class TableColumnShape
        {
            public TableColumnShape(
                string name,
                string declaredType,
                bool isNotNull,
                string defaultValue,
                int primaryKeyOrdinal)
            {
                Name = name;
                DeclaredType = declaredType;
                IsNotNull = isNotNull;
                DefaultValue = defaultValue;
                PrimaryKeyOrdinal = primaryKeyOrdinal;
            }

            public string Name { get; }
            private string DeclaredType { get; }
            private string DefaultValue { get; }
            private bool IsNotNull { get; }
            private int PrimaryKeyOrdinal { get; }

            public bool Matches(TableColumnShape expected)
            {
                return string.Equals(DeclaredType, expected.DeclaredType, StringComparison.Ordinal) &&
                    IsNotNull == expected.IsNotNull &&
                    string.Equals(DefaultValue, expected.DefaultValue, StringComparison.Ordinal) &&
                    PrimaryKeyOrdinal == expected.PrimaryKeyOrdinal;
            }
        }

        private sealed class UniqueIndexShape
        {
            public UniqueIndexShape(string name, string origin)
            {
                Name = name;
                Origin = origin;
            }

            public string Name { get; }
            public string Origin { get; }
        }

        private sealed class SqlDefinitionRow
        {
            public string Name { get; set; }
            public string Sql { get; set; }
        }
    }

    public sealed class SchemaColumnDefinition
    {
        public SchemaColumnDefinition(
            string tableName,
            string columnName,
            string alterDefinition)
            : this(
                tableName,
                columnName,
                ParseDeclaredType(alterDefinition),
                Regex.IsMatch(
                    alterDefinition ?? string.Empty,
                    @"\bNOT\s+NULL\b",
                    RegexOptions.CultureInvariant | RegexOptions.IgnoreCase),
                ParseDefaultValue(alterDefinition),
                alterDefinition)
        {
        }

        public SchemaColumnDefinition(
            string tableName,
            string columnName,
            string declaredType,
            bool isNotNull,
            string defaultValue,
            string alterDefinition)
        {
            TableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
            ColumnName = columnName ?? throw new ArgumentNullException(nameof(columnName));
            DeclaredType = (declaredType ?? throw new ArgumentNullException(nameof(declaredType)))
                .Trim()
                .ToUpperInvariant();
            IsNotNull = isNotNull;
            DefaultValue = NormalizeDefault(defaultValue);
            AlterDefinition = alterDefinition ?? throw new ArgumentNullException(nameof(alterDefinition));
        }

        public string AlterDefinition { get; }
        public string ColumnName { get; }
        public string DeclaredType { get; }
        public string DefaultValue { get; }
        public bool IsNotNull { get; }
        public string TableName { get; }

        public string ToCanonicalMaterial()
        {
            return TableName + "." + ColumnName +
                "|type=" + DeclaredType +
                "|notnull=" + (IsNotNull ? "1" : "0") +
                "|default=" + DefaultValue +
                "|alter=" + AlterDefinition;
        }

        private static string ParseDeclaredType(string alterDefinition)
        {
            if (string.IsNullOrWhiteSpace(alterDefinition))
                throw new ArgumentException("Column definition is required.", nameof(alterDefinition));
            return alterDefinition.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[0];
        }

        private static string ParseDefaultValue(string alterDefinition)
        {
            var match = Regex.Match(
                alterDefinition ?? string.Empty,
                @"\bDEFAULT\s+(?<value>.+)$",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            return match.Success ? match.Groups["value"].Value.Trim() : string.Empty;
        }

        private static string NormalizeDefault(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            while (normalized.Length >= 2 && normalized[0] == '(' && normalized[normalized.Length - 1] == ')')
                normalized = normalized.Substring(1, normalized.Length - 2).Trim();
            return normalized;
        }
    }
}
