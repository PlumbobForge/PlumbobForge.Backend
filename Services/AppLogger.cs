using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace PlumbobForge.Backend.Services;

public static class AppLogger
{
    private static readonly object _fileLock = new();
    private static string? _customLogsDirectory;
    private const long MaxLogFileSize = 5 * 1024 * 1024; // 5 MB cap

    public static string LogsDirectory
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_customLogsDirectory) && Directory.Exists(_customLogsDirectory))
            {
                return _customLogsDirectory;
            }

            return ResolveDefaultLogsDirectory();
        }
    }

    public static void Initialize(string? customBaseDir = null)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(customBaseDir))
            {
                var dir = Path.Combine(customBaseDir, "Logs");
                Directory.CreateDirectory(dir);
                _customLogsDirectory = dir;
            }
            else
            {
                _customLogsDirectory = ResolveDefaultLogsDirectory();
            }

            CleanupLegacyDateLogs();
        }
        catch { }
    }

    public static void SetLogsDirectory(string dirPath)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(dirPath))
            {
                Directory.CreateDirectory(dirPath);
                _customLogsDirectory = dirPath;
                CleanupLegacyDateLogs();
            }
        }
        catch { }
    }

    private static string ResolveDefaultLogsDirectory()
    {
        // 1. Try reading DocumentBaseDir from appsettings.json
        try
        {
            var appDataJson = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "plumbobforge-app",
                "appsettings.json");

            if (File.Exists(appDataJson))
            {
                var text = File.ReadAllText(appDataJson);
                var jNode = System.Text.Json.Nodes.JsonNode.Parse(text);
                var docBaseDir = jNode?["PlumbobForge"]?["DocumentBaseDir"]?.ToString();
                if (!string.IsNullOrWhiteSpace(docBaseDir))
                {
                    var customLogsDir = Path.Combine(docBaseDir, "Logs");
                    Directory.CreateDirectory(customLogsDir);
                    return customLogsDir;
                }
            }
        }
        catch { }

        // 2. Default user Documents/PlumbobForge/Logs
        try
        {
            var defaultDocBase = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "PlumbobForge",
                "Logs");
            Directory.CreateDirectory(defaultDocBase);
            return defaultDocBase;
        }
        catch { }

        // 3. LocalAppData fallback
        try
        {
            var localAppData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PlumbobForge",
                "Logs");
            Directory.CreateDirectory(localAppData);
            return localAppData;
        }
        catch { }

        // 4. Base directory fallback
        return AppContext.BaseDirectory;
    }

    public static void LogInfo(string message, string? category = null)
    {
        WriteEntry("INFO", category, message, null);
    }

    public static void LogWarning(string message, Exception? ex = null, string? category = null)
    {
        WriteEntry("WARN", category, message, ex);
    }

    public static void LogError(string message, Exception? ex = null, string? category = null)
    {
        WriteEntry("ERROR", category, message, ex);
    }

    public static void LogFatal(string prefix, Exception? ex)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"========================= FATAL CRASH: {prefix} =========================");
        sb.AppendLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
        sb.AppendLine($"App Version: {GetAppVersion()}");
        sb.AppendLine($"OS Version: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        sb.AppendLine($"Framework: {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"Working Directory: {Environment.CurrentDirectory}");
        sb.AppendLine($"Base Directory: {AppContext.BaseDirectory}");
        sb.AppendLine($"Logs Directory: {LogsDirectory}");
        sb.AppendLine("--------------------------------------------------------------------------------");
        if (ex != null)
        {
            sb.AppendLine(FormatDetailedException(ex));
        }
        else
        {
            sb.AppendLine("No exception object provided.");
        }
        sb.AppendLine("================================================================================");
        sb.AppendLine();

        var logText = sb.ToString();

        // Print to Console
        Console.Error.WriteLine(logText);

        lock (_fileLock)
        {
            try
            {
                var logsDir = LogsDirectory;
                Directory.CreateDirectory(logsDir);

                // Write to main persistent log with rotation check
                AppendWithRotation(Path.Combine(logsDir, "plumbobforge.log"), Path.Combine(logsDir, "plumbobforge.old.log"), logText);

                // Write to dedicated fatal_crash.log with rotation check
                AppendWithRotation(Path.Combine(logsDir, "fatal_crash.log"), Path.Combine(logsDir, "fatal_crash.old.log"), logText);
            }
            catch
            {
                // Fallback to app directory if documents inaccessible
                try
                {
                    File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "fatal_crash.log"), logText);
                }
                catch { }
            }
        }
    }

    public static void WriteEntry(string level, string? category, string message, Exception? ex)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var cat = string.IsNullOrWhiteSpace(category) ? "App" : category;

        var sb = new StringBuilder();
        sb.Append($"[{timestamp}] [{level,-5}] [{cat}] {message}");

        if (ex != null)
        {
            sb.AppendLine();
            sb.Append(FormatDetailedException(ex));
        }
        sb.AppendLine();

        var logText = sb.ToString();

        // Echo to Console
        if (level == "ERROR" || level == "FATAL")
        {
            Console.Error.Write(logText);
        }
        else
        {
            Console.Write(logText);
        }

        lock (_fileLock)
        {
            try
            {
                var logsDir = LogsDirectory;
                Directory.CreateDirectory(logsDir);

                var mainLogPath = Path.Combine(logsDir, "plumbobforge.log");
                var oldLogPath = Path.Combine(logsDir, "plumbobforge.old.log");

                AppendWithRotation(mainLogPath, oldLogPath, logText);
            }
            catch { }
        }
    }

    private static void AppendWithRotation(string currentLogPath, string rotatedLogPath, string content)
    {
        try
        {
            if (File.Exists(currentLogPath))
            {
                var info = new FileInfo(currentLogPath);
                if (info.Length > MaxLogFileSize)
                {
                    try
                    {
                        File.Move(currentLogPath, rotatedLogPath, overwrite: true);
                    }
                    catch { }
                }
            }

            File.AppendAllText(currentLogPath, content);
        }
        catch { }
    }

    public static string FormatDetailedException(Exception ex, int depth = 0)
    {
        if (ex == null) return string.Empty;

        var indent = new string(' ', depth * 4);
        var sb = new StringBuilder();

        sb.AppendLine($"{indent}Type: {ex.GetType().FullName}");
        sb.AppendLine($"{indent}Message: {ex.Message}");
        sb.AppendLine($"{indent}HResult: 0x{ex.HResult:X8}");

        if (!string.IsNullOrWhiteSpace(ex.Source))
        {
            sb.AppendLine($"{indent}Source: {ex.Source}");
        }

        if (ex.TargetSite != null)
        {
            sb.AppendLine($"{indent}TargetSite: {ex.TargetSite}");
        }

        if (ex.Data != null && ex.Data.Count > 0)
        {
            sb.AppendLine($"{indent}Data:");
            foreach (var key in ex.Data.Keys)
            {
                sb.AppendLine($"{indent}  {key}: {ex.Data[key]}");
            }
        }

        if (!string.IsNullOrWhiteSpace(ex.StackTrace))
        {
            sb.AppendLine($"{indent}StackTrace:");
            foreach (var line in ex.StackTrace.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                sb.AppendLine($"{indent}  {line.Trim()}");
            }
        }

        // Handle AggregateException
        if (ex is AggregateException aggEx && aggEx.InnerExceptions.Count > 0)
        {
            int index = 1;
            foreach (var inner in aggEx.InnerExceptions)
            {
                sb.AppendLine($"{indent}├── Aggregate Inner [{index}/{aggEx.InnerExceptions.Count}]:");
                sb.Append(FormatDetailedException(inner, depth + 1));
                index++;
            }
        }
        else if (ex.InnerException != null)
        {
            sb.AppendLine($"{indent}└── Inner Exception:");
            sb.Append(FormatDetailedException(ex.InnerException, depth + 1));
        }

        return sb.ToString();
    }

    private static string GetAppVersion()
    {
        try
        {
            var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            return asm.GetName().Version?.ToString() ?? "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }

    private static void CleanupLegacyDateLogs()
    {
        try
        {
            var dir = LogsDirectory;
            if (!Directory.Exists(dir)) return;

            // Remove any legacy date-stamped files like plumbobforge-2026-08-28.log
            var oldDateFiles = Directory.GetFiles(dir, "plumbobforge-*.log");
            foreach (var file in oldDateFiles)
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch { }
    }
}

public class FileLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName)
    {
        return new FileLogger(categoryName);
    }

    public void Dispose() { }
}

public class FileLogger : ILogger
{
    private readonly string _categoryName;

    public FileLogger(string categoryName)
    {
        _categoryName = categoryName;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        string message = formatter != null ? formatter(state, exception) : state?.ToString() ?? string.Empty;

        string levelStr = logLevel switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO",
            LogLevel.Warning => "WARN",
            LogLevel.Error => "ERROR",
            LogLevel.Critical => "FATAL",
            _ => "INFO"
        };

        AppLogger.WriteEntry(levelStr, _categoryName, message, exception);
    }
}
