#include "HangulRecomposer.h"
#include "MinHook.h"
#include "TranslationBridge.h"

#include <Windows.h>
#include <TlHelp32.h>

#include <cstdlib>
#include <algorithm>
#include <cstdint>
#include <cstring>
#include <string>
#include <unordered_map>
#include <deque>
#include <cstddef>

namespace
{
    using TextOutAProc = BOOL(WINAPI*)(HDC, int, int, LPCSTR, int);
    using TextOutWProc = BOOL(WINAPI*)(HDC, int, int, LPCWSTR, int);
    using GetTextExtentPoint32AProc = BOOL(WINAPI*)(HDC, LPCSTR, int, LPSIZE);
    using GetTextExtentPoint32WProc = BOOL(WINAPI*)(HDC, LPCWSTR, int, LPSIZE);

    TextOutAProc OriginalTextOutA = nullptr;
    TextOutWProc OriginalTextOutW = nullptr;
    TextOutWProc SystemTextOutW = nullptr;
    GetTextExtentPoint32AProc OriginalGetTextExtentPoint32A = nullptr;
    GetTextExtentPoint32WProc OriginalGetTextExtentPoint32W = nullptr;
    GetTextExtentPoint32WProc SystemGetTextExtentPoint32W = nullptr;
    void* OriginalLegacyToWide = nullptr;
    void* OriginalWideToLegacy = nullptr;
    void* OriginalChatAppend = nullptr;
    void* OriginalChatDraw = nullptr;
    void* ChatLengthContinuation = nullptr;
    void* ChatResetRows = nullptr;
    void* OriginalChatLength = nullptr;
    volatile LONG ChatLayoutHooksInstalled = 0;
    thread_local int ChatLayoutDepth = 0;
    thread_local bool RebuildingChatRows = false;
    thread_local std::unordered_map<void*, unsigned long> ChatLayoutRevisions;
    volatile LONG LoggedChatRebuildCount = 0;
    volatile LONG LoggedChatSourceCount = 0;
    struct NativeChatSource
    {
        unsigned int allocator = 0;
        union Storage
        {
            char inlineText[16];
            const char* pointer;
            Storage() : inlineText{} {}
        } storage;
        unsigned int length = 0;
        unsigned int capacity = 15;
        const char* Text() const { return capacity >= 16 ? storage.pointer : storage.inlineText; }
    };
    static_assert(offsetof(NativeChatSource, length) == 0x14);
    static_assert(offsetof(NativeChatSource, capacity) == 0x18);
    struct ChatSourceFrame
    {
        std::string encoded;
        NativeChatSource record;
    };
    thread_local std::deque<ChatSourceFrame> ChatSourceFrames;
    volatile LONG CachedModeEnabled = 0;
    volatile LONG LastModeCheckTick = 0;
    volatile LONG LoggedConversionCount = 0;
    volatile LONG LoggedMultilineCount = 0;
    HANDLE HookReadyEvent = nullptr;
    constexpr wchar_t CompleteFontFace[] = L"DAoC KDAOC Complete Hangul";
    constexpr int FontCacheCapacity = 16;
    struct FontCacheEntry
    {
        LOGFONTW description = {};
        HFONT font = nullptr;
    };
    FontCacheEntry FontCache[FontCacheCapacity] = {};
    int FontCacheCount = 0;
    SRWLOCK FontCacheLock = SRWLOCK_INIT;
    constexpr unsigned char LegacyToWidePattern[] =
    {
        0x80, 0x3D, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x57, 0x8B, 0xF8, 0x57,
    };

    bool IsPrecomposedModeEnabled();

    bool MatchesLegacyToWidePattern(const unsigned char* candidate)
    {
        for (size_t index = 0; index < sizeof(LegacyToWidePattern); ++index)
        {
            // The absolute address used by CMP changes between client builds.
            if (index >= 2 && index <= 5)
                continue;
            if (candidate[index] != LegacyToWidePattern[index])
                return false;
        }
        return true;
    }

    unsigned char* FindUniqueLegacyToWideInRange(
        unsigned char* start,
        size_t length,
        size_t* matchCount)
    {
        size_t count = 0;
        unsigned char* match = nullptr;
        if (length >= sizeof(LegacyToWidePattern))
        {
            const size_t finalOffset = length - sizeof(LegacyToWidePattern);
            for (size_t offset = 0; offset <= finalOffset; ++offset)
            {
                if (!MatchesLegacyToWidePattern(start + offset))
                    continue;
                match = start + offset;
                ++count;
            }
        }

        if (matchCount != nullptr)
            *matchCount = count;
        return count == 1 ? match : nullptr;
    }

    unsigned char* FindLegacyToWide(HMODULE game, size_t* matchCount)
    {
        if (matchCount != nullptr)
            *matchCount = 0;
        if (game == nullptr)
            return nullptr;

        auto* imageBase = reinterpret_cast<unsigned char*>(game);
        const auto* dosHeader = reinterpret_cast<const IMAGE_DOS_HEADER*>(imageBase);
        if (dosHeader->e_magic != IMAGE_DOS_SIGNATURE || dosHeader->e_lfanew <= 0)
            return nullptr;

        const auto* ntHeaders = reinterpret_cast<const IMAGE_NT_HEADERS*>(
            imageBase + dosHeader->e_lfanew);
        if (ntHeaders->Signature != IMAGE_NT_SIGNATURE)
            return nullptr;

        const size_t imageSize = ntHeaders->OptionalHeader.SizeOfImage;
        const IMAGE_SECTION_HEADER* section = IMAGE_FIRST_SECTION(ntHeaders);
        unsigned char* match = nullptr;
        size_t totalMatches = 0;
        for (WORD index = 0; index < ntHeaders->FileHeader.NumberOfSections; ++index, ++section)
        {
            if ((section->Characteristics & IMAGE_SCN_MEM_EXECUTE) == 0 ||
                section->VirtualAddress >= imageSize)
            {
                continue;
            }

            size_t sectionSize = section->Misc.VirtualSize;
            const size_t available = imageSize - section->VirtualAddress;
            if (sectionSize > available)
                sectionSize = available;

            size_t sectionMatches = 0;
            unsigned char* sectionMatch = FindUniqueLegacyToWideInRange(
                imageBase + section->VirtualAddress,
                sectionSize,
                &sectionMatches);
            if (sectionMatches == 1 && totalMatches == 0)
                match = sectionMatch;
            totalMatches += sectionMatches;
            if (totalMatches > 1)
                match = nullptr;
        }

        if (matchCount != nullptr)
            *matchCount = totalMatches;
        return totalMatches == 1 ? match : nullptr;
    }

    bool RunClientSignatureLocatorSelfTest()
    {
        unsigned char oneMatch[] =
        {
            0x90,
            0x80, 0x3D, 0x78, 0x20, 0xD9, 0x00, 0x00, 0x57, 0x8B, 0xF8, 0x57,
            0x90,
        };
        size_t matches = 0;
        if (FindUniqueLegacyToWideInRange(oneMatch, sizeof(oneMatch), &matches) != oneMatch + 1 ||
            matches != 1)
        {
            return false;
        }

        unsigned char twoMatches[] =
        {
            0x80, 0x3D, 0x78, 0x20, 0xD9, 0x00, 0x00, 0x57, 0x8B, 0xF8, 0x57,
            0x90,
            0x80, 0x3D, 0x78, 0x23, 0xD9, 0x00, 0x00, 0x57, 0x8B, 0xF8, 0x57,
        };
        return FindUniqueLegacyToWideInRange(
            twoMatches,
            sizeof(twoMatches),
            &matches) == nullptr && matches == 2;
    }

