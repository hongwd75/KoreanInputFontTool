using System.ComponentModel;
using System.Diagnostics;
using System.Net;

namespace KoreanInputFontTool;

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
        var version = await RunDockerAsync(
            ["version", "--format", "{{.Server.Version}}"],
            TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);
        if (version.ExitCode == 0)
            return;

        if (!TryStartDockerDesktop())
        {
            throw new InvalidOperationException(
                "Docker Desktop을 찾거나 실행할 수 없습니다. 설치 방법 상세에서 Docker Desktop을 설치한 뒤 다시 시도하세요.\r\n\r\n" +
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
            "Docker Desktop이 제한 시간 안에 준비되지 않았습니다. Docker Desktop 상태를 확인한 뒤 다시 시도하세요.");
    }

    private static bool TryStartDockerDesktop()
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
        var path = candidates.FirstOrDefault(File.Exists);
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

    private static async Task<CommandResult> RunDockerAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = FindDockerCli() ?? "docker.exe",
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
        return candidates.FirstOrDefault(File.Exists);
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
