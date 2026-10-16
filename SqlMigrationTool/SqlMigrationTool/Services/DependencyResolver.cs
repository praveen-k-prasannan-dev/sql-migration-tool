using Microsoft.Data.SqlClient;
using SqlMigrationTool.Models;

namespace SqlMigrationTool.Services;

/// <summary>
/// Orders programmable objects (views, functions, procedures) so that every
/// object comes after the objects it references, using
/// sys.sql_expression_dependencies. Ties are broken by type (functions, views,
/// procedures) and then by name, so the order is stable between backups.
/// </summary>
public static class DependencyResolver
{
    private static int TypeRank(string type) => type switch
    {
        ObjectTypes.Func => 0,
        ObjectTypes.View => 1,
        ObjectTypes.Proc => 2,
        _                => 3
    };

    /// <returns>
    /// The objects in creation order, and the subset that are part of a dependency
    /// cycle (placed last, alphabetically, because no valid order exists).
    /// </returns>
    public static async Task<(List<DatabaseObject> Ordered, List<DatabaseObject> Cyclic)> OrderAsync(
        ConnectionConfig cfg, IReadOnlyList<DatabaseObject> objects)
    {
        const string sql = @"
            SELECT o.object_id, SCHEMA_NAME(o.schema_id) + '.' + o.name
            FROM sys.objects o
            WHERE o.is_ms_shipped = 0;

            SELECT DISTINCT d.referencing_id, d.referenced_id
            FROM sys.sql_expression_dependencies d
            WHERE d.referenced_id IS NOT NULL
              AND d.referencing_id <> d.referenced_id;";

        var byName = objects.ToDictionary(o => o.FullName, StringComparer.OrdinalIgnoreCase);
        var byId   = new Dictionary<int, DatabaseObject>();

        // dependsOn[x] = objects x references; dependents[y] = objects that reference y
        var dependsOn  = objects.ToDictionary(o => o, _ => new HashSet<DatabaseObject>());
        var dependents = objects.ToDictionary(o => o, _ => new List<DatabaseObject>());

        await using (var conn = await SqlConnectionService.OpenAsync(cfg))
        await using (var cmd  = new SqlCommand(sql, conn))
        await using (var rdr  = await cmd.ExecuteReaderAsync())
        {
            while (await rdr.ReadAsync())
                if (byName.TryGetValue(rdr.GetString(1), out var obj))
                    byId[rdr.GetInt32(0)] = obj;

            await rdr.NextResultAsync();
            while (await rdr.ReadAsync())
            {
                if (byId.TryGetValue(rdr.GetInt32(0), out var referencing) &&
                    byId.TryGetValue(rdr.GetInt32(1), out var referenced) &&
                    dependsOn[referencing].Add(referenced))
                {
                    dependents[referenced].Add(referencing);
                }
            }
        }

        // Kahn's algorithm with a sorted ready-set for a deterministic order.
        var comparer  = Comparer<DatabaseObject>.Create((a, b) =>
        {
            var c = TypeRank(a.Type).CompareTo(TypeRank(b.Type));
            return c != 0 ? c : string.Compare(a.FullName, b.FullName, StringComparison.OrdinalIgnoreCase);
        });
        var remaining = dependsOn.ToDictionary(kv => kv.Key, kv => kv.Value.Count);
        var ready     = new SortedSet<DatabaseObject>(remaining.Where(kv => kv.Value == 0).Select(kv => kv.Key), comparer);
        var ordered   = new List<DatabaseObject>(objects.Count);

        while (ready.Count > 0)
        {
            var next = ready.Min!;
            ready.Remove(next);
            ordered.Add(next);

            foreach (var dependent in dependents[next])
                if (--remaining[dependent] == 0)
                    ready.Add(dependent);
        }

        var cyclic = remaining.Where(kv => kv.Value > 0).Select(kv => kv.Key).OrderBy(o => o, comparer).ToList();
        ordered.AddRange(cyclic);
        return (ordered, cyclic);
    }
}
