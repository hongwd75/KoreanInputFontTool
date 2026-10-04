using Microsoft.Win32;

namespace KoreanInputFontTool;

internal static class RegistrySettings
{
    private const string KeyPath = @"Software\KoreanInputFontTool";
    private const string DaocRootPathValue = "DaocRootPath";
    private const string KeyboardLayoutValue = "KeyboardLayout";
    private const string HangulDisplayModeValue = "HangulDisplayMode";
    private const string TranslationEnabledValue = "TranslationEnabled";
    private const string TranslationProviderValue = "TranslationProvider";
    private const string TranslationApiKeyValue = "TranslationApiKey";
    private const string TranslationRegionValue = "TranslationRegion";
    private const string TranslationEndpointValue = "TranslationEndpoint";
    private const string TranslationChannelsValue = "TranslationChannels";
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

    public static TranslationOptions LoadTranslationOptions()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
            if (key is null)
                return TranslationOptions.Default;

            var provider = TranslationOptions.Default.Provider;
            if (key.GetValue(TranslationProviderValue) is string providerText &&
                Enum.TryParse<TranslationProvider>(providerText, ignoreCase: true, out var storedProvider) &&
                Enum.IsDefined(storedProvider))
            {
                provider = storedProvider;
            }

            var channels = TranslationOptions.Default.Channels;
            if (key.GetValue(TranslationChannelsValue) is int channelValue)
            {
                const int channelsBeforeLfgWasAdded =
                    (int)(TranslationChannel.Guild |
                          TranslationChannel.Group |
                          TranslationChannel.Whisper |
                          TranslationChannel.Say);
                var storedChannels = channelValue == channelsBeforeLfgWasAdded
                    ? TranslationChannel.All
                    : (TranslationChannel)channelValue & TranslationChannel.PersistedMask;
                if (storedChannels.HasFlag(TranslationChannel.FullTranslation))
                    storedChannels = TranslationChannel.FullTranslation;
                if (storedChannels != TranslationChannel.None)
                    channels = storedChannels;
            }

            var apiKey = key.GetValue(TranslationApiKeyValue) is byte[] protectedApiKey
                ? UserSecretProtector.Unprotect(protectedApiKey)
                : string.Empty;

            return new TranslationOptions(
                Enabled: key.GetValue(TranslationEnabledValue) is int enabled && enabled != 0,
                Provider: provider,
                ApiKey: apiKey,
                Region: key.GetValue(TranslationRegionValue) as string ?? string.Empty,
                Endpoint: key.GetValue(TranslationEndpointValue) as string ?? TranslationOptions.Default.Endpoint,
                Channels: channels);
        }
        catch
        {
            // A corrupt or unreadable translation setting must not prevent startup.
            return TranslationOptions.Default;
        }
    }

    public static void SaveTranslationOptions(TranslationOptions options)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true)
            ?? throw new InvalidOperationException("번역 설정 레지스트리 키를 만들 수 없습니다.");
        key.SetValue(TranslationEnabledValue, options.Enabled ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue(TranslationProviderValue, options.Provider.ToString(), RegistryValueKind.String);
        key.SetValue(TranslationRegionValue, options.Region.Trim(), RegistryValueKind.String);
        key.SetValue(TranslationEndpointValue, options.Endpoint.Trim(), RegistryValueKind.String);
        var channels = options.Channels.HasFlag(TranslationChannel.FullTranslation)
            ? TranslationChannel.FullTranslation
            : options.Channels & TranslationChannel.All;
        key.SetValue(TranslationChannelsValue, (int)channels, RegistryValueKind.DWord);

        if (string.IsNullOrWhiteSpace(options.ApiKey))
            key.DeleteValue(TranslationApiKeyValue, throwOnMissingValue: false);
        else
            key.SetValue(TranslationApiKeyValue, UserSecretProtector.Protect(options.ApiKey.Trim()), RegistryValueKind.Binary);
    }
}