    void AppendDiagnostic(const std::wstring& message)
    {
        wchar_t localAppData[MAX_PATH] = {};
        if (::GetEnvironmentVariableW(L"LOCALAPPDATA", localAppData, MAX_PATH) == 0)
            return;
        std::wstring directory(localAppData);
        directory += L"\\KoreanInputFontTool";
        ::CreateDirectoryW(directory.c_str(), nullptr);

        wchar_t fileName[80] = {};
        swprintf_s(fileName, L"\\render-hook-%lu.log", ::GetCurrentProcessId());
        const std::wstring path = directory + fileName;
        HANDLE file = ::CreateFileW(
            path.c_str(),
            FILE_APPEND_DATA,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            nullptr,
            OPEN_ALWAYS,
            FILE_ATTRIBUTE_NORMAL,
            nullptr);
        if (file == INVALID_HANDLE_VALUE)
            return;

        SYSTEMTIME time = {};
        ::GetLocalTime(&time);
        wchar_t prefix[48] = {};
        swprintf_s(
            prefix,
            L"[%02u:%02u:%02u.%03u] ",
            time.wHour,
            time.wMinute,
            time.wSecond,
            time.wMilliseconds);
        const std::wstring line = std::wstring(prefix) + message + L"\r\n";
        const int utf8Length = ::WideCharToMultiByte(
            CP_UTF8,
            0,
            line.data(),
            static_cast<int>(line.size()),
            nullptr,
            0,
            nullptr,
            nullptr);
        std::string utf8(static_cast<size_t>(utf8Length), '\0');
        if (utf8Length > 0)
        {
            ::WideCharToMultiByte(
                CP_UTF8,
                0,
                line.data(),
                static_cast<int>(line.size()),
                &utf8[0],
                utf8Length,
                nullptr,
                nullptr);
            DWORD written = 0;
            ::WriteFile(file, utf8.data(), static_cast<DWORD>(utf8.size()), &written, nullptr);
        }
        ::CloseHandle(file);
    }

    void LogConversion(const char* text, int length, const std::wstring& recomposed)
    {
        const LONG sequence = ::InterlockedIncrement(&LoggedConversionCount);
        if (sequence > 64)
            return;

        std::wstring line = L"convert bytes=";
        const int byteLimit = length < 48 ? length : 48;
        for (int index = 0; index < byteLimit; ++index)
        {
            wchar_t value[8] = {};
            swprintf_s(value, L"%02X ", static_cast<unsigned char>(text[index]));
            line += value;
        }
        line += L"=> ";
        const size_t characterLimit = recomposed.size() < 48 ? recomposed.size() : 48;
        for (size_t index = 0; index < characterLimit; ++index)
        {
            wchar_t value[12] = {};
            swprintf_s(value, L"U+%04X ", static_cast<unsigned int>(recomposed[index]));
            line += value;
        }
        AppendDiagnostic(line);
    }

    void LogWideConversion(const wchar_t* text, int length, const std::wstring& recomposed)
    {
        const LONG sequence = ::InterlockedIncrement(&LoggedConversionCount);
        if (sequence > 64)
            return;

        std::wstring line = L"convert wide=";
        const int inputLimit = length < 48 ? length : 48;
        for (int index = 0; index < inputLimit; ++index)
        {
            wchar_t value[12] = {};
            swprintf_s(value, L"U+%04X ", static_cast<unsigned int>(text[index]));
            line += value;
        }
        line += L"=> ";
        const size_t outputLimit = recomposed.size() < 48 ? recomposed.size() : 48;
        for (size_t index = 0; index < outputLimit; ++index)
        {
            wchar_t value[12] = {};
            swprintf_s(value, L"U+%04X ", static_cast<unsigned int>(recomposed[index]));
            line += value;
        }
        AppendDiagnostic(line);
    }

    int __cdecl TryRecomposeLegacyToWide(
        const char* source,
        int sourceLength,
        wchar_t* destination,
        int destinationCapacity)
    {
        if (!IsPrecomposedModeEnabled() || source == nullptr ||
            destination == nullptr || sourceLength <= 0 ||
            destinationCapacity <= 1 || sourceLength > 0x10000)
        {
            return -1;
        }

        std::wstring recomposed;
        const bool hangulChanged = KoreanRenderHook::RecomposeLegacyText(
            source,
            sourceLength,
            recomposed);
        // The shared chat source boundary has already applied translation.
        // Glyph and Unicode conversion paths only recompose Hangul.
        if (!hangulChanged)
        {
            return -1;
        }
        if (recomposed.size() >= static_cast<size_t>(destinationCapacity))
            return -1;

        std::memcpy(
            destination,
            recomposed.data(),
            recomposed.size() * sizeof(wchar_t));
        destination[recomposed.size()] = L'\0';
        destination[destinationCapacity - 1] = L'\0';
        LogConversion(source, sourceLength, recomposed);
        return static_cast<int>(recomposed.size());
    }

    void __cdecl BeginChatLayout()
    {
        ++ChatLayoutDepth;
    }

    const NativeChatSource* __cdecl BeginChatSource(const NativeChatSource* source)
    {
        BeginChatLayout();
        ChatSourceFrames.emplace_back();
        if (!ChatLayoutHooksInstalled || !IsPrecomposedModeEnabled() || source == nullptr ||
            source->length == 0 || source->length > 4096 || source->Text() == nullptr)
            return source;
        std::wstring text;
        KoreanRenderHook::RecomposeLegacyText(source->Text(), static_cast<int>(source->length), text);
        const bool translated = KoreanRenderHook::TryAppendChatTranslation(text);
        if (::InterlockedIncrement(&LoggedChatSourceCount) <= 32)
        {
            wchar_t message[128] = {};
            swprintf_s(message, L"chat source chars=%u translated=%u (shared ANSI/Unicode boundary)",
                source->length, translated ? 1U : 0U);
            AppendDiagnostic(message);
        }
        if (!translated)
            return source;
        auto& frame = ChatSourceFrames.back();
        frame.encoded = KoreanRenderHook::EncodeLegacyChatText(text.data(), static_cast<int>(text.size()));
        // Both branches use 1024-byte temporary row buffers. Do not expand an
        // oversized source past the client's known layout buffer limits.
        if (frame.encoded.size() >= 1024)
            return source;
        frame.record.allocator = source->allocator;
        frame.record.storage.pointer = frame.encoded.c_str();
        frame.record.length = static_cast<unsigned int>(frame.encoded.size());
        frame.record.capacity = (std::max)(16U, frame.record.length);
        return &frame.record;
    }

    void __cdecl EndChatLayout()
    {
        if (ChatSourceFrames.size() == static_cast<size_t>(ChatLayoutDepth))
            ChatSourceFrames.pop_back();
        --ChatLayoutDepth;
    }

