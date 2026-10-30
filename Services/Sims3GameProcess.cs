using System;
using System.Diagnostics;
using System.IO;

namespace PlumbobForge.Backend.Services;

public class GameRunningException : InvalidOperationException
{
    public GameRunningException(string message) : base(message) { }
}

public static class Sims3GameProcess
{
    // TS3.exe is the base game, TS3W.exe the launcher-started game with expansions; TS3EP*/TS3SP* exist on some installs.
    private static readonly string[] ProcessNames = { "TS3", "TS3W", "TS3EP01", "TS3EP02", "TS3EP03", "TS3EP04", "TS3EP05", "TS3EP06", "TS3EP07", "TS3EP08", "TS3EP09", "TS3EP10", "TS3EP11" };

    private static readonly string[] CoreCacheFiles =
    {
        "CASPartCache.package",
        "compositorCache.package",
        "scriptCache.package",
        "simCompositorCache.package",
        "socialCache.package"
    };

    private const string PendingCacheClearFileName = "PendingGameCacheClear.flag";

    public static bool IsGameRunning()
    {
        foreach (var name in ProcessNames)
        {
            Process[] processes = Array.Empty<Process>();
            try
            {
                processes = Process.GetProcessesByName(name);
                if (processes.Length > 0) return true;
            }
            catch
            {
                // Access to the process list can fail; assume not running rather than blocking every edit.
            }
            finally
            {
                foreach (var p in processes) p.Dispose();
            }
        }
        return false;
    }

    public static void ThrowIfGameRunning(string message)
    {
        if (IsGameRunning()) throw new GameRunningException(message);
    }

    private static string GetPendingFlagPath(string appDataDir) => Path.Combine(appDataDir, PendingCacheClearFileName);

    public static void ClearCoreCachesOrDefer(string sims3UserDir, string appDataDir)
    {
        if (string.IsNullOrWhiteSpace(sims3UserDir) || !Directory.Exists(sims3UserDir)) return;

        if (IsGameRunning())
        {
            TryWritePendingFlag(appDataDir);
            return;
        }

        if (DeleteCoreCaches(sims3UserDir))
        {
            TryDeletePendingFlag(appDataDir);
        }
        else
        {
            TryWritePendingFlag(appDataDir);
        }
    }

    public static void ProcessPendingCacheClear(string sims3UserDir, string appDataDir)
    {
        if (string.IsNullOrWhiteSpace(appDataDir) || !File.Exists(GetPendingFlagPath(appDataDir))) return;
        if (IsGameRunning()) return;
        if (string.IsNullOrWhiteSpace(sims3UserDir) || !Directory.Exists(sims3UserDir)) return;

        if (DeleteCoreCaches(sims3UserDir))
        {
            TryDeletePendingFlag(appDataDir);
        }
    }

    private static bool DeleteCoreCaches(string sims3UserDir)
    {
        bool allDeleted = true;
        foreach (var name in CoreCacheFiles)
        {
            string path = Path.Combine(sims3UserDir, name);
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                allDeleted = false;
            }
        }
        return allDeleted;
    }

    private static void TryWritePendingFlag(string appDataDir)
    {
        if (string.IsNullOrWhiteSpace(appDataDir)) return;
        try
        {
            Directory.CreateDirectory(appDataDir);
            File.WriteAllText(GetPendingFlagPath(appDataDir), DateTime.UtcNow.ToString("O"));
        }
        catch { }
    }

    private static void TryDeletePendingFlag(string appDataDir)
    {
        if (string.IsNullOrWhiteSpace(appDataDir)) return;
        try
        {
            string flag = GetPendingFlagPath(appDataDir);
            if (File.Exists(flag)) File.Delete(flag);
        }
        catch { }
    }
}
