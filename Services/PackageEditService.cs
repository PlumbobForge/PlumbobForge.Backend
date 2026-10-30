using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PlumbobForge.Backend.Database;
using S3ForgeTools.GameFiles.Package;
using S3ForgeTools.GameFiles.TS3Pack;
using S3ForgeTools.Utils.Logging;

namespace PlumbobForge.Backend.Services;

public class PackageEditService
{
    private static readonly ILog log = LogManager.GetLogger(nameof(PackageEditService));

    private readonly AppDbContext _db;
    private readonly VaultService _vaultService;
    private readonly ThumbnailService _thumbnailService;

    public PackageEditService(AppDbContext db, VaultService vaultService, ThumbnailService thumbnailService)
    {
        _db = db;
        _vaultService = vaultService;
        _thumbnailService = thumbnailService;
    }

    public async Task ApplyAsync(long metaEntityId, IReadOnlyList<PartEdits> parts)
    {
        parts = parts.Where(p => p.Edits.Count > 0).ToList();
        if (parts.Count == 0) return;

        var meta = await _db.MetaEntities.FirstOrDefaultAsync(m => m.Id == metaEntityId)
            ?? throw new InvalidOperationException($"Item {metaEntityId} no longer exists");

        if (meta.IsVaultManaged)
        {
            foreach (var part in parts)
            {
                long target = part.MetaEntityId ?? meta.Id;
                if (!await _vaultService.ApplyResourceEditsAsync(target, part.Edits))
                {
                    throw new InvalidOperationException($"The vault data of {meta.FileName} could not be updated");
                }
            }
            return;
        }

        Sims3GameProcess.ThrowIfGameRunning("Close The Sims 3 before changing the contents of an item.");
        string path = meta.CompleteFileName;
        if (!File.Exists(path)) throw new FileNotFoundException("The item's file is missing", path);

        await Task.Run(() => RewriteLibraryFile(path, parts));

        var info = new FileInfo(path);
        meta.FileSize = info.Length / 1024.0;
        meta.Filehash = $"{info.Length}_{info.LastWriteTimeUtc.Ticks}";
        if (meta.SetsEntityId.HasValue)
        {
            await SetDirtyTracker.UpdateDirtyStatesForSetsAsync(_db, new[] { meta.SetsEntityId.Value });
        }
        await _db.SaveChangesAsync();

        try
        {
            _thumbnailService.DeleteThumbnail(meta.Id);
            await _thumbnailService.GetThumbnailPathAsync(meta.Id, path);
        }
        catch (Exception ex)
        {
            log.Warn($"Could not regenerate thumbnail after editing {path}: {ex.Message}");
        }
    }

    public async Task SetObjectsEnabledAsync(long metaEntityId, IReadOnlyList<ObjectToggle> changes)
    {
        if (changes.Count == 0) return;

        var meta = await _db.MetaEntities.FirstOrDefaultAsync(m => m.Id == metaEntityId)
            ?? throw new InvalidOperationException($"Item {metaEntityId} no longer exists");

        var rows = await _db.DisabledObjects.Where(d => d.MetaEntityId == meta.Id).ToListAsync();
        foreach (var change in changes)
        {
            var key = change.Key;
            var existing = rows.Where(d => d.Type == key.Type && d.Group == key.Group && d.Instance == key.Instance).ToList();
            if (change.Enabled)
            {
                _db.DisabledObjects.RemoveRange(existing);
                rows.RemoveAll(existing.Contains);
            }
            else if (existing.Count == 0)
            {
                var row = new DisabledObjectEntity { MetaEntityId = meta.Id, Type = key.Type, Group = key.Group, Instance = key.Instance };
                _db.DisabledObjects.Add(row);
                rows.Add(row);
            }
        }

        meta.DisabledObjectCount = rows.Count;
        meta.DisabledObjectsStamp = rows.Count == 0 ? null : ComputeStamp(rows);
        if (!meta.IsVaultManaged && meta.SetsEntityId.HasValue)
        {
            await _db.SaveChangesAsync();
            await SetDirtyTracker.UpdateDirtyStatesForSetsAsync(_db, new[] { meta.SetsEntityId.Value });
        }
        await _db.SaveChangesAsync();

        if (meta.IsVaultManaged)
        {
            await _vaultService.RefreshItemIndexesAsync(meta.Id);
        }
    }

