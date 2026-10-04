using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace KoreanInputFontTool;

internal static class HookFailureDiagnosticWriter
{
    private static readonly object Sync = new();

    public static string? TryAppend(
        Exception exception,
        string daocRoot,
        string hookCompatibilityVersion,
        string? hookPath,
        IReadOnlyCollection<int> processIds)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "hook-errors.log");
            var report = BuildReport(
                exception,
                daocRoot,
                hookCompatibilityVersion,
                hookPath,
                processIds);
            lock (Sync)
                File.AppendAllText(path, report, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return path;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildReport(
        Exception exception,
        string daocRoot,
        string hookCompatibilityVersion,
        string? hookPath,
        IReadOnlyCollection<int> processIds)
    {
        var builder = new StringBuilder();
        builder.AppendLine("============================================================");
        builder.AppendLine($"TimestampLocal: {DateTimeOffset.Now:O}");
        builder.AppendLine($"ApplicationVersion: {Assembly.GetExecutingAssembly().GetName().Version}");
        builder.AppendLine($"HookCompatibilityVersion: {hookCompatibilityVersion}");
        builder.AppendLine($"Framework: {RuntimeInformation.FrameworkDescription}");
        builder.AppendLine($"OS: {RuntimeInformation.OSDescription}");
        builder.AppendLine($"OSArchitecture: {RuntimeInformation.OSArchitecture}");
        builder.AppendLine($"ProcessArchitecture: {RuntimeInformation.ProcessArchitecture}");
        builder.AppendLine($"Is64BitOperatingSystem: {Environment.Is64BitOperatingSystem}");
        builder.AppendLine($"Is64BitProcess: {Environment.Is64BitProcess}");
        builder.AppendLine($"Elevated: {IsElevated()}");
        builder.AppendLine($"CurrentCulture: {CultureInfo.CurrentCulture.Name}");
        builder.AppendLine($"CurrentUICulture: {CultureInfo.CurrentUICulture.Name}");
        builder.AppendLine($"DaocRoot: {daocRoot}");
        AppendFileDetails(builder, "HookFile", hookPath);

        if (processIds.Count == 0)
        {
            builder.AppendLine("GameProcesses: none");
        }
        else
        {
            foreach (var processId in processIds.Order())
                AppendProcessDetails(builder, processId, hookCompatibilityVersion);
        }

        builder.AppendLine("Exception:");
        builder.AppendLine(exception.ToString());
        builder.AppendLine();
        return builder.ToString();
    }

    private static void AppendProcessDetails(
        StringBuilder builder,
        int processId,
        string hookCompatibilityVersion)
    {
        builder.AppendLine($"GameProcess[{processId}]:");
        try
        {
            using var process = Process.GetProcessById(processId);
            builder.AppendLine($"  Name: {process.ProcessName}");
            builder.AppendLine($"  WindowTitle: {SafeRead(() => process.MainWindowTitle)}");
            builder.AppendLine($"  StartTime: {SafeRead(() => process.StartTime.ToString("O", CultureInfo.InvariantCulture))}");
            var executablePath = SafeRead(() => process.MainModule?.FileName ?? "unavailable");
            builder.AppendLine($"  ExecutablePath: {executablePath}");
            if (!string.Equals(executablePath, "unavailable", StringComparison.Ordinal))
                AppendFileDetails(builder, "  Executable", executablePath);
        }
        catch (Exception ex)
        {
            builder.AppendLine($"  ProcessReadError: {ex.GetType().Name}: {ex.Message}");
        }

        var readyEventName =
            $@"Local\KoreanInputFontTool.RenderHook.v{hookCompatibilityVersion}.{processId}";
        try
        {
            var exists = EventWaitHandle.TryOpenExisting(readyEventName, out var readyEvent);
            using (readyEvent)
            {
                builder.AppendLine($"  ReadyEvent: {(exists ? "exists" : "missing")}");
                if (exists && readyEvent is not null)
                    builder.AppendLine($"  ReadyEventSignaled: {readyEvent.WaitOne(0)}");
            }
        }
        catch (Exception ex)
        {
            builder.AppendLine($"  ReadyEventError: {ex.GetType().Name}: {ex.Message}");
        }

        AppendNativeInstallDiagnostics(builder, processId);
    }

    private static void AppendNativeInstallDiagnostics(StringBuilder builder, int processId)
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KoreanInputFontTool",
            $"render-hook-{processId}.log");
        builder.AppendLine($"  NativeLogPath: {path}");
        if (!File.Exists(path))
        {
            builder.AppendLine("  NativeInstallDiagnostics: log missing");
            return;
        }

        try
        {
            var installLines = File.ReadLines(path)
                .Where(line =>
                    line.Contains("install", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("error", StringComparison.OrdinalIgnoreCase))
                .TakeLast(20)
                .ToArray();
            builder.AppendLine("  NativeInstallDiagnostics:");
            if (installLines.Length == 0)
                builder.AppendLine("    no install/error lines");
            else
                foreach (var line in installLines)
                    builder.AppendLine($"    {line}");
        }
        catch (Exception ex)
        {
            builder.AppendLine($"  NativeLogReadError: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void AppendFileDetails(StringBuilder builder, string label, string? path)
    {
        builder.AppendLine($"{label}Path: {path ?? "unavailable"}");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            builder.AppendLine($"{label}Exists: False");
            return;
        }

        try
        {
            var info = new FileInfo(path);
            builder.AppendLine($"{label}Exists: True");
            builder.AppendLine($"{label}Size: {info.Length}");
            builder.AppendLine($"{label}LastWriteTime: {info.LastWriteTimeUtc:O}");
            builder.AppendLine($"{label}Version: {FileVersionInfo.GetVersionInfo(path).FileVersion}");
            using var stream = File.OpenRead(path);
            builder.AppendLine($"{label}Sha256: {Convert.ToHexString(SHA256.HashData(stream))}");
        }
        catch (Exception ex)
        {
            builder.AppendLine($"{label}ReadError: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string SafeRead(Func<string> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            return $"unavailable ({ex.GetType().Name}: {ex.Message})";
        }
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
