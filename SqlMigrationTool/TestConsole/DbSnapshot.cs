using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using SqlMigrationTool.Models;
using SqlMigrationTool.Services;

namespace TestConsole;

/// <summary>
/// A normalised, text-only description of a database's schema and data,
/// grouped by category, so two databases can be compared line by line.
/// </summary>
public sealed class DbSnapshot
{
    public SortedDictionary<string, SortedSet<string>> Categories { get; } = new();

    // ── SQL helpers ─────────────────────────────────────────────────────────

    // Display name of a table or table type, given an object_id expression.
    private static string ParentName(string id) => $"""
        (SELECT CASE WHEN o_.type = 'TT'
                     THEN 'TYPE ' + SCHEMA_NAME(tt_.schema_id) + '.' + tt_.name
                     ELSE SCHEMA_NAME(o_.schema_id) + '.' + o_.name END
         FROM sys.objects o_
         LEFT JOIN sys.table_types tt_ ON tt_.type_table_object_id = o_.object_id
         WHERE o_.object_id = {id})
        """;

    // True when the object_id belongs to a user table or user table type.
    // Table-type objects are always flagged is_ms_shipped = 1, so they are not filtered on it.
    private static string IsUserParent(string id) =>
        $"EXISTS (SELECT 1 FROM sys.objects p_ WHERE p_.object_id = {id} AND (p_.type = 'TT' OR p_.type = 'U' AND p_.is_ms_shipped = 0))";

    private static string IndexColumns(string objId, string idxId, bool included) => $"""
        ISNULL(STUFF((
            SELECT ', ' + c_.name + CASE WHEN ic_.is_descending_key = 1 THEN ' DESC' ELSE '' END
            FROM sys.index_columns ic_
            JOIN sys.columns c_ ON c_.object_id = ic_.object_id AND c_.column_id = ic_.column_id
            WHERE ic_.object_id = {objId} AND ic_.index_id = {idxId}
              AND ic_.is_included_column = {(included ? 1 : 0)}
              {(included ? "" : "AND ic_.key_ordinal > 0")}
            ORDER BY {(included ? "c_.name" : "ic_.key_ordinal")}
            FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, ''), '')
        """;

    private static string AutoName(string isSystemNamed, string name) =>
        $"CASE WHEN {isSystemNamed} = 1 THEN '<auto>' ELSE {name} END";

    // ── Catalog queries ─────────────────────────────────────────────────────
    // Each query returns one row per item; columns are joined with " | ".
    // Categories in HashLast replace their final column with a short hash.

    private static readonly HashSet<string> HashLast = new() { "modules", "xml_schema_collections" };

