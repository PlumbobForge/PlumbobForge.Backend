using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PlumbobForge.Backend.Database;
using S3ForgeTools.Utils;

namespace PlumbobForge.Backend.Services;

// Configurations reach Mods files by renaming them: a file is on (no ".disabled") when the user has it on
// (MetaEntity.Enabled) and the active configuration lets it load (its own rule, else its folder).
public partial class ModsFolderService
{
    public enum ToggleOutcome { Done, HeldOffByConfiguration, Failed }

    public sealed record ApplyResult(int Changed, IReadOnlyList<string> Failed);

    // Items whose file could not be renamed the last time (usually because the game has it open). A scan must not
    // read their on/off switch back from the file, or a failed "turn on" would become "the user turned it off".
    private static readonly HashSet<long> PendingIds = new();
    private static readonly object PendingLock = new();

    // Files the last apply could not rename, for the UI to report.
    public static IReadOnlyList<string> LastApplyFailures { get; private set; } = Array.Empty<string>();

    public async Task<ApplyResult> ApplyConfigurationAsync(CancellationToken ct = default)
    {
        if (!PlumbobForge.Backend.Configuration.ModsFolderSupport.IsEnabled) return new ApplyResult(0, Array.Empty<string>());
        await ScanLock.WaitAsync(ct);
        try
        {
            return await ApplyConfigurationCoreAsync(ct);
        }
        finally
        {
            ScanLock.Release();
        }
    }

    private async Task<ApplyResult> ApplyConfigurationCoreAsync(CancellationToken ct)
    {
        var scope = await ConfigurationScope.LoadActiveAsync(_db);
        var items = await _db.EveryMetaEntity.Where(m => m.IsModsItem).ToListAsync(ct);
        int changed = 0;
        var failed = new List<string>();
        foreach (var meta in items)
        {
            bool desired = meta.Enabled && scope.Includes(meta);
            bool isOn = !ModsResourceCfg.IsDisabledName(meta.CompleteFileName);
            if (desired == isOn)
            {
                ClearPending(meta.Id);
                continue;
            }
            if (TryRenameOnOff(meta, desired)) changed++;
            else failed.Add(meta.FileName);
        }
        if (changed > 0) await _db.SaveChangesAsync(ct);
        LastApplyFailures = failed;
        return new ApplyResult(changed, failed);
    }

    // Turning Mods folder support off: files a configuration held off go back on, files the user turned off stay off.
    // After this PlumbobForge leaves the Mods folder alone. Returns the files that could not be renamed.
    public async Task<ApplyResult> ReleaseConfigurationHoldsAsync(CancellationToken ct = default)
    {
        await ScanLock.WaitAsync(ct);
        try
        {
            var items = await _db.EveryMetaEntity.Where(m => m.IsModsItem && m.Enabled).ToListAsync(ct);
            int changed = 0;
            var failed = new List<string>();
            foreach (var meta in items.Where(m => ModsResourceCfg.IsDisabledName(m.CompleteFileName)))
            {
                if (TryRenameOnOff(meta, true)) changed++;
                else failed.Add(meta.FileName);
            }
            if (changed > 0) await _db.SaveChangesAsync(ct);
            return new ApplyResult(changed, failed);
        }
        finally
        {
            ScanLock.Release();
        }
    }

    // Turns a Mods file on or off for the user. The file only goes on when the active configuration lets it.
    // Returns Failed (and leaves the item as it was) when the file can't be renamed, e.g. because the game has it open.
    public async Task<ToggleOutcome> SetEnabledAsync(long itemId, bool enabled)
    {
        await ScanLock.WaitAsync();
        try
        {
            var meta = await _db.EveryMetaEntity.FirstOrDefaultAsync(m => m.Id == itemId && m.IsModsItem);
            if (meta == null) return ToggleOutcome.Failed;

            var scope = await ConfigurationScope.LoadActiveAsync(_db);
            bool allowed = scope.Includes(meta);
            bool desired = enabled && allowed;
            bool isOn = !ModsResourceCfg.IsDisabledName(meta.CompleteFileName);
            if (desired != isOn && !TryRenameOnOff(meta, desired)) return ToggleOutcome.Failed;

            meta.Enabled = enabled;
            await _db.SaveChangesAsync();
            return enabled && !allowed ? ToggleOutcome.HeldOffByConfiguration : ToggleOutcome.Done;
        }
        finally
        {
            ScanLock.Release();
        }
    }

