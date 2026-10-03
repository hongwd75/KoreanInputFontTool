namespace KoreanInputFontTool;

/// <summary>
/// Converts modern Hangul syllables into context-sensitive Windows-1252 glyph
/// IDs used by the DAoC component-composition font.
/// </summary>
public static class DaocLegacyCodepointEncoder
{
    // Printable Windows-1252 Unicode characters for bytes 80, 82-8C, 8E and
    // 91-96. Extended mode uses them as no-final choseong IDs instead of the
    // unreliable U+0080..U+009F C1 control characters.
    private static readonly char[] ExtendedNoFinalInitialCodes =
    [
        '\u20AC', '\u201A', '\u0192', '\u201E', '\u2026', '\u2020', '\u2021',
        '\u02C6', '\u2030', '\u0160', '\u2039', '\u0152', '\u017D', '\u2018',
        '\u2019', '\u201C', '\u201D', '\u2022', '\u2013'
    ];

    private static readonly int[] InitialCodes =
    [
        1, 2, 4, 7, 8, 9, 17, 18, 19, 21,
        22, 23, 24, 25, 26, 27, 28, 29, 30
    ];

    private static readonly int[] MedialCodes =
    [
        31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41,
        42, 43, 44, 45, 46, 47, 48, 49, 50, 51
    ];

    private static readonly int[] FinalCodes =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 9, 10, 11, 12, 13, 14,
        15, 16, 17, 18, 20, 21, 22, 23, 24, 26, 27, 28, 29, 30
    ];

    private static readonly Dictionary<char, int> StandaloneInitialCodes = new()
    {
        ['ㄱ'] = 1, ['ㄲ'] = 2, ['ㄴ'] = 4, ['ㄷ'] = 7, ['ㄸ'] = 8,
        ['ㄹ'] = 9, ['ㅁ'] = 17, ['ㅂ'] = 18, ['ㅃ'] = 19, ['ㅅ'] = 21,
        ['ㅆ'] = 22, ['ㅇ'] = 23, ['ㅈ'] = 24, ['ㅉ'] = 25, ['ㅊ'] = 26,
        ['ㅋ'] = 27, ['ㅌ'] = 28, ['ㅍ'] = 29, ['ㅎ'] = 30
    };

    private static readonly Dictionary<char, int> StandaloneMedialCodes = new()
    {
        ['ㅏ'] = 31, ['ㅐ'] = 32, ['ㅑ'] = 33, ['ㅒ'] = 34, ['ㅓ'] = 35,
        ['ㅔ'] = 36, ['ㅕ'] = 37, ['ㅖ'] = 38, ['ㅗ'] = 39, ['ㅘ'] = 40,
        ['ㅙ'] = 41, ['ㅚ'] = 42, ['ㅛ'] = 43, ['ㅜ'] = 44, ['ㅝ'] = 45,
        ['ㅞ'] = 46, ['ㅟ'] = 47, ['ㅠ'] = 48, ['ㅡ'] = 49, ['ㅢ'] = 50,
        ['ㅣ'] = 51
    };

    public static string Encode(string text, LegacyGlyphMode glyphMode = LegacyGlyphMode.Kdaoc)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var output = new System.Text.StringBuilder(text.Length * 3);
        foreach (var value in text)
        {
            if (value is >= '\uAC00' and <= '\uD7A3')
            {
                AppendSyllable(output, value, glyphMode);
                continue;
            }

            if (StandaloneInitialCodes.TryGetValue(value, out var initialCode))
            {
                output.Append(EncodeInitial(initialCode));
                continue;
            }

            if (StandaloneMedialCodes.TryGetValue(value, out var medialCode))
            {
                output.Append(EncodeMedial(medialCode, hasFinal: false));
                continue;
            }

            output.Append(value);
        }

        return output.ToString();
    }

    private static void AppendSyllable(
        System.Text.StringBuilder output,
        char syllable,
        LegacyGlyphMode glyphMode)
    {
        var syllableIndex = syllable - 0xAC00;
        var initialIndex = syllableIndex / (21 * 28);
        var medialIndex = (syllableIndex % (21 * 28)) / 28;
        var finalIndex = syllableIndex % 28;
        var hasFinal = finalIndex != 0;

        output.Append(glyphMode == LegacyGlyphMode.ExtendedHangul && !hasFinal
            ? ExtendedNoFinalInitialCodes[initialIndex]
            : EncodeInitial(InitialCodes[initialIndex]));
        output.Append(EncodeMedial(MedialCodes[medialIndex], hasFinal));
        if (hasFinal)
            output.Append(EncodeFinal(FinalCodes[finalIndex]));
    }

    private static char EncodeInitial(int code)
    {
        var adjustment = (code > 2 ? 1 : 0)
            + (code > 4 ? 2 : 0)
            + (code > 9 ? 7 : 0)
            + (code > 19 ? 1 : 0);
        return (char)(0xAF + code - adjustment);
    }

    private static char EncodeMedial(int code, bool hasFinal)
    {
        var encoded = 0xAF + code - 11;
        if (!hasFinal)
        {
            // Match kdaoc print4daoc exactly. Its old "c -= 64" experiment is
            // commented out; the live code applies only this band adjustment.
            encoded = encoded >= 0xD3 ? encoded + 33 : encoded - 35;
        }
        return (char)encoded;
    }

    private static char EncodeFinal(int code)
    {
        var adjustment = (code > 7 ? 1 : 0)
            + (code > 18 ? 1 : 0)
            + (code > 24 ? 1 : 0)
            - 40;
        return (char)(0xAF + code - adjustment);
    }
}