    int __cdecl TryEncodeChatRow(const wchar_t* source, int length, char* destination, int capacity)
    {
        if (ChatLayoutDepth <= 0 || !IsPrecomposedModeEnabled() || source == nullptr ||
            destination == nullptr || length < 0 || length > 1024 || capacity <= 0)
            return -1;
        const std::string encoded = KoreanRenderHook::EncodeLegacyChatText(source, length);
        if (encoded.size() >= static_cast<size_t>(capacity))
            return -1;
        std::memcpy(destination, encoded.data(), encoded.size());
        destination[encoded.size()] = '\0';
        return static_cast<int>(encoded.size());
    }

    __declspec(naked) void HookWideToLegacy()
    {
        __asm
        {
            pushfd
            pushad
            mov eax, dword ptr[esp + 28]
            mov edx, dword ptr[esp + 20]
            mov ecx, dword ptr[esp + 24]
            mov ebx, dword ptr[esp + 40]
            push eax
            push edx
            push ecx
            push ebx
            call TryEncodeChatRow
            add esp, 16
            cmp eax, -1
            je passthrough
            mov dword ptr[esp + 28], eax
            popad
            popfd
            ret
        passthrough:
            popad
            popfd
            jmp dword ptr[OriginalWideToLegacy]
        }
    }

    __declspec(naked) void HookChatAppend()
    {
        __asm
        {
            push ebp
            mov ebp, esp
            sub esp, 4
            pushfd
            pushad
            push dword ptr[ebp + 8]
            call BeginChatSource
            add esp, 4
            mov dword ptr[esp + 28], eax
            popad
            popfd
            mov dword ptr[ebp - 4], eax
            push dword ptr[ebp + 20]
            push dword ptr[ebp + 16]
            push dword ptr[ebp + 12]
            push dword ptr[ebp - 4]
            call dword ptr[OriginalChatAppend]
            pushfd
            pushad
            call EndChatLayout
            popad
            popfd
            mov esp, ebp
            pop ebp
            ret 16
        }
    }

    int __cdecl ChatConvertedLength(const wchar_t* text, const void* record)
    {
        if (IsPrecomposedModeEnabled())
            return static_cast<int>(wcsnlen_s(text, 1024));
        return *reinterpret_cast<const int*>(static_cast<const unsigned char*>(record) + 0x14);
    }

    __declspec(naked) void HookChatLength()
    {
        __asm
        {
            pushfd
            pushad
            push edi
            push esi
            call ChatConvertedLength
            add esp, 8
            mov dword ptr[esp], eax
            popad
            popfd
            mov eax, dword ptr[ebx + 0x6A4]
            jmp dword ptr[ChatLengthContinuation]
        }
    }

    void __cdecl RefreshChatRows(void* chat)
    {
        if (!ChatLayoutHooksInstalled || RebuildingChatRows || chat == nullptr)
            return;
        const unsigned long revision = KoreanRenderHook::ChatTranslationLayoutRevision();
        auto found = ChatLayoutRevisions.find(chat);
        if (found != ChatLayoutRevisions.end() && found->second == revision)
            return;
        if (ChatLayoutRevisions.size() >= 16 && found == ChatLayoutRevisions.end())
            ChatLayoutRevisions.clear();
        ChatLayoutRevisions[chat] = revision;
        // The client's clear/rebuild recalculates scrolling using stale eligible-row
        // totals. Preserve the newest-row offset across that asynchronous reflow.
        auto* scrollOffset = reinterpret_cast<int*>(static_cast<unsigned char*>(chat) + 0x7B0);
        const int previousScrollOffset = *scrollOffset;
        RebuildingChatRows = true;
        // Run the client's existing width-change rebuild on its own UI thread.
        __asm
        {
            pushfd
            pushad
            mov esi, chat
            call dword ptr[ChatResetRows]
            popad
            popfd
        }
        *scrollOffset = previousScrollOffset > 0 ? previousScrollOffset : 0;
        RebuildingChatRows = false;
        if (::InterlockedIncrement(&LoggedChatRebuildCount) <= 32)
            AppendDiagnostic(L"chat layout rebuilt after translation revision");
    }

    __declspec(naked) void HookChatDraw()
    {
        __asm
        {
            pushfd
            pushad
            mov eax, dword ptr[esp + 40]
            push eax
            call RefreshChatRows
            add esp, 4
            popad
            popfd
            jmp dword ptr[OriginalChatDraw]
        }
    }

    // The client uses a register-based helper for nearly every ANSI -> UTF-16
    // UI conversion: EAX=capacity, ESI=destination, EDX=source length and the
    // source pointer is the only stack argument.  Hooking this boundary keeps
    // packet/input data DAoC-compatible while giving the DirectX glyph
    // renderer complete Hangul syllables.
    __declspec(naked) void HookLegacyToWide()
    {
        __asm
        {
            pushfd
            pushad
            mov eax, dword ptr[esp + 28]
            mov edx, dword ptr[esp + 4]
            mov ecx, dword ptr[esp + 20]
            mov ebx, dword ptr[esp + 40]
            push eax
            push edx
            push ecx
            push ebx
            call TryRecomposeLegacyToWide
            add esp, 16
            cmp eax, -1
            je passthrough
            mov dword ptr[esp + 28], eax
            popad
            popfd
            ret

        passthrough:
            popad
            popfd
            jmp dword ptr[OriginalLegacyToWide]
        }
    }

    bool RegisterCompleteFont()
    {
        wchar_t executablePath[MAX_PATH] = {};
        if (::GetModuleFileNameW(nullptr, executablePath, MAX_PATH) == 0)
            return false;
        wchar_t* separator = wcsrchr(executablePath, L'\\');
        if (separator == nullptr)
            return false;
        *separator = L'\0';
        std::wstring fontPath(executablePath);
        fontPath += L"\\ui\\fonts\\korean-font-complete-v18_4.ttf";
        return ::AddFontResourceExW(fontPath.c_str(), FR_PRIVATE, nullptr) > 0;
    }

    HFONT GetCompleteFont(HDC dc)
    {
        const HGDIOBJ currentFont = ::GetCurrentObject(dc, OBJ_FONT);
        LOGFONTW requested = {};
        if (currentFont == nullptr ||
            ::GetObjectW(currentFont, sizeof(requested), &requested) != sizeof(requested))
        {
            return nullptr;
        }

        requested.lfCharSet = DEFAULT_CHARSET;
        requested.lfOutPrecision = OUT_TT_ONLY_PRECIS;
        wcscpy_s(requested.lfFaceName, CompleteFontFace);

        ::AcquireSRWLockExclusive(&FontCacheLock);
        for (int index = 0; index < FontCacheCount; ++index)
        {
            if (std::memcmp(&FontCache[index].description, &requested, sizeof(requested)) == 0)
            {
                const HFONT cached = FontCache[index].font;
                ::ReleaseSRWLockExclusive(&FontCacheLock);
                return cached;
            }
        }

        HFONT created = ::CreateFontIndirectW(&requested);
        if (created != nullptr && FontCacheCount < FontCacheCapacity)
        {
            FontCache[FontCacheCount].description = requested;
            FontCache[FontCacheCount].font = created;
            ++FontCacheCount;
        }
        else if (created != nullptr)
        {
            ::DeleteObject(created);
            created = nullptr;
        }
        ::ReleaseSRWLockExclusive(&FontCacheLock);
        return created;
    }

