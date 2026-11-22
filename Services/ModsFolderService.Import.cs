using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PlumbobForge.Backend.Configuration;
using PlumbobForge.Backend.Database;
using S3ForgeTools.GameFiles.TS3Pack;
using S3ForgeTools.Utils;

namespace PlumbobForge.Backend.Services;

// Imports straight into the game's Mods folder (Mods-only storage, or a Mods folder picked as the target).
// Packages are copied, archives unpacked, and Sims3Packs split into one .package per part. Lots, Sims and worlds
// need the library and are skipped.
public partial class ModsFolderService
{
    public const string DefaultImportFolder = "Packages";

    public async Task<List<MetaEntity>> ImportToModsAsync(IEnumerable<string> files, long? folderId = null, string duplicateAction = "rename", Action<string>? onProgress = null)
    {
        string modsDir = GetModsFolderPath();
        if (string.IsNullOrEmpty(modsDir)) return new List<MetaEntity>();

        string relative = DefaultImportFolder;
        if (folderId.HasValue)
        {
            var folder = await _db.ModsFolders.AsNoTracking().FirstOrDefaultAsync(f => f.Id == folderId.Value);
            if (folder != null) relative = folder.RelativePath;
        }
        string targetDir = Path.Combine(modsDir, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(targetDir);
        await EnsureResourceCfgAsync(modsDir);

        var pkg = _services.GetRequiredService<PKGManager>();
        string stage = Path.Combine(Path.GetTempPath(), "PlumbobForge", "ModsImport", Guid.NewGuid().ToString("N"));
        var copied = new List<string>();
        try
        {
            foreach (string file in files)
            {
                if (!File.Exists(file)) continue;
                string name = Path.GetFileName(file);
                if (PKGManager.IsArchiveExtension(name))
                {
                    string archiveStage = Path.Combine(stage, Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(archiveStage);
                    foreach (string extracted in pkg.ExtractPackagesFromArchive(file, archiveStage, "rename", onProgress))
                        ImportOne(extracted, targetDir, duplicateAction, pkg, stage, copied, onProgress);
                }
                else
                {
                    ImportOne(file, targetDir, duplicateAction, pkg, stage, copied, onProgress);
                }
            }
        }
        finally
        {
            try { if (Directory.Exists(stage)) Directory.Delete(stage, true); } catch { }
        }

        if (copied.Count == 0) return new List<MetaEntity>();
        await ScanAsync();
        var paths = new HashSet<string>(copied, StringComparer.OrdinalIgnoreCase);
        var rows = await _db.EveryMetaEntity.AsNoTracking().Where(m => m.IsModsItem && m.ParentMetaEntityId == null).ToListAsync();
        return rows.Where(m => paths.Contains(m.CompleteFileName)).ToList();
    }

    private void ImportOne(string file, string targetDir, string duplicateAction, PKGManager pkg, string stage, List<string> copied, Action<string>? onProgress)
    {
        string name = Path.GetFileName(file);
        string ext = Path.GetExtension(name).ToLowerInvariant();
        if (ext == ".sims3pack")
        {
            ImportSims3Pack(file, targetDir, duplicateAction, stage, copied, onProgress);
            return;
        }
        if (ext == ".world" || ext == ".sim" || (ext == ".package" && pkg.StaysInLibrary(file)))
        {
            onProgress?.Invoke($"Skipped {name}: lots, Sims and worlds need the library.");
            return;
        }
        if (ext != ".package") return;
        CopyInto(file, targetDir, name, duplicateAction, copied, onProgress);
    }

    // Each part of a Sims3Pack becomes "<name>_PartN.package"; parts holding a lot, Sim or world are left out.
    private void ImportSims3Pack(string file, string targetDir, string duplicateAction, string stage, List<string> copied, Action<string>? onProgress)
    {
        string baseName = Path.GetFileNameWithoutExtension(file);
        try
        {
            using var s3p = new Sims3Pack(file);
            if (s3p.IsCorrupt)
            {
                onProgress?.Invoke($"Skipped {Path.GetFileName(file)}: the Sims3Pack is damaged.");
                return;
            }
            Directory.CreateDirectory(stage);
            int index = 0;
            int skipped = 0;
            foreach (var part in s3p.Packages)
            {
                index++;
                if (part.Resources.Any(r => r.Key.Type == 107542056 || r.Key.Type == 3496170587u || r.Key.Type == 83396964))
                {
                    skipped++;
                    continue;
                }
                string partName = $"{baseName}_Part{index}.package";
                string staged = Path.Combine(stage, Guid.NewGuid().ToString("N") + ".package");
                using (var fs = new FileStream(staged, FileMode.Create, FileAccess.Write))
                {
                    part.Export(fs);
                }
                CopyInto(staged, targetDir, partName, duplicateAction, copied, onProgress);
            }
            if (skipped > 0) onProgress?.Invoke($"{skipped} part(s) of {Path.GetFileName(file)} are lots, Sims or worlds and were skipped.");
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"Could not unpack Sims3Pack into Mods: {file}", ex, "ModsFolderService");
            onProgress?.Invoke($"Could not unpack {Path.GetFileName(file)}: {ex.Message}");
        }
    }

    private static void CopyInto(string source, string targetDir, string name, string duplicateAction, List<string> copied, Action<string>? onProgress)
    {
        var (dest, skip) = ArchiveService.ResolveDestinationPath(targetDir, name, duplicateAction);
        if (skip || dest == null)
        {
            onProgress?.Invoke($"Skipped duplicate file: {name}");
            return;
        }
        WritableFile.Copy(source, dest);
        copied.Add(dest);
        onProgress?.Invoke($"Imported file: {name}");
    }

    // Mods-only users may have no Mods/Resource.cfg yet; without it the game loads nothing from Mods.
    private async Task EnsureResourceCfgAsync(string modsDir)
    {
        if (File.Exists(Path.Combine(modsDir, "Resource.cfg"))) return;
        try
        {
            await _services.GetRequiredService<CacheBuilderService>().SyncToSims3Async();
        }
        catch (Exception ex)
        {
            AppLogger.LogError("Could not write Mods/Resource.cfg", ex, "ModsFolderService");
        }
    }
}
