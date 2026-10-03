using System.Runtime.InteropServices;
using System.Text;

namespace KoreanInputFontTool;

public sealed class LegacyKeyboardHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int VkHangul = 0x15;
    private const int VkMenu = 0x12;
    private const int VkRMenu = 0xA5;
    private const uint HangulScanCode = 0x72;
    private const uint ExtendedHangulScanCode = 0xF2;
    private const uint AltScanCode = 0x38;
    private const int VkBack = 0x08;
    private const int VkEnter = 0x0D;
    private const int VkEscape = 0x1B;
    private const int VkSpace = 0x20;
    private const uint LlkhfExtended = 0x01;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;
    private static readonly UIntPtr InputMarker = new(0xFFC3D44Fu);
    private const uint WmInputLangChangeRequest = 0x0050;
    private const nint UsKeyboardLayout = 0x04090409;
    private const uint KlfActivate = 0x00000001;
    private const uint KlfSetForProcess = 0x00000100;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint NiCompositionString = 0x0015;
    private const uint CpsCancel = 0x0004;

    private readonly LegacyHangulComposer composer = new();
    private readonly NativeMethods.LowLevelKeyboardProc callback;
    private IntPtr hook;
    private bool enabled;
    private bool hangulMode;
    private bool toggleChordHeld;
    private LegacyGlyphMode glyphMode = LegacyGlyphMode.Kdaoc;
    private IntPtr loadedUsKeyboardLayout;
    private IntPtr loadedKoreanKeyboardLayout;
    private bool daocLayoutActive;

    public LegacyKeyboardHook()
    {
        callback = HookCallback;
    }

    public bool Enabled
    {
        get => enabled;
        set
        {
            enabled = value;
            hangulMode = false;
            toggleChordHeld = false;
            composer.Reset();
            HangulModeChanged?.Invoke(false);
        }
    }

    public bool HangulMode => hangulMode;

    public HangulKeyboardLayout KeyboardLayout
    {
        get => composer.KeyboardLayout;
        set
        {
            composer.KeyboardLayout = value;
            composer.Reset();
        }
    }

    public LegacyGlyphMode GlyphMode
    {
        get => glyphMode;
        set
        {
            glyphMode = value;
            composer.Reset();
        }
    }

    public event Action<bool>? HangulModeChanged;
    public event Action<string>? InputDiagnosticChanged;

    public void Start()
    {
        if (hook != IntPtr.Zero)
            return;

        using var process = System.Diagnostics.Process.GetCurrentProcess();
        hook = NativeMethods.SetWindowsHookEx(
            WhKeyboardLl,
            callback,
            NativeMethods.GetModuleHandle(process.MainModule?.ModuleName),
            0);

        if (hook == IntPtr.Zero)
            throw new InvalidOperationException($"키보드 훅 설치 실패: {Marshal.GetLastWin32Error()}");

        loadedKoreanKeyboardLayout = NativeMethods.LoadKeyboardLayout("00000412", 0);
        loadedUsKeyboardLayout = NativeMethods.LoadKeyboardLayout(
            "04090409",
            KlfActivate | KlfSetForProcess);
        ForceUsKeyboardLayout();
    }

    public void Stop()
    {
        if (hook == IntPtr.Zero)
            return;

        NativeMethods.UnhookWindowsHookEx(hook);
        hook = IntPtr.Zero;
        hangulMode = false;
        toggleChordHeld = false;
        composer.Reset();

        if (!IsDaocForeground())
            RestoreKoreanKeyboardLayout();

        if (loadedUsKeyboardLayout != IntPtr.Zero)
        {
            _ = NativeMethods.UnloadKeyboardLayout(loadedUsKeyboardLayout);
            loadedUsKeyboardLayout = IntPtr.Zero;
        }

        if (loadedKoreanKeyboardLayout != IntPtr.Zero)
        {
            _ = NativeMethods.UnloadKeyboardLayout(loadedKoreanKeyboardLayout);
            loadedKoreanKeyboardLayout = IntPtr.Zero;
        }
    }

    public void Dispose() => Stop();

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0)
            return NativeMethods.CallNextHookEx(hook, code, wParam, lParam);

        var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
        if (!enabled || data.ExtraInfo == InputMarker)
            return NativeMethods.CallNextHookEx(hook, code, wParam, lParam);

        var message = wParam.ToInt32();
        var virtualKey = (int)data.VirtualKeyCode;
        var isDedicatedHangul = virtualKey == VkHangul ||
            data.ScanCode is HangulScanCode or ExtendedHangulScanCode;
        var isRightAlt = virtualKey == VkRMenu ||
            (virtualKey == VkMenu && data.ScanCode == AltScanCode && (data.Flags & LlkhfExtended) != 0);
        var isShiftSpace = virtualKey == VkSpace &&
            NativeMethods.GetAsyncKeyState(0x10) < 0;
        var isToggleChord = isRightAlt || isShiftSpace;
        var isHangulToggle = isDedicatedHangul || isToggleChord;

        if (!IsDaocForeground())
        {
            if (daocLayoutActive ||
                (isHangulToggle && (message is WmKeyDown or WmSysKeyDown)))
                RestoreKoreanKeyboardLayout();

            toggleChordHeld = false;
            return NativeMethods.CallNextHookEx(hook, code, wParam, lParam);
        }

        if (!daocLayoutActive)
            ForceUsKeyboardLayout();

        // Korean 106-key keyboards such as Samsung SKG-3000UB can report the
        // dedicated Hangul key as a one-shot 0xF2/extended key-down without a
        // matching key-up. Toggle on every key-down instead of latching it.
        if (isDedicatedHangul)
        {
            if (message is WmKeyDown or WmSysKeyDown)
                ToggleHangulMode();

            return (IntPtr)1;
        }

        if (isToggleChord)
        {
            if (message is WmKeyDown or WmSysKeyDown)
            {
                if (!toggleChordHeld)
                    ToggleHangulMode();
                toggleChordHeld = true;
            }
            else if (message is WmKeyUp or WmSysKeyUp)
            {
                toggleChordHeld = false;
            }

            return (IntPtr)1;
        }

        if (message != WmKeyDown && message != WmSysKeyDown)
            return NativeMethods.CallNextHookEx(hook, code, wParam, lParam);

        if (virtualKey is VkEnter or VkEscape)
        {
            composer.Reset();
            ForceUsKeyboardLayout();
            return NativeMethods.CallNextHookEx(hook, code, wParam, lParam);
        }

        if (!hangulMode)
            return NativeMethods.CallNextHookEx(hook, code, wParam, lParam);

        if (virtualKey == VkBack)
        {
            var previousBackspaceEncoded = DaocLegacyCodepointEncoder.Encode(composer.CurrentText, GlyphMode);
            if (previousBackspaceEncoded.Length == 0)
                return NativeMethods.CallNextHookEx(hook, code, wParam, lParam);

            var update = composer.Backspace();
            var replacementBackspaceEncoded = DaocLegacyCodepointEncoder.Encode(update.TextToSend, GlyphMode);
            return SendReplacement(previousBackspaceEncoded, replacementBackspaceEncoded)
                ? (IntPtr)1
                : NativeMethods.CallNextHookEx(hook, code, wParam, lParam);
        }

        if (virtualKey == VkSpace)
        {
            composer.Reset();
            return NativeMethods.CallNextHookEx(hook, code, wParam, lParam);
        }

        var key = ToQwertyCharacter(virtualKey, NativeMethods.GetAsyncKeyState(0x10) < 0);
        if (key is null)
            return NativeMethods.CallNextHookEx(hook, code, wParam, lParam);

        var previousEncoded = DaocLegacyCodepointEncoder.Encode(composer.CurrentText, GlyphMode);
        var result = composer.Process(key.Value);
        if (!result.Handled)
            return NativeMethods.CallNextHookEx(hook, code, wParam, lParam);

        var replacementEncoded = DaocLegacyCodepointEncoder.Encode(result.TextToSend, GlyphMode);
        return SendReplacement(previousEncoded, replacementEncoded, virtualKey)
            ? (IntPtr)1
            : NativeMethods.CallNextHookEx(hook, code, wParam, lParam);
    }

    private void ToggleHangulMode()
    {
        hangulMode = !hangulMode;
        composer.Reset();
        ForceUsKeyboardLayout();
        HangulModeChanged?.Invoke(hangulMode);
    }

    private static char? ToQwertyCharacter(int virtualKey, bool shift)
    {
        if (virtualKey is >= 0x41 and <= 0x5A)
        {
            var value = (char)virtualKey;
            return shift ? value : char.ToLowerInvariant(value);
        }

        if (virtualKey is >= 0x30 and <= 0x39)
        {
            var index = virtualKey - 0x30;
            return shift ? ")!@#$%^&*("[index] : (char)virtualKey;
        }

        return virtualKey switch
        {
            0xBA => shift ? ':' : ';',
            0xBB => shift ? '+' : '=',
            0xBC => shift ? '<' : ',',
            0xBD => shift ? '_' : '-',
            0xBE => shift ? '>' : '.',
            0xBF => shift ? '?' : '/',
            0xC0 => shift ? '~' : '`',
            0xDB => shift ? '{' : '[',
            0xDC => shift ? '|' : '\\',
            0xDD => shift ? '}' : ']',
            0xDE => shift ? '"' : '\'',
            _ => null
        };
    }

    private bool SendReplacement(string previousEncoded, string replacementEncoded, int trailingVirtualKey = 0)
    {

        var delta = LegacyRenderDiffer.Create(previousEncoded, replacementEncoded);
        var trailingEventCount = trailingVirtualKey == 0 ? 0 : 3;
        var inputs = new List<NativeMethods.INPUT>(
            delta.EraseCodeUnits * 2 + delta.CodeUnitsToAppend.Length * 2 + trailingEventCount);
        for (var i = 0; i < delta.EraseCodeUnits; i++)
        {
            inputs.Add(NativeMethods.INPUT.Unicode('\u0008', false));
            inputs.Add(NativeMethods.INPUT.Unicode('\u0008', true));
        }

        foreach (var codeUnit in delta.CodeUnitsToAppend)
        {
            inputs.Add(NativeMethods.INPUT.Unicode(codeUnit, false));
            inputs.Add(NativeMethods.INPUT.Unicode(codeUnit, true));
        }

        if (trailingVirtualKey != 0)
        {
            inputs.Add(NativeMethods.INPUT.Keyboard(trailingVirtualKey));
            inputs.Add(NativeMethods.INPUT.Unicode('\u0008', false));
            inputs.Add(NativeMethods.INPUT.Unicode('\u0008', true));
        }

        if (inputs.Count == 0)
        {
            ReportInputDiagnostic(delta, 0, 0);
            return true;
        }

        var sent = NativeMethods.SendInput(
            (uint)inputs.Count,
            inputs.ToArray(),
            Marshal.SizeOf<NativeMethods.INPUT>());
        ReportInputDiagnostic(delta, sent, inputs.Count);
        return sent == inputs.Count;
    }

    private void ReportInputDiagnostic(LegacyRenderDelta delta, uint sent, int requested)
    {
        var currentEncoded = DaocLegacyCodepointEncoder.Encode(composer.CurrentText, GlyphMode);
        InputDiagnosticChanged?.Invoke(
            $"조합 '{composer.CurrentText}' / 코드 {FormatCodeUnits(currentEncoded)} / " +
            $"삭제 {delta.EraseCodeUnits}, 추가 {FormatCodeUnits(delta.CodeUnitsToAppend)} / " +
            $"SendInput {sent}/{requested}");
    }

    private static string FormatCodeUnits(string value) => value.Length == 0
        ? "(없음)"
        : string.Join(" ", value.Select(codeUnit => $"U+{(int)codeUnit:X4}"));

    private void ForceUsKeyboardLayout()
    {
        var layout = loadedUsKeyboardLayout != IntPtr.Zero
            ? loadedUsKeyboardLayout
            : (IntPtr)UsKeyboardLayout;
        _ = NativeMethods.ActivateKeyboardLayout(layout, KlfSetForProcess);
        daocLayoutActive = true;
        var window = IntPtr.Zero;
        while ((window = NativeMethods.FindWindowEx(IntPtr.Zero, window, "DAoCMWC", null)) != IntPtr.Zero)
        {
            var inputContext = NativeMethods.ImmGetContext(window);
            if (inputContext != IntPtr.Zero)
            {
                _ = NativeMethods.ImmNotifyIME(inputContext, NiCompositionString, CpsCancel, 0);
                _ = NativeMethods.ImmSetOpenStatus(inputContext, false);
                _ = NativeMethods.ImmReleaseContext(window, inputContext);
            }

            _ = NativeMethods.SendMessageTimeout(
                window,
                WmInputLangChangeRequest,
                1,
                (nint)layout,
                SmtoAbortIfHung,
                100,
                out _);
        }
    }

    private void RestoreKoreanKeyboardLayout()
    {
        if (loadedKoreanKeyboardLayout == IntPtr.Zero)
            return;

        _ = NativeMethods.ActivateKeyboardLayout(
            loadedKoreanKeyboardLayout,
            KlfSetForProcess);
        daocLayoutActive = false;
        _ = NativeMethods.PostMessage(
            NativeMethods.GetForegroundWindow(),
            WmInputLangChangeRequest,
            1,
            (nint)loadedKoreanKeyboardLayout);
    }

    private static bool IsDaocForeground()
    {
        var window = NativeMethods.GetForegroundWindow();
        var className = new StringBuilder(128);
        _ = NativeMethods.GetClassName(window, className, className.Capacity);
        return string.Equals(className.ToString(), "DAoCMWC", StringComparison.OrdinalIgnoreCase);
    }

    private static class NativeMethods
    {
        internal delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc callback, IntPtr module, uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr GetModuleHandle(string? moduleName);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr LoadKeyboardLayout(string keyboardLayoutId, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr ActivateKeyboardLayout(IntPtr keyboardLayout, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnloadKeyboardLayout(IntPtr keyboardLayout);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);

        [DllImport("user32.dll")]
        internal static extern short GetAsyncKeyState(int virtualKey);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr SendMessageTimeout(
            IntPtr window,
            uint message,
            nuint wParam,
            nint lParam,
            uint flags,
            uint timeout,
            out nuint result);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessage(IntPtr window, uint message, nuint wParam, nint lParam);

        [DllImport("imm32.dll")]
        internal static extern IntPtr ImmGetContext(IntPtr window);

        [DllImport("imm32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ImmReleaseContext(IntPtr window, IntPtr inputContext);

        [DllImport("imm32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ImmSetOpenStatus(IntPtr inputContext, [MarshalAs(UnmanagedType.Bool)] bool open);

        [DllImport("imm32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ImmNotifyIME(IntPtr inputContext, uint action, uint index, uint value);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint SendInput(uint count, INPUT[] inputs, int size);

        [DllImport("user32.dll")]
        internal static extern uint MapVirtualKey(uint code, uint mapType);

        [StructLayout(LayoutKind.Sequential)]
        internal struct KBDLLHOOKSTRUCT
        {
            internal uint VirtualKeyCode;
            internal uint ScanCode;
            internal uint Flags;
            internal uint Time;
            internal UIntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct INPUT
        {
            internal uint Type;
            internal INPUTUNION Data;

            internal static INPUT Keyboard(int virtualKey) => new()
            {
                Type = 1,
                Data = new INPUTUNION
                {
                    Keyboard = new KEYBDINPUT
                    {
                        VirtualKey = (ushort)virtualKey,
                        ScanCode = (ushort)MapVirtualKey((uint)virtualKey, 0),
                        ExtraInfo = InputMarker
                    }
                }
            };

            internal static INPUT Unicode(char codeUnit, bool keyUp) => new()
            {
                Type = 1,
                Data = new INPUTUNION
                {
                    Keyboard = new KEYBDINPUT
                    {
                        ScanCode = codeUnit,
                        Flags = KeyEventUnicode | (keyUp ? KeyEventKeyUp : 0),
                        ExtraInfo = InputMarker
                    }
                }
            };
        }

        [StructLayout(LayoutKind.Explicit)]
        internal struct INPUTUNION
        {
            [FieldOffset(0)]
            internal KEYBDINPUT Keyboard;

            [FieldOffset(0)]
            internal MOUSEINPUT Mouse;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MOUSEINPUT
        {
            internal int X;
            internal int Y;
            internal uint MouseData;
            internal uint Flags;
            internal uint Time;
            internal UIntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct KEYBDINPUT
        {
            internal ushort VirtualKey;
            internal ushort ScanCode;
            internal uint Flags;
            internal uint Time;
            internal UIntPtr ExtraInfo;
        }
    }
}