    class ScopedCompleteFont
    {
    public:
        explicit ScopedCompleteFont(HDC dc) : dc_(dc)
        {
            const HFONT font = GetCompleteFont(dc);
            if (font != nullptr)
                previous_ = ::SelectObject(dc, font);
        }

        ~ScopedCompleteFont()
        {
            if (previous_ != nullptr && previous_ != HGDI_ERROR)
                ::SelectObject(dc_, previous_);
        }

        ScopedCompleteFont(const ScopedCompleteFont&) = delete;
        ScopedCompleteFont& operator=(const ScopedCompleteFont&) = delete;

    private:
        HDC dc_ = nullptr;
        HGDIOBJ previous_ = nullptr;
    };

    HMODULE FindClientModule()
    {
        HMODULE module = ::GetModuleHandleW(L"eden.dll");
        if (module == nullptr)
            module = ::GetModuleHandleW(L"eden_2gb.dll");
        if (module == nullptr)
            module = ::GetModuleHandleW(L"game.dll");
        if (module == nullptr)
            module = ::GetModuleHandleW(L"game_2gb.dll");
        if (module != nullptr)
            return module;

        HMODULE mainModule = ::GetModuleHandleW(nullptr);
        wchar_t mainPath[MAX_PATH] = {};
        if (mainModule == nullptr ||
            ::GetModuleFileNameW(mainModule, mainPath, MAX_PATH) == 0)
        {
            return nullptr;
        }
        const wchar_t* baseName = wcsrchr(mainPath, L'\\');
        baseName = baseName == nullptr ? mainPath : baseName + 1;
        return _wcsicmp(baseName, L"eden.dll") == 0 ||
            _wcsicmp(baseName, L"eden_2gb.dll") == 0 ||
            _wcsicmp(baseName, L"game.dll") == 0 ||
            _wcsicmp(baseName, L"game_2gb.dll") == 0
            ? mainModule
            : nullptr;
    }

    bool IsPrecomposedModeEnabled()
    {
        const DWORD now = ::GetTickCount();
        const DWORD previous = static_cast<DWORD>(LastModeCheckTick);
        if (now - previous < 1000)
            return CachedModeEnabled != 0;

        ::InterlockedExchange(&LastModeCheckTick, static_cast<LONG>(now));
        wchar_t value[64] = {};
        DWORD valueSize = sizeof(value);
        const LSTATUS status = ::RegGetValueW(
            HKEY_CURRENT_USER,
            L"Software\\KoreanInputFontTool",
            L"HangulDisplayMode",
            RRF_RT_REG_SZ,
            nullptr,
            value,
            &valueSize);
        const bool enabled = status == ERROR_SUCCESS &&
            _wcsicmp(value, L"PrecomposedHangul") == 0;
        ::InterlockedExchange(&CachedModeEnabled, enabled ? 1L : 0L);
        return enabled;
    }

    bool HasLineBreak(const std::wstring& text)
    {
        return text.find_first_of(L"\r\n") != std::wstring::npos;
    }

    template<typename Callback>
    bool ForEachTextLine(const std::wstring& text, Callback callback)
    {
        size_t start = 0;
        size_t lineIndex = 0;
        for (;;)
        {
            const size_t end = text.find_first_of(L"\r\n", start);
            const size_t length = end == std::wstring::npos ? text.size() - start : end - start;
            if (!callback(text.data() + start, static_cast<int>(length), lineIndex++))
                return false;
            if (end == std::wstring::npos)
                return true;
            start = end + 1;
            if (text[end] == L'\r' && start < text.size() && text[start] == L'\n')
                ++start;
        }
    }

    void LogMultilineLayout(const wchar_t* operation, const SIZE& size, size_t lines)
    {
        if (::InterlockedIncrement(&LoggedMultilineCount) > 32)
            return;
        wchar_t message[128] = {};
        swprintf_s(message, L"multiline %s lines=%u width=%ld height=%ld",
            operation, static_cast<unsigned int>(lines), size.cx, size.cy);
        AppendDiagnostic(message);
    }

    BOOL MeasureMultilineText(HDC dc, const std::wstring& text, LPSIZE size)
    {
        if (!HasLineBreak(text))
            return SystemGetTextExtentPoint32W(dc, text.data(), static_cast<int>(text.size()), size);
        if (size == nullptr)
            return FALSE;
        TEXTMETRICW metrics = {};
        if (!::GetTextMetricsW(dc, &metrics))
            return FALSE;
        const LONG lineHeight = metrics.tmHeight + metrics.tmExternalLeading;
        SIZE result = {};
        size_t lines = 0;
        if (!ForEachTextLine(text, [&](const wchar_t* line, int length, size_t)
        {
            SIZE lineSize = {};
            if (!SystemGetTextExtentPoint32W(dc, line, length, &lineSize))
                return false;
            if (lineSize.cx > result.cx)
                result.cx = lineSize.cx;
            ++lines;
            return true;
        }))
            return FALSE;
        result.cy = metrics.tmHeight + static_cast<LONG>(lines - 1) * lineHeight;
        *size = result;
        LogMultilineLayout(L"measure", result, lines);
        return TRUE;
    }

    BOOL DrawMultilineText(HDC dc, int x, int y, const std::wstring& text)
    {
        if (!HasLineBreak(text))
            return SystemTextOutW(dc, x, y, text.data(), static_cast<int>(text.size()));
        TEXTMETRICW metrics = {};
        if (!::GetTextMetricsW(dc, &metrics))
            return FALSE;
        const UINT alignment = ::GetTextAlign(dc);
        POINT position = {};
        const bool updatePosition = (alignment & TA_UPDATECP) != 0;
        if (alignment == GDI_ERROR || (updatePosition && !::GetCurrentPositionEx(dc, &position)))
            return FALSE;
        if (updatePosition)
        {
            x = position.x;
            y = position.y;
            if (::SetTextAlign(dc, alignment & ~TA_UPDATECP) == GDI_ERROR)
                return FALSE;
        }
        const LONG lineHeight = metrics.tmHeight + metrics.tmExternalLeading;
        size_t lines = 0;
        const bool drawn = ForEachTextLine(text, [&](const wchar_t* line, int length, size_t index)
        {
            ++lines;
            return SystemTextOutW(dc, x, y + static_cast<int>(index) * lineHeight, line, length) != FALSE;
        });
        if (updatePosition)
        {
            ::SetTextAlign(dc, alignment);
            ::MoveToEx(dc, position.x, position.y, nullptr);
        }
        const SIZE result = { 0, metrics.tmHeight + static_cast<LONG>(lines - 1) * lineHeight };
        LogMultilineLayout(L"draw", result, lines);
        return drawn ? TRUE : FALSE;
    }

