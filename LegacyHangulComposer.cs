namespace KoreanInputFontTool;

public sealed record CompositionUpdate(
    bool Handled,
    int EraseUtf16Units,
    string TextToSend);

/// <summary>
/// Hangul composer used by the DAoC input mode.
/// It supports the standard 2-beolsik layout and the 3-beolsik Final layout.
/// The composed syllable is encoded to DAoC component glyph IDs by the caller.
/// </summary>
public sealed class LegacyHangulComposer
{
    private static readonly char[] Initials = "ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ".ToCharArray();
    private static readonly char[] Medials = "ㅏㅐㅑㅒㅓㅔㅕㅖㅗㅘㅙㅚㅛㅜㅝㅞㅟㅠㅡㅢㅣ".ToCharArray();
    private static readonly char[] Finals = "ㄱㄲㄳㄴㄵㄶㄷㄹㄺㄻㄼㄽㄾㄿㅀㅁㅂㅄㅅㅆㅇㅈㅊㅋㅌㅍㅎ".ToCharArray();

    private static readonly Dictionary<char, char> InitialByKey = new()
    {
        ['r'] = 'ㄱ', ['R'] = 'ㄲ', ['s'] = 'ㄴ', ['e'] = 'ㄷ', ['E'] = 'ㄸ',
        ['f'] = 'ㄹ', ['a'] = 'ㅁ', ['q'] = 'ㅂ', ['Q'] = 'ㅃ', ['t'] = 'ㅅ',
        ['T'] = 'ㅆ', ['d'] = 'ㅇ', ['w'] = 'ㅈ', ['W'] = 'ㅉ', ['c'] = 'ㅊ',
        ['z'] = 'ㅋ', ['x'] = 'ㅌ', ['v'] = 'ㅍ', ['g'] = 'ㅎ'
    };

    private static readonly Dictionary<char, char> MedialByKey = new()
    {
        ['k'] = 'ㅏ', ['o'] = 'ㅐ', ['i'] = 'ㅑ', ['O'] = 'ㅒ', ['j'] = 'ㅓ',
        ['p'] = 'ㅔ', ['u'] = 'ㅕ', ['P'] = 'ㅖ', ['h'] = 'ㅗ', ['y'] = 'ㅛ',
        ['n'] = 'ㅜ', ['b'] = 'ㅠ', ['m'] = 'ㅡ', ['l'] = 'ㅣ'
    };

