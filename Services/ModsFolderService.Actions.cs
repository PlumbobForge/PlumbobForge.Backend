using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PlumbobForge.Backend.Database;
using S3ForgeTools.GameFiles.Package;
using S3ForgeTools.Utils;

namespace PlumbobForge.Backend.Services;

// Changes to the Mods folder made from the app: folders, renames, deletes and moves between Mods folders, sets
// and the vault. Every change is made on disk first and then read back with a scan, so the database always
// matches what the game sees.
public partial class ModsFolderService
{
    public enum SkipReason { Missing, Sims3Pack, LibraryOnly, Part, DisabledObjects, NameTaken, Failed }

    public sealed record Skipped(string FileName, SkipReason Reason);

    public sealed record ActionResult(int Done, IReadOnlyList<Skipped> Skipped)
    {
        public static readonly ActionResult Nothing = new(0, Array.Empty<Skipped>());
    }

    // Folders Resource.cfg names (e.g. "Packages", "Overrides", "Fantasy"): renaming or deleting one would stop the
    // game loading it, so they can't be.
    public HashSet<string> GetRootFolderPaths()
    {
        string modsDir = GetModsFolderPath();
        if (string.IsNullOrEmpty(modsDir) || !Directory.Exists(modsDir)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return ModsResourceCfg.Load(modsDir).SearchRoots()
            .Where(r => r.RelativeDir.Length > 0)
            .Select(r => r.RelativeDir)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private string FolderFullPath(string relativePath) =>
        Path.Combine(GetModsFolderPath(), relativePath.Replace('/', Path.DirectorySeparatorChar));

    // Returns the new folder's relative path, or null when the name is taken or invalid.
    public async Task<string?> CreateFolderAsync(long parentFolderId, string name)
    {
        var parent = await _db.ModsFolders.FirstOrDefaultAsync(f => f.Id == parentFolderId);
        string? clean = CleanName(name);
        if (parent == null || clean == null) return null;

        string full = Path.Combine(FolderFullPath(parent.RelativePath), clean);
        if (Directory.Exists(full) || File.Exists(full)) return null;
        try { Directory.CreateDirectory(full); }
        catch (Exception ex)
        {
            AppLogger.LogError($"Could not create Mods folder {full}", ex, "ModsFolderService");
            return null;
        }
        await ScanAsync();
        return parent.RelativePath + "/" + clean;
    }

    // Renames the real folder. Rows below it are moved along first, so customisation, tags and favourites stay.
    public async Task<bool> RenameFolderAsync(long folderId, string newName)
    {
        var folder = await _db.ModsFolders.FirstOrDefaultAsync(f => f.Id == folderId);
        string? clean = CleanName(newName);
        if (folder == null || clean == null || GetRootFolderPaths().Contains(folder.RelativePath)) return false;
        if (clean == folder.Name) return true;

        string parentRel = ParentOf(folder.RelativePath);
        string newRel = parentRel.Length == 0 ? clean : parentRel + "/" + clean;
        string oldFull = FolderFullPath(folder.RelativePath);
        string newFull = FolderFullPath(newRel);
        bool caseOnly = string.Equals(oldFull, newFull, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && (Directory.Exists(newFull) || File.Exists(newFull))) return false;

        await ScanLock.WaitAsync();
        try
        {
            if (caseOnly)
            {
                string temp = oldFull + "_" + Guid.NewGuid().ToString("N")[..6];
                Directory.Move(oldFull, temp);
                Directory.Move(temp, newFull);
            }
            else
            {
                Directory.Move(oldFull, newFull);
            }

            string oldPrefix = folder.RelativePath + "/";
            foreach (var f in await _db.ModsFolders.ToListAsync())
            {
                if (f.Id == folder.Id)
                {
                    f.RelativePath = newRel;
                    f.Name = clean;
                }
                else if (f.RelativePath.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    f.RelativePath = newRel + "/" + f.RelativePath[oldPrefix.Length..];
                }
            }

            string oldFullPrefix = oldFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string newFullPrefix = newFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var m in await _db.EveryMetaEntity.Where(m => m.IsModsItem).ToListAsync())
            {
                if (m.CompleteFileName.StartsWith(oldFullPrefix, StringComparison.OrdinalIgnoreCase))
                    m.CompleteFileName = newFullPrefix + m.CompleteFileName[oldFullPrefix.Length..];
            }
            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"Could not rename Mods folder {oldFull}", ex, "ModsFolderService");
            return false;
        }
        finally
        {
            ScanLock.Release();
        }

        await ScanAsync();
        return true;
    }

