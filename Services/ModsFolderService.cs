using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PlumbobForge.Backend.Configuration;
using PlumbobForge.Backend.Database;
using S3ForgeTools.Utils;

namespace PlumbobForge.Backend.Services;

// Files in the game's Mods folder, managed where they lie. A scan reads Mods/Resource.cfg, finds the files it loads
// (plus .disabled ones and packages/Sims3Packs in the same folders it doesn't load) and keeps one item per file and
// one ModsFolders row per real folder. Rows are matched by path first, then by size and write time, so tags and
// favourites survive a rename or move done in Explorer.
public partial class ModsFolderService
{
    private readonly AppDbContext _db;
    private readonly IOptionsMonitor<PlumbobForgeOptions> _options;
    private readonly PackageTypeService _packageTypeService;
    private readonly IServiceProvider _services;

    // One scan at a time across scopes (startup, watcher, manual refresh).
    private static readonly SemaphoreSlim ScanLock = new(1, 1);

    public ModsFolderService(AppDbContext db, IOptionsMonitor<PlumbobForgeOptions> options, PackageTypeService packageTypeService, IServiceProvider services)
    {
        _db = db;
        _options = options;
        _packageTypeService = packageTypeService;
        _services = services;
    }

    public string GetModsFolderPath()
    {
        string s3 = Sims3PathResolver.ResolveSims3UserFolder(_options.CurrentValue.Sims3UserDir);
        return string.IsNullOrWhiteSpace(s3) ? string.Empty : Path.Combine(s3, "Mods");
    }

    public sealed record ScanResult(int Added, int Updated, int Removed, int Folders);

    private sealed record Found(string FullPath, string RelativePath, string RelativeKey, bool Disabled, long Length, long WriteTicks, int? Priority);

    public async Task<ScanResult> ScanAsync(CancellationToken ct = default)
    {
        if (!ModsFolderSupport.IsEnabled) return new ScanResult(0, 0, 0, 0);
        await ScanLock.WaitAsync(ct);
        try
        {
            return await ScanCoreAsync(ct);
        }
        finally
        {
            ScanLock.Release();
        }
    }

    private async Task<ScanResult> ScanCoreAsync(CancellationToken ct)
    {
        string modsDir = GetModsFolderPath();
        var (found, dirs) = Directory.Exists(modsDir)
            ? await Task.Run(() => FindFiles(modsDir), ct)
            : (new List<Found>(), new List<string>());

        var existing = await _db.EveryMetaEntity.Where(m => m.IsModsItem).ToListAsync(ct);
        var byKey = new Dictionary<string, MetaEntity>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in existing)
        {
            string? key = RelativeKeyOf(modsDir, m.CompleteFileName);
            if (key != null) byKey.TryAdd(key, m);
        }

        var matched = new HashSet<long>();
        var unmatchedFound = new List<Found>();
        // The on/off switch is read back once folders are linked, since the configuration works per folder.
        var states = new List<(MetaEntity Meta, bool Disabled, bool IsNew)>();
        int updated = 0;
        foreach (var f in found)
        {
            if (byKey.TryGetValue(f.RelativeKey, out var meta) && matched.Add(meta.Id))
            {
                states.Add((meta, f.Disabled, false));
                if (Apply(meta, f, refreshType: false)) updated++;
            }
            else
            {
                unmatchedFound.Add(f);
            }
        }

        // Moved or renamed outside the app: same size and write time as a row whose file is gone.
        var missing = existing.Where(m => !matched.Contains(m.Id)).ToList();
        var missingByHash = missing.GroupBy(m => m.Filehash).ToDictionary(g => g.Key, g => new Queue<MetaEntity>(g));
        var toAdd = new List<Found>();
        foreach (var f in unmatchedFound)
        {
            if (missingByHash.TryGetValue(HashOf(f), out var queue) && queue.Count > 0)
            {
                var meta = queue.Dequeue();
                matched.Add(meta.Id);
                states.Add((meta, f.Disabled, false));
                Apply(meta, f, refreshType: false);
                updated++;
            }
            else
            {
                toAdd.Add(f);
            }
        }

        var folders = await SyncFoldersAsync(found, dirs, ct);

        int added = 0;
        foreach (var f in toAdd)
        {
            ct.ThrowIfCancellationRequested();
            var meta = new MetaEntity
            {
                IsModsItem = true,
                InstallDate = new DateTime(f.WriteTicks, DateTimeKind.Utc).ToLocalTime().ToString(),
                Manifest = string.Empty,
                Description = string.Empty,
            };
            Apply(meta, f, refreshType: true);
            states.Add((meta, f.Disabled, true));
            _db.MetaEntities.Add(meta);
            added++;
        }

