namespace PlumbobForge.Backend.Configuration;

public class PlumbobForgeOptions
{
    public string DocumentBaseDir { get; set; } = "";
    public string DownloadFolderName { get; set; } = "Downloads";
    public string ArchiveFolderName { get; set; } = "Archive";
    public string TS3PackFolderName { get; set; } = "Downloads";
    public string ManagedPackageFolderName { get; set; } = "Library";
    public string SetCacheFolderName { get; set; } = "Builds";
    public string LegacyPackageFolderName { get; set; } = "Legacy";
    public string TS3PackStoreFolderName { get; set; } = "Store";
    public string GameFilesDir { get; set; } = "";
    public string Sims3UserDir { get; set; } = "";
    public bool HasSeenWalkthrough { get; set; } = false;
    public bool HasCompletedUpgradeWizard { get; set; } = false;
    // The one-time "Feedback moved to the ? menu" callout.
    public bool HasSeenHelpMoveTip { get; set; } = false;
    public string Language { get; set; } = "auto";
    public string Theme { get; set; } = "Dark";
    public string AccentColor { get; set; } = "Emerald";
    public bool IsCustomTheme { get; set; } = false;
    public string CustomThemeBaseColor { get; set; } = "#1e293b";
    public bool CustomThemeIsDark { get; set; } = true;
    public bool UseCustomAccent { get; set; } = false;
    public string CustomThemeAccentColor { get; set; } = "#10b981";
    public double CustomThemeIntensity { get; set; } = 45.0;
    public string CacheMethod { get; set; } = "Dynamic";
    public bool EnableSingleSourceVault { get; set; } = false;
    // Mods folder support: PlumbobForge lists and manages the files in the game's Mods folder.
    public bool EnableModsFolder { get; set; } = false;
    // Mods-only storage mode: no library or vault.
    public bool ModsOnly { get; set; } = false;
    // Existing users are asked once after updating whether to turn Mods folder support on.
    public bool HasAnsweredModsSupport { get; set; } = false;
    public bool AutoRebuildStaticCache { get; set; } = false;
    public bool EnableAutoScan { get; set; } = true;
    // Off by default: deleting compositorCache/simCompositorCache makes the game rebuild textures while it runs (stutter).
    public bool AutoClearGameCaches { get; set; } = false;
    public bool OptimizeTextures { get; set; } = false;
    // Accessibility: a set or Mods folder lists the items of its sub-sets or sub-folders too, so one click shows them all.
    public bool ShowSubfolderItems { get; set; } = false;
    // 0 means no cap.
    public int TextureMaxDimension { get; set; } = 2048;
    public System.Collections.Generic.List<string> ObservedFolders { get; set; } = new();
    public System.Collections.Generic.List<string> CustomColorPresets { get; set; } = new();
    public string LogsFolderName { get; set; } = "Logs";
    public string LastActiveTool { get; set; } = "Cache";
    // Tool sections pinned as their own title-bar tabs, in pin order.
    public System.Collections.Generic.List<string> PinnedTools { get; set; } = new();

    public string DownloadFolderPath => string.IsNullOrEmpty(DownloadFolderName) ? "" : System.IO.Path.Combine(DocumentBaseDir, DownloadFolderName);
    public string ArchiveFolderPath => string.IsNullOrEmpty(ArchiveFolderName) ? "" : System.IO.Path.Combine(DocumentBaseDir, ArchiveFolderName);
    public string TS3PackFolderPath => string.IsNullOrEmpty(TS3PackFolderName) ? "" : System.IO.Path.Combine(DocumentBaseDir, TS3PackFolderName);
    public string ManagedPackageFolderPath => string.IsNullOrEmpty(ManagedPackageFolderName) ? "" : System.IO.Path.Combine(DocumentBaseDir, ManagedPackageFolderName);
    public string SetCacheFolderPath => string.IsNullOrEmpty(SetCacheFolderName) ? "" : System.IO.Path.Combine(DocumentBaseDir, SetCacheFolderName);
    public string LegacyPackageFolderPath => string.IsNullOrEmpty(LegacyPackageFolderName) ? "" : System.IO.Path.Combine(DocumentBaseDir, LegacyPackageFolderName);
    public string TS3PackStoreFolderPath => string.IsNullOrEmpty(TS3PackStoreFolderName) ? "" : System.IO.Path.Combine(DocumentBaseDir, TS3PackStoreFolderName);
    public string LogsFolderPath => string.IsNullOrEmpty(LogsFolderName) ? "" : System.IO.Path.Combine(DocumentBaseDir, LogsFolderName);
}
