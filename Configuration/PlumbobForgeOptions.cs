namespace PlumbobForge.Backend.Configuration;

public class PlumbobForgeOptions
{
    public const string SectionName = "PlumbobForge";

    public string DocumentBaseDir { get; set; } = "";
    public string DownloadFolderName { get; set; } = "Downloads";
    public string ArchiveFolderName { get; set; } = "Archives";
    public string TS3PackFolderName { get; set; } = "ConvertedTS3Packs";
    public string ManagedPackageFolderName { get; set; } = "Library";
    public string SetCacheFolderName { get; set; } = "Builds";
    public string LegacyPackageFolderName { get; set; } = "Legacy";
    public string TS3PackStoreFolderName { get; set; } = "TS3PackStore";
    public string GameFilesDir { get; set; } = "";
    public int CompressionLevel { get; set; } = 1;
    public bool HasSeenWalkthrough { get; set; } = false;
    public bool HasCompletedUpgradeWizard { get; set; } = false;
    public string Language { get; set; } = "auto";
    public string Theme { get; set; } = "Dark";
    public string AccentColor { get; set; } = "Emerald";
    public string CacheMethod { get; set; } = "Dynamic";
    public bool EnableAutoScan { get; set; } = true;
    public System.Collections.Generic.List<string> ObservedFolders { get; set; } = new();
    public string LogsFolderName { get; set; } = "Logs";
    public string LastActiveTool { get; set; } = "Cache";

    public string DownloadFolderPath => string.IsNullOrEmpty(DownloadFolderName) ? "" : System.IO.Path.Combine(DocumentBaseDir, DownloadFolderName);
    public string ArchiveFolderPath => string.IsNullOrEmpty(ArchiveFolderName) ? "" : System.IO.Path.Combine(DocumentBaseDir, ArchiveFolderName);
    public string TS3PackFolderPath => string.IsNullOrEmpty(TS3PackFolderName) ? "" : System.IO.Path.Combine(DocumentBaseDir, TS3PackFolderName);
    public string ManagedPackageFolderPath => string.IsNullOrEmpty(ManagedPackageFolderName) ? "" : System.IO.Path.Combine(DocumentBaseDir, ManagedPackageFolderName);
    public string SetCacheFolderPath => string.IsNullOrEmpty(SetCacheFolderName) ? "" : System.IO.Path.Combine(DocumentBaseDir, SetCacheFolderName);
    public string LegacyPackageFolderPath => string.IsNullOrEmpty(LegacyPackageFolderName) ? "" : System.IO.Path.Combine(DocumentBaseDir, LegacyPackageFolderName);
    public string TS3PackStoreFolderPath => string.IsNullOrEmpty(TS3PackStoreFolderName) ? "" : System.IO.Path.Combine(DocumentBaseDir, TS3PackStoreFolderName);
    public string LogsFolderPath => string.IsNullOrEmpty(LogsFolderName) ? "" : System.IO.Path.Combine(DocumentBaseDir, LogsFolderName);
}