    bool RunMultilineLayoutSelfTest()
    {
        SystemGetTextExtentPoint32W = &::GetTextExtentPoint32W;
        SystemTextOutW = &::TextOutW;
        HDC dc = ::CreateCompatibleDC(nullptr);
        if (dc == nullptr)
            return false;
        TEXTMETRICW metrics = {};
        SIZE longest = {};
        bool passed = ::GetTextMetricsW(dc, &metrics) != FALSE &&
            ::GetTextExtentPoint32W(dc, L"Longest line", 12, &longest) != FALSE;
        const std::wstring cases[] =
        {
            L"Longest line\r\nx", L"x\nLongest line", L"Longest line\rx",
        };
        for (const auto& text : cases)
        {
            SIZE measured = {};
            passed = passed && MeasureMultilineText(dc, text, &measured) &&
                measured.cx == longest.cx &&
                measured.cy == metrics.tmHeight * 2 + metrics.tmExternalLeading;
        }
        SIZE emptyLines = {};
        passed = passed && MeasureMultilineText(dc, L"\r\n\r\n", &emptyLines) &&
            emptyLines.cx == 0 &&
            emptyLines.cy == metrics.tmHeight * 3 + metrics.tmExternalLeading * 2;
        HBITMAP bitmap = ::CreateBitmap(256, 128, 1, 1, nullptr);
        if (bitmap == nullptr)
            passed = false;
        else
        {
            HGDIOBJ previous = ::SelectObject(dc, bitmap);
            passed = passed && DrawMultilineText(dc, 0, 0, L"Original\r\nTranslation");
            ::SelectObject(dc, previous);
            ::DeleteObject(bitmap);
        }
        ::DeleteDC(dc);
        return passed;
    }

    int WINAPI TestChatAppendTarget(int first, int second, int third, int fourth)
    {
        return ChatLayoutDepth == 1 ? first + second + third + fourth : -1;
    }

    __declspec(naked) void TestChatLengthContinuation()
    {
        __asm { ret }
    }

    __declspec(naked) void TestChatResetTarget()
    {
        __asm
        {
            inc dword ptr[esi]
            mov dword ptr[esi + 0x7B0], 7
            ret
        }
    }

    int WINAPI TestChatDrawTarget(int* counter)
    {
        return *counter + 5;
    }

    bool RunChatHookAdapterSelfTest()
    {
        OriginalChatAppend = reinterpret_cast<void*>(&TestChatAppendTarget);
        int appendResult = 0;
        __asm
        {
            pushad
            push 4
            push 3
            push 2
            push 1
            call HookChatAppend
            mov appendResult, eax
            popad
        }
        OriginalChatAppend = nullptr;
        if (appendResult != 10 || ChatLayoutDepth != 0)
            return false;

        const std::wstring sourceText = L"Hello\n북문";
        const wchar_t* source = sourceText.data();
        const int sourceCharacters = static_cast<int>(sourceText.size());
        char buffer[512] = {};
        int encodedLength = 0;
        const LONG previousMode = CachedModeEnabled;
        const LONG previousTick = LastModeCheckTick;
        CachedModeEnabled = 1;
        LastModeCheckTick = static_cast<LONG>(::GetTickCount());
        BeginChatLayout();
        __asm
        {
            pushad
            mov eax, 512
            lea edx, buffer
            mov ecx, sourceCharacters
            push source
            call HookWideToLegacy
            add esp, 4
            mov encodedLength, eax
            popad
        }
        EndChatLayout();
        std::wstring decoded;
        KoreanRenderHook::RecomposeLegacyText(buffer, encodedLength, decoded);

        unsigned char record[0x18] = {};
        unsigned char chat[0x6A8] = {};
        record[0x14] = 1; // Original length deliberately differs from UTF-16.
        ChatLengthContinuation = reinterpret_cast<void*>(&TestChatLengthContinuation);
        int counted = 0;
        __asm
        {
            pushad
            lea ebx, chat
            lea edi, record
            mov esi, source
            call HookChatLength
            mov counted, edi
            popad
        }
        ChatLengthContinuation = nullptr;
        CachedModeEnabled = previousMode;
        LastModeCheckTick = previousTick;
        if (decoded != sourceText || counted != sourceCharacters || ChatLayoutDepth != 0)
            return false;
        int drawChat[0x7B4 / sizeof(int)] = {};
        int& resetCount = drawChat[0];
        int drawResult = 0;
        ChatResetRows = reinterpret_cast<void*>(&TestChatResetTarget);
        OriginalChatDraw = reinterpret_cast<void*>(&TestChatDrawTarget);
        ::InterlockedExchange(&ChatLayoutHooksInstalled, 1);
        __asm
        {
            pushad
            lea eax, drawChat
            push eax
            call HookChatDraw
            mov drawResult, eax
            lea eax, drawChat
            push eax
            call HookChatDraw
            popad
        }
        const bool bottomPreserved = drawChat[0x7B0 / sizeof(int)] == 0;
        ChatLayoutRevisions.clear();
        drawChat[0x7B0 / sizeof(int)] = 3;
        RefreshChatRows(drawChat);
        const bool historyPreserved = drawChat[0x7B0 / sizeof(int)] == 3;
        ::InterlockedExchange(&ChatLayoutHooksInstalled, 0);
        ChatResetRows = nullptr;
        OriginalChatDraw = nullptr;
        ChatLayoutRevisions.clear();
        return resetCount == 2 && drawResult == 6 && bottomPreserved && historyPreserved && !RebuildingChatRows;
    }

    bool CheckChatSourceBoundary(const std::wstring& original, const std::wstring& expected)
    {
        const std::string encoded = KoreanRenderHook::EncodeLegacyChatText(original.data(), static_cast<int>(original.size()));
        NativeChatSource source;
        source.length = static_cast<unsigned int>(encoded.size());
        if (encoded.size() < 16)
            std::memcpy(source.storage.inlineText, encoded.c_str(), encoded.size() + 1);
        else
        {
            source.storage.pointer = encoded.c_str();
            source.capacity = source.length;
        }
        const auto* transformed = BeginChatSource(&source);
        std::wstring decoded;
        KoreanRenderHook::RecomposeLegacyText(transformed->Text(), static_cast<int>(transformed->length), decoded);
        const bool passed = decoded == expected && std::string(source.Text(), source.length) == encoded;
        EndChatLayout();
        return passed && ChatLayoutDepth == 0 && ChatSourceFrames.empty();
    }

    bool RunSharedChatSourceSelfTest()
    {
        const LONG previousMode = CachedModeEnabled;
        const LONG previousTick = LastModeCheckTick;
        CachedModeEnabled = 1;
        LastModeCheckTick = static_cast<LONG>(::GetTickCount());
        ::InterlockedExchange(&ChatLayoutHooksInstalled, 1);
        const bool passed = KoreanRenderHook::RunChatSourceBoundarySelfTest(&CheckChatSourceBoundary);
        ::InterlockedExchange(&ChatLayoutHooksInstalled, 0);
        CachedModeEnabled = previousMode;
        LastModeCheckTick = previousTick;
        return passed;
    }

