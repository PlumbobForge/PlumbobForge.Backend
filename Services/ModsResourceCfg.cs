using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace PlumbobForge.Backend.Services;

// Reads Mods/Resource.cfg the way the game does, to know which files in the Mods folder it loads and at what
// priority. Lines: "Priority N", "PackedFile <pattern>" (segments may hold * and ?), "Scan <folder>" (reads that
// folder's own Resource.cfg) and "DirectoryFiles <folder> autoupdate" (loose files, not packages; ignored).
// Paths are relative to the .cfg's folder and may be quoted. PlumbobForge's own folders are left out entirely:
// the caches, the vault, the Hider's packages and the custom collection icons package.
public sealed class ModsResourceCfg
{
    public sealed record Pattern(string RelativePattern, int Priority, Regex Match);

    public string ModsDir { get; }
    public IReadOnlyList<Pattern> Patterns => _patterns;
    public bool Exists { get; private set; }

    // Where Mods/Resource.cfg scans PlumbobForge's own Cache/Config (the library caches or vault shards): its priority
    // and how many package patterns come before it. Null when the line is missing.
    public int? CacheConfigPriority { get; private set; }
    public int CacheConfigOrder { get; private set; }

    private readonly List<Pattern> _patterns = new();
    private const int MaxScanDepth = 4;

