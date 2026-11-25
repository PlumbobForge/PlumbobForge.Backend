using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using S3ForgeTools.Utils;

namespace PlumbobForge.Backend.Services;

// Root folders of the Mods folder (Mods/Packages, Mods/Overrides, Mods/Fantasy...): each one is loaded by its own
// lines in Mods/Resource.cfg. New root folders get a block like the one Packages has, at the end of the file:
//   Priority 500
//   PackedFile Fantasy/*.package ... PackedFile Fantasy/*/*/*/*/*.package
public partial class ModsFolderService
{
    public const int DefaultRootPriority = 500;
    private const int RootPatternDepth = 5;

    // The ones PlumbobForge itself relies on (imports, custom icons, the Hider), so their lines are never rewritten.
    private static readonly string[] StandardRoots = { "Packages", "Overrides" };

    public enum RootFolderError { None, InvalidName, HasSpaces, Reserved, Exists, NoResourceCfg, Failed }

    public sealed record RootFolderResult(string? RelativePath, RootFolderError Error);

    // Creates Mods/<name> and adds its lines to Mods/Resource.cfg so the game loads it. Names with spaces are refused:
    // the game's Resource.cfg reader is not known to take quoted paths.
    public async Task<RootFolderResult> CreateRootFolderAsync(string name, int priority = DefaultRootPriority)
    {
        string? clean = CleanName(name);
        if (clean == null) return new(null, RootFolderError.InvalidName);
        if (clean.Any(char.IsWhiteSpace)) return new(null, RootFolderError.HasSpaces);
        if (ModsResourceCfg.IsPlumbobForgePath(clean) || clean.Equals("Resource.cfg", StringComparison.OrdinalIgnoreCase))
            return new(null, RootFolderError.Reserved);

        string modsDir = GetModsFolderPath();
        string cfgPath = Path.Combine(modsDir, "Resource.cfg");
        if (string.IsNullOrEmpty(modsDir) || !File.Exists(cfgPath)) return new(null, RootFolderError.NoResourceCfg);

        string full = Path.Combine(modsDir, clean);
        if (Directory.Exists(full) || File.Exists(full)) return new(null, RootFolderError.Exists);

        try { Directory.CreateDirectory(full); }
        catch (Exception ex)
        {
            AppLogger.LogError($"Could not create Mods folder {full}", ex, "ModsFolderService");
            return new(null, RootFolderError.Failed);
        }

        try
        {
            var lines = ReadCfgLines(cfgPath);
            // A folder Resource.cfg already loads (e.g. made by hand and deleted) keeps its lines as they are.
            if (!ModsResourceCfg.Load(modsDir).SearchRoots().Any(r => r.RelativeDir.Equals(clean, StringComparison.OrdinalIgnoreCase)))
            {
                AppendRootBlock(lines, clean, priority);
                WritableFile.WriteAllLines(cfgPath, lines);
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"Could not add {clean} to Mods/Resource.cfg", ex, "ModsFolderService");
            try { Directory.Delete(full); } catch { }
            return new(null, RootFolderError.Failed);
        }

        await ScanAsync();
        return new(clean, RootFolderError.None);
    }

    // The priority the root folder's files load at, or null when it isn't a root folder whose lines are in
    // Mods/Resource.cfg itself (a nested .cfg, or Packages/Overrides, whose lines PlumbobForge leaves alone).
    public int? GetEditableRootPriority(string relativePath)
    {
        if (!IsEditableRoot(relativePath)) return null;
        string modsDir = GetModsFolderPath();
        var cfg = ModsResourceCfg.Load(modsDir);
        var lines = ReadCfgLines(Path.Combine(modsDir, "Resource.cfg"));
        if (RootLineIndexes(lines, relativePath).Count == 0) return null;
        return cfg.Patterns.FirstOrDefault(p => RootOf(p.RelativePattern).Equals(relativePath, StringComparison.OrdinalIgnoreCase))?.Priority;
    }

