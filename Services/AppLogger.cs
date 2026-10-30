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
    private static string? _cachedLogsDirectory;

    private static string? _mainLogPath;
    private static string? _oldLogPath;
    private static string? _errorsLogPath;
    private static string? _oldErrorsLogPath;
    private static string? _fatalLogPath;
    private static string? _oldFatalLogPath;

    private static long _mainLogSize = -1;
    private static long _errorsLogSize = -1;

    private const long MaxLogFileSize = 5 * 1024 * 1024;    // 5 MB cap for main log
    private const long MaxErrorsLogFileSize = 2 * 1024 * 1024; // 2 MB cap for errors log

    // Deduplication / flood protection tracking
    private static string? _lastSignature;
    private static int _repeatedCount = 0;
    private static DateTime _lastRepeatTime = DateTime.MinValue;

    public static LogLevel MinimumLogLevel { get; set; } = LogLevel.Information;

    public static string LogsDirectory
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_customLogsDirectory))
            {
                return _customLogsDirectory;
            }

            return _cachedLogsDirectory ??= ResolveDefaultLogsDirectory();
        }
    }

    public static string ErrorsLogPath => _errorsLogPath ?? Path.Combine(LogsDirectory, "errors.log");

    public static void Initialize(string? customBaseDir = null)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(customBaseDir))
            {
                var dir = Path.Combine(customBaseDir, "Logs");
                Directory.CreateDirectory(dir);
                _customLogsDirectory = dir;
                _cachedLogsDirectory = dir;
            }
            else
            {
                _customLogsDirectory = ResolveDefaultLogsDirectory();
                _cachedLogsDirectory = _customLogsDirectory;
            }

            UpdateCachedFilePaths(_cachedLogsDirectory);
            CleanupLegacyDateLogs();

            // Bridge S3ForgeTools internal logger so errors and warnings from package
            // parsers and Sims3 services are captured in plumbobforge.log and errors.log.
            S3ForgeTools.Utils.Logging.LogManager.OnLogEntry = (level, module, message) =>
            {
                switch (level)
                {
                    case S3ForgeTools.Utils.Logging.LogManager.LogLevel.Fatal:
                        LogFatal($"[{module}] {message}", null);
                        break;
                    case S3ForgeTools.Utils.Logging.LogManager.LogLevel.Error:
                        LogError(message, null, module);
                        break;
                    case S3ForgeTools.Utils.Logging.LogManager.LogLevel.Warn:
                        LogWarning(message, null, module);
                        break;
                    case S3ForgeTools.Utils.Logging.LogManager.LogLevel.Info:
                        LogInfo(message, module);
                        break;
                    case S3ForgeTools.Utils.Logging.LogManager.LogLevel.Debug:
                        LogDebug(message, module);
                        break;
                }
            };

            // Write diagnostic startup banner
            WriteStartupBanner();
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
                _cachedLogsDirectory = dirPath;
                UpdateCachedFilePaths(dirPath);
                CleanupLegacyDateLogs();
            }
        }
        catch { }
    }

    private static void UpdateCachedFilePaths(string dir)
    {
        _mainLogPath = Path.Combine(dir, "plumbobforge.log");
        _oldLogPath = Path.Combine(dir, "plumbobforge.old.log");
        _errorsLogPath = Path.Combine(dir, "errors.log");
        _oldErrorsLogPath = Path.Combine(dir, "errors.old.log");
        _fatalLogPath = Path.Combine(dir, "fatal_crash.log");
        _oldFatalLogPath = Path.Combine(dir, "fatal_crash.old.log");

        _mainLogSize = -1;
        _errorsLogSize = -1;
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
                    UpdateCachedFilePaths(customLogsDir);
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
            UpdateCachedFilePaths(defaultDocBase);
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
            UpdateCachedFilePaths(localAppData);
            return localAppData;
        }
        catch { }

        // 4. Base directory fallback
        UpdateCachedFilePaths(AppContext.BaseDirectory);
        return AppContext.BaseDirectory;
    }

    private static void WriteStartupBanner()
    {
        var sb = new StringBuilder();
        sb.AppendLine("================================================================================");
        sb.AppendLine($"PlumbobForge Desktop v{GetAppVersion()} Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}) | Runtime: {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"BaseDir: {AppContext.BaseDirectory}");
        sb.AppendLine($"LogsDir: {LogsDirectory}");
        sb.AppendLine("================================================================================");
        WriteRaw(sb.ToString(), isError: false);
    }

    public static void LogDebug(string message, string? category = null)
    {
        if (MinimumLogLevel <= LogLevel.Debug)
        {
            WriteEntry("DEBUG", category, message, null);
        }
    }

    public static void LogInfo(string message, string? category = null)
    {
        if (MinimumLogLevel <= LogLevel.Information)
        {
            WriteEntry("INFO", category, message, null);
        }
    }

    public static void LogWarning(string message, Exception? ex = null, string? category = null)
    {
        if (MinimumLogLevel <= LogLevel.Warning)
        {
            WriteEntry("WARN", category, message, ex);
        }
    }

    public static void LogError(string message, Exception? ex = null, string? category = null)
    {
        if (MinimumLogLevel <= LogLevel.Error)
        {
            WriteEntry("ERROR", category, message, ex);
        }
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
                EnsurePathsInitialized();

                // Flush any pending repeat counts
                FlushRepeatedMessage();

                // Write to main persistent log
                AppendWithRotation(_mainLogPath!, _oldLogPath!, logText, ref _mainLogSize, MaxLogFileSize);

                // Write to dedicated errors.log
                AppendWithRotation(_errorsLogPath!, _oldErrorsLogPath!, logText, ref _errorsLogSize, MaxErrorsLogFileSize);

                // Write to dedicated fatal_crash.log
                AppendWithRotation(_fatalLogPath!, _oldFatalLogPath!, logText, ref _mainLogSize, MaxLogFileSize);
            }
            catch
            {
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
        var cat = string.IsNullOrWhiteSpace(category) ? "App" : category;
        bool isWarningOrError = level is "WARN" or "ERROR" or "FATAL";

        lock (_fileLock)
        {
            // Flood protection / message deduplication:
            // If the same log line repeats rapidly in a loop, suppress and count it.
            string signature = $"{level}|{cat}|{message}";
            var now = DateTime.UtcNow;

            if (signature == _lastSignature && (now - _lastRepeatTime).TotalSeconds < 5)
            {
                _repeatedCount++;
                _lastRepeatTime = now;
                if (_repeatedCount < 3)
                {
                    // Allow first 2 repeats through for immediate visibility
                }
                else
                {
                    // Suppress subsequent repeats until a different message arrives or 25 repeats occur
                    if (_repeatedCount % 25 == 0)
                    {
                        var repeatMsg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level,-5}] [{cat}] ... [Previous message repeated {_repeatedCount} times]\n";
                        WriteRaw(repeatMsg, isWarningOrError);
                    }
                    return;
                }
            }
            else
            {
                if (_repeatedCount >= 3)
                {
                    var repeatSummary = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level,-5}] [{cat}] ... [Previous message repeated {_repeatedCount} times]\n";
                    WriteRaw(repeatSummary, isWarningOrError);
                }
                _lastSignature = signature;
                _repeatedCount = 1;
                _lastRepeatTime = now;
            }

            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
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
            if (isWarningOrError)
            {
                Console.Error.Write(logText);
            }
            else
            {
                Console.Write(logText);
            }

            WriteRaw(logText, isWarningOrError);
        }
    }

    private static void FlushRepeatedMessage()
    {
        if (_repeatedCount >= 3 && _lastSignature != null)
        {
            var repeatSummary = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [INFO ] [App] ... [Previous message repeated {_repeatedCount} times]\n";
            WriteRaw(repeatSummary, isError: false);
            _repeatedCount = 0;
            _lastSignature = null;
        }
    }

    private static void WriteRaw(string content, bool isError)
    {
        try
        {
            EnsurePathsInitialized();

            // Write to main persistent log
            AppendWithRotation(_mainLogPath!, _oldLogPath!, content, ref _mainLogSize, MaxLogFileSize);

            // Write to dedicated errors.log for pure, high-signal warning/error tracking
            if (isError)
            {
                AppendWithRotation(_errorsLogPath!, _oldErrorsLogPath!, content, ref _errorsLogSize, MaxErrorsLogFileSize);
            }
        }
        catch { }
    }

    private static void EnsurePathsInitialized()
    {
        if (_mainLogPath == null)
        {
            var dir = LogsDirectory;
            UpdateCachedFilePaths(dir);
        }
    }

    private static void AppendWithRotation(
        string currentLogPath,
        string rotatedLogPath,
        string content,
        ref long currentSize,
        long maxFileSize)
    {
        try
        {
            if (currentSize < 0)
            {
                currentSize = File.Exists(currentLogPath) ? new FileInfo(currentLogPath).Length : 0;
            }

            if (currentSize + content.Length > maxFileSize)
            {
                try
                {
                    File.Move(currentLogPath, rotatedLogPath, overwrite: true);
                    currentSize = 0;
                }
                catch { }
            }

            File.AppendAllText(currentLogPath, content);
            currentSize += content.Length;
        }
        catch
        {
            // If caching had a stale size, reset to re-read on next attempt
            currentSize = -1;
        }
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

            // Remove legacy date-stamped files like plumbobforge-2026-08-28.log
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

    public bool IsEnabled(LogLevel logLevel)
    {
        if (logLevel == LogLevel.None) return false;
        if (logLevel < AppLogger.MinimumLogLevel) return false;

        // Filter out noisy framework logs below Warning level (e.g. EF Core SQL query dumps)
        if (logLevel < LogLevel.Warning)
        {
            if (_categoryName.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase) ||
                _categoryName.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase) ||
                _categoryName.StartsWith("Microsoft.Extensions", StringComparison.OrdinalIgnoreCase) ||
                _categoryName.StartsWith("System.Net", StringComparison.OrdinalIgnoreCase) ||
                _categoryName.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

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