        int removed = 0;
        foreach (var meta in existing.Where(m => !matched.Contains(m.Id)))
        {
            _db.MetaEntities.Remove(meta);
            removed++;
        }

        await _db.SaveChangesAsync(ct);

        // Folder links for every item, now that new folders have ids.
        var all = await _db.EveryMetaEntity.Where(m => m.IsModsItem).ToListAsync(ct);
        foreach (var meta in all)
        {
            string? key = RelativeKeyOf(modsDir, meta.CompleteFileName);
            string dir = key == null ? string.Empty : ParentOf(key);
            long? folderId = dir.Length > 0 && folders.TryGetValue(dir, out var folder) ? folder.Id : null;
            if (meta.ModsFolderId != folderId) meta.ModsFolderId = folderId;
        }
        await _db.SaveChangesAsync(ct);

        await RemoveStaleFoldersAsync(folders.Keys, ct);

        var applied = await ReconcileAfterScanAsync(states, ct);

        return new ScanResult(added, updated + applied.Changed, removed, folders.Count);
    }

    private bool Apply(MetaEntity meta, Found f, bool refreshType)
    {
        bool changed = false;
        string fileName = ModsResourceCfg.StripDisabled(Path.GetFileName(f.FullPath));
        string hash = HashOf(f);
        bool contentChanged = meta.Filehash != hash;

        if (meta.CompleteFileName != f.FullPath) { meta.CompleteFileName = f.FullPath; changed = true; }
        if (meta.FileName != fileName) { meta.FileName = fileName; changed = true; }
        if (meta.ModsPriority != f.Priority) { meta.ModsPriority = f.Priority; changed = true; }
        if (contentChanged)
        {
            meta.Filehash = hash;
            meta.FileSize = f.Length / 1024.0;
            changed = true;
        }

        bool isSims3Pack = fileName.EndsWith(".sims3pack", StringComparison.OrdinalIgnoreCase);
        string fileType = isSims3Pack ? "TS3PACK" : "DBPF";
        if (meta.FileType != fileType) { meta.FileType = fileType; changed = true; }

        // The type is detected once, and again when the file's content changes, unless the user tagged it.
        if (refreshType || (contentChanged && !meta.IsUserTagged))
        {
            PackageTypeInfo typeInfo = PackageTypeInfo.Other;
            try { typeInfo = _packageTypeService.DetectPackageType(f.FullPath, isSims3Pack); }
            catch { }
            meta.PackageType = typeInfo.PackageType;
            meta.CASCategories = typeInfo.CASCategories;
            meta.CASAge = typeInfo.CASAge;
            meta.CASGender = typeInfo.CASGender;
            meta.CASOutfitCategory = typeInfo.CASOutfitCategory;
            typeInfo.ApplyBuildBuyTo(meta);
            changed = true;
        }
        return changed;
    }

    private static string HashOf(Found f) => $"{f.Length}_{f.WriteTicks}";

    // Files and folders under the folders Resource.cfg loads packages from. Folders are listed even when empty, so a
    // folder made in the app shows up before anything is moved into it.
    private static (List<Found> Files, List<string> Dirs) FindFiles(string modsDir)
    {
        var cfg = ModsResourceCfg.Load(modsDir);
        var result = new List<Found>();
        var dirs = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (relDir, recursive) in cfg.SearchRoots())
        {
            string dir = relDir.Length == 0 ? cfg.ModsDir : Path.Combine(cfg.ModsDir, relDir.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(dir)) continue;
            Walk(cfg, dir, recursive, result, dirs, seen);
        }
        return (result, dirs);
    }

    private static void Walk(ModsResourceCfg cfg, string dir, bool recursive, List<Found> files, List<string> dirs, HashSet<string> seen)
    {
        string? relDir = cfg.ToRelative(dir);
        if (relDir == null || ModsResourceCfg.IsPlumbobForgePath(relDir) || !seen.Add(dir)) return;
        if (relDir.Length > 0) dirs.Add(relDir);

        string[] entries;
        try { entries = Directory.GetFiles(dir); }
        catch { entries = Array.Empty<string>(); }
        foreach (var path in entries)
        {
            if (!IsModFile(path)) continue;
            string? rel = cfg.ToRelative(path);
            if (rel == null || ModsResourceCfg.IsPlumbobForgePath(rel)) continue;
            FileInfo info;
            try { info = new FileInfo(path); } catch { continue; }
            files.Add(new Found(path, rel, ModsResourceCfg.StripDisabled(rel), ModsResourceCfg.IsDisabledName(path),
                info.Length, info.LastWriteTimeUtc.Ticks, cfg.PriorityOf(rel)));
        }
        if (!recursive) return;

        string[] subDirs;
        try { subDirs = Directory.GetDirectories(dir); }
        catch { return; }
        foreach (var sub in subDirs)
        {
            // Junctions and symlinks could loop.
            try { if (new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue; } catch { continue; }
            Walk(cfg, sub, true, files, dirs, seen);
        }
    }

    public static bool IsModFile(string path)
    {
        string name = ModsResourceCfg.StripDisabled(Path.GetFileName(path));
        return name.EndsWith(".package", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".sims3pack", StringComparison.OrdinalIgnoreCase);
    }

    // Relative path inside Mods without .disabled, e.g. "Packages/Hair/foo.package".
    private static string? RelativeKeyOf(string modsDir, string fullPath)
    {
        if (string.IsNullOrEmpty(modsDir) || string.IsNullOrEmpty(fullPath)) return null;
        string rel;
        try { rel = Path.GetRelativePath(modsDir, fullPath).Replace('\\', '/'); }
        catch { return null; }
        if (rel.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(rel)) return null;
        return ModsResourceCfg.StripDisabled(rel);
    }

    private static string ParentOf(string relativePath)
    {
        int slash = relativePath.LastIndexOf('/');
        return slash < 0 ? string.Empty : relativePath[..slash];
    }

    // One row per folder that holds a found file, plus every folder above it. Existing rows keep the user's
    // icon, colour and description.
    private async Task<Dictionary<string, ModsFolderEntity>> SyncFoldersAsync(List<Found> found, List<string> dirs, CancellationToken ct)
    {
        var rows = await _db.ModsFolders.ToListAsync(ct);
        var byPath = new Dictionary<string, ModsFolderEntity>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows) byPath.TryAdd(r.RelativePath, r);

        var needed = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in dirs.Concat(found.Select(f => ParentOf(f.RelativeKey))))
        {
            string dir = path;
            while (dir.Length > 0 && needed.Add(dir)) dir = ParentOf(dir);
        }

        // Parents first, so each child can point at its parent.
        foreach (var path in needed.OrderBy(p => p.Count(c => c == '/')))
        {
            if (byPath.ContainsKey(path)) continue;
            var row = new ModsFolderEntity
            {
                RelativePath = path,
                Name = path[(path.LastIndexOf('/') + 1)..],
                IsExpanded = true
            };
            string parent = ParentOf(path);
            if (parent.Length > 0 && byPath.TryGetValue(parent, out var parentRow)) row.ParentModsFolder = parentRow;
            _db.ModsFolders.Add(row);
            byPath[path] = row;
        }
        await _db.SaveChangesAsync(ct);
        return byPath.Where(kv => needed.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    // Rows of folders that are gone go, with the user's customisation. Deepest first: the parent link is Restrict.
    private async Task RemoveStaleFoldersAsync(IEnumerable<string> keepPaths, CancellationToken ct)
    {
        var keep = new HashSet<string>(keepPaths, StringComparer.OrdinalIgnoreCase);
        var drop = (await _db.ModsFolders.ToListAsync(ct))
            .Where(f => !keep.Contains(f.RelativePath))
            .OrderByDescending(f => f.RelativePath.Count(c => c == '/'))
            .ToList();
        foreach (var f in drop)
        {
            _db.ModsFolders.Remove(f);
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task<ModsFolderEntity?> UpdateFolderAsync(long folderId, string? icon, string? color, string? description)
    {
        var folder = await _db.ModsFolders.FirstOrDefaultAsync(f => f.Id == folderId);
        if (folder == null) return null;
        folder.Icon = string.IsNullOrWhiteSpace(icon) ? null : icon;
        folder.Color = string.IsNullOrWhiteSpace(color) ? null : color;
        folder.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        await _db.SaveChangesAsync();
        return folder;
    }

    public async Task SetFolderExpandedAsync(long folderId, bool expanded)
    {
        var folder = await _db.ModsFolders.FirstOrDefaultAsync(f => f.Id == folderId);
        if (folder == null || folder.IsExpanded == expanded) return;
        folder.IsExpanded = expanded;
        await _db.SaveChangesAsync();
    }
}
