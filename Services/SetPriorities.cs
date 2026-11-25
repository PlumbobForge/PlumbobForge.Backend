using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PlumbobForge.Backend.Database;

namespace PlumbobForge.Backend.Services;

// A set's Resource.cfg load priority. A set without its own takes its parent's; top-level sets default to 500, the
// priority Packages loads at. One merged cache file loads at one priority, so every other priority gets its own cache
// folder next to the default one: "StaticCache" and "StaticCache_P1000", "ConfigItems" and "ConfigItems_P1000".
public static class SetPriorities
{
    public const int Default = 500;

    public static int Effective(long? setId, IReadOnlyDictionary<long, (long? Parent, int? Own)> sets)
    {
        var seen = new HashSet<long>();
        while (setId is long id && seen.Add(id) && sets.TryGetValue(id, out var set))
        {
            if (set.Own is int own) return own;
            setId = set.Parent;
        }
        return Default;
    }

    // Every set's effective priority, by set id.
    public static async Task<Dictionary<long, int>> LoadAsync(AppDbContext db)
    {
        var rows = await db.SetsEntities.AsNoTracking()
            .Select(s => new { s.Id, s.ParentSetsEntityId, s.Priority })
            .ToListAsync();
        var sets = rows.ToDictionary(r => r.Id, r => (r.ParentSetsEntityId, r.Priority));
        return rows.ToDictionary(r => r.Id, r => Effective(r.Id, sets));
    }

    public static int Of(long? setId, IReadOnlyDictionary<long, int> effective) =>
        setId is long id && effective.TryGetValue(id, out int priority) ? priority : Default;

    public static int Of(MetaEntity item, IReadOnlyDictionary<long, int> effective) => Of(item.SetsEntityId, effective);

    public static string FolderName(string baseName, int priority) =>
        priority == Default ? baseName : $"{baseName}_P{priority.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    // The priority a cache folder name stands for, or null when the name isn't one of baseName's folders.
    public static int? PriorityOfFolder(string folderName, string baseName)
    {
        if (folderName.Equals(baseName, StringComparison.OrdinalIgnoreCase)) return Default;
        string prefix = baseName + "_P";
        if (!folderName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        return int.TryParse(folderName.AsSpan(prefix.Length), System.Globalization.NumberStyles.AllowLeadingSign,
            System.Globalization.CultureInfo.InvariantCulture, out int priority) && priority != Default ? priority : null;
    }

    // baseName's folders that exist in the cache folder, with their priorities.
    public static List<(string Path, int Priority)> FoldersIn(string cacheDir, string baseName)
    {
        var found = new List<(string, int)>();
        if (!Directory.Exists(cacheDir)) return found;
        foreach (var dir in Directory.GetDirectories(cacheDir))
        {
            if (PriorityOfFolder(Path.GetFileName(dir), baseName) is int priority) found.Add((dir, priority));
        }
        return found.OrderBy(f => f.Item2).ThenBy(f => f.Item1, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // A Resource.cfg body: the default priority first (so it is always there), then each other priority with its lines,
    // lowest first. Lines keep their order within a priority.
    public static string BuildResourceCfg(IEnumerable<(int Priority, string Line)> lines)
    {
        var groups = lines.GroupBy(l => l.Priority).ToDictionary(g => g.Key, g => g.Select(l => l.Line).ToList());
        var cfg = new StringBuilder();
        cfg.AppendLine($"Priority {Default}");
        if (groups.TryGetValue(Default, out var defaults))
        {
            foreach (var line in defaults) cfg.AppendLine(line);
        }
        foreach (var priority in groups.Keys.Where(p => p != Default).OrderBy(p => p))
        {
            cfg.AppendLine($"Priority {priority.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            foreach (var line in groups[priority]) cfg.AppendLine(line);
        }
        return cfg.ToString();
    }
}
