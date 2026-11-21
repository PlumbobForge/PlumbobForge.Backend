using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PlumbobForge.Backend.Database;
using S3ForgeTools.GameFiles.Package;

namespace PlumbobForge.Backend.Services;

// Tags every package that holds a C# script assembly (S3SA) as "Script mod", and a core mod as "Core mod" too.
public static class ScriptModTags
{
    public const string ScriptTag = "Script mod";
    public const string CoreTag = "Core mod";

    private const uint ScriptAssemblyType = 0x073FAA07;

    // Marker in the PlumbobForge folder: the whole library is tagged once, after that only new files are.
    private const string MarkerFileName = "script-tags.v1";

    // Instances of EA's own script assemblies in Game\Bin (gameplay.package, scripts.package and simcore.package of the
    // patched base game). A core mod replaces one of these assemblies and keeps its instance; an ordinary script mod
    // adds an assembly with an instance of its own.
    private static readonly HashSet<ulong> EaAssemblyInstances =
    [
        0x0CAE1C361E05B2B3, 0xB9C90FDC6793BC0A, 0x03D6C8D903CE868C, 0xF7C3ADE896D4E765,
        0xC356DF69B70ADD42, 0x78CF6CF5304D0C4F, 0x600F9EA1DDC99FB1,
        0x6AC101133051BEF1, 0x342EE04373CF1E1C, 0x28EE9D383A73463E
    ];

    public static (bool IsScript, bool IsCore) Detect(string path)
    {
        if (!path.EndsWith(".package", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return (false, false);
        try
        {
            using var package = new DBPFPackage(path);
            bool isScript = false;
            foreach (var res in package.Resources)
            {
                if (res.Key.Type != ScriptAssemblyType) continue;
                isScript = true;
                if (EaAssemblyInstances.Contains(res.Key.Instance)) return (true, true);
            }
            return (isScript, false);
        }
        catch
        {
            return (false, false);
        }
    }

    // Adds the tags the file calls for. Tags already there, including the user's own, are kept. Returns true on a change.
    public static bool Apply(MetaEntity meta)
    {
        var (isScript, isCore) = Detect(meta.CompleteFileName);
        if (!isScript) return false;

        var tags = string.IsNullOrWhiteSpace(meta.UserTags)
            ? new List<string>()
            : meta.UserTags.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        bool changed = false;
        foreach (string tag in isCore ? new[] { ScriptTag, CoreTag } : new[] { ScriptTag })
        {
            if (tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) continue;
            tags.Add(tag);
            changed = true;
        }

        if (changed) meta.UserTags = string.Join(", ", tags);
        return changed;
    }

    // One pass over every package already in the library. Runs once; the marker file stops later runs.
    public static async Task<int> TagLibraryOnceAsync(AppDbContext db, string documentBaseDir)
    {
        if (string.IsNullOrEmpty(documentBaseDir)) return 0;
        string marker = Path.Combine(documentBaseDir, MarkerFileName);
        if (File.Exists(marker)) return 0;

        var metas = await db.MetaEntities
            .Where(m => !m.IsVaultManaged && m.FileType == "DBPF")
            .ToListAsync();
        int tagged = 0;
        foreach (var meta in metas)
        {
            if (Apply(meta)) tagged++;
        }
        if (tagged > 0) await db.SaveChangesAsync();

        try { File.WriteAllText(marker, DateTime.Now.ToString("O")); } catch { }
        AppLogger.LogInfo($"Tagged {tagged} script mods in the library.", "ScriptModTags");
        return tagged;
    }
}