    // Sends the folder and everything in it to the Recycle Bin.
    public async Task<bool> DeleteFolderAsync(long folderId)
    {
        var folder = await _db.ModsFolders.FirstOrDefaultAsync(f => f.Id == folderId);
        if (folder == null || GetRootFolderPaths().Contains(folder.RelativePath)) return false;
        string full = FolderFullPath(folder.RelativePath);
        bool ok = !Directory.Exists(full) || RecycleBinHelper.SendToRecycleBin(full);
        await ScanAsync();
        return ok;
    }

    // Renames a Mods file, keeping its extension and ".disabled".
    public async Task<bool> RenameItemAsync(long itemId, string newBaseName)
    {
        var meta = await _db.EveryMetaEntity.FirstOrDefaultAsync(m => m.Id == itemId && m.IsModsItem);
        string? clean = CleanName(newBaseName);
        if (meta == null || clean == null || !File.Exists(meta.CompleteFileName)) return false;

        string ext = Path.GetExtension(ModsResourceCfg.StripDisabled(meta.CompleteFileName));
        if (clean.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) clean = clean[..^ext.Length];
        string suffix = ModsResourceCfg.IsDisabledName(meta.CompleteFileName) ? ModsResourceCfg.DisabledSuffix : string.Empty;
        string target = Path.Combine(Path.GetDirectoryName(meta.CompleteFileName)!, clean + ext + suffix);
        if (string.Equals(target, meta.CompleteFileName, StringComparison.Ordinal)) return true;
        if (!string.Equals(target, meta.CompleteFileName, StringComparison.OrdinalIgnoreCase) && NameTaken(target)) return false;

        try { WritableFile.Move(meta.CompleteFileName, target); }
        catch (Exception ex)
        {
            AppLogger.LogError($"Could not rename Mods file {meta.CompleteFileName}", ex, "ModsFolderService");
            return false;
        }
        meta.CompleteFileName = target;
        meta.FileName = clean + ext;
        await _db.SaveChangesAsync();
        return true;
    }

    // Sends Mods files to the Recycle Bin.
    public async Task<ActionResult> DeleteItemsAsync(IEnumerable<long> itemIds)
    {
        var ids = itemIds.ToList();
        var metas = await _db.EveryMetaEntity.Where(m => ids.Contains(m.Id) && m.IsModsItem).ToListAsync();
        var skipped = new List<Skipped>();
        int done = 0;
        foreach (var meta in metas)
        {
            if (File.Exists(meta.CompleteFileName) && !RecycleBinHelper.SendToRecycleBin(meta.CompleteFileName))
            {
                skipped.Add(new Skipped(meta.FileName, SkipReason.Failed));
                continue;
            }
            _db.MetaEntities.Remove(meta);
            done++;
        }
        await _db.SaveChangesAsync();
        return new ActionResult(done, skipped);
    }