    bool RunChatRowEncodingSelfTest()
    {
        for (unsigned int syllable = 0xAC00; syllable <= 0xD7A3; ++syllable)
        {
            const wchar_t value = static_cast<wchar_t>(syllable);
            const std::string encoded = KoreanRenderHook::EncodeLegacyChatText(&value, 1);
            std::wstring decoded;
            if (!KoreanRenderHook::RecomposeLegacyText(encoded.data(), static_cast<int>(encoded.size()), decoded) ||
                decoded != std::wstring(1, value))
                return false;
        }
        const std::wstring source = L"[Alliance] Player: Hello\n[번역] : 북문에서 만나기";
        const std::string encoded = KoreanRenderHook::EncodeLegacyChatText(source.data(), static_cast<int>(source.size()));
        std::wstring decoded;
        KoreanRenderHook::RecomposeLegacyText(encoded.data(), static_cast<int>(encoded.size()), decoded);
        return decoded == source;
    }

    BOOL WINAPI HookTextOutA(HDC dc, int x, int y, LPCSTR text, int length)
    {
        if (!IsPrecomposedModeEnabled() || SystemTextOutW == nullptr)
            return OriginalTextOutA(dc, x, y, text, length);

        std::wstring recomposed;
        const bool hangulChanged = KoreanRenderHook::RecomposeLegacyText(text, length, recomposed);
        const bool translationChanged = false;
        if (!hangulChanged && !translationChanged && !HasLineBreak(recomposed))
            return OriginalTextOutA(dc, x, y, text, length);
        LogConversion(text, length, recomposed);
        const ScopedCompleteFont font(dc);
        return DrawMultilineText(dc, x, y, recomposed);
    }

    BOOL WINAPI HookTextOutW(HDC dc, int x, int y, LPCWSTR text, int length)
    {
        if (!IsPrecomposedModeEnabled())
            return OriginalTextOutW(dc, x, y, text, length);

        std::wstring recomposed;
        const bool hangulChanged = KoreanRenderHook::RecomposeLegacyWideText(text, length, recomposed);
        const bool translationChanged = false;
        if (!hangulChanged && !translationChanged && !HasLineBreak(recomposed))
            return OriginalTextOutW(dc, x, y, text, length);
        LogWideConversion(text, length, recomposed);
        const ScopedCompleteFont font(dc);
        return DrawMultilineText(dc, x, y, recomposed);
    }

    BOOL WINAPI HookGetTextExtentPoint32A(HDC dc, LPCSTR text, int length, LPSIZE size)
    {
        if (!IsPrecomposedModeEnabled() || SystemGetTextExtentPoint32W == nullptr)
            return OriginalGetTextExtentPoint32A(dc, text, length, size);

        std::wstring recomposed;
        const bool hangulChanged = KoreanRenderHook::RecomposeLegacyText(text, length, recomposed);
        const bool translationChanged = false;
        if (!hangulChanged && !translationChanged && !HasLineBreak(recomposed))
            return OriginalGetTextExtentPoint32A(dc, text, length, size);
        const ScopedCompleteFont font(dc);
        return MeasureMultilineText(dc, recomposed, size);
    }

    BOOL WINAPI HookGetTextExtentPoint32W(HDC dc, LPCWSTR text, int length, LPSIZE size)
    {
        if (!IsPrecomposedModeEnabled())
            return OriginalGetTextExtentPoint32W(dc, text, length, size);

        std::wstring recomposed;
        const bool hangulChanged = KoreanRenderHook::RecomposeLegacyWideText(text, length, recomposed);
        const bool translationChanged = false;
        if (!hangulChanged && !translationChanged && !HasLineBreak(recomposed))
            return OriginalGetTextExtentPoint32W(dc, text, length, size);
        const ScopedCompleteFont font(dc);
        return MeasureMultilineText(dc, recomposed, size);
    }

    void** FindImportSlot(HMODULE module, const char* importedDll, const char* functionName)
    {
        if (module == nullptr)
            return nullptr;

        auto* base = reinterpret_cast<unsigned char*>(module);
        auto* dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
        if (dos->e_magic != IMAGE_DOS_SIGNATURE)
            return nullptr;
        auto* nt = reinterpret_cast<IMAGE_NT_HEADERS*>(base + dos->e_lfanew);
        if (nt->Signature != IMAGE_NT_SIGNATURE)
            return nullptr;

        const auto& directory = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
        if (directory.VirtualAddress == 0)
            return nullptr;

        auto* descriptor = reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(
            base + directory.VirtualAddress);
        for (; descriptor->Name != 0; ++descriptor)
        {
            const auto* dllName = reinterpret_cast<const char*>(base + descriptor->Name);
            if (_stricmp(dllName, importedDll) != 0)
                continue;

            if (descriptor->OriginalFirstThunk == 0)
                return nullptr;
            auto* names = reinterpret_cast<IMAGE_THUNK_DATA*>(
                base + descriptor->OriginalFirstThunk);
            auto* functions = reinterpret_cast<IMAGE_THUNK_DATA*>(
                base + descriptor->FirstThunk);
            for (; names->u1.AddressOfData != 0; ++names, ++functions)
            {
                if (IMAGE_SNAP_BY_ORDINAL(names->u1.Ordinal))
                    continue;
                auto* import = reinterpret_cast<IMAGE_IMPORT_BY_NAME*>(
                    base + names->u1.AddressOfData);
                if (std::strcmp(reinterpret_cast<const char*>(import->Name), functionName) == 0)
                    return reinterpret_cast<void**>(&functions->u1.Function);
            }
        }
        return nullptr;
    }

    bool PatchImportSlot(void** slot, void* replacement, void** original)
    {
        DWORD oldProtection = 0;
        if (slot == nullptr || !::VirtualProtect(
            slot,
            sizeof(void*),
            PAGE_READWRITE,
            &oldProtection))
        {
            return false;
        }

        *original = ::InterlockedExchangePointer(slot, replacement);
        DWORD ignored = 0;
        ::VirtualProtect(slot, sizeof(void*), oldProtection, &ignored);
        ::FlushInstructionCache(::GetCurrentProcess(), slot, sizeof(void*));
        return *original != nullptr;
    }

    unsigned char* FindUniqueExactCode(HMODULE module, const unsigned char* pattern, size_t length)
    {
        auto* base = reinterpret_cast<unsigned char*>(module);
        const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
        const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS*>(base + dos->e_lfanew);
        const auto* section = IMAGE_FIRST_SECTION(nt);
        unsigned char* match = nullptr;
        for (WORD index = 0; index < nt->FileHeader.NumberOfSections; ++index, ++section)
        {
            if ((section->Characteristics & IMAGE_SCN_MEM_EXECUTE) == 0 ||
                section->VirtualAddress >= nt->OptionalHeader.SizeOfImage)
                continue;
            const size_t size = (std::min)(static_cast<size_t>(section->Misc.VirtualSize),
                static_cast<size_t>(nt->OptionalHeader.SizeOfImage - section->VirtualAddress));
            if (size < length)
                continue;
            auto* start = base + section->VirtualAddress;
            for (size_t offset = 0; offset <= size - length; ++offset)
            {
                if (std::memcmp(start + offset, pattern, length) != 0)
                    continue;
                if (match != nullptr)
                    return nullptr;
                match = start + offset;
            }
        }
        return match;
    }

