using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PlumbobForge.Backend.Database;

namespace PlumbobForge.Backend.Services;

public sealed class ConfigurationScope
{
    public static readonly ConfigurationScope Empty = new(new HashSet<long>(), new Dictionary<long, bool>());

    public IReadOnlySet<long> SetIds { get; }
    /// <summary>Top-level item id → true (always included) / false (always excluded).</summary>
    public IReadOnlyDictionary<long, bool> Overrides { get; }

    public ConfigurationScope(IReadOnlySet<long> setIds, IReadOnlyDictionary<long, bool> overrides)
    {
        SetIds = setIds;
        Overrides = overrides;
    }

    public static async Task<ConfigurationScope> LoadActiveAsync(AppDbContext db)
    {
        var config = await db.ConfigEntities.AsNoTracking()
            .Where(c => c.Active)
            .Select(c => new { c.Id, SetIds = c.ConfigSetsEntities.Select(cs => cs.SetsEntityId).ToList() })
            .FirstOrDefaultAsync();
        if (config == null) return Empty;
        return await LoadAsync(db, config.Id, config.SetIds);
    }

    public static async Task<ConfigurationScope> LoadAsync(AppDbContext db, long configId, IEnumerable<long> setIds)
    {
        var overrides = await db.ConfigItemOverrides.AsNoTracking()
            .Where(o => o.ConfigEntityId == configId)
            .Select(o => new { o.MetaEntityId, o.Include })
            .ToDictionaryAsync(o => o.MetaEntityId, o => o.Include);
        return new ConfigurationScope(setIds.ToHashSet(), overrides);
    }

    public static long TopLevelId(MetaEntity item) => item.ParentMetaEntityId ?? item.Id;

    public bool Includes(long topLevelItemId, long? setId) =>
        Overrides.TryGetValue(topLevelItemId, out bool include)
            ? include
            : setId.HasValue && SetIds.Contains(setId.Value);

    public bool Includes(MetaEntity item) => Includes(TopLevelId(item), item.SetsEntityId);

    public bool IsLoaded(MetaEntity item) => item.Enabled && Includes(item);

    public string GetShardIndexScope(long shardSetId, IEnumerable<long> topLevelItemIdsInShard)
    {
        var sb = new StringBuilder(SetIds.Contains(shardSetId) ? "in" : "out");
        foreach (var id in topLevelItemIdsInShard.Distinct().Where(Overrides.ContainsKey).OrderBy(id => id))
        {
            sb.Append(';').Append(id).Append(Overrides[id] ? '+' : '-');
        }
        return sb.ToString();
    }

    public static bool ShardIndexMatches(string? writtenScope, string expectedScope) =>
        (writtenScope ?? "in") == expectedScope;
}
