#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PlumbobForge.Backend.Configuration;
using PlumbobForge.Backend.Database;
using S3ForgeTools.GameFiles.Package;

namespace PlumbobForge.Backend.Services;

public record MigrationResult(int PackagesMigrated, int SetsMigrated, int ConfigurationsMigrated, int SkippedMissingFiles = 0, int CollectionsMigrated = 0);

public enum VaultMigrationBackupMode
{
    ZipArchive,
    KeepFolder,
    DeleteImmediately
}

public enum VaultRevertMode
{
    FolderBackup,
    ZipArchive,
    ReconstructFromVault
}

public record VaultRevertChoice(
    VaultRevertMode Mode,
    string? CustomZipPath = null,
    bool PreserveDeltaItems = true
);

public class MigrationService
{
    private readonly AppDbContext _db;
    private readonly IOptionsMonitor<PlumbobForgeOptions> _optionsMonitor;
    private PlumbobForgeOptions _options => _optionsMonitor.CurrentValue ?? new PlumbobForgeOptions();
    private readonly CacheBuilderService _cacheBuilderService;
    private readonly Sims3CollectionService _collectionService;
    private readonly LocalizationService _localizer;
    private readonly VaultService _vaultService;

    private static readonly string[] LibraryContentExtensions = { ".package", ".sims3pack", ".world", ".sim" };

    public MigrationService(
        AppDbContext db,
        IOptionsMonitor<PlumbobForgeOptions> options,
        CacheBuilderService cacheBuilderService,
        Sims3CollectionService collectionService,
        LocalizationService localizer,
        VaultService vaultService)
    {
        _db = db;
        _optionsMonitor = options;
        _cacheBuilderService = cacheBuilderService;
        _collectionService = collectionService;
        _localizer = localizer;
        _vaultService = vaultService;
    }

    private static string Timestamp() => DateTime.Now.ToString("yyyyMMdd_HHmmss");

    private static bool IsUnderDirectory(string path, string directory)
    {
        string full = Path.GetFullPath(path);
        string dir = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
    }

    private static void DeleteFilesExcept(string dir, HashSet<string> keep)
    {
        foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
        {
            if (keep.Contains(Path.GetFullPath(file))) continue;
            try { File.Delete(file); } catch { }
        }
        DeleteEmptySubdirectories(dir);
    }

    private static void DeleteEmptySubdirectories(string dir)
    {
        foreach (var sub in Directory.GetDirectories(dir))
        {
            DeleteEmptySubdirectories(sub);
            try
            {
                if (!Directory.EnumerateFileSystemEntries(sub).Any()) Directory.Delete(sub);
            }
            catch { }
        }
    }