    // Moves items into a Mods folder: Mods files are moved on disk, library files are moved out of the library,
    // vault items are written out as packages and taken out of the vault.
    public async Task<ActionResult> MoveToFolderAsync(IEnumerable<long> itemIds, long folderId)
    {
        var folder = await _db.ModsFolders.AsNoTracking().FirstOrDefaultAsync(f => f.Id == folderId);
        if (folder == null) return ActionResult.Nothing;
        string targetDir = FolderFullPath(folder.RelativePath);
        if (!Directory.Exists(targetDir)) return ActionResult.Nothing;

        var ids = itemIds.Distinct().ToList();
        var metas = await _db.EveryMetaEntity.Where(m => ids.Contains(m.Id)).ToListAsync();
        var skipped = new List<Skipped>();
        var dirtySets = new HashSet<long>();
        var fromVault = new List<(MetaEntity Meta, List<string> Files)>();
        int done = 0;

        await ScanLock.WaitAsync();
        try
        {
            foreach (var meta in metas)
            {
                if (meta.IsModsItem)
                {
                    if (!File.Exists(meta.CompleteFileName)) { skipped.Add(new Skipped(meta.FileName, SkipReason.Missing)); continue; }
                    if (string.Equals(Path.GetDirectoryName(meta.CompleteFileName), targetDir.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) continue;
                    string target = FreeName(targetDir, Path.GetFileName(meta.CompleteFileName));
                    try { WritableFile.Move(meta.CompleteFileName, target); }
                    catch (Exception ex)
                    {
                        AppLogger.LogError($"Could not move Mods file {meta.CompleteFileName}", ex, "ModsFolderService");
                        skipped.Add(new Skipped(meta.FileName, SkipReason.Failed));
                        continue;
                    }
                    meta.CompleteFileName = target;
                    meta.FileName = ModsResourceCfg.StripDisabled(Path.GetFileName(target));
                    done++;
                    continue;
                }

                var reason = await WhyNotToModsAsync(meta);
                if (reason != null) { skipped.Add(new Skipped(meta.FileName, reason.Value)); continue; }

                if (meta.IsVaultManaged)
                {
                    var files = await ExportVaultItemAsync(meta, targetDir);
                    if (files == null) { skipped.Add(new Skipped(meta.FileName, SkipReason.Failed)); continue; }
                    fromVault.Add((meta, files));
                    continue;
                }

                // A library file becomes a Mods item in place: same id, so its preview, tags and optimizer backups stay.
                if (!File.Exists(meta.CompleteFileName)) { skipped.Add(new Skipped(meta.FileName, SkipReason.Missing)); continue; }
                string name = meta.FileName + (meta.Enabled ? string.Empty : ModsResourceCfg.DisabledSuffix);
                string dest = FreeName(targetDir, name);
                string source = meta.CompleteFileName;
                long? oldSet = meta.SetsEntityId;

                // The database changes first, so the library watcher never sees a library item whose file is gone.
                meta.IsModsItem = true;
                meta.SetsEntityId = null;
                meta.CompleteFileName = dest;
                meta.FileName = ModsResourceCfg.StripDisabled(Path.GetFileName(dest));
                meta.ExcludedByAnyConfig = false;
                _db.ConfigItemOverrides.RemoveRange(_db.ConfigItemOverrides.Where(o => o.MetaEntityId == meta.Id));
                await _db.SaveChangesAsync();
                try
                {
                    WritableFile.Move(source, dest);
                }
                catch (Exception ex)
                {
                    AppLogger.LogError($"Could not move library file {source} to Mods", ex, "ModsFolderService");
                    meta.IsModsItem = false;
                    meta.SetsEntityId = oldSet;
                    meta.CompleteFileName = source;
                    meta.FileName = Path.GetFileName(source);
                    await _db.SaveChangesAsync();
                    skipped.Add(new Skipped(Path.GetFileName(source), SkipReason.Failed));
                    continue;
                }
                if (oldSet.HasValue) dirtySets.Add(oldSet.Value);
                if (meta.DisabledObjectCount > 0) await ParkDisabledObjectsAsync(meta);
                done++;
            }
            await _db.SaveChangesAsync();
        }
        finally
        {
            ScanLock.Release();
        }

        // Vault items: the written packages are new Mods items that take over the user's tags; then the vault lets go.
        if (fromVault.Count > 0)
        {
            await ScanAsync();
            var vault = _services.GetRequiredService<VaultService>();
            foreach (var (meta, files) in fromVault)
            {
                foreach (var file in files)
                {
                    var row = await _db.EveryMetaEntity.FirstOrDefaultAsync(m => m.IsModsItem && m.CompleteFileName == file);
                    if (row != null) CopyUserFields(meta, row, keepType: files.Count == 1);
                }
                if (meta.SetsEntityId.HasValue) dirtySets.Add(meta.SetsEntityId.Value);
                await _db.SaveChangesAsync();
                if (await vault.DeleteModsBatchAsync(new[] { meta.Id })) done++;
                else skipped.Add(new Skipped(meta.FileName, SkipReason.Failed));
            }
        }

        if (dirtySets.Count > 0)
        {
            await SetDirtyTracker.UpdateDirtyStatesForSetsAsync(_db, dirtySets);
            await _db.SaveChangesAsync();
        }
        await ScanAsync();
        return new ActionResult(done, skipped);
    }

    // Moves Mods files into a set: imported like any file (into the library folder or the vault), then the Mods
    // file goes to the Recycle Bin. Tags, favourites and on/off carry over.
    public async Task<ActionResult> MoveToSetAsync(IEnumerable<long> itemIds, long setId)
    {
        var ids = itemIds.Distinct().ToList();
        var metas = await _db.EveryMetaEntity.AsNoTracking().Where(m => ids.Contains(m.Id) && m.IsModsItem).ToListAsync();
        var skipped = new List<Skipped>();
        int done = 0;
        var pkg = _services.GetRequiredService<PKGManager>();
        var vault = _services.GetRequiredService<VaultService>();
        bool vaultMode = _options.CurrentValue.EnableSingleSourceVault;

        foreach (var meta in metas)
        {
            if (!File.Exists(meta.CompleteFileName)) { skipped.Add(new Skipped(meta.FileName, SkipReason.Missing)); continue; }

            // The import goes by extension, so a turned-off file is staged under its real name.
            string stageDir = Path.Combine(Path.GetTempPath(), "PF_Mods_Move_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(stageDir);
                string staged = Path.Combine(stageDir, meta.FileName);
                File.Copy(meta.CompleteFileName, staged);

                // Parked objects go back under their own keys; the new item turns them off its own way below.
                var parked = ReadParkedKeys(staged);
                if (parked.Count > 0)
                {
                    var edits = ModsObjectParking.BuildToggleEdits(staged, parked.Select(k => new ObjectToggle(k, true)).ToList());
                    PackageEditService.RewriteLibraryFile(staged, new[] { new PartEdits(0, null, edits) }, recycleOriginal: false);
                }

                var imported = await pkg.ImportFilesAsync(new[] { staged }, null, "rename", setId);
                if (imported.Count == 0) { skipped.Add(new Skipped(meta.FileName, SkipReason.Failed)); continue; }

                foreach (var created in imported)
                {
                    var row = await _db.EveryMetaEntity.FirstOrDefaultAsync(m => m.Id == created.Id);
                    if (row == null) continue;
                    CopyUserFields(meta, row, keepType: true);
                    if (!meta.Enabled)
                    {
                        if (row.IsVaultManaged)
                        {
                            await _db.SaveChangesAsync();
                            await vault.ToggleModAsync(row.Id, false);
                        }
                        else
                        {
                            row.Enabled = false;
                        }
                    }
                }
                await _db.SaveChangesAsync();
                if (parked.Count > 0)
                {
                    var editor = _services.GetRequiredService<PackageEditService>();
                    var off = parked.Select(k => new ObjectToggle(k, false)).ToList();
                    foreach (var created in imported.Where(c => c.ParentMetaEntityId == null))
                    {
                        await editor.SetObjectsEnabledAsync(created.Id, off);
                    }
                }
                if (!vaultMode) await SetDirtyTracker.UpdateDirtyStatesForSetsAsync(_db, new[] { setId });
                await _db.SaveChangesAsync();

                if (RecycleBinHelper.SendToRecycleBin(meta.CompleteFileName) || !File.Exists(meta.CompleteFileName))
                {
                    var stale = await _db.EveryMetaEntity.FirstOrDefaultAsync(m => m.Id == meta.Id);
                    if (stale != null) { _db.MetaEntities.Remove(stale); await _db.SaveChangesAsync(); }
                }
                done++;
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"Could not move Mods file {meta.CompleteFileName} into a set", ex, "ModsFolderService");
                skipped.Add(new Skipped(meta.FileName, SkipReason.Failed));
            }
            finally
            {
                try { if (Directory.Exists(stageDir)) Directory.Delete(stageDir, true); } catch { }
            }
        }

        await ScanAsync();
        return new ActionResult(done, skipped);
    }