    // Moves the root folder's lines to a fresh block at the end of Mods/Resource.cfg with the new priority.
    public async Task<bool> SetRootPriorityAsync(string relativePath, int priority)
    {
        if (!IsEditableRoot(relativePath)) return false;
        string modsDir = GetModsFolderPath();
        string cfgPath = Path.Combine(modsDir, "Resource.cfg");
        try
        {
            var lines = ReadCfgLines(cfgPath);
            var indexes = RootLineIndexes(lines, relativePath);
            if (indexes.Count == 0) return false;
            if (GetEditableRootPriority(relativePath) == priority) return true;

            for (int k = indexes.Count - 1; k >= 0; k--) lines.RemoveAt(indexes[k]);
            RemoveDeadPriorityLines(lines);
            AppendRootBlock(lines, relativePath, priority);
            WritableFile.WriteAllLines(cfgPath, lines);
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"Could not change {relativePath}'s priority in Mods/Resource.cfg", ex, "ModsFolderService");
            return false;
        }
        await ScanAsync();
        return true;
    }

    private bool IsEditableRoot(string relativePath) =>
        !string.IsNullOrEmpty(relativePath) && !relativePath.Contains('/') &&
        !StandardRoots.Contains(relativePath, StringComparer.OrdinalIgnoreCase) &&
        GetRootFolderPaths().Contains(relativePath);

    private static List<string> ReadCfgLines(string cfgPath)
    {
        var lines = File.ReadAllLines(cfgPath).ToList();
        while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static void AppendRootBlock(List<string> lines, string root, int priority)
    {
        lines.Add($"Priority {priority}");
        for (int depth = 0; depth < RootPatternDepth; depth++)
            lines.Add($"PackedFile {root}/{string.Concat(Enumerable.Repeat("*/", depth))}*.package");
    }

    // PackedFile lines in the main Resource.cfg whose fixed folder is exactly this root.
    private static List<int> RootLineIndexes(List<string> lines, string root)
    {
        var result = new List<int>();
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i].Trim();
            if (!line.StartsWith("PackedFile", StringComparison.OrdinalIgnoreCase) || line.Length <= "PackedFile".Length ||
                !char.IsWhiteSpace(line["PackedFile".Length])) continue;
            string pattern = line["PackedFile".Length..].Trim().Trim('"').Replace('\\', '/');
            if (RootOf(pattern).Equals(root, StringComparison.OrdinalIgnoreCase)) result.Add(i);
        }
        return result;
    }

    // The fixed folder of a pattern: "Fantasy" for "Fantasy/*/*.package", "Fantasy/Hair" for "Fantasy/Hair/*.package".
    private static string RootOf(string pattern)
    {
        var segments = pattern.Split('/');
        return string.Join('/', segments.Take(segments.Length - 1).TakeWhile(seg => seg.IndexOfAny(new[] { '*', '?' }) < 0));
    }

    public enum FolderDeleteOutcome { Deleted, NotAllowed, NoPackages, MoveFailed, Failed }

    // Packages, when Resource.cfg loads it and it is there: where a deleted folder's files can be kept.
    public async Task<long?> GetPackagesFolderIdAsync()
    {
        if (!GetRootFolderPaths().Contains(DefaultImportFolder) || !Directory.Exists(FolderFullPath(DefaultImportFolder))) return null;
        return await _db.ModsFolders.AsNoTracking().Where(f => f.RelativePath == DefaultImportFolder).Select(f => (long?)f.Id).FirstOrDefaultAsync();
    }

    // Packages and Overrides can't go: imports, the custom icons and the Hider live there.
    public static bool IsStandardRoot(string relativePath) => StandardRoots.Contains(relativePath, StringComparer.OrdinalIgnoreCase);

    // Deletes a Mods folder like a set: its files either move to Packages first (moveToPackages) or go with it, to the
    // Recycle Bin or for good (permanent). A folder Resource.cfg loads also loses its lines there.
    public async Task<FolderDeleteOutcome> DeleteFolderAsync(long folderId, bool moveToPackages = false, bool permanent = false)
    {
        var folder = await _db.ModsFolders.AsNoTracking().FirstOrDefaultAsync(f => f.Id == folderId);
        if (folder == null || IsStandardRoot(folder.RelativePath)) return FolderDeleteOutcome.NotAllowed;
        string rel = folder.RelativePath;
        string full = FolderFullPath(rel);

        if (moveToPackages)
        {
            var packagesId = await GetPackagesFolderIdAsync();
            if (packagesId == null) return FolderDeleteOutcome.NoPackages;
            string prefix = full.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var ids = await _db.EveryMetaEntity.Where(m => m.IsModsItem && m.CompleteFileName.StartsWith(prefix)).Select(m => m.Id).ToListAsync();
            if (ids.Count > 0)
            {
                var moved = await MoveToFolderAsync(ids, packagesId.Value);
                // Files left behind would go with the folder, so it stays.
                if (moved.Skipped.Count > 0)
                {
                    await ScanAsync();
                    return FolderDeleteOutcome.MoveFailed;
                }
            }
        }

        bool removed;
        if (!Directory.Exists(full)) removed = true;
        else if (permanent)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(full, true);
                removed = true;
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"Could not delete Mods folder {full}", ex, "ModsFolderService");
                removed = false;
            }
        }
        else removed = RecycleBinHelper.SendToRecycleBin(full);

        if (removed) RemoveCfgLinesFor(rel);
        await ScanAsync();
        return removed ? FolderDeleteOutcome.Deleted : FolderDeleteOutcome.Failed;
    }

    // Drops the PackedFile and Scan lines of Mods/Resource.cfg that load from this folder or a folder inside it.
    private void RemoveCfgLinesFor(string relativePath)
    {
        string cfgPath = Path.Combine(GetModsFolderPath(), "Resource.cfg");
        if (!File.Exists(cfgPath)) return;
        try
        {
            var lines = ReadCfgLines(cfgPath);
            bool Inside(string dir) => dir.Equals(relativePath, StringComparison.OrdinalIgnoreCase) ||
                                       dir.StartsWith(relativePath + "/", StringComparison.OrdinalIgnoreCase);
            int before = lines.Count;
            lines.RemoveAll(line =>
            {
                string t = line.Trim();
                int space = t.IndexOfAny(new[] { ' ', '\t' });
                if (space <= 0) return false;
                string keyword = t[..space];
                string arg = t[space..].Trim().Trim('"').Replace('\\', '/');
                if (keyword.Equals("PackedFile", StringComparison.OrdinalIgnoreCase)) return RootOf(arg).Length > 0 && Inside(RootOf(arg));
                if (keyword.Equals("Scan", StringComparison.OrdinalIgnoreCase)) return Inside(arg.TrimEnd('/'));
                return false;
            });
            if (lines.Count == before) return;
            RemoveDeadPriorityLines(lines);
            WritableFile.WriteAllLines(cfgPath, lines);
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"Could not remove {relativePath} from Mods/Resource.cfg", ex, "ModsFolderService");
        }
    }

    // A Priority line followed only by another Priority line (or nothing) no longer applies to anything.
    private static void RemoveDeadPriorityLines(List<string> lines)
    {
        static bool IsPriority(string l) => l.TrimStart().StartsWith("Priority", StringComparison.OrdinalIgnoreCase);
        static bool IsBlank(string l) { var t = l.Trim(); return t.Length == 0 || t.StartsWith('#') || t.StartsWith("//", StringComparison.Ordinal); }
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            if (!IsPriority(lines[i])) continue;
            int next = i + 1;
            while (next < lines.Count && IsBlank(lines[next])) next++;
            if (next >= lines.Count || IsPriority(lines[next])) lines.RemoveAt(i);
        }
    }
}