    private static void SetAside(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                string target = Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}_{Timestamp()}{Path.GetExtension(path)}");
                File.Move(path, target);
            }
            else if (Directory.Exists(path))
            {
                Directory.Move(path, $"{path}_{Timestamp()}");
            }
        }
        catch { }
    }

    public async Task<(int MigratedCount, long BytesReclaimed)> MigrateToVaultAsync(
        VaultMigrationBackupMode backupMode = VaultMigrationBackupMode.ZipArchive,
        Action<string>? onProgress = null,
        ITaskProgressReporter? progress = null)
    {
        // Migration replaces the cache packages the running game has open.
        Sims3GameProcess.ThrowIfGameRunning(_localizer.GetString("progress.game_running_vault"));

        string stepId = "migrate_to_vault";
        string stepTitle = _localizer.GetString("progress.vault_migrating_step");
        if (string.IsNullOrEmpty(stepTitle) || stepTitle == "progress.vault_migrating_step")
            stepTitle = "Migrating CC Library to Single-Source Sharded Vault...";

        onProgress?.Invoke(_localizer.GetString("progress.vault_scanning"));

        var unmigrated = await _db.MetaEntities
            .Include(m => m.SetsEntity)
            .Where(m => !m.IsVaultManaged)
            .ToListAsync();

        int total = unmigrated.Count;
        if (total == 0)
        {
            string alreadyTitle = _localizer.GetString("progress.vault_already_migrated");
            progress?.StartStep(stepId, stepTitle, badge: alreadyTitle, progress: 1.0);
            progress?.CompleteStep(stepId, finalBadge: alreadyTitle);
            _options.EnableSingleSourceVault = true;
            await _cacheBuilderService.SyncToSims3Async(onProgress);
            return (0, 0);
        }

        progress?.StartStep(stepId, stepTitle, badge: $"0/{total} (0%)", progress: 0.0);

        var defaultSet = await _db.SetsEntities.FirstOrDefaultAsync(s => s.Name == "Default")
            ?? await _db.SetsEntities.FirstOrDefaultAsync();

        if (defaultSet == null)
        {
            defaultSet = new SetsEntity { Name = "Default", FolderName = "Default", IsDefault = true };
            _db.SetsEntities.Add(defaultSet);
            await _db.SaveChangesAsync();
        }

        int migrated = 0;
        long totalBytesReclaimed = 0;
        var failures = new List<string>();

        var setGroups = unmigrated.GroupBy(m => m.SetsEntity ?? defaultSet).ToList();

        foreach (var group in setGroups)
        {
            var targetSet = group.Key;
            var groupItems = group.ToList();

            onProgress?.Invoke($"Batch migrating set '{targetSet.Name}' ({groupItems.Count} items)...");

            int baseMigrated = migrated;
            Action<int, string> onItemProgress = (batchCount, fileName) =>
            {
                int current = baseMigrated + batchCount;
                double pct = total > 0 ? (double)current / total : 0.0;
                int pctInt = (int)(pct * 100);
                progress?.UpdateStep(stepId,
                    title: _localizer.GetString("progress.vault_migrating_item", fileName),
                    badge: $"{current}/{total} ({pctInt}%)",
                    progress: pct);
            };

            var batch = await _vaultService.MigrateSetBatchAsync(
                targetSet,
                groupItems,
                onProgress,
                onItemProgress);

            migrated += batch.MigratedCount;
            totalBytesReclaimed += batch.BytesReclaimed;
            failures.AddRange(batch.Failures);

            double finalGroupPct = total > 0 ? (double)migrated / total : 1.0;
            int finalPctInt = (int)(finalGroupPct * 100);
            progress?.UpdateStep(stepId,
                title: _localizer.GetString("progress.vault_migrated_set", targetSet.Name),
                badge: $"{migrated}/{total} ({finalPctInt}%)",
                progress: finalGroupPct);
        }

        progress?.CompleteStep(stepId, finalBadge: $"{migrated}/{total}");

        if (failures.Count > 0)
        {
            progress?.WarningStep("vault_migration_failures", "Some items could not be migrated and stay in the library", $"{failures.Count}", failures);
            onProgress?.Invoke($"{failures.Count} items could not be migrated and were left in the library.");
        }

        // Anything that is still not vault-managed (.world, .sim, failures) must stay in the library.
        string libraryDir = _options.ManagedPackageFolderPath;
        var keepPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(libraryDir))
        {
            var stillLibraryPaths = await _db.MetaEntities
                .Where(m => !m.IsVaultManaged)
                .Select(m => new { m.CompleteFileName, m.FileName })
                .ToListAsync();
            foreach (var item in stillLibraryPaths)
            {
                foreach (var candidate in new[] { item.CompleteFileName, Path.Combine(libraryDir, item.FileName) })
                {
                    if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate) && IsUnderDirectory(candidate, libraryDir))
                    {
                        keepPaths.Add(Path.GetFullPath(candidate));
                    }
                }
            }
        }

        if (Directory.Exists(libraryDir))
        {
            try
            {
                long stagingBytes = 0;
                try
                {
                    var di = new DirectoryInfo(libraryDir);
                    stagingBytes = di.EnumerateFiles("*", SearchOption.AllDirectories)
                        .Where(fi => !keepPaths.Contains(fi.FullName))
                        .Sum(fi => fi.Length);
                }
                catch { }

                string oldBackupDir = libraryDir + "_Backup";

                if (backupMode == VaultMigrationBackupMode.ZipArchive)
                {
                    string stepBackupId = "backup_library";
                    string parentDir = Path.GetDirectoryName(libraryDir) ?? libraryDir;
                    string zipPath = Path.Combine(parentDir, "Library_Backup.zip");
                    SetAside(zipPath);

                    var files = Directory.GetFiles(libraryDir, "*", SearchOption.AllDirectories);
                    int totalFiles = files.Length;
                    int filesDone = 0;
                    string baseDir = libraryDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                    progress?.StartStep(stepBackupId, _localizer.GetString("progress.vault_backup_zip"), badge: $"0/{totalFiles} (0%)", progress: 0.0);
                    onProgress?.Invoke($"Creating compressed backup archive '{Path.GetFileName(zipPath)}'...");

                    using (var zipFs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 262144))
                    using (var zipArchive = new ZipArchive(zipFs, ZipArchiveMode.Create))
                    {
                        foreach (var filePath in files)
                        {
                            string relativePath = Path.GetRelativePath(baseDir, filePath);
                            var entry = zipArchive.CreateEntry(relativePath, CompressionLevel.Optimal);
                            entry.LastWriteTime = File.GetLastWriteTime(filePath);
                            using (var entryStream = entry.Open())
                            using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 262144))
                            {
                                await fileStream.CopyToAsync(entryStream);
                            }

                            filesDone++;
                            if (filesDone % 5 == 0 || filesDone == totalFiles)
                            {
                                double pct = totalFiles > 0 ? (double)filesDone / totalFiles : 0.0;
                                int pctInt = (int)(pct * 100);
                                progress?.UpdateStep(stepBackupId,
                                    title: _localizer.GetString("progress.vault_backup_compressing", Path.GetFileName(filePath)),
                                    badge: $"{filesDone}/{totalFiles} ({pctInt}%)",
                                    progress: pct);
                            }
                        }
                    }

                    long zipBytes = new FileInfo(zipPath).Length;
                    totalBytesReclaimed += Math.Max(0, stagingBytes - zipBytes);
                    progress?.CompleteStep(stepBackupId, finalBadge: $"{totalFiles}/{totalFiles} (100%)");
                    onProgress?.Invoke($"Compressed backup saved ({zipBytes / (1024.0 * 1024.0):F1} MB). Cleaning up loose staging files...");

                    DeleteFilesExcept(libraryDir, keepPaths);

                    // An older folder backup no longer matches this zip; keep it, but not under the name revert looks for.
                    SetAside(oldBackupDir);
                }
                else if (backupMode == VaultMigrationBackupMode.KeepFolder)
                {
                    string stepBackupId = "backup_library";
                    progress?.StartStep(stepBackupId, _localizer.GetString("progress.vault_backup_keep"), progress: 0.5);
                    SetAside(oldBackupDir);
                    Directory.Move(libraryDir, oldBackupDir);
                    Directory.CreateDirectory(libraryDir);

                    // Put back what stays in the library.
                    foreach (var keep in keepPaths)
                    {
                        string rel = Path.GetRelativePath(libraryDir, keep);
                        string moved = Path.Combine(oldBackupDir, rel);
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(keep)!);
                            if (File.Exists(moved)) File.Copy(moved, keep, overwrite: true);
                        }
                        catch (Exception ex)
                        {
                            onProgress?.Invoke($"Could not restore {rel} to the library: {ex.Message}");
                        }
                    }

                    onProgress?.Invoke($"Staging library renamed to {Path.GetFileName(oldBackupDir)}");
                    progress?.CompleteStep(stepBackupId, finalBadge: _localizer.GetString("progress.vault_folder_renamed"));
                }
                else if (backupMode == VaultMigrationBackupMode.DeleteImmediately)
                {
                    string stepCleanupId = "clean_staging";
                    progress?.StartStep(stepCleanupId, _localizer.GetString("progress.vault_cleanup_staging"), progress: 0.5);
                    onProgress?.Invoke("Cleaning up staging files to reclaim disk space...");
                    DeleteFilesExcept(libraryDir, keepPaths);
                    SetAside(oldBackupDir);

                    totalBytesReclaimed += stagingBytes;
                    progress?.CompleteStep(stepCleanupId, finalBadge: "Space reclaimed");
                }
            }
            catch (Exception ex)
            {
                onProgress?.Invoke($"Warning during staging backup/cleanup: {ex.Message}");
            }
        }

        // Automatically clean up obsolete merged cache files from Mods/Cache/
        string stepCleanCacheId = "cleanup_cache";
        try
        {
            string s3Dir = _cacheBuilderService?.GetSims3FolderPath() ?? _options.DocumentBaseDir;
            string sims3CacheDir = Path.Combine(s3Dir, "Mods", "Cache");
            if (Directory.Exists(sims3CacheDir))
            {
                progress?.StartStep(stepCleanCacheId, "Cleaning up obsolete cache files...", progress: 0.5);
                onProgress?.Invoke("Cleaning up obsolete merged cache files...");
                var oldCacheFiles = Directory.GetFiles(sims3CacheDir, "*.package", SearchOption.AllDirectories);
                foreach (var oldPkg in oldCacheFiles)
                {
                    try
                    {
                        var fi = new FileInfo(oldPkg);
                        totalBytesReclaimed += fi.Length;
                        fi.Delete();
                    }
                    catch { }
                }

                foreach (var dir in Directory.GetDirectories(sims3CacheDir))
                {
                    string dirName = Path.GetFileName(dir);
                    if (!dirName.Equals("Config", StringComparison.OrdinalIgnoreCase))
                    {
                        try { Directory.Delete(dir, true); } catch { }
                    }
                }

                progress?.CompleteStep(stepCleanCacheId, finalBadge: "Cache cleaned");
            }
        }
        catch (Exception ex)
        {
            onProgress?.Invoke($"Warning: Could not clean old cache files: {ex.Message}");
        }

        // Point the game at the shards right away (writes the vault Resource.cfg).
        _options.EnableSingleSourceVault = true;
        await _cacheBuilderService!.SyncToSims3Async(onProgress);

        progress?.CompleteStep(stepId, finalBadge: $"{migrated} items migrated");
        return (migrated, totalBytesReclaimed);
    }

    public async Task<VaultMigrationBatchResult> MigrateRemainingLibraryItemsAsync(Action<string>? onProgress = null)
    {
        var remaining = (await _db.MetaEntities
                .Include(m => m.SetsEntity)
                .Where(m => !m.IsVaultManaged && m.ParentMetaEntityId == null)
                .ToListAsync())
            .Where(m => (m.FileName.EndsWith(".package", StringComparison.OrdinalIgnoreCase) || IsSims3PackItem(m))
                        && !PackageTypeHelper.IsLibraryOnlyType(m.PackageType))
            .ToList();
        if (remaining.Count == 0) return new VaultMigrationBatchResult(0, 0, new List<long>(), new List<string>());

        var defaultSet = await _db.SetsEntities.FirstOrDefaultAsync(s => s.Name == "Default" || s.IsDefault)
            ?? await _db.SetsEntities.FirstAsync();

        // MigrateSetBatchAsync points CompleteFileName at the shard, so remember where each library copy was.
        var libraryPaths = remaining.ToDictionary(m => m.Id, m => File.Exists(m.CompleteFileName)
            ? m.CompleteFileName
            : Path.Combine(_options.ManagedPackageFolderPath, m.FileName));

        int migrated = 0;
        long reclaimed = 0;
        var migratedIds = new List<long>();
        var failures = new List<string>();
        foreach (var group in remaining.GroupBy(m => m.SetsEntity ?? defaultSet))
        {
            var batch = await _vaultService.MigrateSetBatchAsync(group.Key, group.ToList(), onProgress);
            migrated += batch.MigratedCount;
            reclaimed += batch.BytesReclaimed;
            migratedIds.AddRange(batch.MigratedIds);
            failures.AddRange(batch.Failures);
        }

        foreach (var id in migratedIds)
        {
            string path = libraryPaths[id];
            if (!IsUnderDirectory(path, _options.ManagedPackageFolderPath)) continue;
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { onProgress?.Invoke($"Could not delete migrated library file {path}: {ex.Message}"); }
        }

        return new VaultMigrationBatchResult(migrated, reclaimed, migratedIds, failures);
    }

    private static bool FileMatchesHash(string path, string? filehash)
    {
        if (string.IsNullOrEmpty(filehash)) return false;
        var parts = filehash.Split('_');
        if (parts.Length != 2 || !long.TryParse(parts[0], out long length) || !long.TryParse(parts[1], out long ticks)) return false;

        var fi = new FileInfo(path);
        if (!fi.Exists || fi.Length != length) return false;

        long diff = Math.Abs(fi.LastWriteTimeUtc.Ticks - ticks);
        long tolerance = TimeSpan.FromSeconds(2).Ticks;
        if (diff <= tolerance) return true;

        long hour = TimeSpan.FromHours(1).Ticks;
        if (diff > TimeSpan.FromHours(14).Ticks + tolerance) return false;
        long rem = diff % hour;
        return rem <= tolerance || hour - rem <= tolerance;
    }

    private static bool IsSims3PackItem(MetaEntity meta) =>
        meta.FileType == "TS3PACK" || meta.PackageType == "Sims3Pack" || meta.FileName.EndsWith(".sims3pack", StringComparison.OrdinalIgnoreCase);

    private sealed class RevertPlan
    {
        public Dictionary<long, string> RenamedFiles { get; } = new();
        public Dictionary<long, long> StandaloneChildren { get; } = new();
        public HashSet<long> MetasToDelete { get; } = new();
        public List<string> Failures { get; } = new();
        public List<string> Warnings { get; } = new();
        public Dictionary<long, List<MetaEntity>> LibraryParts { get; } = new();
    }

    private async Task<int> ExportVaultItemAsync(MetaEntity meta, List<MetaEntity> allVaultMetas, string targetDir, RevertPlan plan)
    {
        int exported = 0;
        try
        {
            if (meta.VaultChunks.Count > 0)
            {
                string fileName = meta.FileName.EndsWith(".sims3pack", StringComparison.OrdinalIgnoreCase)
                    ? Path.ChangeExtension(meta.FileName, ".package")
                    : meta.FileName;

                if (await _vaultService.ExportSinglePackageAsync(meta.Id, Path.Combine(targetDir, fileName)))
                {
                    exported++;
                    if (!string.Equals(fileName, meta.FileName, StringComparison.OrdinalIgnoreCase)) plan.RenamedFiles[meta.Id] = fileName;
                }
                else
                {
                    plan.Warnings.Add($"{meta.FileName}: nothing to export");
                }
                return exported;
            }

            var children = allVaultMetas.Where(m => m.ParentMetaEntityId == meta.Id).ToList();
            foreach (var child in children.Where(c => c.VaultChunks.Count > 0))
            {
                if (await _vaultService.ExportSinglePackageAsync(child.Id, Path.Combine(targetDir, child.FileName)))
                {
                    exported++;
                    plan.StandaloneChildren[child.Id] = meta.Id;
                }
            }

            // Lot, Sim and world parts are already library files; a side folder gets a copy.
            if (plan.LibraryParts.TryGetValue(meta.Id, out var libraryParts))
            {
                bool intoLibrary = string.Equals(Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(_options.ManagedPackageFolderPath).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
                foreach (var part in libraryParts)
                {
                    if (!File.Exists(part.CompleteFileName))
                    {
                        plan.Warnings.Add($"{part.FileName}: library file missing");
                        continue;
                    }
                    if (!intoLibrary)
                    {
                        Directory.CreateDirectory(targetDir);
                        File.Copy(part.CompleteFileName, Path.Combine(targetDir, part.FileName), overwrite: true);
                    }
                    exported++;
                    plan.StandaloneChildren[part.Id] = meta.Id;
                }
            }

            if (IsSims3PackItem(meta))
            {
                // The parent's content now lives in its sub-packages.
                plan.MetasToDelete.Add(meta.Id);
                if (exported == 0) plan.Warnings.Add($"{meta.FileName}: no packages inside");
            }
            else if (exported == 0)
            {
                plan.Warnings.Add($"{meta.FileName}: nothing to export");
            }
        }
        catch (Exception ex)
        {
            plan.Failures.Add($"{meta.FileName}: {ex.Message}");
        }
        return exported;
    }

    public async Task<int> RevertFromVaultAsync(
        VaultRevertChoice choice,
        Action<string>? onProgress = null,
        ITaskProgressReporter? progress = null)
    {
        // Reverting deletes the shards the running game has open.
        Sims3GameProcess.ThrowIfGameRunning(_localizer.GetString("progress.game_running_vault"));

        string stepId = "revert_vault";
        string stepTitle = _localizer.GetString("progress.vault_reverting_step");
        if (string.IsNullOrEmpty(stepTitle) || stepTitle == "progress.vault_reverting_step")
            stepTitle = "Reverting from Vault Mode to Default Mode...";

        progress?.StartStep(stepId, stepTitle, progress: 0.0);
        onProgress?.Invoke("Preparing to revert to default mode...");

        string libraryDir = _options.ManagedPackageFolderPath;
        Directory.CreateDirectory(libraryDir);

        int restoredCount = 0;
        var plan = new RevertPlan();

        // Step 1: Restore files according to chosen mode
        string stepRestoreId = "revert_restore_files";
        progress?.StartStep(stepRestoreId, "Restoring Library files...", progress: 0.1);

        if (choice.Mode == VaultRevertMode.FolderBackup)
        {
            string backupDir = libraryDir + "_Backup";
            if (!Directory.Exists(backupDir))
            {
                throw new DirectoryNotFoundException($"Backup folder not found at: {backupDir}");
            }

            onProgress?.Invoke($"Restoring files from '{Path.GetFileName(backupDir)}'...");
            var backupFiles = Directory.GetFiles(backupDir, "*", SearchOption.AllDirectories);
            int fileIdx = 0;
            foreach (var file in backupFiles)
            {
                string relPath = Path.GetRelativePath(backupDir, file);
                string destFile = Path.Combine(libraryDir, relPath);
                string? destSubDir = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrEmpty(destSubDir)) Directory.CreateDirectory(destSubDir);

                File.Copy(file, destFile, overwrite: true);
                restoredCount++;
                fileIdx++;
                if (fileIdx % 20 == 0 || fileIdx == backupFiles.Length)
                {
                    double pct = (double)fileIdx / backupFiles.Length;
                    progress?.UpdateStep(stepRestoreId, progress: pct, badge: $"{fileIdx}/{backupFiles.Length}");
                }
            }
            progress?.CompleteStep(stepRestoreId, finalBadge: $"{backupFiles.Length} files restored");
        }
        else if (choice.Mode == VaultRevertMode.ZipArchive)
        {
            string parentDir = Path.GetDirectoryName(libraryDir) ?? libraryDir;
            string zipPath = !string.IsNullOrWhiteSpace(choice.CustomZipPath)
                ? choice.CustomZipPath
                : Path.Combine(parentDir, "Library_Backup.zip");

            if (!File.Exists(zipPath))
            {
                throw new FileNotFoundException($"Backup archive not found at: {zipPath}");
            }

            onProgress?.Invoke($"Extracting backup archive '{Path.GetFileName(zipPath)}'...");
            using (var zipFs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 262144))
            using (var zipArchive = new ZipArchive(zipFs, ZipArchiveMode.Read))
            {
                int totalEntries = zipArchive.Entries.Count;
                int entryIdx = 0;
                foreach (var entry in zipArchive.Entries)
                {
                    entryIdx++;
                    if (string.IsNullOrEmpty(entry.Name)) continue;

                    string destFile = Path.GetFullPath(Path.Combine(libraryDir, entry.FullName));
                    if (!IsUnderDirectory(destFile, libraryDir))
                    {
                        onProgress?.Invoke($"Skipped archive entry outside the library: {entry.FullName}");
                        continue;
                    }

                    string? destSubDir = Path.GetDirectoryName(destFile);
                    if (!string.IsNullOrEmpty(destSubDir)) Directory.CreateDirectory(destSubDir);

                    entry.ExtractToFile(destFile, overwrite: true);
                    restoredCount++;
                    if (entryIdx % 20 == 0 || entryIdx == totalEntries)
                    {
                        double pct = (double)entryIdx / totalEntries;
                        progress?.UpdateStep(stepRestoreId, progress: pct, badge: $"{entryIdx}/{totalEntries}");
                    }
                }
            }
            progress?.CompleteStep(stepRestoreId, finalBadge: "Archive extracted");
        }

        var allVaultMetas = await _db.MetaEntities
            .Include(m => m.VaultChunks)
            .Where(m => m.IsVaultManaged)
            .ToListAsync();
        var topLevelVaultMetas = allVaultMetas.Where(m => m.ParentMetaEntityId == null).ToList();

        var vaultIds = allVaultMetas.Select(m => m.Id).ToHashSet();
        var libraryPartMetas = (await _db.MetaEntities
                .Where(m => !m.IsVaultManaged && m.ParentMetaEntityId.HasValue)
                .ToListAsync())
            .Where(m => vaultIds.Contains(m.ParentMetaEntityId!.Value))
            .ToList();
        foreach (var group in libraryPartMetas.GroupBy(m => m.ParentMetaEntityId!.Value))
        {
            plan.LibraryParts[group.Key] = group.ToList();
        }

        var existingFiles = Directory.GetFiles(libraryDir, "*.*", SearchOption.AllDirectories)
            .GroupBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var existingS3PBaseNames = new HashSet<string>(existingFiles.Keys
            .Where(f => f.EndsWith(".sims3pack", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileNameWithoutExtension)!, StringComparer.OrdinalIgnoreCase);

        // Step 2: Export what the restored library does not already contain
        if (choice.Mode == VaultRevertMode.ReconstructFromVault)
        {
            onProgress?.Invoke("Exporting all custom content packages directly from Vault shards...");
            int total = topLevelVaultMetas.Count;
            int idx = 0;
            foreach (var meta in topLevelVaultMetas)
            {
                idx++;
                restoredCount += await ExportVaultItemAsync(meta, allVaultMetas, libraryDir, plan);

                if (idx % 10 == 0 || idx == total)
                {
                    double pct = (double)idx / total;
                    progress?.UpdateStep(stepRestoreId, progress: pct, badge: $"{idx}/{total}");
                }
            }
            progress?.CompleteStep(stepRestoreId, finalBadge: $"{total} mods exported");
        }
        else
        {
            string stepDeltaId = "revert_delta_items";
            progress?.StartStep(stepDeltaId, "Preserving newly added Vault mods...", progress: 0.5);

            // Items not wanted in the library are still exported, into a side folder, so nothing is lost.
            string deltaDir = choice.PreserveDeltaItems ? libraryDir : $"{libraryDir}_VaultOnly_{Timestamp()}";

            int deltaCount = 0;
            foreach (var meta in topLevelVaultMetas)
            {
                bool inBackup;
                if (IsSims3PackItem(meta) && meta.VaultChunks.Count == 0)
                {
                    inBackup = existingS3PBaseNames.Contains(Path.GetFileNameWithoutExtension(meta.FileName));
                }
                else
                {
                    // Same name and same file as when it was migrated. A different file means it was replaced while in vault mode.
                    inBackup = existingFiles.TryGetValue(meta.FileName, out var restoredPath) && FileMatchesHash(restoredPath, meta.Filehash);
                }
                if (inBackup) continue;

                onProgress?.Invoke($"Exporting '{meta.FileName}' from Vault...");
                int exported = await ExportVaultItemAsync(meta, allVaultMetas, deltaDir, plan);
                if (exported > 0) deltaCount++;
                restoredCount += exported;

                if (!choice.PreserveDeltaItems)
                {
                    plan.MetasToDelete.Add(meta.Id);
                    foreach (var child in allVaultMetas.Where(m => m.ParentMetaEntityId == meta.Id)) plan.MetasToDelete.Add(child.Id);
                    if (plan.LibraryParts.TryGetValue(meta.Id, out var parts))
                    {
                        foreach (var part in parts) plan.MetasToDelete.Add(part.Id);
                    }
                }
            }

            string badge = deltaCount == 0 ? "Up to date"
                : choice.PreserveDeltaItems ? $"{deltaCount} new mods preserved"
                : $"{deltaCount} vault-only mods saved to {Path.GetFileName(deltaDir)}";
            progress?.CompleteStep(stepDeltaId, finalBadge: badge);
        }

        // Nothing has been changed in the database or the vault yet. Stop here if any export failed.
        if (plan.Failures.Count > 0)
        {
            progress?.ErrorStep("revert_export_failed", "Some mods could not be exported from the vault", string.Join(Environment.NewLine, plan.Failures));
            throw new InvalidOperationException($"Revert stopped: {plan.Failures.Count} mods could not be exported from the vault. Nothing was removed. {string.Join("; ", plan.Failures.Take(5))}");
        }
        if (plan.Warnings.Count > 0)
        {
            progress?.WarningStep("revert_export_warnings", "Some items had nothing to export", $"{plan.Warnings.Count}", plan.Warnings);
        }

        // Step 3: Database updates (one transaction)
        string stepDbId = "revert_update_db";
        progress?.StartStep(stepDbId, "Updating database records...", progress: 0.7);
        onProgress?.Invoke("Updating database records...");

        var diskMap = Directory.GetFiles(libraryDir, "*.*", SearchOption.AllDirectories)
            .GroupBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var libraryFilesToRemove = new List<string>();
        await using (var tx = await _db.Database.BeginTransactionAsync())
        {
            await _db.Database.ExecuteSqlRawAsync("DELETE FROM VaultChunks;");

            var byId = allVaultMetas.ToDictionary(m => m.Id);
            // Captured up front: removing a parent makes EF null its children's ParentMetaEntityId immediately.
            var originalParentIds = allVaultMetas.Concat(libraryPartMetas).ToDictionary(m => m.Id, m => m.ParentMetaEntityId);

            // Lot, Sim and world parts: standalone library items when their Sims3Pack becomes its parts; removed when
            // the Sims3Pack itself is back in the library (it holds them) or the vault-only item is not kept.
            foreach (var part in libraryPartMetas)
            {
                long parentId = originalParentIds[part.Id]!.Value;
                if (plan.MetasToDelete.Contains(part.Id) || !plan.MetasToDelete.Contains(parentId) || !byId.TryGetValue(parentId, out var partParent))
                {
                    _db.MetaEntities.Remove(part);
                    libraryFilesToRemove.Add(part.CompleteFileName);
                    continue;
                }
                part.SetsEntityId = partParent.SetsEntityId;
                part.Enabled = partParent.Enabled;
                part.IsFavorite = partParent.IsFavorite;
                part.Description = partParent.Description;
                part.UserTags = partParent.UserTags;
                part.IsUserTagged = part.IsUserTagged || partParent.IsUserTagged;
                part.ParentMetaEntityId = null;
            }
            foreach (var meta in allVaultMetas)
            {
                if (plan.MetasToDelete.Contains(meta.Id))
                {
                    _db.MetaEntities.Remove(meta);
                    continue;
                }

                if (originalParentIds[meta.Id].HasValue)
                {
                    if (plan.StandaloneChildren.TryGetValue(meta.Id, out var parentId) && byId.TryGetValue(parentId, out var parent))
                    {
                        // The sub-package replaces its Sims3Pack in the library; carry the user's metadata over.
                        meta.SetsEntityId = parent.SetsEntityId;
                        meta.Enabled = parent.Enabled;
                        meta.IsFavorite = parent.IsFavorite;
                        meta.Description = parent.Description;
                        meta.UserTags = parent.UserTags;
                        meta.IsUserTagged = parent.IsUserTagged;
                    }
                    else
                    {
                        string basePart = System.Text.RegularExpressions.Regex.Replace(Path.GetFileNameWithoutExtension(meta.FileName), @"_Part\d+$", "");
                        if (existingS3PBaseNames.Contains(basePart))
                        {
                            // Its .sims3pack is back in the library.
                            _db.MetaEntities.Remove(meta);
                            continue;
                        }
                    }
                    meta.ParentMetaEntityId = null;
                }

                if (plan.RenamedFiles.TryGetValue(meta.Id, out var newName))
                {
                    meta.FileName = newName;
                    meta.FileType = "DBPF";
                    if (meta.PackageType == "Sims3Pack") meta.PackageType = "Other";
                }

                meta.IsVaultManaged = false;
                meta.VaultShardId = null;
                meta.CompleteFileName = diskMap.TryGetValue(meta.FileName, out var actualPath)
                    ? actualPath
                    : Path.Combine(libraryDir, meta.FileName);
            }

            // Library mode must rebuild every set: migration emptied Mods/Cache.
            var sets = await _db.SetsEntities.ToListAsync();
            foreach (var set in sets)
            {
                set.Dirty = true;
                set.CachedHash = null;
            }

            await _db.SaveChangesAsync();
            await _db.Database.ExecuteSqlRawAsync("DELETE FROM VaultHoles; DELETE FROM VaultShards;");
            await tx.CommitAsync();
        }

        // Raw SQL bypassed the change tracker; drop every cached entity so nothing still looks vault-managed.
        _db.ChangeTracker.Clear();

        foreach (var path in libraryFilesToRemove)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { onProgress?.Invoke($"Could not remove {Path.GetFileName(path)}: {ex.Message}"); }
        }

        // Files restored from a backup that no mod references any more (deleted, or replaced under another name while in vault mode).
        if (choice.Mode != VaultRevertMode.ReconstructFromVault)
        {
            var referenced = new HashSet<string>(await _db.MetaEntities.Select(m => m.FileName).ToListAsync(), StringComparer.OrdinalIgnoreCase);
            string unreferencedDir = $"{libraryDir}_Unreferenced_{Timestamp()}";
            int setAside = 0;
            foreach (var file in Directory.GetFiles(libraryDir, "*.*", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(file);
                if (!LibraryContentExtensions.Contains(Path.GetExtension(name).ToLowerInvariant())) continue;
                if (referenced.Contains(name)) continue;

                try
                {
                    string dest = Path.Combine(unreferencedDir, Path.GetRelativePath(libraryDir, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Move(file, dest);
                    setAside++;
                }
                catch { }
            }
            if (setAside > 0)
            {
                onProgress?.Invoke($"{setAside} backup files no longer used by any mod were moved to {Path.GetFileName(unreferencedDir)}");
            }
        }

        progress?.CompleteStep(stepDbId, finalBadge: "Database updated");

        // Step 4: Keep the vault shards as a backup outside Mods (the game no longer loads them), clean up
        string stepCleanId = "revert_cleanup";
        progress?.StartStep(stepCleanId, "Cleaning up Vault shard files...", progress: 0.85);
        onProgress?.Invoke("Moving Vault shard files aside and updating Sims 3 configuration...");

        string s3Dir = _cacheBuilderService?.GetSims3FolderPath() ?? _options.DocumentBaseDir;
        string vaultDir = VaultService.GetVaultRootDirectory(s3Dir);
        if (Directory.Exists(vaultDir))
        {
            string vaultBackup = Path.Combine(s3Dir, $"PF_Vault_RevertBackup_{Timestamp()}");
            try
            {
                Directory.Move(vaultDir, vaultBackup);
                onProgress?.Invoke($"Vault shards kept at {vaultBackup}; delete it once you have checked your library.");
            }
            catch (Exception ex)
            {
                onProgress?.Invoke($"Could not move the vault folder aside ({ex.Message}); it was left in place and is no longer loaded.");
            }
        }

        if (choice.Mode == VaultRevertMode.FolderBackup)
        {
            string backupDir = libraryDir + "_Backup";
            if (Directory.Exists(backupDir))
            {
                try { Directory.Delete(backupDir, true); } catch { }
            }
        }

        _options.EnableSingleSourceVault = false;

        // Migration emptied Mods/Cache; rebuild it so the game sees the library again.
        await _cacheBuilderService!.RebuildCacheAsync(forceRebuild: true, onProgress, null, progress);
        await _cacheBuilderService.SyncToSims3Async(onProgress);

        progress?.CompleteStep(stepCleanId, finalBadge: "Cleaned up");
        progress?.CompleteStep(stepId, finalBadge: "Revert complete");
        onProgress?.Invoke("Reversion to Default Mode completed successfully.");

        return restoredCount;
    }


    public async Task<MigrationResult> MigrateCcMagicAsync(
        bool isFullMigration = true,
        string? customPath = null,
        Action<string>? onProgress = null,
        ITaskProgressReporter? progress = null)
    {
        string ccMagicPath = !string.IsNullOrWhiteSpace(customPath)
            ? customPath
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Electronic Arts", "CC Magic");

        if (!Directory.Exists(ccMagicPath))
        {
            throw new DirectoryNotFoundException($"Legacy CC Magic directory not found at: {ccMagicPath}");
        }

        string docBase = !string.IsNullOrWhiteSpace(_options.DocumentBaseDir)
            ? _options.DocumentBaseDir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PlumbobForge");
        string libraryFolderName = !string.IsNullOrWhiteSpace(_options.ManagedPackageFolderName) ? _options.ManagedPackageFolderName : "Library";
        string libraryPath = Path.Combine(docBase, libraryFolderName);
        Directory.CreateDirectory(libraryPath);

        await _db.Database.EnsureCreatedAsync();

        if (isFullMigration)
        {
            return await MigrateCcMagicFullAsync(ccMagicPath, libraryPath, onProgress, progress);
        }
        else
        {
            return await MigrateCcMagicSimpleAsync(ccMagicPath, libraryPath, onProgress, progress);
        }
    }

    private async Task<MigrationResult> MigrateCcMagicSimpleAsync(
        string ccMagicPath,
        string libraryPath,
        Action<string>? onProgress,
        ITaskProgressReporter? progress)
    {
        var files = Directory.GetFiles(ccMagicPath, "*.*", SearchOption.AllDirectories)
            .Where(s => s.EndsWith(".package", StringComparison.OrdinalIgnoreCase) || s.EndsWith(".sims3pack", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (files.Count == 0) return new MigrationResult(0, 0, 0, 0, 0);

        string stepFiles = "import_ccmagic_simple";
        progress?.StartStep(stepFiles, _localizer.GetString("progress.importing_ccmagic_packages"), badge: $"0/{files.Count}", progress: 0.0);

        int count = 0;
        int updateInterval = Math.Max(1, files.Count / 100);

        foreach (var file in files)
        {
            count++;
            if (count % updateInterval == 0 || count == files.Count)
            {
                double pct = (double)count / Math.Max(1, files.Count);
                progress?.UpdateStep(stepFiles, progress: pct, badge: $"{count}/{files.Count}");
            }

            var dest = Path.Combine(libraryPath, Path.GetFileName(file));
            if (!string.Equals(file, dest, StringComparison.OrdinalIgnoreCase) && !File.Exists(dest))
            {
                File.Copy(file, dest, overwrite: true);
            }
        }

        progress?.CompleteStep(stepFiles, finalBadge: _localizer.GetString("progress.packages_imported_badge", count));
        return new MigrationResult(count, 0, 0, 0, 0);
    }

    private async Task<MigrationResult> MigrateCcMagicFullAsync(
        string ccMagicPath,
        string libraryPath,
        Action<string>? onProgress,
        ITaskProgressReporter? progress)
    {
        string dbPath = Path.Combine(ccMagicPath, "Settings", "Launcher2.db");
        if (!File.Exists(dbPath))
        {
            return await MigrateCcMagicSimpleAsync(ccMagicPath, libraryPath, onProgress, progress);
        }

        int packagesMigrated = 0;
        int setsMigrated = 0;
        int configsMigrated = 0;

        var legacySetIdToPlumbobSet = new Dictionary<long, SetsEntity>();
        var legacyConfigIdToPlumbobConfig = new Dictionary<long, ConfigEntity>();
        var legacySetIdToCollectionId = new Dictionary<long, ulong>();
        var fileToLegacySetMap = new Dictionary<string, (long? LegacySetId, string? Description, bool Enabled)>(StringComparer.OrdinalIgnoreCase);

        // 1. Read Launcher2.db
        string stepDb = "read_ccmagic_db";
        progress?.StartStep(stepDb, _localizer.GetString("progress.reading_ccmagic_db"), progress: 0.0);

        using (var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
        {
            await conn.OpenAsync();

            var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    tables.Add(reader.GetString(0));
                }
            }

            string? setsTable = FindTable(tables, "SetsEntities", "SetsEntity", "Sets", "Set");
            string? configsTable = FindTable(tables, "ConfigEntities", "ConfigEntity", "Configurations", "Configs", "Configuration");
            string? configSetsTable = FindTable(tables, "ConfigSetsEntities", "ConfigSetsEntity", "ConfigSets", "ConfigurationSets", "Config_Sets", "Configurations_Sets");
            string? packagesTable = FindTable(tables, "MetaEntities", "MetaEntity", "Packages", "Package", "Files", "File", "Items", "Contents");

            var existingSets = await _db.SetsEntities.ToListAsync();
            var setsByName = new Dictionary<string, SetsEntity>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in existingSets)
            {
                setsByName[s.Name] = s;
            }
            var legacyParentMap = new Dictionary<long, long>();

            // 1a. Import Sets
            if (!string.IsNullOrEmpty(setsTable))
            {
                var setColumns = await GetTableColumnsAsync(conn, setsTable);
                string colId = FindColumn(setColumns, "Id", "SetId", "RowId") ?? setColumns.FirstOrDefault() ?? "Id";
                string colName = FindColumn(setColumns, "Name", "SetName", "Title") ?? "Name";
                string? colDesc = FindColumn(setColumns, "Description", "Desc", "Details");
                string? colParent = FindColumn(setColumns, "ParentSetsEntityId", "ParentId", "ParentSetId", "Parent");
                string? colFolder = FindColumn(setColumns, "FolderName", "Folder");
                string? colIsDef = FindColumn(setColumns, "Default", "IsDefault");
                string? colCollection = FindColumn(setColumns, "Collection", "CollectionId");

                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT * FROM \"{setsTable}\"";
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    long legacyId = reader.GetInt64(reader.GetOrdinal(colId));
                    string name = reader.GetString(reader.GetOrdinal(colName));

                    if (string.Equals(name, "Legacy", StringComparison.OrdinalIgnoreCase)) continue;

                    string? desc = colDesc != null && !reader.IsDBNull(reader.GetOrdinal(colDesc)) ? reader.GetString(reader.GetOrdinal(colDesc)) : null;
                    string? folder = colFolder != null && !reader.IsDBNull(reader.GetOrdinal(colFolder)) ? reader.GetString(reader.GetOrdinal(colFolder)) : name;
                    bool isDef = colIsDef != null && !reader.IsDBNull(reader.GetOrdinal(colIsDef)) && Convert.ToBoolean(reader.GetValue(reader.GetOrdinal(colIsDef)));

                    if (colParent != null && !reader.IsDBNull(reader.GetOrdinal(colParent)))
                    {
                        long pId = reader.GetInt64(reader.GetOrdinal(colParent));
                        if (pId > 0 && pId != legacyId) legacyParentMap[legacyId] = pId;
                    }

                    if (colCollection != null && !reader.IsDBNull(reader.GetOrdinal(colCollection)))
                    {
                        string collVal = reader.GetString(reader.GetOrdinal(colCollection));
                        if (ulong.TryParse(collVal, out ulong cId) && cId != 0)
                        {
                            legacySetIdToCollectionId[legacyId] = cId;
                        }
                    }

                    if (!setsByName.TryGetValue(name, out var setEntity))
                    {
                        setEntity = new SetsEntity
                        {
                            Name = name,
                            FolderName = !string.IsNullOrWhiteSpace(folder) ? folder : name,
                            LongName = name,
                            Description = desc,
                            Dirty = true,
                            IsExpanded = true,
                            IsDefault = isDef || string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase)
                        };
                        _db.SetsEntities.Add(setEntity);
                        setsByName[name] = setEntity;
                        setsMigrated++;
                    }
                    else
                    {
                        if (!string.IsNullOrWhiteSpace(desc) && string.IsNullOrWhiteSpace(setEntity.Description))
                            setEntity.Description = desc;
                        setEntity.Dirty = true;
                    }
                    legacySetIdToPlumbobSet[legacyId] = setEntity;
                }
                await _db.SaveChangesAsync();

                foreach (var (childLegId, parentLegId) in legacyParentMap)
                {
                    if (legacySetIdToPlumbobSet.TryGetValue(childLegId, out var childSet) &&
                        legacySetIdToPlumbobSet.TryGetValue(parentLegId, out var parentSet))
                    {
                        childSet.ParentSetsEntityId = parentSet.Id;
                    }
                }
                await _db.SaveChangesAsync();
            }

            // 1b. Import Configurations
            if (!string.IsNullOrEmpty(configsTable))
            {
                var configColumns = await GetTableColumnsAsync(conn, configsTable);
                string colId = FindColumn(configColumns, "Id", "ConfigId", "RowId") ?? configColumns.FirstOrDefault() ?? "Id";
                string colName = FindColumn(configColumns, "Name", "ConfigName", "Title") ?? "Name";
                string? colDesc = FindColumn(configColumns, "Description", "Desc");
                string? colActive = FindColumn(configColumns, "Active", "IsActive");
                string? colDefault = FindColumn(configColumns, "Default", "IsDefault");

                var existingConfigs = await _db.ConfigEntities.Include(c => c.ConfigSetsEntities).ToListAsync();
                var configsByName = new Dictionary<string, ConfigEntity>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in existingConfigs) configsByName[c.Name] = c;

                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT * FROM \"{configsTable}\"";
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    long legacyId = reader.GetInt64(reader.GetOrdinal(colId));
                    string name = reader.GetString(reader.GetOrdinal(colName));
                    string? desc = colDesc != null && !reader.IsDBNull(reader.GetOrdinal(colDesc)) ? reader.GetString(reader.GetOrdinal(colDesc)) : null;
                    bool active = colActive != null && !reader.IsDBNull(reader.GetOrdinal(colActive)) && Convert.ToBoolean(reader.GetValue(reader.GetOrdinal(colActive)));
                    bool isDef = colDefault != null && !reader.IsDBNull(reader.GetOrdinal(colDefault)) && Convert.ToBoolean(reader.GetValue(reader.GetOrdinal(colDefault)));

                    if (!configsByName.TryGetValue(name, out var configEntity))
                    {
                        configEntity = new ConfigEntity
                        {
                            Name = name,
                            Description = desc ?? "Migrated from CC Magic",
                            Active = active,
                            Default = isDef
                        };
                        _db.ConfigEntities.Add(configEntity);
                        configsByName[name] = configEntity;
                        configsMigrated++;
                    }
                    else if (!string.IsNullOrWhiteSpace(desc) && string.IsNullOrWhiteSpace(configEntity.Description))
                        configEntity.Description = desc;

                    legacyConfigIdToPlumbobConfig[legacyId] = configEntity;
                }
                await _db.SaveChangesAsync();
            }

            // 1c. Import ConfigSets Associations
            if (!string.IsNullOrEmpty(configSetsTable))
            {
                var csColumns = await GetTableColumnsAsync(conn, configSetsTable);
                string? colConfigId = FindColumn(csColumns, "ConfigEntity_Id", "ConfigEntityId", "ConfigId", "ConfigurationId");
                string? colSetId = FindColumn(csColumns, "SetsEntity_Id", "SetsEntityId", "SetId");

                if (colConfigId != null && colSetId != null)
                {
                    var existingConfigSets = await _db.ConfigSetsEntities.ToListAsync();
                    var linkedPairs = new HashSet<(long ConfigId, long SetId)>(
                        existingConfigSets.Select(cs => (cs.ConfigEntityId, cs.SetsEntityId)));

                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"SELECT \"{colConfigId}\", \"{colSetId}\" FROM \"{configSetsTable}\"";
                    using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        if (!reader.IsDBNull(0) && !reader.IsDBNull(1))
                        {
                            long legCfgId = reader.GetInt64(0);
                            long legSetId = reader.GetInt64(1);

                            if (legacyConfigIdToPlumbobConfig.TryGetValue(legCfgId, out var pbConfig) &&
                                legacySetIdToPlumbobSet.TryGetValue(legSetId, out var pbSet))
                            {
                                if (linkedPairs.Add((pbConfig.Id, pbSet.Id)))
                                {
                                    _db.ConfigSetsEntities.Add(new ConfigSetsEntity
                                    {
                                        ConfigEntityId = pbConfig.Id,
                                        SetsEntityId = pbSet.Id
                                    });
                                }
                            }
                        }
                    }
                    await _db.SaveChangesAsync();
                }
            }

            // 1d. Map Package Records
            if (!string.IsNullOrEmpty(packagesTable))
            {
                var pkgColumns = await GetTableColumnsAsync(conn, packagesTable);
                string? colFileName = FindColumn(pkgColumns, "FileName", "Filename", "Name", "Path");
                string? colSetId = FindColumn(pkgColumns, "SetsEntity_Id", "SetsEntityId", "SetId");
                string? colDesc = FindColumn(pkgColumns, "Description", "Desc", "Notes", "Note", "Comment", "Comments");
                string? colEnabled = FindColumn(pkgColumns, "Enabled", "Active");

                if (colFileName != null)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"SELECT * FROM \"{packagesTable}\"";
                    using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        string rawFile = reader.GetString(reader.GetOrdinal(colFileName));
                        string fileName = Path.GetFileName(rawFile);
                        string fileNameNoExt = Path.GetFileNameWithoutExtension(rawFile);
                        long? legSetId = colSetId != null && !reader.IsDBNull(reader.GetOrdinal(colSetId)) ? reader.GetInt64(reader.GetOrdinal(colSetId)) : null;
                        string? desc = colDesc != null && !reader.IsDBNull(reader.GetOrdinal(colDesc)) ? reader.GetString(reader.GetOrdinal(colDesc)) : null;
                        if (!string.IsNullOrWhiteSpace(desc)) desc = desc.Trim();
                        bool enabled = colEnabled == null || reader.IsDBNull(reader.GetOrdinal(colEnabled)) || Convert.ToBoolean(reader.GetValue(reader.GetOrdinal(colEnabled)));

                        if (!string.IsNullOrWhiteSpace(fileName))
                        {
                            fileToLegacySetMap[fileName] = (legSetId, desc, enabled);
                            if (!string.IsNullOrWhiteSpace(fileNameNoExt) && !fileToLegacySetMap.ContainsKey(fileNameNoExt))
                                fileToLegacySetMap[fileNameNoExt] = (legSetId, desc, enabled);
                        }
                    }
                }
            }
        }

        progress?.CompleteStep(stepDb, finalBadge: _localizer.GetString("progress.sets_configs_migrated_badge", setsMigrated, configsMigrated));

        var files = Directory.GetFiles(ccMagicPath, "*.*", SearchOption.AllDirectories)
            .Where(s => s.EndsWith(".package", StringComparison.OrdinalIgnoreCase) || s.EndsWith(".sims3pack", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var physicalFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files) physicalFileNames.Add(Path.GetFileName(f));

        string stepFiles = "import_ccmagic_files";
        progress?.StartStep(stepFiles, _localizer.GetString("progress.importing_ccmagic_sets"), badge: $"0/{files.Count}", progress: 0.0);

        var defaultSet = await _db.SetsEntities.FirstOrDefaultAsync(s => s.Name == "Default") ?? await _db.SetsEntities.FirstOrDefaultAsync();
        var allExistingMetas = await _db.MetaEntities.ToListAsync();
        var metaMap = new Dictionary<string, MetaEntity>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in allExistingMetas) metaMap[m.FileName] = m;

        int current = 0;
        int updateInterval = Math.Max(1, files.Count / 100);

        foreach (var file in files)
        {
            current++;
            if (current % updateInterval == 0 || current == files.Count)
            {
                double pct = (double)current / Math.Max(1, files.Count);
                progress?.UpdateStep(stepFiles, progress: pct, badge: $"{current}/{files.Count}");
            }

            string fileName = Path.GetFileName(file);
            string fileNameNoExt = Path.GetFileNameWithoutExtension(fileName);
            var dest = Path.Combine(libraryPath, fileName);
            if (!string.Equals(file, dest, StringComparison.OrdinalIgnoreCase) && !File.Exists(dest))
            {
                File.Copy(file, dest, overwrite: true);
                packagesMigrated++;
            }

            long targetSetId = defaultSet?.Id ?? 1;
            string? itemDesc = null;
            bool isEnabled = true;

            if (fileToLegacySetMap.TryGetValue(fileName, out var mapInfo) || fileToLegacySetMap.TryGetValue(fileNameNoExt, out mapInfo))
            {
                if (mapInfo.LegacySetId.HasValue && legacySetIdToPlumbobSet.TryGetValue(mapInfo.LegacySetId.Value, out var matchedSet))
                {
                    targetSetId = matchedSet.Id;
                    matchedSet.Dirty = true;
                }
                itemDesc = mapInfo.Description;
                isEnabled = mapInfo.Enabled;
            }

            if (!metaMap.TryGetValue(fileName, out var meta))
            {
                meta = new MetaEntity
                {
                    FileName = fileName,
                    CompleteFileName = dest,
                    FileType = Path.GetExtension(fileName),
                    Filehash = string.Empty,
                    PackageType = "Unknown",
                    SetsEntityId = targetSetId,
                    Description = itemDesc,
                    Enabled = isEnabled
                };
                _db.MetaEntities.Add(meta);
                metaMap[fileName] = meta;
            }
            else
            {
                meta.SetsEntityId = targetSetId;
                meta.Enabled = isEnabled;
                if (!string.IsNullOrWhiteSpace(itemDesc)) meta.Description = itemDesc;
            }
        }

        foreach (var (key, info) in fileToLegacySetMap)
        {
            if (string.IsNullOrWhiteSpace(info.Description)) continue;
            if (metaMap.TryGetValue(key, out var existingMeta) ||
                metaMap.TryGetValue($"{key}.package", out existingMeta) ||
                metaMap.TryGetValue($"{key}.sims3pack", out existingMeta))
            {
                existingMeta.Description = info.Description;
            }
        }

        await _db.SaveChangesAsync();
        progress?.CompleteStep(stepFiles, finalBadge: _localizer.GetString("progress.items_imported_badge", packagesMigrated));

        // 1d. Migrate Collections linked in CC Magic
        int collectionsMigrated = 0;
        if (legacySetIdToCollectionId.Count > 0)
        {
            string stepCollections = "import_ccmagic_collections";
            progress?.StartStep(stepCollections, _localizer.GetString("progress.migrating_collections_ccmagic"), badge: _localizer.GetString("progress.badge_initializing"), progress: 0.0);

            try
            {
                var existingUserCollections = await _collectionService.LoadUserCollectionsAsync();
                var existingCollMap = existingUserCollections.ToDictionary(c => c.Id, c => c);

                var collGroups = legacySetIdToCollectionId.GroupBy(kvp => kvp.Value).ToList();
                int currentColl = 0;

                foreach (var group in collGroups)
                {
                    currentColl++;
                    ulong collectionId = group.Key;
                    var legSetIds = group.Select(g => g.Key).ToList();
                    var pbSetIds = new List<long>();
                    foreach (var legSetId in legSetIds)
                    {
                        if (legacySetIdToPlumbobSet.TryGetValue(legSetId, out var pbSet))
                        {
                            pbSetIds.Add(pbSet.Id);
                        }
                    }

                    if (pbSetIds.Count == 0) continue;

                    string collName = "Collection";
                    TGI_Key iconKey = new TGI_Key(0x2F7D0004, 0, 0);

                    if (existingCollMap.TryGetValue(collectionId, out var existingColl))
                    {
                        collName = existingColl.Name;
                        iconKey = existingColl.IconKey;
                    }
                    else
                    {
                        var firstSet = legacySetIdToPlumbobSet.GetValueOrDefault(legSetIds.First());
                        if (firstSet != null) collName = firstSet.Name;
                    }

                    var saveRes = await _collectionService.SaveCollectionAsync(collectionId, collName, iconKey, pbSetIds);
                    if (saveRes.Success)
                    {
                        collectionsMigrated++;
                    }

                    double pct = (double)currentColl / Math.Max(1, collGroups.Count);
                    progress?.UpdateStep(stepCollections, progress: pct, badge: $"{collectionsMigrated}/{collGroups.Count}");
                }

                progress?.CompleteStep(stepCollections, finalBadge: _localizer.GetString("progress.collections_migrated_badge", collectionsMigrated));
            }
            catch (Exception ex)
            {
                progress?.WarningStep("collections_error", "Failed to migrate some collections", ex.Message, new List<string> { ex.Message });
            }
        }

        var missingFiles = fileToLegacySetMap.Keys.Where(k => !physicalFileNames.Contains(k)).ToList();
        if (missingFiles.Count > 0)
        {
            var previewList = missingFiles.Take(50).Select(f => $"{f} (not found in CC Magic folder)").ToList();
            progress?.WarningStep("skipped_missing_files", "Items found in CC Magic database but missing on disk", $"{missingFiles.Count} files missing", previewList);
        }

        return new MigrationResult(packagesMigrated, setsMigrated, configsMigrated, missingFiles.Count, collectionsMigrated);
    }

    private static string? FindTable(HashSet<string> tables, params string[] candidates)
    {
        foreach (var c in candidates)
        {
            var match = tables.FirstOrDefault(t => t.Equals(c, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }
        return null;
    }

    private static async Task<List<string>> GetTableColumnsAsync(SqliteConnection conn, string tableName)
    {
        var columns = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{tableName}\")";
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
        return columns;
    }

    private static string? FindColumn(List<string> columns, params string[] candidates)
    {
        foreach (var c in candidates)
        {
            var match = columns.FirstOrDefault(col => col.Equals(c, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }
        return null;
    }

    private async Task<MigrationResult> MigrateS3moSimpleAsync(
        string s3moPath,
        string libraryPath,
        Action<string>? onProgress,
        ITaskProgressReporter? progress)
    {
        string modsDir = Path.Combine(s3moPath, "Mods");
        string searchDir = Directory.Exists(modsDir) ? modsDir : s3moPath;

        var files = Directory.GetFiles(searchDir, "*.*", SearchOption.AllDirectories)
            .Where(s => s.EndsWith(".package", StringComparison.OrdinalIgnoreCase) || s.EndsWith(".sims3pack", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (files.Count == 0) return new MigrationResult(0, 0, 0, 0, 0);

        string stepFiles = "import_s3mo_simple";
        progress?.StartStep(stepFiles, _localizer.GetString("progress.importing_s3mo_packages"), badge: $"0/{files.Count}", progress: 0.0);

        int count = 0;
        int updateInterval = Math.Max(1, files.Count / 100);

        foreach (var file in files)
        {
            count++;
            if (count % updateInterval == 0 || count == files.Count)
            {
                double pct = (double)count / Math.Max(1, files.Count);
                progress?.UpdateStep(stepFiles, progress: pct, badge: $"{count}/{files.Count}");
            }

            var dest = Path.Combine(libraryPath, Path.GetFileName(file));
            if (!string.Equals(file, dest, StringComparison.OrdinalIgnoreCase) && !File.Exists(dest))
            {
                File.Copy(file, dest, overwrite: true);
            }
        }

        progress?.CompleteStep(stepFiles, finalBadge: _localizer.GetString("progress.packages_imported_badge", count));
        return new MigrationResult(count, 0, 0, 0, 0);
    }

    public async Task<MigrationResult> MigrateS3moAsync(
        string s3moPath,
        bool isFullMigration = true,
        Action<string>? onProgress = null,
        ITaskProgressReporter? progress = null)
    {
        if (string.IsNullOrWhiteSpace(s3moPath) || !Directory.Exists(s3moPath))
        {
            throw new DirectoryNotFoundException($"Sims 3 Mod Organizer folder not found at: {s3moPath}");
        }

        string modsDir = Path.Combine(s3moPath, "Mods");
        string profilesDir = Path.Combine(s3moPath, "Profiles");
        string iniPath = Path.Combine(s3moPath, "s3mo.ini");

        if (!Directory.Exists(modsDir) && !Directory.Exists(profilesDir))
        {
            throw new InvalidOperationException("The selected folder does not contain 'Mods' or 'Profiles' subdirectories.");
        }

        string docBase = !string.IsNullOrWhiteSpace(_options.DocumentBaseDir)
            ? _options.DocumentBaseDir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PlumbobForge");
        string libraryFolderName = !string.IsNullOrWhiteSpace(_options.ManagedPackageFolderName) ? _options.ManagedPackageFolderName : "Library";
        string libraryPath = Path.Combine(docBase, libraryFolderName);
        Directory.CreateDirectory(libraryPath);

        await _db.Database.EnsureCreatedAsync();

        if (!isFullMigration)
        {
            return await MigrateS3moSimpleAsync(s3moPath, libraryPath, onProgress, progress);
        }

        string? activeProfile = null;
        if (File.Exists(iniPath))
        {
            var iniLines = await File.ReadAllLinesAsync(iniPath);
            foreach (var line in iniLines)
            {
                if (line.StartsWith("Profile=", StringComparison.OrdinalIgnoreCase))
                {
                    activeProfile = line.Substring("Profile=".Length).Trim();
                    break;
                }
            }
        }

        int totalPackagesMigrated = 0;
        int totalSetsMigrated = 0;
        int totalConfigsMigrated = 0;

        var existingSets = await _db.SetsEntities.ToListAsync();
        var setsByName = new Dictionary<string, SetsEntity>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in existingSets) setsByName[s.Name] = s;

        var existingMetas = await _db.MetaEntities.ToListAsync();
        var metasByFileName = new Dictionary<string, MetaEntity>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in existingMetas) metasByFileName[m.FileName] = m;

        if (Directory.Exists(modsDir))
        {
            var modDirs = Directory.GetDirectories(modsDir);
            string stepMods = "migrate_s3mo_mods";
            progress?.StartStep(stepMods, _localizer.GetString("progress.importing_s3mo_mods"), badge: $"0/{modDirs.Length}", progress: 0.0);

            int modIndex = 0;
            foreach (var modPath in modDirs)
            {
                modIndex++;
                string modName = Path.GetFileName(modPath);
                progress?.UpdateStep(stepMods, progress: (double)modIndex / Math.Max(1, modDirs.Length), badge: $"{modIndex}/{modDirs.Length}");

                if (!setsByName.TryGetValue(modName, out var setEntity))
                {
                    setEntity = new SetsEntity
                    {
                        Name = modName,
                        FolderName = modName,
                        LongName = modName,
                        Dirty = true,
                        IsExpanded = true,
                        IsDefault = false
                    };
                    _db.SetsEntities.Add(setEntity);
                    await _db.SaveChangesAsync();
                    setsByName[modName] = setEntity;
                    totalSetsMigrated++;
                }

                var packageFiles = Directory.GetFiles(modPath, "*.*", SearchOption.AllDirectories)
                    .Where(f => f.EndsWith(".package", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".sims3pack", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var pkgFile in packageFiles)
                {
                    string fileName = Path.GetFileName(pkgFile);
                    string destPath = Path.Combine(libraryPath, fileName);
                    if (!string.Equals(pkgFile, destPath, StringComparison.OrdinalIgnoreCase) && !File.Exists(destPath))
                    {
                        File.Copy(pkgFile, destPath, overwrite: true);
                        totalPackagesMigrated++;
                    }

                    if (!metasByFileName.TryGetValue(fileName, out var meta))
                    {
                        meta = new MetaEntity
                        {
                            FileName = fileName,
                            CompleteFileName = destPath,
                            FileType = Path.GetExtension(fileName),
                            Filehash = string.Empty,
                            PackageType = "Unknown",
                            SetsEntityId = setEntity.Id,
                            SetsEntity = setEntity,
                            Enabled = true
                        };
                        _db.MetaEntities.Add(meta);
                        metasByFileName[fileName] = meta;
                    }
                    else
                    {
                        meta.CompleteFileName = destPath;
                        meta.SetsEntityId = setEntity.Id;
                        meta.SetsEntity = setEntity;
                    }
                }
            }

            await _db.SaveChangesAsync();
            progress?.CompleteStep(stepMods, finalBadge: _localizer.GetString("progress.total_sets_packages_badge", totalSetsMigrated, totalPackagesMigrated));
        }

        if (Directory.Exists(profilesDir))
        {
            var profileDirs = Directory.GetDirectories(profilesDir);
            string stepProfiles = "migrate_s3mo_profiles";
            progress?.StartStep(stepProfiles, _localizer.GetString("progress.importing_s3mo_profiles"), badge: $"0/{profileDirs.Length}", progress: 0.0);

            var existingConfigs = await _db.ConfigEntities.Include(c => c.ConfigSetsEntities).ToListAsync();
            var configsByName = new Dictionary<string, ConfigEntity>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in existingConfigs) configsByName[c.Name] = c;

            var existingConfigSets = await _db.ConfigSetsEntities.ToListAsync();
            var linkedPairs = new HashSet<(long ConfigId, long SetId)>(
                existingConfigSets.Select(cs => (cs.ConfigEntityId, cs.SetsEntityId)));

            int profileIndex = 0;
            foreach (var profPath in profileDirs)
            {
                profileIndex++;
                string profileName = Path.GetFileName(profPath);
                progress?.UpdateStep(stepProfiles, progress: (double)profileIndex / Math.Max(1, profileDirs.Length), badge: $"{profileIndex}/{profileDirs.Length}");

                if (!configsByName.TryGetValue(profileName, out var configEntity))
                {
                    configEntity = new ConfigEntity
                    {
                        Name = profileName,
                        Description = "Migrated from Sims 3 Mod Organizer",
                        Active = string.Equals(profileName, activeProfile, StringComparison.OrdinalIgnoreCase),
                        Default = string.Equals(profileName, "Default", StringComparison.OrdinalIgnoreCase)
                    };
                    _db.ConfigEntities.Add(configEntity);
                    configsByName[profileName] = configEntity;
                    totalConfigsMigrated++;
                }

                await _db.SaveChangesAsync();

                string modlistPath = Path.Combine(profPath, "modlist.txt");
                if (File.Exists(modlistPath))
                {
                    var lines = await File.ReadAllLinesAsync(modlistPath);
                    foreach (var rawLine in lines)
                    {
                        var line = rawLine.Trim();
                        if (line.StartsWith("+"))
                        {
                            string targetModName = line.Substring(1).Trim();
                            if (setsByName.TryGetValue(targetModName, out var targetSet))
                            {
                                if (linkedPairs.Add((configEntity.Id, targetSet.Id)))
                                {
                                    _db.ConfigSetsEntities.Add(new ConfigSetsEntity
                                    {
                                        ConfigEntityId = configEntity.Id,
                                        SetsEntityId = targetSet.Id
                                    });
                                }
                            }
                        }
                    }
                }
            }

            await _db.SaveChangesAsync();
            progress?.CompleteStep(stepProfiles, finalBadge: _localizer.GetString("progress.total_configs_migrated_badge", totalConfigsMigrated));
        }

        await _db.SaveChangesAsync();
        return new MigrationResult(totalPackagesMigrated, totalSetsMigrated, totalConfigsMigrated, 0);
    }
}