    // A library item's turned-off objects were left out at rebuild; in Mods they are parked in the file instead. If
    // that fails the objects count as on again, which is what the game then sees.
    private async Task ParkDisabledObjectsAsync(MetaEntity meta)
    {
        var rows = await _db.DisabledObjects.Where(d => d.MetaEntityId == meta.Id).ToListAsync();
        if (rows.Count == 0) return;
        try
        {
            var toggles = rows.Select(r => new ObjectToggle(new TGI_Key(r.Type, r.Group, r.Instance), false)).ToList();
            string path = meta.CompleteFileName;
            await Task.Run(() =>
            {
                var edits = ModsObjectParking.BuildToggleEdits(path, toggles);
                if (edits.Count > 0) PackageEditService.RewriteLibraryFile(path, new[] { new PartEdits(0, null, edits) });
            });
            var info = new FileInfo(path);
            meta.FileSize = info.Length / 1024.0;
            meta.Filehash = $"{info.Length}_{info.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"Could not keep the turned-off objects of {meta.FileName} off in Mods", ex, "ModsFolderService");
            _db.DisabledObjects.RemoveRange(rows);
            meta.DisabledObjectCount = 0;
            meta.DisabledObjectsStamp = null;
        }
        await _db.SaveChangesAsync();
    }

    // Original keys of the objects parked (turned off) in a Mods package file.
    private static List<TGI_Key> ReadParkedKeys(string path)
    {
        if (ModsResourceCfg.StripDisabled(path).EndsWith(".sims3pack", StringComparison.OrdinalIgnoreCase)) return new List<TGI_Key>();
        using var pkg = new DBPFPackage(path);
        var keys = new List<TGI_Key>();
        foreach (var r in pkg.Resources)
        {
            if (ModsObjectParking.TryUnpark(r.Key, out var original)) keys.Add(original);
        }
        return keys;
    }