    // 3-beolsik Final (세벌식 최종) jamo keys. Printable symbol keys are
    // deliberately omitted here; this table only owns Hangul composition.
    private static readonly Dictionary<char, (JamoRole Role, char Jamo)> SebeolsikFinalByKey = new()
    {
        ['a'] = (JamoRole.Final, 'ㅇ'), ['b'] = (JamoRole.Medial, 'ㅜ'),
        ['c'] = (JamoRole.Medial, 'ㅔ'), ['d'] = (JamoRole.Medial, 'ㅣ'),
        ['e'] = (JamoRole.Medial, 'ㅕ'), ['f'] = (JamoRole.Medial, 'ㅏ'),
        ['g'] = (JamoRole.Medial, 'ㅡ'), ['h'] = (JamoRole.Initial, 'ㄴ'),
        ['i'] = (JamoRole.Initial, 'ㅁ'), ['j'] = (JamoRole.Initial, 'ㅇ'),
        ['k'] = (JamoRole.Initial, 'ㄱ'), ['l'] = (JamoRole.Initial, 'ㅈ'),
        ['m'] = (JamoRole.Initial, 'ㅎ'), ['n'] = (JamoRole.Initial, 'ㅅ'),
        ['o'] = (JamoRole.Initial, 'ㅊ'), ['p'] = (JamoRole.Initial, 'ㅍ'),
        ['q'] = (JamoRole.Final, 'ㅅ'), ['r'] = (JamoRole.Medial, 'ㅐ'),
        ['s'] = (JamoRole.Final, 'ㄴ'), ['t'] = (JamoRole.Medial, 'ㅓ'),
        ['u'] = (JamoRole.Initial, 'ㄷ'), ['v'] = (JamoRole.Medial, 'ㅗ'),
        ['w'] = (JamoRole.Final, 'ㄹ'), ['x'] = (JamoRole.Final, 'ㄱ'),
        ['y'] = (JamoRole.Initial, 'ㄹ'), ['z'] = (JamoRole.Final, 'ㅁ'),

        ['0'] = (JamoRole.Initial, 'ㅋ'), ['1'] = (JamoRole.Final, 'ㅎ'),
        ['2'] = (JamoRole.Final, 'ㅆ'), ['3'] = (JamoRole.Final, 'ㅂ'),
        ['4'] = (JamoRole.Medial, 'ㅛ'), ['5'] = (JamoRole.Medial, 'ㅠ'),
        ['6'] = (JamoRole.Medial, 'ㅑ'), ['7'] = (JamoRole.Medial, 'ㅖ'),
        ['8'] = (JamoRole.Medial, 'ㅢ'), ['9'] = (JamoRole.Medial, 'ㅜ'),
        [';'] = (JamoRole.Initial, 'ㅂ'), ['\''] = (JamoRole.Initial, 'ㅌ'),
        ['/'] = (JamoRole.Medial, 'ㅗ'),

        ['!'] = (JamoRole.Final, 'ㄲ'), ['#'] = (JamoRole.Final, 'ㅈ'),
        ['$'] = (JamoRole.Final, 'ㄿ'), ['%'] = (JamoRole.Final, 'ㄾ'),
        ['@'] = (JamoRole.Final, 'ㄺ'), ['A'] = (JamoRole.Final, 'ㄷ'),
        ['C'] = (JamoRole.Final, 'ㅋ'), ['D'] = (JamoRole.Final, 'ㄼ'),
        ['E'] = (JamoRole.Final, 'ㄵ'), ['F'] = (JamoRole.Final, 'ㄻ'),
        ['G'] = (JamoRole.Medial, 'ㅒ'), ['Q'] = (JamoRole.Final, 'ㅍ'),
        ['R'] = (JamoRole.Final, 'ㅀ'), ['S'] = (JamoRole.Final, 'ㄶ'),
        ['T'] = (JamoRole.Final, 'ㄽ'), ['V'] = (JamoRole.Final, 'ㄳ'),
        ['W'] = (JamoRole.Final, 'ㅌ'), ['X'] = (JamoRole.Final, 'ㅄ'),
        ['Z'] = (JamoRole.Final, 'ㅊ')
    };

    private readonly Dictionary<(char, char), char> initialCombinations = new()
    {
        [('ㄱ', 'ㄱ')] = 'ㄲ', [('ㄷ', 'ㄷ')] = 'ㄸ', [('ㅂ', 'ㅂ')] = 'ㅃ',
        [('ㅅ', 'ㅅ')] = 'ㅆ', [('ㅈ', 'ㅈ')] = 'ㅉ'
    };

    private readonly Dictionary<(char, char), char> medialCombinations = new()
    {
        [('ㅗ', 'ㅏ')] = 'ㅘ', [('ㅗ', 'ㅐ')] = 'ㅙ', [('ㅗ', 'ㅣ')] = 'ㅚ',
        [('ㅜ', 'ㅓ')] = 'ㅝ', [('ㅜ', 'ㅔ')] = 'ㅞ', [('ㅜ', 'ㅣ')] = 'ㅟ',
        [('ㅡ', 'ㅣ')] = 'ㅢ'
    };

    private readonly Dictionary<(char, char), char> finalCombinations = new()
    {
        [('ㄱ', 'ㅅ')] = 'ㄳ', [('ㄴ', 'ㅈ')] = 'ㄵ', [('ㄴ', 'ㅎ')] = 'ㄶ',
        [('ㄹ', 'ㄱ')] = 'ㄺ', [('ㄹ', 'ㅁ')] = 'ㄻ', [('ㄹ', 'ㅂ')] = 'ㄼ',
        [('ㄹ', 'ㅅ')] = 'ㄽ', [('ㄹ', 'ㅌ')] = 'ㄾ', [('ㄹ', 'ㅍ')] = 'ㄿ',
        [('ㄹ', 'ㅎ')] = 'ㅀ', [('ㅂ', 'ㅅ')] = 'ㅄ', [('ㅅ', 'ㅅ')] = 'ㅆ'
    };