    bool InstallChatLayoutHooks(HMODULE game, unsigned char* legacyToWide)
    {
        constexpr unsigned char countPattern[] =
        {
            0x8B, 0x83, 0xA4, 0x06, 0, 0, 0x8B, 0x7F, 0x14,
            0x83, 0x65, 0xF8, 0, 0x85, 0xC0, 0x59, 0x89, 0x7D, 0xFC,
        };
        auto* count = FindUniqueExactCode(game, countPattern, sizeof(countPattern));
        const auto base = reinterpret_cast<uintptr_t>(game);
        if (count == nullptr || reinterpret_cast<uintptr_t>(count) < base + 0x3EF)
            return false;
        // Relative relationships are accepted only with all independent code
        // signatures and the conversion CALL target validated. No fixed RVA.
        auto* append = count - 0x199;
        auto* reset = count - 0x1AE;
        auto* draw = count - 0x3EF;
        auto* inverse = legacyToWide + 0x42;
        constexpr unsigned char appendPrefix[] = { 0x55,0x8B,0xEC,0xB8,0x08,0x14,0,0 };
        constexpr unsigned char drawPrefix[] =
        {
            0x55,0x8B,0xEC,0x83,0xEC,0x48,0x83,0x65,0xF8,0,0xD9,0xEE,
            0x83,0x65,0xEC,0,0xD9,0x5D,0xB8,
        };
        constexpr unsigned char resetMiddle[] = { 0x83,0x8E,0x90,0x8E,0x01,0,0xFF,0x56,0xE8 };
        constexpr unsigned char inversePrefix[] = { 0x53,0x56,0x33,0xDB,0x38,0x1D };
        int conversionDisplacement = 0;
        std::memcpy(&conversionDisplacement, count - 4, sizeof(conversionDisplacement));
        if (std::memcmp(append, appendPrefix, sizeof(appendPrefix)) != 0 ||
            std::memcmp(draw, drawPrefix, sizeof(drawPrefix)) != 0 ||
            reset[0] != 0x8B || reset[1] != 0xCE || reset[2] != 0xE8 ||
            std::memcmp(reset + 7, resetMiddle, sizeof(resetMiddle)) != 0 ||
            std::memcmp(inverse, inversePrefix, sizeof(inversePrefix)) != 0 ||
            count[-5] != 0xE8 || count + conversionDisplacement != legacyToWide)
            return false;
        void* targets[] = { append, count, draw, inverse };
        void* detours[] =
        {
            reinterpret_cast<void*>(&HookChatAppend), reinterpret_cast<void*>(&HookChatLength),
            reinterpret_cast<void*>(&HookChatDraw), reinterpret_cast<void*>(&HookWideToLegacy),
        };
        void** originals[] = { &OriginalChatAppend, &OriginalChatLength, &OriginalChatDraw, &OriginalWideToLegacy };
        ChatLengthContinuation = count + 9;
        ChatResetRows = reset;
        for (int index = 0; index < 4; ++index)
        {
            if (::MH_CreateHook(targets[index], detours[index], originals[index]) != MH_OK)
            {
                for (int created = 0; created < index; ++created)
                    ::MH_RemoveHook(targets[created]);
                return false;
            }
        }
        // Queue all four hooks and suspend threads only once when applying.
        for (void* target : targets)
            ::MH_QueueEnableHook(target);
        if (::MH_ApplyQueued() != MH_OK)
        {
            for (void* target : targets)
            {
                ::MH_DisableHook(target);
                ::MH_RemoveHook(target);
            }
            return false;
        }
        ::InterlockedExchange(&ChatLayoutHooksInstalled, 1);
        AppendDiagnostic(L"install chat layout hooks: native LF/wrap/count/rebuild + legacy row encoding");
        return true;
    }

    bool InstallHooks()
    {
        static_assert(sizeof(void*) == 4, "KoreanRenderHook32 must be built for Win32");
        if (!KoreanRenderHook::RunRecomposerSelfTest())
            return false;

        const bool fontRegistered = RegisterCompleteFont();

        HMODULE game = FindClientModule();
        HMODULE gdi = ::GetModuleHandleW(L"gdi32.dll");
        if (game == nullptr || gdi == nullptr)
            return false;

        size_t signatureMatches = 0;
        auto* legacyToWide = FindLegacyToWide(game, &signatureMatches);
        if (legacyToWide == nullptr)
        {
            wchar_t message[128] = {};
            swprintf_s(
                message,
                L"install failed: unsupported client text conversion signature (matches=%zu)",
                signatureMatches);
            AppendDiagnostic(message);
            return false;
        }

        const MH_STATUS initializeStatus = ::MH_Initialize();
        if (initializeStatus != MH_OK && initializeStatus != MH_ERROR_ALREADY_INITIALIZED)
        {
            AppendDiagnostic(L"install failed: MinHook initialization");
            return false;
        }
        if (::MH_CreateHook(
            legacyToWide,
            reinterpret_cast<void*>(&HookLegacyToWide),
            &OriginalLegacyToWide) != MH_OK ||
            ::MH_EnableHook(legacyToWide) != MH_OK)
        {
            AppendDiagnostic(L"install failed: client text conversion hook");
            return false;
        }

        if (!InstallChatLayoutHooks(game, legacyToWide))
            AppendDiagnostic(L"chat translation disabled: native layout signatures not supported");

        SystemTextOutW = reinterpret_cast<TextOutWProc>(::GetProcAddress(gdi, "TextOutW"));
        SystemGetTextExtentPoint32W = reinterpret_cast<GetTextExtentPoint32WProc>(
            ::GetProcAddress(gdi, "GetTextExtentPoint32W"));
        void** textOutSlot = FindImportSlot(game, "GDI32.dll", "TextOutA");
        void** textOutWideSlot = FindImportSlot(game, "GDI32.dll", "TextOutW");
        void** extentSlot = FindImportSlot(game, "GDI32.dll", "GetTextExtentPoint32A");
        void** extentWideSlot = FindImportSlot(game, "GDI32.dll", "GetTextExtentPoint32W");
        if (SystemTextOutW == nullptr || SystemGetTextExtentPoint32W == nullptr ||
            textOutSlot == nullptr || textOutWideSlot == nullptr ||
            extentSlot == nullptr || extentWideSlot == nullptr)
        {
            AppendDiagnostic(L"install failed: GDI imports not found");
            return false;
        }

        if (!PatchImportSlot(
            extentSlot,
            reinterpret_cast<void*>(&HookGetTextExtentPoint32A),
            reinterpret_cast<void**>(&OriginalGetTextExtentPoint32A)))
        {
            AppendDiagnostic(L"install failed: GetTextExtentPoint32A patch");
            return false;
        }
        if (!PatchImportSlot(
            extentWideSlot,
            reinterpret_cast<void*>(&HookGetTextExtentPoint32W),
            reinterpret_cast<void**>(&OriginalGetTextExtentPoint32W)))
        {
            AppendDiagnostic(L"install failed: GetTextExtentPoint32W patch");
            return false;
        }
        if (!PatchImportSlot(
            textOutSlot,
            reinterpret_cast<void*>(&HookTextOutA),
            reinterpret_cast<void**>(&OriginalTextOutA)))
        {
            AppendDiagnostic(L"install failed: TextOutA patch");
            return false;
        }
        const bool installed = PatchImportSlot(
            textOutWideSlot,
            reinterpret_cast<void*>(&HookTextOutW),
            reinterpret_cast<void**>(&OriginalTextOutW));
        AppendDiagnostic(installed
            ? (fontRegistered
                ? L"install ok v18.44: client text conversion + native chat layout + GDI hooks, complete font registered"
                : L"install ok v18.44: client text conversion + native chat layout + GDI hooks, complete font registration failed")
            : L"install failed: TextOutW patch");
        return installed;
    }