    private async Task<SkipReason?> WhyNotToModsAsync(MetaEntity meta)
    {
        if (meta.ParentMetaEntityId.HasValue) return SkipReason.Part;
        // A library file keeps its turned-off objects off by parking them; a vault item's would need every exported
        // file worked out, so it stays where it is.
        if (meta.DisabledObjectCount > 0 && meta.IsVaultManaged) return SkipReason.DisabledObjects;
        if (PackageTypeHelper.IsLibraryOnlyType(meta.PackageType) ||
            meta.FileName.EndsWith(".world", StringComparison.OrdinalIgnoreCase) ||
            meta.FileName.EndsWith(".sim", StringComparison.OrdinalIgnoreCase)) return SkipReason.LibraryOnly;
        if (!meta.IsVaultManaged && meta.FileName.EndsWith(".sims3pack", StringComparison.OrdinalIgnoreCase)) return SkipReason.Sims3Pack;
        // A Sims3Pack in the vault with lot or Sim parts: those parts can't live in Mods.
        if (meta.IsVaultManaged && await _db.MetaEntities.AnyAsync(m => m.ParentMetaEntityId == meta.Id && !m.IsVaultManaged))
            return SkipReason.LibraryOnly;
        return null;
    }

    // Writes a vault item out as package(s) into targetDir. Returns the files, or null when nothing was written.
    private async Task<List<string>?> ExportVaultItemAsync(MetaEntity meta, string targetDir)
    {
        string temp = Path.Combine(Path.GetTempPath(), "PF_Vault_ToMods_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temp);
            string baseName = Path.ChangeExtension(meta.FileName, ".package");
            var vault = _services.GetRequiredService<VaultService>();
            if (!await vault.ExportModAsync(meta.Id, Path.Combine(temp, baseName))) return null;

            var result = new List<string>();
            foreach (var file in Directory.GetFiles(temp, "*.package"))
            {
                string name = Path.GetFileName(file) + (meta.Enabled ? string.Empty : ModsResourceCfg.DisabledSuffix);
                string dest = FreeName(targetDir, name);
                WritableFile.Move(file, dest);
                result.Add(dest);
            }
            return result.Count > 0 ? result : null;
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"Could not write vault item {meta.FileName} to Mods", ex, "ModsFolderService");
            return null;
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { }
        }
    }

    private static void CopyUserFields(MetaEntity from, MetaEntity to, bool keepType)
    {
        to.IsFavorite = from.IsFavorite;
        to.UserTags = from.UserTags;
        to.Description = from.Description;
        to.URL = from.URL;
        to.InstallDate = from.InstallDate ?? to.InstallDate;
        if (keepType && from.IsUserTagged)
        {
            to.IsUserTagged = true;
            to.PackageType = from.PackageType;
            to.CASCategories = from.CASCategories;
            to.CASAge = from.CASAge;
            to.CASGender = from.CASGender;
            to.CASOutfitCategory = from.CASOutfitCategory;
            to.BBKind = from.BBKind;
            to.BBRooms = from.BBRooms;
            to.BBFunctions = from.BBFunctions;
        }
    }

    // A free file name in dir, "name (2).package" style; a turned-off copy of the name counts as taken.
    private static string FreeName(string dir, string fileName)
    {
        bool disabled = ModsResourceCfg.IsDisabledName(fileName);
        string plain = ModsResourceCfg.StripDisabled(fileName);
        string stem = Path.GetFileNameWithoutExtension(plain);
        string ext = Path.GetExtension(plain);
        string suffix = disabled ? ModsResourceCfg.DisabledSuffix : string.Empty;
        string candidate = Path.Combine(dir, plain + suffix);
        for (int n = 2; NameTaken(candidate); n++)
            candidate = Path.Combine(dir, $"{stem} ({n}){ext}{suffix}");
        return candidate;
    }

    private static bool NameTaken(string path)
    {
        string plain = ModsResourceCfg.StripDisabled(path);
        return File.Exists(plain) || File.Exists(plain + ModsResourceCfg.DisabledSuffix) || Directory.Exists(plain);
    }

    private static string? CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string clean = string.Join("_", name.Trim().Split(Path.GetInvalidFileNameChars())).Trim().TrimEnd('.');
        return clean.Length == 0 || clean == "." || clean == ".." ? null : clean;
    }
}
