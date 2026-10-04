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
        patched += PatchNamedChatFonts(
            Path.Combine(uiRoot, "atlantis", "assets.xml"),
            targetFontName);
        patched += PatchNamedChatFonts(
            Path.Combine(uiRoot, "custom", "assets.xml"),
            targetFontName);
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

    private static int PatchNamedChatFonts(string path, string targetFontName)
    {
        if (!File.Exists(path))
            return 0;

        var xml = File.ReadAllText(path, Encoding.Latin1);
        var matched = 0;
        var changed = false;
        var profiles = new (string Name, int Height)[]
        {
            ("chat_small", ChatSmallHeight),
            ("chat_large", ChatLargeHeight)
        };

        foreach (var profile in profiles)
        {
            if (!TryPatchTtfDefinition(
                    xml,
                    profile.Name,
                    targetFontName,
                    profile.Height,
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

    private static bool TryPatchTtfDefinition(
        string xml,
        string name,
        string targetFontName,
        int height,
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

        replaced = xml;
        return false;
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
