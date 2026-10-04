using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace KoreanInputFontTool;

internal sealed class LegacyRenderHookService
{
    // Keep this value stable across application releases while the native hook ABI is unchanged.
    internal const string HookCompatibilityVersion = "18_30";
    private const string ResourceName = "KoreanInputFontTool.Native.KoreanRenderHook32.dll.br";
    private const string HookFileName = "KoreanRenderHook32-v18_30.dll";
    private const string GameWindowTitlePrefix = "Dark Age of Camelot";
    private static readonly string[] GameProcessNames =
    [
        "eden.dll",
        "eden_2gb.dll",
        "eden",
        "eden_2gb",
        "camelot",
        "camelot_2gb",
        "game",
        "game_2gb"
    ];
    private readonly HashSet<int> injectedProcessIds = [];
    private string? extractedHookPath;
    private string? lastFailureSignature;
    private string? lastFailureLogPath;

    public IReadOnlyCollection<int> ActiveProcessIds => injectedProcessIds.ToArray();

    public RenderHookResult EnsureInjected(string daocRoot)
    {
        var gameProcesses = FindGameProcesses(daocRoot).ToArray();
        if (gameProcesses.Length == 0)
        {
            injectedProcessIds.Clear();
            lastFailureSignature = null;
            return new(false, "완성형 출력 대기 중 — DAoC 실행 후 자동 적용");
        }
        var activeProcessIds = gameProcesses.Select(process => process.Id).ToHashSet();

        var hookPath = ExtractHook();
        var rundll32 = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "SysWOW64",
            "rundll32.exe");
        if (!File.Exists(rundll32))
            throw new FileNotFoundException("32비트 rundll32.exe를 찾지 못했습니다.", rundll32);

        var newlyInjected = 0;
        foreach (var gameProcess in gameProcesses)
        {
            using (gameProcess)
            {
                if (injectedProcessIds.Contains(gameProcess.Id))
                    continue;

                using var readyEvent = new EventWaitHandle(
                    false,
                    EventResetMode.ManualReset,
                    ReadyEventName(gameProcess.Id));
                var startInfo = new ProcessStartInfo
                {
                    FileName = rundll32,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                startInfo.ArgumentList.Add($"{hookPath},Inject");
                startInfo.ArgumentList.Add(gameProcess.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                using var injector = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("32비트 렌더 훅 주입기를 시작하지 못했습니다.");

                if (!injector.WaitForExit(10_000))
                    throw new TimeoutException($"DAoC PID {gameProcess.Id} 렌더 훅 주입 시간이 초과되었습니다.");
                if (injector.ExitCode != 0)
                    throw new InvalidOperationException(
                        $"DAoC PID {gameProcess.Id} 렌더 훅 주입 실패 (오류 {injector.ExitCode}).");
                if (!readyEvent.WaitOne(5_000))
                    throw new TimeoutException(
                        $"DAoC PID {gameProcess.Id}에서 GDI 렌더 훅 활성화를 확인하지 못했습니다.");

                injectedProcessIds.Add(gameProcess.Id);
                newlyInjected++;
            }
        }

        injectedProcessIds.RemoveWhere(pid => !activeProcessIds.Contains(pid));
        lastFailureSignature = null;
        return newlyInjected > 0
            ? new(true, $"완성형 출력 훅 적용 완료 — DAoC {newlyInjected}개")
            : new(false, "완성형 출력 훅 실행 중");
    }

    public string? RecordFailure(string daocRoot, Exception exception)
    {
        var processIds = new HashSet<int>(injectedProcessIds);
        try
        {
            foreach (var process in FindGameProcesses(daocRoot))
            {
                using (process)
                    processIds.Add(process.Id);
            }
        }
        catch
        {
            // The original exception remains the primary diagnostic.
        }

        var signature = $"{exception.GetType().FullName}|{exception.Message}|{string.Join(',', processIds.Order())}";
        if (string.Equals(lastFailureSignature, signature, StringComparison.Ordinal))
            return lastFailureLogPath;

        lastFailureSignature = signature;
        lastFailureLogPath = HookFailureDiagnosticWriter.TryAppend(
            exception,
            daocRoot,
            HookCompatibilityVersion,
            extractedHookPath,
            processIds.ToArray());
        return lastFailureLogPath;
    }

    private static IReadOnlyList<Process> FindGameProcesses(string daocRoot)
    {
        var matches = new List<Process>();
        var candidateProcessIds = new HashSet<int>();
        var titleMatchedProcessIds = new HashSet<int>();
        var window = IntPtr.Zero;
        while ((window = NativeMethods.FindWindowEx(
            IntPtr.Zero,
            window,
            null,
            null)) != IntPtr.Zero)
        {
            var title = new StringBuilder(256);
            _ = NativeMethods.GetWindowText(window, title, title.Capacity);
            if (!title.ToString().StartsWith(
                GameWindowTitlePrefix,
                StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _ = NativeMethods.GetWindowThreadProcessId(window, out var processId);
            if (processId != 0)
            {
                candidateProcessIds.Add(unchecked((int)processId));
                titleMatchedProcessIds.Add(unchecked((int)processId));
            }
        }

        foreach (var processName in GameProcessNames)
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                candidateProcessIds.Add(process.Id);
                process.Dispose();
            }
        }

        var expectedRoot = Path.GetFullPath(daocRoot).TrimEnd(Path.DirectorySeparatorChar);
        foreach (var processId in candidateProcessIds)
        {
            Process? process = null;
            var matched = false;
            try
            {
                process = Process.GetProcessById(processId);
                var executable = process.MainModule?.FileName;
                var executableRoot = executable is null ? null : Path.GetDirectoryName(executable);
                if (executableRoot is not null &&
                    string.Equals(
                        Path.GetFullPath(executableRoot).TrimEnd(Path.DirectorySeparatorChar),
                        expectedRoot,
                        StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(process);
                    matched = true;
                }
                else if (titleMatchedProcessIds.Contains(processId) &&
                    string.IsNullOrWhiteSpace(executableRoot))
                {
                    matches.Add(process);
                    matched = true;
                }
            }
            catch
            {
                if (process is not null && titleMatchedProcessIds.Contains(processId))
                {
                    matches.Add(process);
                    matched = true;
                }
            }

            if (!matched)
                process?.Dispose();
        }
        return matches;
    }

    private static string ReadyEventName(int processId) =>
        $@"Local\KoreanInputFontTool.RenderHook.v18_30.{processId}";

    private string ExtractHook()
    {
        if (extractedHookPath is not null && File.Exists(extractedHookPath))
            return extractedHookPath;

        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("내장 32비트 완성형 렌더 훅을 찾지 못했습니다.");
        using var decompressor = new BrotliStream(resource, CompressionMode.Decompress);
        using var memory = new MemoryStream();
        decompressor.CopyTo(memory);
        var bytes = memory.ToArray();

        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KoreanInputFontTool",
            "NativeHook",
            HookCompatibilityVersion);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, HookFileName);
        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            File.WriteAllBytes(path, bytes);

        extractedHookPath = path;
        return path;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr FindWindowEx(
            IntPtr parent,
            IntPtr childAfter,
            string? className,
            string? windowName);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(
            IntPtr window,
            out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowText(
            IntPtr window,
            StringBuilder windowText,
            int maxCount);
    }
}

internal readonly record struct RenderHookResult(bool Changed, string Message);