    DWORD WINAPI HookWorker(void*)
    {
        wchar_t eventName[96] = {};
        swprintf_s(
            eventName,
            L"Local\\KoreanInputFontTool.RenderHook.v18_44.%lu",
            ::GetCurrentProcessId());
        HookReadyEvent = ::CreateEventW(nullptr, TRUE, FALSE, eventName);
        KoreanRenderHook::ResetChatTranslationBridge();
        for (int attempt = 0; attempt < 120; ++attempt)
        {
            if (FindClientModule() != nullptr)
            {
                const bool installed = InstallHooks();
                if (installed && HookReadyEvent != nullptr)
                    ::SetEvent(HookReadyEvent);
                while (installed)
                {
                    KoreanRenderHook::PollChatTranslationResponses();
                    ::Sleep(250);
                }
                break;
            }
            ::Sleep(500);
        }
        return 0;
    }

    uintptr_t FindRemoteModuleBase(DWORD processId, const wchar_t* moduleName)
    {
        HANDLE snapshot = ::CreateToolhelp32Snapshot(
            TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32,
            processId);
        if (snapshot == INVALID_HANDLE_VALUE)
            return 0;

        MODULEENTRY32W entry = {};
        entry.dwSize = sizeof(entry);
        uintptr_t result = 0;
        if (::Module32FirstW(snapshot, &entry))
        {
            do
            {
                if (_wcsicmp(entry.szModule, moduleName) == 0)
                {
                    result = reinterpret_cast<uintptr_t>(entry.modBaseAddr);
                    break;
                }
            } while (::Module32NextW(snapshot, &entry));
        }
        ::CloseHandle(snapshot);
        return result;
    }

    DWORD InjectSelf(DWORD processId)
    {
        wchar_t dllPath[MAX_PATH] = {};
        HMODULE self = nullptr;
        if (!::GetModuleHandleExW(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
            GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCWSTR>(&InjectSelf),
            &self) ||
            ::GetModuleFileNameW(self, dllPath, MAX_PATH) == 0)
        {
            return ::GetLastError();
        }
        const wchar_t* moduleName = wcsrchr(dllPath, L'\\');
        moduleName = moduleName == nullptr ? dllPath : moduleName + 1;
        if (FindRemoteModuleBase(processId, moduleName) != 0)
            return ERROR_SUCCESS;

        HANDLE process = ::OpenProcess(
            PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION |
            PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ,
            FALSE,
            processId);
        if (process == nullptr)
            return ::GetLastError();

        const SIZE_T pathBytes = (wcslen(dllPath) + 1) * sizeof(wchar_t);
        void* remotePath = ::VirtualAllocEx(
            process,
            nullptr,
            pathBytes,
            MEM_COMMIT | MEM_RESERVE,
            PAGE_READWRITE);
        if (remotePath == nullptr)
        {
            const DWORD error = ::GetLastError();
            ::CloseHandle(process);
            return error;
        }

        SIZE_T written = 0;
        if (!::WriteProcessMemory(process, remotePath, dllPath, pathBytes, &written) ||
            written != pathBytes)
        {
            const DWORD error = ::GetLastError();
            ::VirtualFreeEx(process, remotePath, 0, MEM_RELEASE);
            ::CloseHandle(process);
            return error == ERROR_SUCCESS ? ERROR_WRITE_FAULT : error;
        }

        HMODULE localKernel32 = ::GetModuleHandleW(L"kernel32.dll");
        const uintptr_t remoteKernel32 = FindRemoteModuleBase(processId, L"kernel32.dll");
        FARPROC localLoadLibraryW = localKernel32 == nullptr
            ? nullptr
            : ::GetProcAddress(localKernel32, "LoadLibraryW");
        if (localKernel32 == nullptr || remoteKernel32 == 0 || localLoadLibraryW == nullptr)
        {
            ::VirtualFreeEx(process, remotePath, 0, MEM_RELEASE);
            ::CloseHandle(process);
            return ERROR_PROC_NOT_FOUND;
        }

        const uintptr_t loadLibraryRva =
            reinterpret_cast<uintptr_t>(localLoadLibraryW) -
            reinterpret_cast<uintptr_t>(localKernel32);
        auto remoteLoadLibraryW = reinterpret_cast<LPTHREAD_START_ROUTINE>(
            remoteKernel32 + loadLibraryRva);
        HANDLE thread = ::CreateRemoteThread(
            process,
            nullptr,
            0,
            remoteLoadLibraryW,
            remotePath,
            0,
            nullptr);
        if (thread == nullptr)
        {
            const DWORD error = ::GetLastError();
            ::VirtualFreeEx(process, remotePath, 0, MEM_RELEASE);
            ::CloseHandle(process);
            return error;
        }

        const DWORD waitResult = ::WaitForSingleObject(thread, 10'000);
        DWORD remoteModule = 0;
        if (waitResult == WAIT_OBJECT_0)
            ::GetExitCodeThread(thread, &remoteModule);
        ::CloseHandle(thread);
        ::VirtualFreeEx(process, remotePath, 0, MEM_RELEASE);
        ::CloseHandle(process);
        if (waitResult == WAIT_TIMEOUT)
            return WAIT_TIMEOUT;
        return remoteModule == 0 ? ERROR_DLL_INIT_FAILED : ERROR_SUCCESS;
    }
}

extern "C" void CALLBACK Inject(HWND, HINSTANCE, LPSTR commandLine, int)
{
    const DWORD processId = std::strtoul(commandLine, nullptr, 10);
    const DWORD result = processId == 0 ? ERROR_INVALID_PARAMETER : InjectSelf(processId);
    ::ExitProcess(result);
}

extern "C" void CALLBACK SelfTest(HWND, HINSTANCE, LPSTR, int)
{
    ::ExitProcess(RunClientSignatureLocatorSelfTest() &&
        RunMultilineLayoutSelfTest() &&
        RunChatRowEncodingSelfTest() &&
        RunChatHookAdapterSelfTest() &&
        RunSharedChatSourceSelfTest() &&
        KoreanRenderHook::RunRecomposerSelfTest() &&
        KoreanRenderHook::RunChatTranslationBridgeSelfTest()
        ? ERROR_SUCCESS
        : ERROR_INVALID_DATA);
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        ::DisableThreadLibraryCalls(module);
        HANDLE worker = ::CreateThread(nullptr, 0, HookWorker, nullptr, 0, nullptr);
        if (worker != nullptr)
            ::CloseHandle(worker);
    }
    return TRUE;
}
