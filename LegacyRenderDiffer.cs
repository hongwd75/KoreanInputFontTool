namespace KoreanInputFontTool;

public sealed record LegacyRenderDelta(
    int EraseCodeUnits,
    string CodeUnitsToAppend);

/// <summary>
/// Produces the smallest suffix replacement for kdaoc's overlaid glyph stream.
/// Rewriting a complete syllable breaks the font's component positioning.
/// </summary>
public static class LegacyRenderDiffer
{
    public static LegacyRenderDelta Create(string previous, string replacement)
    {
        previous ??= string.Empty;
        replacement ??= string.Empty;

        var common = 0;
        var limit = Math.Min(previous.Length, replacement.Length);
        while (common < limit && previous[common] == replacement[common])
            common++;

        return new LegacyRenderDelta(
            previous.Length - common,
            replacement[common..]);
    }
}
