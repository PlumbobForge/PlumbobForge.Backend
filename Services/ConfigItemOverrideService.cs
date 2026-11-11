using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PlumbobForge.Backend.Configuration;
using PlumbobForge.Backend.Database;

namespace PlumbobForge.Backend.Services;

public sealed record ConfigItemRuleSummary(
    long ConfigId,
    string ConfigName,
    bool IsActive,
    int ItemCount,
    int InConfigSetCount,
    int IncludedCount,
    int ExcludedCount)
{
    public int FollowSetCount => ItemCount - IncludedCount - ExcludedCount;
}

public sealed record ConfigItemRuleResult(bool ActiveConfigChanged, int LibrarySetsNeedingRebuild);

public class ConfigItemOverrideService
{
    private readonly AppDbContext _db;
    private readonly IOptionsMonitor<PlumbobForgeOptions> _optionsMonitor;

    public ConfigItemOverrideService(AppDbContext db, IOptionsMonitor<PlumbobForgeOptions> optionsMonitor)
    {
        _db = db;
        _optionsMonitor = optionsMonitor;
    }

    private async Task<List<long>> ToTopLevelIdsAsync(IEnumerable<long> itemIds)
    {
        var ids = itemIds.Distinct().ToList();
        return await _db.MetaEntities
            .Where(m => ids.Contains(m.Id))
            .Select(m => m.ParentMetaEntityId ?? m.Id)
            .Distinct()
            .ToListAsync();
    }

    public async Task<List<ConfigItemRuleSummary>> SummarizeAsync(IEnumerable<long> itemIds)
    {
        var topIds = await ToTopLevelIdsAsync(itemIds);
        var items = await _db.MetaEntities.AsNoTracking()
            .Where(m => topIds.Contains(m.Id))
            .Select(m => new { m.Id, m.SetsEntityId })
            .ToListAsync();
        var configs = await _db.ConfigEntities.AsNoTracking()
            .Select(c => new { c.Id, c.Name, c.Active, SetIds = c.ConfigSetsEntities.Select(cs => cs.SetsEntityId).ToList() })
            .ToListAsync();
        var rules = await _db.ConfigItemOverrides.AsNoTracking()
            .Where(o => topIds.Contains(o.MetaEntityId))
            .ToListAsync();

        return configs
            .OrderByDescending(c => c.Active).ThenBy(c => c.Id)
            .Select(c =>
            {
                var setIds = c.SetIds.ToHashSet();
                var own = rules.Where(r => r.ConfigEntityId == c.Id).ToList();
                return new ConfigItemRuleSummary(
                    c.Id,
                    c.Name,
                    c.Active,
                    items.Count,
                    items.Count(i => i.SetsEntityId.HasValue && setIds.Contains(i.SetsEntityId.Value)),
                    own.Count(r => r.Include),
                    own.Count(r => !r.Include));
            })
            .ToList();
    }

    public async Task<ConfigItemRuleResult> SetRuleAsync(long configId, IEnumerable<long> itemIds, bool? include)
    {
        var config = await _db.ConfigEntities.AsNoTracking().FirstOrDefaultAsync(c => c.Id == configId)
            ?? throw new InvalidOperationException("The configuration no longer exists.");
        var topIds = await ToTopLevelIdsAsync(itemIds);
        if (topIds.Count == 0) return new ConfigItemRuleResult(false, 0);

        var existing = await _db.ConfigItemOverrides
            .Where(o => o.ConfigEntityId == configId && topIds.Contains(o.MetaEntityId))
            .ToDictionaryAsync(o => o.MetaEntityId);

        bool changed = false;
        foreach (long id in topIds)
        {
            existing.TryGetValue(id, out var row);
            if (include == null)
            {
                if (row == null) continue;
                _db.ConfigItemOverrides.Remove(row);
                changed = true;
            }
            else if (row == null)
            {
                _db.ConfigItemOverrides.Add(new ConfigItemOverrideEntity { ConfigEntityId = configId, MetaEntityId = id, Include = include.Value });
                changed = true;
            }
            else if (row.Include != include.Value)
            {
                row.Include = include.Value;
                changed = true;
            }
        }
        if (!changed) return new ConfigItemRuleResult(false, 0);

        await _db.SaveChangesAsync();
        int dirtied = await RefreshOverrideFlagsAsync(topIds);
        return new ConfigItemRuleResult(config.Active, dirtied);
    }

    /// <summary>Copies one configuration's rules to another (used when duplicating a configuration).</summary>
    public async Task CopyRulesAsync(long fromConfigId, long toConfigId)
    {
        var rules = await _db.ConfigItemOverrides.AsNoTracking().Where(o => o.ConfigEntityId == fromConfigId).ToListAsync();
        if (rules.Count == 0) return;
        foreach (var rule in rules)
        {
            _db.ConfigItemOverrides.Add(new ConfigItemOverrideEntity { ConfigEntityId = toConfigId, MetaEntityId = rule.MetaEntityId, Include = rule.Include });
        }
        await _db.SaveChangesAsync();
    }

    public async Task<Dictionary<long, bool>> GetRulesAsync(long configId) =>
        await _db.ConfigItemOverrides.AsNoTracking()
            .Where(o => o.ConfigEntityId == configId)
            .ToDictionaryAsync(o => o.MetaEntityId, o => o.Include);

    public async Task<int> RefreshOverrideFlagsAsync(IEnumerable<long>? topLevelIds = null)
    {
        var ids = topLevelIds?.ToList();
        var withRules = (await _db.ConfigItemOverrides.Select(o => o.MetaEntityId).Distinct().ToListAsync()).ToHashSet();

        var items = await _db.MetaEntities
            .Where(m => ids == null || ids.Contains(m.Id) || (m.ParentMetaEntityId.HasValue && ids.Contains(m.ParentMetaEntityId.Value)))
            .ToListAsync();

        var changedSetIds = new HashSet<long>();
        foreach (var item in items)
        {
            bool flag = withRules.Contains(item.ParentMetaEntityId ?? item.Id);
            if (item.HasConfigOverrides == flag) continue;
            item.HasConfigOverrides = flag;
            if (item.SetsEntityId.HasValue) changedSetIds.Add(item.SetsEntityId.Value);
        }

        int dirtied = 0;
        if (changedSetIds.Count > 0)
        {
            var options = _optionsMonitor.CurrentValue;
            bool vault = options.EnableSingleSourceVault;
            bool isStatic = !vault && string.Equals(options.CacheMethod, "Static", StringComparison.OrdinalIgnoreCase);
            var sets = await _db.SetsEntities.Include(s => s.MetaEntities).Where(s => changedSetIds.Contains(s.Id)).ToListAsync();
            foreach (var set in sets)
            {
                // The flag only decides which cache folder a dynamic library build puts the item in. Vault mode and the
                // static bundle do not care, so a set that was clean stays clean there.
                if (vault || isStatic)
                {
                    if (!set.Dirty) set.CachedHash = SetDirtyTracker.ComputeContentHash(set);
                    continue;
                }
                bool wasDirty = set.Dirty;
                SetDirtyTracker.UpdateDirtyState(set);
                if (set.Dirty && !wasDirty) dirtied++;
            }
        }

        await _db.SaveChangesAsync();
        return dirtied;
    }
}