    private static readonly (string Category, string Sql)[] Queries =
    {
        ("database", """
            SELECT 'collation', CAST(DATABASEPROPERTYEX(DB_NAME(), 'Collation') AS nvarchar(128))
            UNION ALL
            SELECT 'compatibility_level', CAST(compatibility_level AS varchar(10))
            FROM sys.databases WHERE name = DB_NAME()
            """),

        ("schemas", """
            SELECT name FROM sys.schemas WHERE schema_id BETWEEN 5 AND 16383
            """),

        ("types", """
            SELECT SCHEMA_NAME(t.schema_id) + '.' + t.name,
                   CASE WHEN t.is_table_type = 1 THEN 'TABLE' ELSE TYPE_NAME(t.system_type_id) END,
                   t.max_length, t.precision, t.scale, t.is_nullable, ISNULL(t.collation_name, '')
            FROM sys.types t
            WHERE t.is_user_defined = 1
            """),

        ("xml_schema_collections", """
            SELECT SCHEMA_NAME(x.schema_id) + '.' + x.name,
                   CAST(XML_SCHEMA_NAMESPACE(SCHEMA_NAME(x.schema_id), x.name) AS nvarchar(max))
            FROM sys.xml_schema_collections x
            WHERE x.xml_collection_id > 1
            """),

        ("sequences", """
            -- start_value is not compared: restoring the position uses ALTER SEQUENCE ... RESTART WITH,
            -- which also overwrites start_value. The next value (below) is what must match.
            SELECT SCHEMA_NAME(s.schema_id) + '.' + s.name, TYPE_NAME(s.user_type_id),
                   'incr='  + CAST(s.increment AS varchar(40)),
                   'min='   + CAST(s.minimum_value AS varchar(40)),
                   'max='   + CAST(s.maximum_value AS varchar(40)),
                   'cycle=' + CAST(s.is_cycling AS varchar(1)),
                   'cache=' + CASE WHEN s.is_cached = 0 THEN 'none' ELSE ISNULL(CAST(s.cache_size AS varchar(20)), 'default') END,
                   -- the value NEXT VALUE FOR will return (current_value alone differs after RESTART)
                   'next=' + CAST(CASE WHEN s.last_used_value IS NULL THEN CAST(s.start_value AS decimal(38,0))
                                       ELSE CAST(s.last_used_value AS decimal(38,0)) + CAST(s.increment AS decimal(38,0))
                                  END AS varchar(40))
            FROM sys.sequences s
            """),

        ("synonyms", """
            SELECT SCHEMA_NAME(schema_id) + '.' + name, base_object_name FROM sys.synonyms
            """),

        ("partition_functions", """
            SELECT pf.name, TYPE_NAME(pp.system_type_id), pf.type_desc,
                   CASE WHEN pf.boundary_value_on_right = 1 THEN 'RIGHT' ELSE 'LEFT' END,
                   ISNULL(STUFF((SELECT ', ' + CAST(v.value AS nvarchar(200))
                                 FROM sys.partition_range_values v
                                 WHERE v.function_id = pf.function_id
                                 ORDER BY v.boundary_id
                                 FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, ''), '')
            FROM sys.partition_functions pf
            JOIN sys.partition_parameters pp ON pp.function_id = pf.function_id
            """),

        ("partition_schemes", """
            SELECT ps.name, pf.name,
                   STUFF((SELECT ', ' + ds.name
                          FROM sys.destination_data_spaces dds
                          JOIN sys.data_spaces ds ON ds.data_space_id = dds.data_space_id
                          WHERE dds.partition_scheme_id = ps.data_space_id
                          ORDER BY dds.destination_id
                          FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, '')
            FROM sys.partition_schemes ps
            JOIN sys.partition_functions pf ON pf.function_id = ps.function_id
            """),

        ("tables", """
            SELECT SCHEMA_NAME(t.schema_id) + '.' + t.name,
                   'on=' + ds.name,
                   'partition_col=' + ISNULL((SELECT c.name FROM sys.index_columns ic
                                              JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                                              WHERE ic.object_id = t.object_id AND ic.index_id = i.index_id
                                                AND ic.partition_ordinal = 1), ''),
                   t.temporal_type_desc, 'memory_optimized=' + CAST(t.is_memory_optimized AS varchar(1)),
                   t.lock_escalation_desc
            FROM sys.tables t
            JOIN sys.indexes i      ON i.object_id = t.object_id AND i.index_id IN (0, 1)
            JOIN sys.data_spaces ds ON ds.data_space_id = i.data_space_id
            WHERE t.is_ms_shipped = 0
            """),

        ("columns", $"""
            SELECT {ParentName("c.object_id")},
                   RIGHT('00' + CAST(ROW_NUMBER() OVER (PARTITION BY c.object_id ORDER BY c.column_id) AS varchar(3)), 3),
                   c.name,
                   CASE WHEN ty.is_user_defined = 1 THEN SCHEMA_NAME(ty.schema_id) + '.' ELSE '' END + ty.name,
                   'len=' + CAST(c.max_length AS varchar(10)),
                   'prec=' + CAST(c.precision AS varchar(5)) + ',' + CAST(c.scale AS varchar(5)),
                   CASE WHEN c.is_nullable = 1 THEN 'NULL' ELSE 'NOT NULL' END,
                   ISNULL(c.collation_name, ''),
                   ISNULL('IDENTITY(' + CAST(ic.seed_value AS varchar(40)) + ',' + CAST(ic.increment_value AS varchar(40)) + ')', ''),
                   ISNULL('AS ' + cc.definition + CASE WHEN cc.is_persisted = 1 THEN ' PERSISTED' ELSE '' END, ''),
                   CASE WHEN c.is_sparse = 1 THEN 'SPARSE' ELSE '' END,
                   CASE WHEN c.is_rowguidcol = 1 THEN 'ROWGUIDCOL' ELSE '' END,
                   ISNULL('DEFAULT ' + {AutoName("dc.is_system_named", "dc.name")} + ' ' + dc.definition, ''),
                   ISNULL('XML(' + SCHEMA_NAME(x.schema_id) + '.' + x.name + ')', '')
            FROM sys.columns c
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            LEFT JOIN sys.identity_columns ic      ON ic.object_id = c.object_id AND ic.column_id = c.column_id
            LEFT JOIN sys.computed_columns cc      ON cc.object_id = c.object_id AND cc.column_id = c.column_id
            LEFT JOIN sys.default_constraints dc   ON dc.object_id = c.default_object_id
            LEFT JOIN sys.xml_schema_collections x ON x.xml_collection_id = c.xml_collection_id AND c.xml_collection_id > 1
            WHERE {IsUserParent("c.object_id")}
            """),

        ("key_constraints", $"""
            SELECT {ParentName("k.parent_object_id")},
                   {AutoName("k.is_system_named", "k.name")},
                   k.type_desc, i.type_desc,
                   '(' + {IndexColumns("i.object_id", "i.index_id", false)} + ')',
                   'on=' + ISNULL(ds.name, '')
            FROM sys.key_constraints k
            JOIN sys.indexes i ON i.object_id = k.parent_object_id AND i.index_id = k.unique_index_id
            LEFT JOIN sys.data_spaces ds ON ds.data_space_id = i.data_space_id
            WHERE {IsUserParent("k.parent_object_id")}
            """),

        ("check_constraints", $"""
            SELECT {ParentName("cc.parent_object_id")},
                   {AutoName("cc.is_system_named", "cc.name")},
                   cc.definition,
                   'disabled=' + CAST(cc.is_disabled AS varchar(1)),
                   'not_trusted=' + CAST(cc.is_not_trusted AS varchar(1)),
                   'not_for_repl=' + CAST(cc.is_not_for_replication AS varchar(1))
            FROM sys.check_constraints cc
            WHERE {IsUserParent("cc.parent_object_id")}
            """),

        ("foreign_keys", """
            SELECT OBJECT_SCHEMA_NAME(fk.parent_object_id) + '.' + OBJECT_NAME(fk.parent_object_id),
                   fk.name,
                   '(' + STUFF((SELECT ', ' + COL_NAME(fkc.parent_object_id, fkc.parent_column_id)
                                FROM sys.foreign_key_columns fkc
                                WHERE fkc.constraint_object_id = fk.object_id
                                ORDER BY fkc.constraint_column_id
                                FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, '') + ')',
                   '-> ' + OBJECT_SCHEMA_NAME(fk.referenced_object_id) + '.' + OBJECT_NAME(fk.referenced_object_id),
                   '(' + STUFF((SELECT ', ' + COL_NAME(fkc.referenced_object_id, fkc.referenced_column_id)
                                FROM sys.foreign_key_columns fkc
                                WHERE fkc.constraint_object_id = fk.object_id
                                ORDER BY fkc.constraint_column_id
                                FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, '') + ')',
                   'del=' + fk.delete_referential_action_desc,
                   'upd=' + fk.update_referential_action_desc,
                   'disabled=' + CAST(fk.is_disabled AS varchar(1)),
                   'not_trusted=' + CAST(fk.is_not_trusted AS varchar(1)),
                   'not_for_repl=' + CAST(fk.is_not_for_replication AS varchar(1))
            FROM sys.foreign_keys fk
            WHERE fk.is_ms_shipped = 0
            """),

        ("indexes", $"""
            SELECT {ParentName("i.object_id")},
                   i.name, i.type_desc,
                   CASE WHEN i.is_unique = 1 THEN 'UNIQUE' ELSE '' END,
                   '(' + {IndexColumns("i.object_id", "i.index_id", false)} + ')',
                   'include=(' + {IndexColumns("i.object_id", "i.index_id", true)} + ')',
                   'where=' + ISNULL(i.filter_definition, ''),
                   'fill=' + CAST(CASE WHEN i.fill_factor = 0 THEN 100 ELSE i.fill_factor END AS varchar(3)),
                   'compression=' + ISNULL(p.data_compression_desc, ''),
                   'on=' + ISNULL(ds.name, ''),
                   'disabled=' + CAST(i.is_disabled AS varchar(1))
            FROM sys.indexes i
            LEFT JOIN sys.data_spaces ds ON ds.data_space_id = i.data_space_id
            OUTER APPLY (SELECT TOP 1 p.data_compression_desc FROM sys.partitions p
                         WHERE p.object_id = i.object_id AND p.index_id = i.index_id
                         ORDER BY p.partition_number) p
            WHERE i.type > 0 AND i.is_primary_key = 0 AND i.is_unique_constraint = 0
              AND {IsUserParent("i.object_id")}
            """),

        ("triggers", """
            SELECT CASE WHEN t.parent_class = 0 THEN 'DATABASE'
                        ELSE OBJECT_SCHEMA_NAME(t.parent_id) + '.' + OBJECT_NAME(t.parent_id) END,
                   t.name,
                   CASE WHEN t.is_instead_of_trigger = 1 THEN 'INSTEAD OF' ELSE 'AFTER' END,
                   ISNULL(STUFF((SELECT ', ' + te.type_desc FROM sys.trigger_events te
                                 WHERE te.object_id = t.object_id ORDER BY te.type_desc
                                 FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, ''), ''),
                   'disabled=' + CAST(t.is_disabled AS varchar(1))
            FROM sys.triggers t
            WHERE t.is_ms_shipped = 0
            """),

        ("modules", """
            SELECT COALESCE(SCHEMA_NAME(o.schema_id) + '.' + o.name, 'DATABASE.' + t.name),
                   COALESCE(o.type_desc, 'DATABASE_TRIGGER'),
                   'ansi_nulls=' + CAST(m.uses_ansi_nulls AS varchar(1)),
                   'quoted_id=' + CAST(m.uses_quoted_identifier AS varchar(1)),
                   m.definition
            FROM sys.sql_modules m
            LEFT JOIN sys.objects  o ON o.object_id = m.object_id
            LEFT JOIN sys.triggers t ON t.object_id = m.object_id AND t.parent_class = 0
            WHERE ISNULL(o.is_ms_shipped, 0) = 0
            """),

        ("extended_properties", """
            SELECT ep.class_desc,
                   CASE ep.class
                       WHEN 0 THEN 'DATABASE'
                       WHEN 1 THEN OBJECT_SCHEMA_NAME(ep.major_id) + '.' + OBJECT_NAME(ep.major_id)
                                   + CASE WHEN ep.minor_id > 0 THEN '.' + ISNULL(COL_NAME(ep.major_id, ep.minor_id), '?') ELSE '' END
                       WHEN 3 THEN SCHEMA_NAME(ep.major_id)
                       ELSE CAST(ep.major_id AS varchar(20)) + '/' + CAST(ep.minor_id AS varchar(20))
                   END,
                   ep.name, CAST(ep.value AS nvarchar(4000))
            FROM sys.extended_properties ep
            """),

        ("principals", """
            SELECT name, type_desc, ISNULL(default_schema_name, ''), authentication_type_desc
            FROM sys.database_principals
            WHERE principal_id > 4 AND is_fixed_role = 0
            """),

        ("role_members", """
            SELECT USER_NAME(role_principal_id), USER_NAME(member_principal_id)
            FROM sys.database_role_members
            WHERE member_principal_id > 4
            """),

        ("permissions", """
            SELECT USER_NAME(p.grantee_principal_id), p.class_desc,
                   CASE p.class
                       WHEN 0 THEN 'DATABASE'
                       WHEN 1 THEN OBJECT_SCHEMA_NAME(p.major_id) + '.' + OBJECT_NAME(p.major_id)
                                   + ISNULL('.' + COL_NAME(p.major_id, NULLIF(p.minor_id, 0)), '')
                       WHEN 3 THEN SCHEMA_NAME(p.major_id)
                       WHEN 6 THEN (SELECT SCHEMA_NAME(schema_id) + '.' + name FROM sys.types WHERE user_type_id = p.major_id)
                       ELSE CAST(p.major_id AS varchar(20))
                   END,
                   p.permission_name, p.state_desc
            FROM sys.database_permissions p
            WHERE p.grantee_principal_id > 4
            """),
    };

