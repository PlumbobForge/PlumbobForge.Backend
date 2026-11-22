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
    // Mods folders the configuration turns off. Every other Mods folder is on.
    public IReadOnlySet<long> OffModsFolderIds { get; }

    public ConfigurationScope(IReadOnlySet<long> setIds, IReadOnlyDictionary<long, bool> overrides, IReadOnlySet<long>? offModsFolderIds = null)
    {
        SetIds = setIds;
        Overrides = overrides;
        OffModsFolderIds = offModsFolderIds ?? new HashSet<long>();
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
        var offFolders = await db.ConfigModsFoldersOff.AsNoTracking()
            .Where(o => o.ConfigEntityId == configId)
            .Select(o => o.ModsFolderId)
            .ToListAsync();
        return new ConfigurationScope(setIds.ToHashSet(), overrides, offFolders.ToHashSet());
    }

    public static long TopLevelId(MetaEntity item) => item.ParentMetaEntityId ?? item.Id;

    public bool Includes(long topLevelItemId, long? setId) =>
        Overrides.TryGetValue(topLevelItemId, out bool include)
            ? include
            : setId.HasValue && SetIds.Contains(setId.Value);

    public bool Includes(MetaEntity item) => item.IsModsItem
        ? IncludesModsItem(item.Id, item.ModsFolderId)
        : Includes(TopLevelId(item), item.SetsEntityId);

    // A Mods file follows its own rule, else the folder it sits in. Files straight in a load root have no folder and
    // are always on. With no configuration at all, everything in Mods is on.
    public bool IncludesModsItem(long itemId, long? modsFolderId) =>
        Overrides.TryGetValue(itemId, out bool include)
            ? include
            : !modsFolderId.HasValue || !OffModsFolderIds.Contains(modsFolderId.Value);

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
