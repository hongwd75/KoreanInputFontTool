using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace KoreanInputFontTool;

internal static class AdministratorBootstrap
{
    private const string ElevationMarker = "--korean-input-font-tool-elevated";
    private const int OperationCanceled = 1223;

    public static bool EnsureAdministrator()
    {
        if (IsAdministrator())
            return true;

        var arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (arguments.Contains(ElevationMarker, StringComparer.OrdinalIgnoreCase))
        {
            MessageBox.Show(
                "관리자 권한으로 실행하지 못했습니다.",
                "KoreanInputFontTool",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return false;
        }

        try
        {
            var executablePath = Environment.ProcessPath ?? Application.ExecutablePath;
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = true,
                Verb = "runas"
            };

            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);
            startInfo.ArgumentList.Add(ElevationMarker);

            if (Process.Start(startInfo) is null)
                throw new InvalidOperationException("관리자 권한 프로세스를 시작하지 못했습니다.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == OperationCanceled)
        {
            MessageBox.Show(
                "관리자 권한 승격이 취소되어 프로그램을 종료합니다.",
                "KoreanInputFontTool",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"관리자 권한으로 다시 실행하지 못했습니다.\n\n{ex.Message}",
                "KoreanInputFontTool",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        return false;
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
