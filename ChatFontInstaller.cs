using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
namespace KoreanInputFontTool;

internal static class ChatFontInstaller
{
    private const string TargetFontName = "korean-font.ttf";
    private const string PrecomposedTargetFontName = "korean-font-complete-v18_4.ttf";
    private const string FontBundleResourceName = "KoreanInputFontTool.Assets.DaocLegacyFonts.br";
    private const int ChatSmallHeight = 11;
    private const int ChatLargeHeight = 14;
    private const string BackupSuffix = ".korean-input-font-tool.original";
    private static readonly (string Name, int Height, bool ConvertBitmap)[] CompleteUiFonts =
    [
        ("myriadbold", 13, false),
        ("minion", 16, false),
        ("button_small", 10, true),
        ("button_large", 12, true),
        ("brit9", 9, true),
        ("brit9s", 9, true),
        ("arial9", 9, true),
        ("arial11", 11, true),
        ("arial14", 14, true),
        ("title", 24, true),
        ("barb10", 10, true)
    ];

    public static string Apply(string daocRoot, LegacyGlyphMode glyphMode)
    {
        var uiRoot = Path.Combine(daocRoot, "ui");
        var fontBytes = LoadFont(glyphMode);
        var targetFontName = glyphMode == LegacyGlyphMode.PrecomposedHangul
            ? PrecomposedTargetFontName
            : TargetFontName;
        var targetFonts = TargetFontFiles(uiRoot, targetFontName).ToArray();
        var targetFont = targetFonts[0];

        if (!Directory.Exists(uiRoot))
            throw new DirectoryNotFoundException($"DAoC UI 폴더를 찾지 못했습니다: {uiRoot}");

        foreach (var fontPath in targetFonts)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fontPath)!);
            WriteFontIfChanged(fontPath, fontBytes);
        }

        var patched = 0;
        var patchCompleteUi = glyphMode == LegacyGlyphMode.PrecomposedHangul;
        patched += PatchNamedFonts(
            Path.Combine(uiRoot, "atlantis", "assets.xml"),
            targetFontName,
            patchCompleteUi);
        patched += PatchNamedFonts(
            Path.Combine(uiRoot, "custom", "assets.xml"),
            targetFontName,
            patchCompleteUi);
        patched += PatchSingleTtf(
            Path.Combine(uiRoot, "custom", "ghost_chatfont.xml"),
            "ghost_chat_font",
            targetFontName);

        if (patched == 0)
            throw new InvalidOperationException("적용 가능한 채팅 TTF 선언을 찾지 못했습니다.");

        return targetFont;
    }

    private static byte[] LoadFont(LegacyGlyphMode glyphMode)
    {
        var displayName = glyphMode switch
        {
            LegacyGlyphMode.ExtendedHangul => "확장된 한글",
            LegacyGlyphMode.PrecomposedHangul => "완성형 한글",
            _ => "KDAOC"
        };
        using var stream = typeof(ChatFontInstaller).Assembly
            .GetManifestResourceStream(FontBundleResourceName)
            ?? throw new InvalidOperationException("내장 한글 폰트 번들을 찾지 못했습니다.");
        using var decompressor = new BrotliStream(stream, CompressionMode.Decompress);
        using var memory = new MemoryStream();
        decompressor.CopyTo(memory);
        var bundle = memory.ToArray();
        if (bundle.Length < sizeof(int) * 3)
            throw new InvalidOperationException("내장 한글 폰트 번들이 손상되었습니다.");

        var kdaocLength = BitConverter.ToInt32(bundle, 0);
        var extendedLength = BitConverter.ToInt32(bundle, sizeof(int));
        var precomposedLength = BitConverter.ToInt32(bundle, sizeof(int) * 2);
        if (kdaocLength <= 0 || extendedLength <= 0 || precomposedLength <= 0 ||
            sizeof(int) * 3 + kdaocLength + extendedLength + precomposedLength != bundle.Length)
        {
            throw new InvalidOperationException("내장 한글 폰트 번들의 길이 정보가 올바르지 않습니다.");
        }

        var headerLength = sizeof(int) * 3;
        var (offset, length) = glyphMode switch
        {
            LegacyGlyphMode.ExtendedHangul => (headerLength + kdaocLength, extendedLength),
            LegacyGlyphMode.PrecomposedHangul =>
                (headerLength + kdaocLength + extendedLength, precomposedLength),
            _ => (headerLength, kdaocLength)
        };
        var selected = bundle.AsSpan(offset, length).ToArray();
        if (selected.Length == 0)
            throw new InvalidOperationException($"내장 {displayName} 폰트가 비어 있습니다.");
        return selected;
    }

    public static bool Restore(string daocRoot)
    {
        var uiRoot = Path.Combine(daocRoot, "ui");
        var restored = false;
        foreach (var path in CandidateXmlFiles(uiRoot))
        {
            var backup = path + BackupSuffix;
            if (!File.Exists(backup))
                continue;

            File.Copy(backup, path, overwrite: true);
            restored = true;
        }

        foreach (var targetFontName in new[] { TargetFontName, PrecomposedTargetFontName })
        {
            foreach (var targetFont in TargetFontFiles(uiRoot, targetFontName))
            {
                try
                {
                    if (File.Exists(targetFont))
                        File.Delete(targetFont);
                }
                catch (IOException)
                {
                    // A running DAoC process can retain a private font handle.
                }
                catch (UnauthorizedAccessException)
                {
                    // Restore the XML even when a loaded font file remains locked.
                }
            }
        }
        return restored;
    }

    private static int PatchNamedFonts(
        string path,
        string targetFontName,
        bool patchCompleteUi)
    {
        if (!File.Exists(path))
            return 0;

        var xml = File.ReadAllText(path, Encoding.Latin1);
        var matched = 0;
        var changed = false;
        var profiles = new List<(string Name, int Height, bool ConvertBitmap)>
        {
            ("chat_small", ChatSmallHeight, false),
            ("chat_large", ChatLargeHeight, false)
        };
        if (patchCompleteUi)
            profiles.AddRange(CompleteUiFonts);

        foreach (var profile in profiles)
        {
            if (!TryPatchFontDefinition(
                    xml,
                    profile.Name,
                    targetFontName,
                    profile.Height,
                    profile.ConvertBitmap,
                    out var replaced))
            {
                continue;
            }

            matched++;
            if (!string.Equals(xml, replaced, StringComparison.Ordinal))
                changed = true;
            xml = replaced;
        }

        if (changed)
            WriteWithBackup(path, xml);
        return matched;
    }

    private static bool TryPatchFontDefinition(
        string xml,
        string name,
        string targetFontName,
        int height,
        bool convertBitmap,
        out string replaced)
    {
        var ttfPattern = $"(<TTFFont\\s*>\\s*<Name>{Regex.Escape(name)}</Name>\\s*<File>)[^<]*(</File>)";
        if (Regex.IsMatch(
                xml,
                ttfPattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(2)))
        {
            replaced = Regex.Replace(
                xml,
                ttfPattern,
                $"$1fonts/{targetFontName}$2",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(2));
            replaced = ReplaceFontHeight(replaced, name, height);
            return true;
        }

        if (!convertBitmap)
        {
            replaced = xml;
            return false;
        }

        var bitmapPattern =
            $"(?<indent>^[ \\t]*)<Font\\s*>\\s*<Name>{Regex.Escape(name)}</Name>\\s*" +
            "<File>[^<]*</File>\\s*</Font>";
        var match = Regex.Match(
            xml,
            bitmapPattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline,
            TimeSpan.FromSeconds(2));
        if (!match.Success)
        {
            replaced = xml;
            return false;
        }

        var indent = match.Groups["indent"].Value;
        var newLine = xml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var definition =
            $"{indent}<TTFFont>{newLine}" +
            $"{indent}    <Name>{name}</Name>{newLine}" +
            $"{indent}    <File>fonts/{targetFontName}</File>{newLine}" +
            $"{indent}    <Height>{height}</Height>{newLine}" +
            $"{indent}    <Antialiased>true</Antialiased>{newLine}" +
            $"{indent}    <Hint>2</Hint>{newLine}" +
            $"{indent}</TTFFont>";
        replaced = Regex.Replace(
            xml,
            bitmapPattern,
            _ => definition,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline,
            TimeSpan.FromSeconds(2));
        return true;
    }

    private static int PatchSingleTtf(string path, string name, string targetFontName)
    {
        if (!File.Exists(path))
            return 0;

        var xml = File.ReadAllText(path, Encoding.Latin1);
        var pattern = $"(<TTFFont\\s*>\\s*<Name>{Regex.Escape(name)}</Name>\\s*<File>)[^<]*(</File>)";
        if (!Regex.IsMatch(xml, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)))
            return 0;

        var replaced = Regex.Replace(
            xml,
            pattern,
            $"$1fonts/{targetFontName}$2",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(2));
        replaced = ReplaceFontHeight(replaced, name, ChatSmallHeight);
        if (!string.Equals(xml, replaced, StringComparison.Ordinal))
            WriteWithBackup(path, replaced);
        return 1;
    }

    private static string ReplaceFontHeight(string xml, string name, int height)
    {
        var pattern = $"(<TTFFont\\s*>\\s*<Name>{Regex.Escape(name)}</Name>[\\s\\S]*?<Height>)[^<]*(</Height>)";
        return Regex.Replace(
            xml,
            pattern,
            match => $"{match.Groups[1].Value}{height}{match.Groups[2].Value}",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(2));
    }

    private static void WriteWithBackup(string path, string xml)
    {
        var backup = path + BackupSuffix;
        if (!File.Exists(backup))
            File.Copy(path, backup);
        File.WriteAllText(path, xml, Encoding.Latin1);
    }

    private static void WriteFontIfChanged(string path, byte[] fontBytes)
    {
        if (File.Exists(path))
        {
            var existing = File.ReadAllBytes(path);
            if (existing.AsSpan().SequenceEqual(fontBytes))
                return;
        }
        File.WriteAllBytes(path, fontBytes);
    }

    private static IEnumerable<string> TargetFontFiles(string uiRoot, string targetFontName)
    {
        yield return Path.Combine(uiRoot, "fonts", targetFontName);
        yield return Path.Combine(uiRoot, "atlantis", "fonts", targetFontName);
        yield return Path.Combine(uiRoot, "custom", "fonts", targetFontName);
    }

    private static IEnumerable<string> CandidateXmlFiles(string uiRoot)
    {
        yield return Path.Combine(uiRoot, "atlantis", "assets.xml");
        yield return Path.Combine(uiRoot, "custom", "assets.xml");
        yield return Path.Combine(uiRoot, "custom", "ghost_chatfont.xml");
    }
}
