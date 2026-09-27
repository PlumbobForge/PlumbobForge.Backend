using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PlumbobForge.Backend.Configuration;

namespace PlumbobForge.Backend.Services;

public class LocalizationService
{
    private static Func<string, object[]?, string>? _externalProvider;

    public static void SetExternalProvider(Func<string, object[]?, string> provider)
    {
        _externalProvider = provider;
    }

    private readonly IOptionsMonitor<PlumbobForgeOptions> _options;
    private readonly Dictionary<string, string> _strings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _fallbackStrings = new(StringComparer.OrdinalIgnoreCase);

    public LocalizationService(IOptionsMonitor<PlumbobForgeOptions> options)
    {
        _options = options;
        LoadStrings();
        _options.OnChange(_ => LoadStrings());
    }

    public string GetCurrentLanguage()
    {
        string configured = _options.CurrentValue?.Language ?? "auto";
        if (string.Equals(configured, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return CultureInfo.InstalledUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
        }
        return configured.ToLowerInvariant();
    }

    public string GetString(string key, params object[] args)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;

        if (_externalProvider != null)
        {
            try
            {
                var res = _externalProvider(key, args);
                if (!string.IsNullOrEmpty(res) && !string.Equals(res, key, StringComparison.OrdinalIgnoreCase))
                {
                    return res;
                }
            }
            catch { }
        }

        // Try direct key or progress/common/app prefix
        string? val = null;
        if (!_strings.TryGetValue(key, out val) &&
            !_strings.TryGetValue($"progress.{key}", out val) &&
            !_strings.TryGetValue($"common.{key}", out val))
        {
            if (!_fallbackStrings.TryGetValue(key, out val) &&
                !_fallbackStrings.TryGetValue($"progress.{key}", out val) &&
                !_fallbackStrings.TryGetValue($"common.{key}", out val))
            {
                val = key;
            }
        }

        if (args != null && args.Length > 0)
        {
            try
            {
                return string.Format(CultureInfo.CurrentUICulture, val ?? key, args);
            }
            catch
            {
                return val ?? key;
            }
        }

        return val ?? key;
    }

    private void LoadStrings()
    {
        _fallbackStrings.Clear();
        _strings.Clear();

        LoadDictForLang("en", _fallbackStrings);

        string lang = GetCurrentLanguage();
        if (!string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase))
        {
            LoadDictForLang(lang, _strings);
        }
        else
        {
            foreach (var kvp in _fallbackStrings)
            {
                _strings[kvp.Key] = kvp.Value;
            }
        }
    }

    private void LoadDictForLang(string lang, Dictionary<string, string> targetDict)
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string[] candidatePaths =
        {
            Path.Combine(baseDir, "Locales", $"{lang}.json"),
            Path.Combine(baseDir, "Assets", "Locales", $"{lang}.json"),
            Path.Combine(baseDir, "..", "PlumbobForge.Desktop", "Assets", "Locales", $"{lang}.json"),
            Path.Combine(baseDir, "..", "..", "..", "..", "PlumbobForge.Desktop", "Assets", "Locales", $"{lang}.json")
        };

        foreach (var path in candidatePaths)
        {
            if (File.Exists(path))
            {
                try
                {
                    using var stream = File.OpenRead(path);
                    using var doc = JsonDocument.Parse(stream);
                    FlattenElement(string.Empty, doc.RootElement, targetDict);
                    if (targetDict.Count > 0) return;
                }
                catch { }
            }
        }
    }

    private static void FlattenElement(string prefix, JsonElement element, Dictionary<string, string> dict)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    string key = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}.{prop.Name}";
                    FlattenElement(key, prop.Value, dict);
                }
                break;

            case JsonValueKind.String:
                dict[prefix] = element.GetString() ?? string.Empty;
                break;

            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                dict[prefix] = element.ToString();
                break;
        }
    }
}
