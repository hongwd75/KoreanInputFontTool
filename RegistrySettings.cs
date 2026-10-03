using Microsoft.Win32;

namespace KoreanInputFontTool;

internal static class RegistrySettings
{
    private const string KeyPath = @"Software\KoreanInputFontTool";
    private const string DaocRootPathValue = "DaocRootPath";
    private const string KeyboardLayoutValue = "KeyboardLayout";
    private const string HangulDisplayModeValue = "HangulDisplayMode";
    private const string DefaultDaocRootPath = @"C:\Game\Electronic Arts\Dark Age of Camelot";

    public static string LoadDaocRootPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
            if (key?.GetValue(DaocRootPathValue) is string path &&
                !string.IsNullOrWhiteSpace(path))
            {
                return path.Trim();
            }
        }
        catch
        {
            // A missing or unreadable user setting must not prevent startup.
        }

        return DefaultDaocRootPath;
    }

    public static void SaveDaocRootPath(string path)
    {
        var normalizedPath = path.Trim();
        if (normalizedPath.Length == 0)
            return;

        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true)
            ?? throw new InvalidOperationException("DAoC 폴더 설정 레지스트리 키를 만들 수 없습니다.");
        key.SetValue(DaocRootPathValue, normalizedPath, RegistryValueKind.String);
    }

    public static HangulKeyboardLayout LoadKeyboardLayout()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
            var stored = key?.GetValue(KeyboardLayoutValue);
            if (stored is string text &&
                Enum.TryParse<HangulKeyboardLayout>(text, ignoreCase: true, out var layout) &&
                Enum.IsDefined(layout))
            {
                return layout;
            }

            if (stored is int number && Enum.IsDefined(typeof(HangulKeyboardLayout), number))
                return (HangulKeyboardLayout)number;
        }
        catch
        {
            // A missing or unreadable user setting must not prevent startup.
        }

        return HangulKeyboardLayout.Dubeolsik;
    }

    public static void SaveKeyboardLayout(HangulKeyboardLayout layout)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true)
            ?? throw new InvalidOperationException("입력 모드 설정 레지스트리 키를 만들 수 없습니다.");
        key.SetValue(KeyboardLayoutValue, layout.ToString(), RegistryValueKind.String);
    }

    public static LegacyGlyphMode LoadHangulDisplayMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
            var stored = key?.GetValue(HangulDisplayModeValue);
            if (stored is string text &&
                Enum.TryParse<LegacyGlyphMode>(text, ignoreCase: true, out var mode) &&
                Enum.IsDefined(mode))
            {
                return mode;
            }

            if (stored is int number && Enum.IsDefined(typeof(LegacyGlyphMode), number))
                return (LegacyGlyphMode)number;
        }
        catch
        {
            // A missing or unreadable user setting must not prevent startup.
        }

        return LegacyGlyphMode.Kdaoc;
    }

    public static void SaveHangulDisplayMode(LegacyGlyphMode mode)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true)
            ?? throw new InvalidOperationException("한글출력 설정 레지스트리 키를 만들 수 없습니다.");
        key.SetValue(HangulDisplayModeValue, mode.ToString(), RegistryValueKind.String);
    }
}
