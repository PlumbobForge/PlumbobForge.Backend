using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PlumbobForge.Backend.Configuration;

namespace PlumbobForge.Backend.Services;

public class DownloadsWatcherService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DownloadsWatcherService> _logger;
    private readonly System.Collections.Generic.List<FileSystemWatcher> _watchers = new();

    private CancellationTokenSource? _libraryDebounceCts;
    private readonly object _libraryDebounceLock = new();
    private bool _isLibraryScanRunning = false;
    private bool _pendingLibraryRescan = false;

    private CancellationTokenSource? _modsDebounceCts;
    private readonly object _modsDebounceLock = new();
    private bool _isModsScanRunning = false;
    private bool _pendingModsRescan = false;
    private string _modsDir = string.Empty;

    private CancellationTokenSource? _autoImportDebounceCts;
    private readonly object _autoImportDebounceLock = new();
    private bool _isAutoImportRunning = false;
    private bool _pendingAutoImport = false;

    public DownloadsWatcherService(IServiceProvider serviceProvider, ILogger<DownloadsWatcherService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ReloadWatchers();

        // Defer initial scan slightly so app launch and initial UI render have 100% of CPU and disk bandwidth
        try
        {
            await Task.Delay(3000, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using (var scope = _serviceProvider.CreateScope())
        {
            var options = ResolveOptions(scope);
            if (options.EnableAutoScan)
            {
                _ = Task.Run(() => TriggerAutoImportAsync("Startup scan"));
            }
        }

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
    }

    private PlumbobForgeOptions ResolveOptions(IServiceScope? scope = null)
    {
        try
        {
            var optMonitor = (scope?.ServiceProvider ?? _serviceProvider).GetService<IOptionsMonitor<PlumbobForgeOptions>>();
            if (optMonitor != null) return optMonitor.CurrentValue;

            var opt = (scope?.ServiceProvider ?? _serviceProvider).GetService<IOptions<PlumbobForgeOptions>>();
            if (opt != null) return opt.Value;
        }
        catch { }

        return new PlumbobForgeOptions();
    }

    public void ReloadWatchers(PlumbobForgeOptions? updatedOptions = null)
    {
        try
        {
            StopWatchers();

            using var scope = _serviceProvider.CreateScope();
            var pkgManager = scope.ServiceProvider.GetRequiredService<PKGManager>();
            var options = updatedOptions ?? ResolveOptions(scope);

            // 1. Determine base document directory and library directory
            string baseDocDir = options.DocumentBaseDir;
            if (string.IsNullOrWhiteSpace(baseDocDir))
            {
                baseDocDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PlumbobForge");
            }

            string libraryFolderName = !string.IsNullOrWhiteSpace(options.ManagedPackageFolderName) ? options.ManagedPackageFolderName : "Library";
            string libraryDir = Path.Combine(baseDocDir, libraryFolderName);

            // 2. Watch main Library folder (including subdirectories for set folders and restored Recycle Bin files)
            try
            {
                if (!Directory.Exists(libraryDir))
                {
                    Directory.CreateDirectory(libraryDir);
                }

                if (Directory.Exists(libraryDir))
                {
                    _logger.LogInformation("Starting main Library folder watcher on: {Path}", libraryDir);
                    var libWatcher = new FileSystemWatcher(libraryDir)
                    {
                        IncludeSubdirectories = true,
                        InternalBufferSize = 65536,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime | NotifyFilters.DirectoryName,
                        Filter = "",
                        EnableRaisingEvents = true
                    };
                    libWatcher.Created += OnLibraryFileEvent;
                    libWatcher.Changed += OnLibraryFileEvent;
                    libWatcher.Deleted += OnLibraryFileEvent;
                    libWatcher.Renamed += OnLibraryFileRenamed;
                    libWatcher.Error += (s, e) => _logger.LogWarning(e.GetException(), "Library FileSystemWatcher error encountered.");
                    _watchers.Add(libWatcher);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start watcher for Library folder: {Path}", libraryDir);
            }

            // Watch the game's Mods folder: files the user adds, removes, renames or turns off outside the app.
            try
            {
                var modsService = scope.ServiceProvider.GetRequiredService<ModsFolderService>();
                string modsDir = modsService.GetModsFolderPath();
                if (!string.IsNullOrWhiteSpace(modsDir) && Directory.Exists(modsDir))
                {
                    _modsDir = Path.GetFullPath(modsDir);
                    _logger.LogInformation("Starting Mods folder watcher on: {Path}", modsDir);
                    var modsWatcher = new FileSystemWatcher(modsDir)
                    {
                        IncludeSubdirectories = true,
                        InternalBufferSize = 65536,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.DirectoryName,
                        Filter = "",
                        EnableRaisingEvents = true
                    };
                    modsWatcher.Created += OnModsFileEvent;
                    modsWatcher.Changed += OnModsFileEvent;
                    modsWatcher.Deleted += OnModsFileEvent;
                    modsWatcher.Renamed += OnModsFileRenamed;
                    modsWatcher.Error += (s, e) =>
                    {
                        _logger.LogWarning(e.GetException(), "Mods FileSystemWatcher error encountered.");
                        ScheduleDebouncedModsScan();
                    };
                    _watchers.Add(modsWatcher);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start watcher for the Mods folder");
            }

            // 3. Determine observed folders
            var folders = new System.Collections.Generic.List<string>();
            if (options.ObservedFolders != null && options.ObservedFolders.Count > 0)
            {
                foreach (var folder in options.ObservedFolders)
                {
                    if (!string.IsNullOrWhiteSpace(folder) && !folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                    {
                        folders.Add(folder);
                    }
                }
            }
            else
            {
                string downloads = pkgManager.GetDownloadsFolderPath();
                if (!string.IsNullOrWhiteSpace(downloads) && !folders.Contains(downloads, StringComparer.OrdinalIgnoreCase))
                {
                    folders.Add(downloads);
                }
            }

            // 4. Watch observed folders (if EnableAutoScan is true)
            if (options.EnableAutoScan)
            {
                foreach (var dir in folders)
                {
                    try
                    {
                        if (!Directory.Exists(dir))
                        {
                            try { Directory.CreateDirectory(dir); } catch { }
                        }

                        if (Directory.Exists(dir))
                        {
                            _logger.LogInformation("Starting observed folder watcher on: {Path}", dir);
                            var watcher = new FileSystemWatcher(dir)
                            {
                                InternalBufferSize = 65536,
                                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime | NotifyFilters.DirectoryName,
                                Filter = "",
                                EnableRaisingEvents = true
                            };
                            watcher.Created += OnObservedFileEvent;
                            watcher.Changed += OnObservedFileEvent;
                            watcher.Renamed += OnObservedFileRenamed;
                            watcher.Error += (s, e) => _logger.LogWarning(e.GetException(), "Observed folder FileSystemWatcher error on {Path}", dir);
                            _watchers.Add(watcher);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to start watcher for observed folder: {Path}", dir);
                    }
                }

                if (updatedOptions != null)
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(2000);
                        await TriggerAutoImportAsync("Observed folders updated");
                    });
                }
            }
            else
            {
                _logger.LogInformation("Auto-scan is disabled in settings. Skipping observed folder watchers.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reloading folder watchers.");
        }
    }

    private void StopWatchers()
    {
        foreach (var w in _watchers)
        {
            try
            {
                w.EnableRaisingEvents = false;
                w.Dispose();
            }
            catch { }
        }
        _watchers.Clear();
    }

    private void OnObservedFileEvent(object sender, FileSystemEventArgs e)
    {
        if (IsImportableExtension(e.FullPath))
        {
            ScheduleDebouncedAutoImport();
        }
    }

    private void OnObservedFileRenamed(object sender, RenamedEventArgs e)
    {
        if (IsImportableExtension(e.FullPath) || IsImportableExtension(e.OldFullPath))
        {
            ScheduleDebouncedAutoImport();
        }
    }

    private void OnLibraryFileEvent(object sender, FileSystemEventArgs e)
    {
        if (IsPackageExtension(e.FullPath))
        {
            ScheduleDebouncedLibraryScan();
        }
    }

    private void OnLibraryFileRenamed(object sender, RenamedEventArgs e)
    {
        if (IsPackageExtension(e.FullPath) || IsPackageExtension(e.OldFullPath))
        {
            ScheduleDebouncedLibraryScan();
        }
    }

    private void OnModsFileEvent(object sender, FileSystemEventArgs e)
    {
        if (IsRelevantModsPath(e.FullPath, e.ChangeType != WatcherChangeTypes.Changed)) ScheduleDebouncedModsScan();
    }

    private void OnModsFileRenamed(object sender, RenamedEventArgs e)
    {
        if (IsRelevantModsPath(e.FullPath, true) || IsRelevantModsPath(e.OldFullPath, true)) ScheduleDebouncedModsScan();
    }

    // Mods files, Resource.cfg files and (for adds, removes and renames) folders, outside PlumbobForge's own folders,
    // whose cache and vault writes would otherwise trigger a scan each time.
    private bool IsRelevantModsPath(string path, bool folderEvent)
    {
        if (!PlumbobForge.Backend.Configuration.ModsFolderSupport.IsEnabled) return false;
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrEmpty(_modsDir)) return false;
        string rel;
        try { rel = Path.GetRelativePath(_modsDir, path).Replace('\\', '/'); }
        catch { return false; }
        if (rel.StartsWith("../", StringComparison.Ordinal) || ModsResourceCfg.IsPlumbobForgePath(rel)) return false;
        string name = Path.GetFileName(path);
        if (name.Equals("Resource.cfg", StringComparison.OrdinalIgnoreCase)) return true;
        if (ModsFolderService.IsModFile(path)) return true;
        return folderEvent && Path.GetExtension(name).Length == 0;
    }

    private void ScheduleDebouncedModsScan()
    {
        lock (_modsDebounceLock)
        {
            _modsDebounceCts?.Cancel();
            _modsDebounceCts?.Dispose();
            _modsDebounceCts = new CancellationTokenSource();
            var token = _modsDebounceCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(1500, token);
                    if (token.IsCancellationRequested) return;
                    await ExecuteModsScanWithCoalescingAsync();
                }
                catch (OperationCanceledException) { }
            }, token);
        }
    }

    private async Task ExecuteModsScanWithCoalescingAsync()
    {
        lock (_modsDebounceLock)
        {
            if (_isModsScanRunning)
            {
                _pendingModsRescan = true;
                return;
            }
            _isModsScanRunning = true;
        }

        try
        {
            bool again;
            do
            {
                lock (_modsDebounceLock) _pendingModsRescan = false;
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var result = await scope.ServiceProvider.GetRequiredService<ModsFolderService>().ScanAsync();
                    // The app's own renames already match the database, so they report no change and cause no reload.
                    if (result.Added + result.Updated + result.Removed > 0)
                    {
                        await scope.ServiceProvider.GetRequiredService<NotificationService>()
                            .BroadcastAsync("library_changed", new { reason = "Mods folder changed" });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during Mods folder rescan.");
                }
                lock (_modsDebounceLock) again = _pendingModsRescan;
            } while (again);
        }
        finally
        {
            lock (_modsDebounceLock) _isModsScanRunning = false;
        }
    }

    private static bool IsImportableExtension(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string ext = Path.GetExtension(path);
        return ext.Equals(".package", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".sims3pack", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".rar", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".7z", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPackageExtension(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string ext = Path.GetExtension(path);
        return ext.Equals(".package", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".sims3pack", StringComparison.OrdinalIgnoreCase);
    }

    private void ScheduleDebouncedLibraryScan()
    {
        lock (_libraryDebounceLock)
        {
            _libraryDebounceCts?.Cancel();
            _libraryDebounceCts?.Dispose();
            _libraryDebounceCts = new CancellationTokenSource();
            var token = _libraryDebounceCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(600, token);
                    if (token.IsCancellationRequested) return;

                    await ExecuteLibraryScanWithCoalescingAsync();
                }
                catch (OperationCanceledException) { }
            }, token);
        }
    }

    private async Task ExecuteLibraryScanWithCoalescingAsync()
    {
        lock (_libraryDebounceLock)
        {
            if (_isLibraryScanRunning)
            {
                _pendingLibraryRescan = true;
                return;
            }
            _isLibraryScanRunning = true;
        }

        try
        {
            do
            {
                lock (_libraryDebounceLock)
                {
                    _pendingLibraryRescan = false;
                }

                await TriggerLibraryScanAsync("Library file modified/restored");

            } while (_pendingLibraryRescan);
        }
        finally
        {
            lock (_libraryDebounceLock)
            {
                _isLibraryScanRunning = false;
            }
        }
    }

    private void ScheduleDebouncedAutoImport()
    {
        lock (_autoImportDebounceLock)
        {
            _autoImportDebounceCts?.Cancel();
            _autoImportDebounceCts?.Dispose();
            _autoImportDebounceCts = new CancellationTokenSource();
            var token = _autoImportDebounceCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(500, token);
                    if (token.IsCancellationRequested) return;

                    await ExecuteAutoImportWithCoalescingAsync();
                }
                catch (OperationCanceledException) { }
            }, token);
        }
    }

    private async Task ExecuteAutoImportWithCoalescingAsync()
    {
        lock (_autoImportDebounceLock)
        {
            if (_isAutoImportRunning)
            {
                _pendingAutoImport = true;
                return;
            }
            _isAutoImportRunning = true;
        }

        try
        {
            do
            {
                lock (_autoImportDebounceLock)
                {
                    _pendingAutoImport = false;
                }

                await TriggerAutoImportAsync("Observed folder new files");

            } while (_pendingAutoImport);
        }
        finally
        {
            lock (_autoImportDebounceLock)
            {
                _isAutoImportRunning = false;
            }
        }
    }

    private async Task TriggerAutoImportAsync(string reason)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var options = ResolveOptions(scope);
            if (!options.EnableAutoScan) return;

            _logger.LogInformation("Triggering automatic import from Downloads ({Reason})...", reason);
            var pkgManager = scope.ServiceProvider.GetRequiredService<PKGManager>();
            var notifier = scope.ServiceProvider.GetRequiredService<NotificationService>();

            var duplicates = pkgManager.CheckDownloadsDuplicates();
            if (duplicates.Count > 0)
            {
                _logger.LogInformation("Found {Count} duplicate file(s) in observed folders. Prompting user modal...", duplicates.Count);
                await notifier.BroadcastAsync("auto_import_duplicates", duplicates);
                return;
            }

            int count = await pkgManager.ImportFromDownloadsAsync(msg => _logger.LogInformation("[AutoImport] {Msg}", msg), "rename");
            if (count > 0)
            {
                _logger.LogInformation("Auto-imported {Count} package(s) from observed folders.", count);
                await notifier.BroadcastAsync("items_imported", new { count });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during automatic import from Downloads.");
        }
    }

    private async Task TriggerLibraryScanAsync(string reason)
    {
        try
        {
            _logger.LogInformation("Triggering automatic Library rescan ({Reason})...", reason);
            using var scope = _serviceProvider.CreateScope();
            var pkgManager = scope.ServiceProvider.GetRequiredService<PKGManager>();
            var notifier = scope.ServiceProvider.GetRequiredService<NotificationService>();

            await pkgManager.ScanLibraryDiskAsync(msg => _logger.LogInformation("[LibraryWatcher] {Msg}", msg));
            await notifier.BroadcastAsync("library_changed", new { reason });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during automatic Library rescan.");
        }
    }

    public override void Dispose()
    {
        StopWatchers();
        lock (_libraryDebounceLock)
        {
            _libraryDebounceCts?.Cancel();
            _libraryDebounceCts?.Dispose();
        }
        lock (_autoImportDebounceLock)
        {
            _autoImportDebounceCts?.Cancel();
            _autoImportDebounceCts?.Dispose();
        }
        lock (_modsDebounceLock)
        {
            _modsDebounceCts?.Cancel();
            _modsDebounceCts?.Dispose();
        }
        base.Dispose();
    }
}
