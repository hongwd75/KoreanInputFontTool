using KoreanInputFontTool;

static void Check(string keys, string expected, HangulKeyboardLayout layout = HangulKeyboardLayout.Dubeolsik, bool backspace = false)
{
    var composer = new LegacyHangulComposer { KeyboardLayout = layout };
    var text = "";
    void Apply(CompositionUpdate update)
    {
        if (!update.Handled) throw new Exception("Unhandled input");
        text = text[..(text.Length - update.EraseUtf16Units)] + update.TextToSend;
    }
    foreach (var key in keys) Apply(composer.Process(key));
    if (backspace) Apply(composer.Backspace());
    if (text != expected) throw new Exception($"{layout}: {keys}: expected {expected}, got {text}");
}

Check("bb", "ㅠㅠ");
Check("bbbb", "ㅠㅠㅠㅠ");
Check("nn", "ㅜㅜ");
Check("kj", "ㅏㅓ");
Check("hk", "ㅘ");
Check("hkl", "ㅘㅣ");
Check("brk", "ㅠ가");
Check("gksrmf", "한글");
Check("bb", "ㅠ", backspace: true);
Check("hk", "ㅗ", backspace: true);
Check("55", "ㅠㅠ", HangulKeyboardLayout.SebeolsikFinal);
Check("55", "ㅠ", HangulKeyboardLayout.SebeolsikFinal, backspace: true);
Console.WriteLine("Passed 12 composer regression cases.");
