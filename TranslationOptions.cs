namespace KoreanInputFontTool;

internal enum TranslationProvider
{
    MicrosoftTranslator,
    GoogleCloudTranslation,
    LibreTranslate
}

[Flags]
internal enum TranslationChannel
{
    None = 0,
    Guild = 1,
    Group = 2,
    Whisper = 4,
    Say = 8,
    Lfg = 16,
    FullTranslation = 32,
    Alliance = 64,
    All = Guild | Group | Whisper | Say | Lfg | Alliance,
    PersistedMask = All | FullTranslation
}

internal sealed record TranslationOptions(
    bool Enabled,
    TranslationProvider Provider,
    string ApiKey,
    string Region,
    string Endpoint,
    TranslationChannel Channels)
{
    public static TranslationOptions Default { get; } = new(
        Enabled: false,
        Provider: TranslationProvider.MicrosoftTranslator,
        ApiKey: string.Empty,
        Region: string.Empty,
        Endpoint: "http://localhost:5000",
        Channels: TranslationChannel.All);
}