    // ── Capture ─────────────────────────────────────────────────────────────

    public static async Task<DbSnapshot> CaptureAsync(ConnectionConfig cfg)
    {
        var snap = new DbSnapshot();
        await using var conn = await SqlConnectionService.OpenAsync(cfg);

        foreach (var (category, sql) in Queries)
        {
            var lines = snap.Category(category);
            await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                var values = new string[rdr.FieldCount];
                for (int i = 0; i < values.Length; i++)
                    values[i] = rdr.IsDBNull(i) ? "NULL" : Convert.ToString(rdr.GetValue(i), CultureInfo.InvariantCulture)!;

                if (HashLast.Contains(category))
                    values[^1] = "#" + ShortHash(values[^1]);

                lines.Add(string.Join(" | ", values));
            }
        }

        await CaptureDataAsync(conn, snap.Category("data"));
        return snap;
    }

    // Row count plus an order-independent checksum of every comparable column.
    private static async Task CaptureDataAsync(SqlConnection conn, SortedSet<string> lines)
    {
        const string colSql = """
            SELECT SCHEMA_NAME(t.schema_id), t.name, c.name, TYPE_NAME(c.system_type_id), c.is_computed
            FROM sys.tables t
            JOIN sys.columns c ON c.object_id = t.object_id
            WHERE t.is_ms_shipped = 0
            ORDER BY 1, 2, c.column_id
            """;

        var tables = new List<(string schema, string table, List<string> exprs)>();
        await using (var cmd = new SqlCommand(colSql, conn))
        await using (var rdr = await cmd.ExecuteReaderAsync())
        {
            while (await rdr.ReadAsync())
            {
                var (schema, table) = (rdr.GetString(0), rdr.GetString(1));
                if (tables.Count == 0 || tables[^1].schema != schema || tables[^1].table != table)
                    tables.Add((schema, table, new List<string>()));

                if (rdr.GetBoolean(4)) continue;    // computed: covered by the column definition
                var col = $"[{rdr.GetString(2).Replace("]", "]]")}]";
                var expr = rdr.GetString(3) switch
                {
                    "timestamp"                              => null,   // regenerated by the server
                    "xml" or "text" or "ntext"               => $"CAST({col} AS nvarchar(max))",
                    "image"                                  => $"CAST({col} AS varbinary(max))",
                    "geography" or "geometry" or "hierarchyid" => $"CAST({col} AS varbinary(max))",
                    _                                        => col
                };
                if (expr is not null) tables[^1].exprs.Add(expr);
            }
        }

        foreach (var (schema, table, exprs) in tables)
        {
            var name = $"{schema}.{table}";
            var checksum = exprs.Count == 0 ? "0"
                : $"ISNULL(SUM(CAST(BINARY_CHECKSUM({string.Join(", ", exprs)}) AS bigint)), 0)";
            var sql = $"SELECT COUNT_BIG(*), {checksum} FROM [{schema.Replace("]", "]]")}].[{table.Replace("]", "]]")}]";
            try
            {
                await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 300 };
                await using var rdr = await cmd.ExecuteReaderAsync();
                await rdr.ReadAsync();
                lines.Add($"{name} | rows={rdr.GetInt64(0)} | checksum={rdr.GetValue(1)}");
            }
            catch (SqlException ex)
            {
                lines.Add($"{name} | ERROR {ex.Message}");
            }
        }
    }

    private SortedSet<string> Category(string name)
    {
        if (!Categories.TryGetValue(name, out var set))
            Categories[name] = set = new SortedSet<string>(StringComparer.Ordinal);
        return set;
    }

    private static string ShortHash(string text)
    {
        var normalised = Regex.Replace(text, @"\s+", " ").Trim();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalised)))[..12];
    }

    // ── Compare ─────────────────────────────────────────────────────────────

    public sealed record CategoryDiff(string Category, int SourceCount, int TargetCount,
                                      List<string> Missing, List<string> Extra)
    {
        public int Differences => Missing.Count + Extra.Count;
    }

    public static List<CategoryDiff> Compare(DbSnapshot source, DbSnapshot target)
    {
        var names = source.Categories.Keys.Union(target.Categories.Keys).OrderBy(n => n);
        var result = new List<CategoryDiff>();
        foreach (var name in names)
        {
            var src = source.Categories.GetValueOrDefault(name) ?? new SortedSet<string>();
            var tgt = target.Categories.GetValueOrDefault(name) ?? new SortedSet<string>();
            result.Add(new CategoryDiff(name, src.Count, tgt.Count,
                src.Except(tgt).ToList(), tgt.Except(src).ToList()));
        }
        return result;
    }
}