    private char? initial;
    private char? medial;
    private char? final;

    public HangulKeyboardLayout KeyboardLayout { get; set; } = HangulKeyboardLayout.Dubeolsik;

    public string CurrentText => BuildText(initial, medial, final);

    public CompositionUpdate Process(char key)
    {
        var oldText = CurrentText;
        if (KeyboardLayout == HangulKeyboardLayout.SebeolsikFinal)
        {
            if (!SebeolsikFinalByKey.TryGetValue(key, out var input))
                return new CompositionUpdate(false, 0, string.Empty);

            return input.Role switch
            {
                JamoRole.Initial => ProcessConsonant(oldText, input.Jamo),
                JamoRole.Medial => ProcessVowel(oldText, input.Jamo),
                JamoRole.Final => ProcessExplicitFinal(oldText, input.Jamo),
                _ => new CompositionUpdate(false, 0, string.Empty)
            };
        }

        if (InitialByKey.TryGetValue(key, out var consonant))
            return ProcessConsonant(oldText, consonant);

        if (MedialByKey.TryGetValue(key, out var vowel))
            return ProcessVowel(oldText, vowel);

        return new CompositionUpdate(false, 0, string.Empty);
    }

    public CompositionUpdate Backspace()
    {
        var oldText = CurrentText;
        if (final is not null)
            final = finalCombinations.FirstOrDefault(x => x.Value == final.Value).Key.Item1 is char first && first != '\0'
                ? first
                : null;
        else if (medial is not null)
            medial = medialCombinations.FirstOrDefault(x => x.Value == medial.Value).Key.Item1 is char first && first != '\0'
                ? first
                : null;
        else if (initial is not null)
            initial = initialCombinations.FirstOrDefault(x => x.Value == initial.Value).Key.Item1 is char first && first != '\0'
                ? first
                : null;

        return new CompositionUpdate(true, oldText.Length, CurrentText);
    }

    public void Reset() => (initial, medial, final) = (null, null, null);

    private CompositionUpdate ProcessConsonant(string oldText, char consonant)
    {
        if (initial is null)
        {
            initial = consonant;
            return Update(oldText);
        }

        if (medial is null)
        {
            if (initialCombinations.TryGetValue((initial.Value, consonant), out var combinedInitial))
            {
                initial = combinedInitial;
                return Update(oldText);
            }

            var committed = initial.Value.ToString();
            initial = consonant;
            return new CompositionUpdate(true, oldText.Length, committed + CurrentText);
        }

        if (final is null)
        {
            if (ToFinalIndex(consonant) > 0)
            {
                final = consonant;
                return Update(oldText);
            }

            var committed = BuildSyllable(initial.Value, medial.Value, null).ToString();
            (initial, medial, final) = (consonant, null, null);
            return new CompositionUpdate(true, oldText.Length, committed + CurrentText);
        }

        if (finalCombinations.TryGetValue((final.Value, consonant), out var combinedFinal))
        {
            final = combinedFinal;
            return Update(oldText);
        }

        var finished = BuildSyllable(initial.Value, medial.Value, final.Value).ToString();
        (initial, medial, final) = (consonant, null, null);
        return new CompositionUpdate(true, oldText.Length, finished + CurrentText);
    }

