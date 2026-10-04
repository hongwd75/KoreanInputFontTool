using System.ComponentModel;
using System.Diagnostics;
using System.Net;

namespace KoreanInputFontTool;

internal sealed class DockerDesktopNotInstalledException : InvalidOperationException
{
    public DockerDesktopNotInstalledException()
        : base("Docker Desktop이 설치되어 있지 않습니다.")
    {
    }
}

internal sealed class WslNotInstalledException : InvalidOperationException
{
    public WslNotInstalledException()
        : base("WSL 2가 설치되어 있지 않습니다.")
    {
    }
}

internal static class LibreTranslateLocalServerService
{
    private const string ContainerName = "libretranslate";
    private const string ImageName = "libretranslate/libretranslate";
    private static readonly HttpClient ProbeClient = new()
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    public static bool IsSupportedLocalEndpoint(string endpoint) =>
        TryGetLocalEndpoint(endpoint, out _);

    public static bool IsDockerDesktopInstalled() =>
        FindDockerCli() is not null || FindDockerDesktop() is not null;

    public static async Task<bool> IsWslInstalledAsync(CancellationToken cancellationToken)
    {
        var wsl = FindWsl();
        if (wsl is null)
            return false;

        var version = await RunCommandAsync(
            wsl,
            ["--version"],
            TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);
        if (version.ExitCode == 0)
            return true;

        var status = await RunCommandAsync(
            wsl,
            ["--status"],
            TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);
        return status.ExitCode == 0;
    }

    public static async Task<bool> IsAvailableAsync(
        string endpoint,
        CancellationToken cancellationToken)
    {
        if (!TryGetLocalEndpoint(endpoint, out var endpointUri))
            return false;

        var languagesUri = new Uri(
            $"{endpointUri.Scheme}://{endpointUri.Authority}/languages",
            UriKind.Absolute);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, languagesUri);
            using var response = await ProbeClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    public static async Task StartAsync(
        string endpoint,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!TryGetLocalEndpoint(endpoint, out var endpointUri))
        {
            throw new InvalidOperationException(
                "자동 시작은 http://localhost 또는 http://127.0.0.1 주소에서만 사용할 수 있습니다.");
        }
        if (await IsAvailableAsync(endpoint, cancellationToken).ConfigureAwait(false))
            return;

        progress?.Report("Docker Desktop 실행 상태를 확인하고 있습니다.");
        await EnsureDockerEngineAsync(progress, cancellationToken).ConfigureAwait(false);