    private static string ComputeStamp(IEnumerable<DisabledObjectEntity> rows)
    {
        string joined = string.Join(";", rows.Select(r => $"{r.Type:X8}-{r.Group:X8}-{r.Instance:X16}").OrderBy(s => s, StringComparer.Ordinal));
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(hash, 0, 6);
    }

    /// <param name="recycleOriginal">False keeps the previous version next to the file as ".pfbak" (used by tests).</param>
    public static void RewriteLibraryFile(string path, IReadOnlyList<PartEdits> parts, bool recycleOriginal = true)
    {
        bool isSims3Pack = path.EndsWith(".sims3pack", StringComparison.OrdinalIgnoreCase);
        string tempPath = path + ".pftmp";
        string backupPath = path + ".pfbak";
        if (File.Exists(tempPath)) File.Delete(tempPath);
        // A leftover backup exists only when an earlier edit could not recycle it, so it is the only copy of
        // that version: recycle it now, or keep it under another name.
        if (File.Exists(backupPath) && !RecycleBinHelper.SendToRecycleBin(backupPath))
        {
            string keptPath = $"{path}.{DateTime.Now:yyyyMMdd-HHmmss}.pfbak";
            File.Move(backupPath, keptPath);
            log.Warn($"Kept an earlier version of {path} as {keptPath}");
        }

        try
        {
            if (isSims3Pack)
            {
                var replacements = parts.ToDictionary(
                    p => p.PartIndex,
                    p => DBPFPackageEditor.Apply(Sims3PackWriter.ReadPackageBytes(path, p.PartIndex), p.Edits));
                Sims3PackWriter.Rewrite(path, tempPath, replacements);
            }
            else
            {
                if (parts.Count != 1 || parts[0].PartIndex != 0)
                {
                    throw new ArgumentException("A package has a single part", nameof(parts));
                }
                using var source = new DBPFPackage(path);
                using var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 262144);
                DBPFPackageEditor.Apply(source, output, parts[0].Edits);
            }

            VerifyEditedFile(tempPath, isSims3Pack, parts);
            File.Replace(tempPath, path, backupPath, ignoreMetadataErrors: true);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }

        if (recycleOriginal && !RecycleBinHelper.SendToRecycleBin(backupPath))
        {
            log.Warn($"Could not move the previous version of {path} to the Recycle Bin; it is kept as {backupPath}");
        }
    }

    private static void VerifyEditedFile(string tempPath, bool isSims3Pack, IReadOnlyList<PartEdits> parts)
    {
        if (isSims3Pack)
        {
            using var s3p = new Sims3Pack(tempPath);
            if (s3p.IsCorrupt || s3p.IsEncrypted) throw new InvalidDataException("The rewritten Sims3Pack could not be read back");
            foreach (var part in parts)
            {
                if (part.PartIndex >= s3p.Packages.Count) throw new InvalidDataException("The rewritten Sims3Pack lost a package");
                CheckEdits(s3p.Packages[part.PartIndex], part.Edits);
            }
        }
        else
        {
            using var pkg = new DBPFPackage(tempPath);
            CheckEdits(pkg, parts[0].Edits);
        }
    }

    private static void CheckEdits(DBPFPackage pkg, IReadOnlyList<ResourceEdit> edits)
    {
        foreach (var edit in edits.GroupBy(e => e.Key).Select(g => g.Last()))
        {
            var res = pkg.GetResource(edit.Key);
            if (edit.NewData == null)
            {
                if (res != null) throw new InvalidDataException($"Resource {edit.Key} was not removed");
            }
            else if (res == null || !res.Read().AsSpan().SequenceEqual(edit.NewData))
            {
                throw new InvalidDataException($"Resource {edit.Key} was not written correctly");
            }
        }
    }
}