    private static readonly Regex HiderFile = new(@"^coll_0x[0-9a-f]{16}_hider\.package$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public const string CustomIconsFileName = "PlumbobForge_CustomIcons.package";
    public const string DisabledSuffix = ".disabled";

    private ModsResourceCfg(string modsDir)
    {
        ModsDir = Path.GetFullPath(modsDir);
    }

    public static ModsResourceCfg Load(string modsDir)
    {
        var cfg = new ModsResourceCfg(modsDir);
        string main = Path.Combine(cfg.ModsDir, "Resource.cfg");
        if (File.Exists(main))
        {
            cfg.Exists = true;
            cfg.ReadFile(main, 0);
        }
        return cfg;
    }

    // Forward slashes, relative to Mods; null when the path lies outside Mods.
    public string? ToRelative(string fullPath)
    {
        string rel = Path.GetRelativePath(ModsDir, Path.GetFullPath(fullPath)).Replace('\\', '/');
        if (rel == ".") return string.Empty;
        if (rel.StartsWith("../", StringComparison.Ordinal) || rel == ".." || Path.IsPathRooted(rel)) return null;
        return rel;
    }

    // True for folders and files that belong to PlumbobForge, never shown as Mods items.
    public static bool IsPlumbobForgePath(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)) return false;
        var parts = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        if (parts[0].Equals("Cache", StringComparison.OrdinalIgnoreCase)) return true;
        if (parts[0].Equals("PF_Vault", StringComparison.OrdinalIgnoreCase)) return true;
        if (parts.Length >= 2 && parts[0].Equals("Packages", StringComparison.OrdinalIgnoreCase) &&
            parts[1].Equals("PF_Vault", StringComparison.OrdinalIgnoreCase)) return true;
        if (parts.Length == 2 && parts[0].Equals("Packages", StringComparison.OrdinalIgnoreCase) &&
            parts[1].Equals(CustomIconsFileName, StringComparison.OrdinalIgnoreCase)) return true;
        if (parts.Length == 2 && parts[0].Equals("Overrides", StringComparison.OrdinalIgnoreCase) &&
            HiderFile.IsMatch(StripDisabled(parts[1]))) return true;
        return false;
    }

    public static bool IsDisabledName(string name) => name.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase);

    public static string StripDisabled(string name) => IsDisabledName(name) ? name[..^DisabledSuffix.Length] : name;

    // The priority the game loads this file at, or null when no PackedFile line matches. A disabled file is
    // matched as if it were on, so turning it back on is known to load it.
    public int? PriorityOf(string relativePath)
    {
        string rel = StripDisabled(relativePath);
        foreach (var p in _patterns)
        {
            if (p.Match.IsMatch(rel)) return p.Priority;
        }
        return null;
    }

    // The load position of a file: its priority and the index of the first pattern that matches it (earlier lines
    // load first). Null when no pattern matches.
    public (int Priority, int Order)? LoadPositionOf(string relativePath)
    {
        string rel = StripDisabled(relativePath);
        for (int i = 0; i < _patterns.Count; i++)
        {
            if (_patterns[i].Match.IsMatch(rel)) return (_patterns[i].Priority, i);
        }
        return null;
    }

    // Folders to look for Mods files in: the fixed part of each pattern ("Packages" for "Packages/*/*.package").
    // A pattern with no fixed folder covers only files directly in Mods, so an unlisted backup folder never shows up.
    public IEnumerable<(string RelativeDir, bool Recursive)> SearchRoots()
    {
        var roots = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _patterns)
        {
            // Only patterns that load packages (not e.g. "DCCache/*.dbc").
            if (!p.RelativePattern.EndsWith(".package", StringComparison.OrdinalIgnoreCase)) continue;
            var segments = p.RelativePattern.Split('/');
            var fixedSegments = segments.Take(segments.Length - 1).TakeWhile(s => s.IndexOfAny(new[] { '*', '?' }) < 0).ToList();
            string dir = string.Join('/', fixedSegments);
            bool recursive = dir.Length > 0;
            if (IsPlumbobForgePath(dir)) continue;
            roots[dir] = roots.TryGetValue(dir, out var r) ? r || recursive : recursive;
        }
        // A root inside another recursive root is already covered.
        foreach (var (dir, recursive) in roots)
        {
            bool covered = roots.Any(o => o.Value && o.Key.Length > 0 && !o.Key.Equals(dir, StringComparison.OrdinalIgnoreCase) &&
                                          dir.StartsWith(o.Key + "/", StringComparison.OrdinalIgnoreCase));
            if (!covered) yield return (dir, recursive);
        }
    }

    private void ReadFile(string cfgPath, int depth)
    {
        string baseDir = Path.GetDirectoryName(cfgPath) ?? ModsDir;
        int priority = 0;
        string[] lines;
        try { lines = File.ReadAllLines(cfgPath); }
        catch { return; }

        foreach (var raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal)) continue;
            int space = line.IndexOfAny(new[] { ' ', '\t' });
            if (space <= 0) continue;
            string keyword = line[..space];
            string arg = line[space..].Trim();

            if (keyword.Equals("Priority", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(arg.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), out int p)) priority = p;
            }
            else if (keyword.Equals("PackedFile", StringComparison.OrdinalIgnoreCase))
            {
                string? rel = ResolvePattern(baseDir, Unquote(arg));
                if (rel == null || IsPlumbobForgePath(rel)) continue;
                _patterns.Add(new Pattern(rel, priority, ToRegex(rel)));
            }
            else if (keyword.Equals("Scan", StringComparison.OrdinalIgnoreCase))
            {
                if (depth >= MaxScanDepth) continue;
                string? relDir = ResolvePattern(baseDir, Unquote(arg).TrimEnd('/', '\\'));
                if (relDir != null && CacheConfigPriority == null &&
                    (relDir.Equals("Cache/Config", StringComparison.OrdinalIgnoreCase) || relDir.Equals("Cache/Config/Resource.cfg", StringComparison.OrdinalIgnoreCase)))
                {
                    CacheConfigPriority = priority;
                    CacheConfigOrder = _patterns.Count;
                }
                if (relDir == null || IsPlumbobForgePath(relDir)) continue;
                if (relDir.EndsWith("/Resource.cfg", StringComparison.OrdinalIgnoreCase)) relDir = relDir[..^"/Resource.cfg".Length];
                string nested = Path.Combine(ModsDir, relDir.Replace('/', Path.DirectorySeparatorChar), "Resource.cfg");
                if (File.Exists(nested)) ReadFile(nested, depth + 1);
            }
        }
    }

    private static string Unquote(string arg)
    {
        string a = arg.Trim();
        if (a.Length >= 2 && a[0] == '"')
        {
            int end = a.IndexOf('"', 1);
            if (end > 0) return a[1..end];
        }
        // Anything after the path (e.g. "autoupdate") is not part of it.
        int space = a.IndexOfAny(new[] { ' ', '\t' });
        return space > 0 ? a[..space] : a;
    }

    // Relative to Mods with forward slashes, or null when it points outside Mods.
    private string? ResolvePattern(string baseDir, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return null;
        string normalized = pattern.Replace('\\', '/');
        // Path.GetFullPath keeps the wildcards and collapses "..".
        string full;
        try
        {
            full = Path.IsPathRooted(normalized)
                ? Path.GetFullPath(normalized)
                : Path.GetFullPath(Path.Combine(baseDir, normalized.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch { return null; }
        return ToRelative(full);
    }

    private static Regex ToRegex(string relativePattern)
    {
        string body = string.Join("/", relativePattern.Split('/').Select(seg =>
            Regex.Escape(seg).Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]")));
        return new Regex("^" + body + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }
}