        progress?.Report("LibreTranslate 컨테이너 상태를 확인하고 있습니다.");
        var inspect = await RunDockerAsync(
            ["inspect", "--format", "{{.State.Status}}", ContainerName],
            TimeSpan.FromSeconds(20),
            cancellationToken).ConfigureAwait(false);
        if (inspect.ExitCode == 0)
        {
            if (!string.Equals(inspect.Output.Trim(), "running", StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report("기존 LibreTranslate 컨테이너를 시작하고 있습니다.");
                var start = await RunDockerAsync(
                    ["start", ContainerName],
                    TimeSpan.FromMinutes(2),
                    cancellationToken).ConfigureAwait(false);
                EnsureSuccess(start, "기존 LibreTranslate 컨테이너를 시작하지 못했습니다.");
            }
        }
        else
        {
            progress?.Report("LibreTranslate Docker 이미지 설치 여부를 확인하고 있습니다.");
            var imageInspect = await RunDockerAsync(
                ["image", "inspect", ImageName],
                TimeSpan.FromSeconds(20),
                cancellationToken).ConfigureAwait(false);
            if (imageInspect.ExitCode != 0)
            {
                progress?.Report("LibreTranslate Docker 이미지를 다운로드하고 있습니다. 네트워크에 따라 오래 걸릴 수 있습니다.");
                var pull = await RunDockerAsync(
                    ["pull", ImageName],
                    TimeSpan.FromMinutes(30),
                    cancellationToken).ConfigureAwait(false);
                EnsureSuccess(pull, "LibreTranslate Docker 이미지를 다운로드하지 못했습니다.");
            }

            progress?.Report("LibreTranslate 컨테이너를 생성하고 있습니다.");
            var run = await RunDockerAsync(
                [
                    "run", "-d",
                    "--name", ContainerName,
                    "-p", $"127.0.0.1:{endpointUri.Port}:5000",
                    "--restart", "unless-stopped",
                    ImageName,
                    "--load-only", "en,ko"
                ],
                TimeSpan.FromMinutes(3),
                cancellationToken).ConfigureAwait(false);
            EnsureSuccess(run, "LibreTranslate 컨테이너를 만들지 못했습니다.");
        }

        progress?.Report("영어·한국어 번역 모델을 다운로드하고 불러오는 중입니다.");
        for (var attempt = 0; attempt < 90; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsAvailableAsync(endpoint, cancellationToken).ConfigureAwait(false))
                return;
            if (attempt > 0 && attempt % 5 == 0)
            {
                progress?.Report(
                    $"영어·한국어 번역 모델 다운로드·준비 중... {attempt * 2}초 경과");
            }
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            "컨테이너는 시작했지만 번역 서버 준비가 끝나지 않았습니다. " +
            "잠시 후 다시 확인하거나 설치 방법 상세의 docker logs 명령으로 상태를 확인하세요.");
    }

    public static async Task InstallDockerDesktopAsync(
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var winget = FindWinget();
        if (winget is null)
        {
            throw new InvalidOperationException(
                "Windows 패키지 관리자(winget)를 찾을 수 없습니다. " +
                "설치 방법 상세에서 Docker Desktop을 직접 설치하세요.");
        }

        progress?.Report("Docker Desktop 설치 파일을 다운로드하고 설치하고 있습니다.");
        var install = await RunCommandAsync(
            winget,
            [
                "install",
                "--id", "Docker.DockerDesktop",
                "--exact",
                "--source", "winget",
                "--accept-source-agreements",
                "--accept-package-agreements",
                "--silent",
                "--disable-interactivity"
            ],
            TimeSpan.FromMinutes(45),
            cancellationToken).ConfigureAwait(false);
        if (install.ExitCode != 0 && !IsDockerDesktopInstalled())
            EnsureSuccess(install, "Docker Desktop을 자동 설치하지 못했습니다.");

        if (!IsDockerDesktopInstalled())
        {
            throw new InvalidOperationException(
                "Docker Desktop 설치는 완료되었지만 실행 파일을 찾지 못했습니다. " +
                "Windows를 다시 시작한 뒤 재시도하거나 설치 방법 상세를 확인하세요.");
        }

        progress?.Report("Docker Desktop 설치 완료. WSL 2 실행 환경을 확인합니다.");
    }

    public static async Task InstallWslAsync(
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var wsl = FindWsl();
        if (wsl is null)
        {
            throw new InvalidOperationException(
                "wsl.exe를 찾을 수 없습니다. Windows 업데이트 상태를 확인하고 " +
                "설치 방법 상세에서 WSL 수동 설치 방법을 확인하세요.");
        }

        progress?.Report("WSL 2 필수 구성요소를 설치하고 있습니다.");
        var install = await RunCommandAsync(
            wsl,
            ["--install"],
            TimeSpan.FromMinutes(20),
            cancellationToken).ConfigureAwait(false);
        if (install.ExitCode is not (0 or 1641 or 3010))
            EnsureSuccess(install, "WSL 2를 자동 설치하지 못했습니다.");

        progress?.Report("WSL 2 설치 명령을 완료했습니다. Windows를 다시 시작해야 합니다.");
    }

    private static bool TryGetLocalEndpoint(string endpoint, out Uri endpointUri)
    {
        endpointUri = null!;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var parsed) ||
            !string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var isLoopback = string.Equals(parsed.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            IPAddress.TryParse(parsed.Host, out var address) && IPAddress.IsLoopback(address);
        if (!isLoopback || parsed.Port is < 1 or > 65535)
            return false;

        endpointUri = parsed;
        return true;
    }

    private static async Task EnsureDockerEngineAsync(
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!await IsWslInstalledAsync(cancellationToken).ConfigureAwait(false))
            throw new WslNotInstalledException();

        if (!IsDockerDesktopInstalled())
            throw new DockerDesktopNotInstalledException();

        var version = await RunDockerAsync(
            ["version", "--format", "{{.Server.Version}}"],
            TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);
        if (version.ExitCode == 0)
            return;

        if (!TryStartDockerDesktop())
        {
            throw new InvalidOperationException(
                "Docker Desktop을 실행할 수 없습니다. 시작 메뉴에서 Docker Desktop을 실행한 뒤 다시 시도하세요.\r\n\r\n" +
                Summarize(version.Error));
        }

        progress?.Report("Docker Desktop이 준비될 때까지 기다리고 있습니다.");
        for (var attempt = 0; attempt < 60; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            version = await RunDockerAsync(
                ["version", "--format", "{{.Server.Version}}"],
                TimeSpan.FromSeconds(10),
                cancellationToken).ConfigureAwait(false);
            if (version.ExitCode == 0)
                return;
        }

        throw new TimeoutException(
            "Docker Desktop이 제한 시간 안에 준비되지 않았습니다. " +
            "처음 실행했다면 Docker 사용 약관 동의와 WSL 설치를 완료하세요. " +
            "Windows 재시작이 요구된 경우 재시작 후 다시 시도하세요.");
    }

    private static bool TryStartDockerDesktop()
    {
        var path = FindDockerDesktop();
        if (path is null)
            return false;

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindDockerDesktop()
    {
        var candidates = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Docker", "Docker", "Docker Desktop.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Docker", "Docker", "Docker Desktop.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "DockerDesktop", "Docker Desktop.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static async Task<CommandResult> RunDockerAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        await RunCommandAsync(
            FindDockerCli() ?? "docker.exe",
            arguments,
            timeout,
            cancellationToken).ConfigureAwait(false);

    private static async Task<CommandResult> RunCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        Process? process = null;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Win32Exception ex)
        {
            return new CommandResult(-1, string.Empty, ex.Message);
        }
        if (process is null)
            return new CommandResult(-1, string.Empty, "docker.exe를 시작하지 못했습니다.");

        using (process)
        using (var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeoutCancellation.CancelAfter(timeout);
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(timeoutCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }
                if (cancellationToken.IsCancellationRequested)
                    throw;
                return new CommandResult(-2, string.Empty, "Docker 명령 실행 시간이 초과되었습니다.");
            }

            return new CommandResult(
                process.ExitCode,
                await outputTask.ConfigureAwait(false),
                await errorTask.ConfigureAwait(false));
        }
    }

    private static string? FindDockerCli()
    {
        var candidates = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Docker", "Docker", "resources", "bin", "docker.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Docker", "Docker", "resources", "bin", "docker.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "DockerDesktop", "resources", "bin", "docker.exe")
        };
        return candidates.FirstOrDefault(File.Exists) ?? FindOnPath("docker.exe");
    }

    private static string? FindWinget()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        var candidate = Path.Combine(
            localApplicationData,
            "Microsoft", "WindowsApps", "winget.exe");
        return File.Exists(candidate) ? candidate : FindOnPath("winget.exe");
    }

    private static string? FindWsl()
    {
        var candidate = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "wsl.exe");
        return File.Exists(candidate) ? candidate : FindOnPath("wsl.exe");
    }

    private static string? FindOnPath(string fileName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
            return null;

        foreach (var directory in pathValue.Split(Path.PathSeparator))
        {
            var trimmed = directory.Trim().Trim('"');
            if (trimmed.Length == 0)
                continue;
            try
            {
                var candidate = Path.Combine(trimmed, fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (Exception) when (
                directory.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
            }
        }

        return null;
    }

    private static void EnsureSuccess(CommandResult result, string message)
    {
        if (result.ExitCode == 0)
            return;
        throw new InvalidOperationException(
            message + "\r\n\r\n" + Summarize(result.Error.Length > 0 ? result.Error : result.Output));
    }

    private static string Summarize(string value)
    {
        var text = value.Trim();
        if (text.Length == 0)
            return "Docker에서 오류 내용을 반환하지 않았습니다.";
        return text.Length <= 1_000 ? text : text[^1_000..];
    }

    private sealed record CommandResult(int ExitCode, string Output, string Error);
}