    // Renames the file to or from ".disabled" and updates the row (not saved). False when it can't.
    private bool TryRenameOnOff(MetaEntity meta, bool on)
    {
        string current = meta.CompleteFileName;
        string target = on ? ModsResourceCfg.StripDisabled(current) : current + ModsResourceCfg.DisabledSuffix;
        try
        {
            if (!File.Exists(current) || File.Exists(target))
            {
                MarkPending(meta.Id);
                return false;
            }
            WritableFile.Move(current, target);
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"Could not turn Mods file {(on ? "on" : "off")}: {current}", ex, "ModsFolderService");
            MarkPending(meta.Id);
            return false;
        }

        meta.CompleteFileName = target;
        try
        {
            var info = new FileInfo(target);
            meta.Filehash = $"{info.Length}_{info.LastWriteTimeUtc.Ticks}";
        }
        catch { }
        ClearPending(meta.Id);
        return true;
    }

    private static void MarkPending(long id) { lock (PendingLock) PendingIds.Add(id); }
    private static void ClearPending(long id) { lock (PendingLock) PendingIds.Remove(id); }
    private static bool IsPending(long id) { lock (PendingLock) return PendingIds.Contains(id); }

    // After a scan: reads the user's switch back from files renamed outside the app, then puts every file in the state
    // the configuration wants. A file found off while the configuration lets it load was turned off by the user; a
    // file found off in a folder the configuration turns off keeps the switch it had (new files count as on).
    private async Task<ApplyResult> ReconcileAfterScanAsync(List<(MetaEntity Meta, bool Disabled, bool IsNew)> states, CancellationToken ct)
    {
        var scope = await ConfigurationScope.LoadActiveAsync(_db);
        bool any = false;
        foreach (var (meta, disabled, isNew) in states)
        {
            if (!isNew && IsPending(meta.Id)) continue;
            bool enabled = !disabled || (!scope.Includes(meta) && (isNew || meta.Enabled));
            if (meta.Enabled != enabled) { meta.Enabled = enabled; any = true; }
        }
        if (any) await _db.SaveChangesAsync(ct);
        return await ApplyConfigurationCoreAsync(ct);
    }

    // Folder on/off per configuration. Folders not listed are on.
    public async Task<HashSet<long>> GetOffFoldersAsync(long configId) =>
        (await _db.ConfigModsFoldersOff.AsNoTracking()
            .Where(o => o.ConfigEntityId == configId)
            .Select(o => o.ModsFolderId)
            .ToListAsync()).ToHashSet();

    // Returns true when the configuration changed.
    public async Task<bool> SetFoldersOnAsync(long configId, IEnumerable<long> folderIds, bool on)
    {
        var ids = folderIds.Distinct().ToList();
        var rows = await _db.ConfigModsFoldersOff.Where(o => o.ConfigEntityId == configId && ids.Contains(o.ModsFolderId)).ToListAsync();
        bool changed = false;
        if (on)
        {
            _db.ConfigModsFoldersOff.RemoveRange(rows);
            changed = rows.Count > 0;
        }
        else
        {
            var existing = rows.Select(r => r.ModsFolderId).ToHashSet();
            var known = (await _db.ModsFolders.Where(f => ids.Contains(f.Id)).Select(f => f.Id).ToListAsync()).ToHashSet();
            foreach (long id in ids.Where(i => !existing.Contains(i) && known.Contains(i)))
            {
                _db.ConfigModsFoldersOff.Add(new ConfigModsFolderOffEntity { ConfigEntityId = configId, ModsFolderId = id });
                changed = true;
            }
        }
        if (changed) await _db.SaveChangesAsync();
        return changed;
    }

    public async Task CopyFolderStatesAsync(long fromConfigId, long toConfigId)
    {
        var ids = await _db.ConfigModsFoldersOff.AsNoTracking().Where(o => o.ConfigEntityId == fromConfigId).Select(o => o.ModsFolderId).ToListAsync();
        foreach (long id in ids) _db.ConfigModsFoldersOff.Add(new ConfigModsFolderOffEntity { ConfigEntityId = toConfigId, ModsFolderId = id });
        if (ids.Count > 0) await _db.SaveChangesAsync();
    }
}
