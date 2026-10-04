using System.Text.RegularExpressions;

namespace KoreanInputFontTool;

internal readonly record struct ParsedChatMessage(
    TranslationChannel Channel,
    string OriginalLine,
    string Message);

internal static partial class ChatMessageParser
{
    [GeneratedRegex(
        @"^\s*(?:\[(?<channel>Guild|Group|Whisper|Say|LFG)\]\s*[^:]+?\s*:\s*|(?<sender>[^,\r\n]+?)\s+(?<verb>sends|says),\s*)(?<message>.+?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChatLinePattern();

    [GeneratedRegex(
        @"^\s*\[[^\]\r\n]+\]\s*[^:\r\n]+?\s*:\s*(?<message>.+?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GenericChannelLinePattern();

    [GeneratedRegex(@"[A-Za-z]", RegexOptions.CultureInvariant)]
    private static partial Regex EnglishPattern();

    [GeneratedRegex(@"[\u1100-\u11FF\u3130-\u318F\uAC00-\uD7A3]", RegexOptions.CultureInvariant)]
    private static partial Regex HangulPattern();

    public static bool TryParse(
        string line,
        bool fullTranslation,
        out ParsedChatMessage message)
    {
        message = default;
        if (string.IsNullOrWhiteSpace(line))
            return false;

        var match = ChatLinePattern().Match(line);
        if (!match.Success)
        {
            if (!fullTranslation)
                return false;

            var trimmedLine = line.Trim();
            if (trimmedLine.StartsWith('/'))
                return false;

            var genericMatch = GenericChannelLinePattern().Match(line);
            var genericBody = genericMatch.Success
                ? genericMatch.Groups["message"].Value.Trim()
                : trimmedLine;
            if (!IsTranslatable(genericBody))
                return false;

            message = new ParsedChatMessage(
                TranslationChannel.FullTranslation,
                line,
                genericBody);
            return true;
        }

        var body = match.Groups["message"].Value.Trim();
        if (!IsTranslatable(body))
            return false;

        var channel = match.Groups["sender"].Success
            ? match.Groups["verb"].Value.ToUpperInvariant() switch
            {
                "SENDS" => TranslationChannel.Whisper,
                "SAYS" => TranslationChannel.Say,
                _ => TranslationChannel.None
            }
            : match.Groups["channel"].Value.ToUpperInvariant() switch
        {
            "GUILD" => TranslationChannel.Guild,
            "GROUP" => TranslationChannel.Group,
            "WHISPER" => TranslationChannel.Whisper,
            "SAY" => TranslationChannel.Say,
            "LFG" => TranslationChannel.Lfg,
            _ => TranslationChannel.None
        };
        if (channel == TranslationChannel.None)
            return false;

        message = new ParsedChatMessage(channel, line, body);
        return true;
    }

    private static bool IsTranslatable(string text) =>
        EnglishPattern().IsMatch(text) && !HangulPattern().IsMatch(text);
}
