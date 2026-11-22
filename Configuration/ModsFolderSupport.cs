namespace PlumbobForge.Backend.Configuration;

// Process-wide switch for Mods folder support, set from PlumbobForgeOptions at startup and whenever the setting changes.
// When it is off, Mods items stay in the database (tags, notes, favorites) but every tool behaves as if they did not exist.
public static class ModsFolderSupport
{
    public static bool IsEnabled { get; private set; }

    // Mods-only storage: no library or vault, PlumbobForge only manages the Mods folder.
    public static bool ModsOnly { get; private set; }

    public static void Apply(PlumbobForgeOptions options)
    {
        ModsOnly = options.ModsOnly;
        IsEnabled = options.ModsOnly || options.EnableModsFolder;
    }
}