    private CompositionUpdate ProcessVowel(string oldText, char vowel)
    {
        if (initial is null)
        {
            medial = CombineMedial(medial, vowel);
            return Update(oldText);
        }

        if (medial is null)
        {
            medial = vowel;
            return Update(oldText);
        }

        if (final is null && medialCombinations.TryGetValue((medial.Value, vowel), out var combinedMedial))
        {
            medial = combinedMedial;
            return Update(oldText);
        }

        if (final is not null)
        {
            var (previousFinal, nextInitial) = SplitFinal(final.Value);
            var committed = BuildSyllable(initial.Value, medial.Value, previousFinal).ToString();
            (initial, medial, final) = (nextInitial, vowel, null);
            return new CompositionUpdate(true, oldText.Length, committed + CurrentText);
        }

        var finished = BuildSyllable(initial.Value, medial.Value, null).ToString();
        (initial, medial, final) = (null, vowel, null);
        return new CompositionUpdate(true, oldText.Length, finished + CurrentText);
    }

    private CompositionUpdate ProcessExplicitFinal(string oldText, char consonant)
    {
        // A valid three-set jongseong belongs to the active syllable. If the
        // user starts with a jongseong key, keep the input usable as a
        // standalone consonant rather than losing the keystroke.
        if (initial is null || medial is null)
            return ProcessConsonant(oldText, consonant);

        if (final is null)
        {
            final = consonant;
            return Update(oldText);
        }

        if (finalCombinations.TryGetValue((final.Value, consonant), out var combinedFinal))
        {
            final = combinedFinal;
            return Update(oldText);
        }

        var finished = BuildSyllable(initial.Value, medial.Value, final.Value).ToString();
        (initial, medial, final) = (consonant, null, null);
        return new CompositionUpdate(true, oldText.Length, finished + CurrentText);
    }

    private CompositionUpdate Update(string oldText) =>
        new(true, oldText.Length, CurrentText);

    private static char CombineMedial(char? current, char next) =>
        current is not null && new Dictionary<(char, char), char>
        {
            [('ㅗ', 'ㅏ')] = 'ㅘ', [('ㅗ', 'ㅐ')] = 'ㅙ', [('ㅗ', 'ㅣ')] = 'ㅚ',
            [('ㅜ', 'ㅓ')] = 'ㅝ', [('ㅜ', 'ㅔ')] = 'ㅞ', [('ㅜ', 'ㅣ')] = 'ㅟ',
            [('ㅡ', 'ㅣ')] = 'ㅢ'
        }.TryGetValue((current.Value, next), out var combined)
            ? combined
            : next;

    private static (char? Previous, char? NextInitial) SplitFinal(char value) => value switch
    {
        'ㄳ' => ('ㄱ', 'ㅅ'), 'ㄵ' => ('ㄴ', 'ㅈ'), 'ㄶ' => ('ㄴ', 'ㅎ'),
        'ㄺ' => ('ㄹ', 'ㄱ'), 'ㄻ' => ('ㄹ', 'ㅁ'), 'ㄼ' => ('ㄹ', 'ㅂ'),
        'ㄽ' => ('ㄹ', 'ㅅ'), 'ㄾ' => ('ㄹ', 'ㅌ'), 'ㄿ' => ('ㄹ', 'ㅍ'),
        'ㅀ' => ('ㄹ', 'ㅎ'), 'ㅄ' => ('ㅂ', 'ㅅ'), _ => (null, value)
    };

    private static int ToFinalIndex(char value)
    {
        var index = Array.IndexOf(Finals, value);
        return index < 0 ? 0 : index + 1;
    }

    private static string BuildText(char? initial, char? medial, char? final)
    {
        if (initial is null && medial is null)
            return string.Empty;
        if (initial is null)
            return medial!.Value.ToString();
        if (medial is null)
            return initial.Value.ToString();
        return BuildSyllable(initial.Value, medial.Value, final).ToString();
    }

    private static char BuildSyllable(char initial, char medial, char? final)
    {
        var cho = Array.IndexOf(Initials, initial);
        var jung = Array.IndexOf(Medials, medial);
        var jong = final is null ? 0 : ToFinalIndex(final.Value);
        if (cho < 0 || jung < 0)
            return medial;

        return (char)(0xAC00 + ((cho * 21) + jung) * 28 + jong);
    }

    private enum JamoRole
    {
        Initial,
        Medial,
        Final
    }
}
