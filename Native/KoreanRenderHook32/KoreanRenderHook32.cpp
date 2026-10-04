#include "HangulRecomposer.h"
#include "MinHook.h"
#include "TranslationBridge.h"

#include <Windows.h>
#include <TlHelp32.h>

#include <cstdlib>
#include <cstdint>
#include <cstring>
#include <string>

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
    volatile LONG CachedModeEnabled = 0;
    volatile LONG LastModeCheckTick = 0;
    volatile LONG LoggedConversionCount = 0;
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
        const std::wstring hangulOnly = recomposed;
        bool translationChanged = KoreanRenderHook::TryAppendChatTranslation(recomposed);
        if (!hangulChanged && !translationChanged)
        {
            return -1;
        }
        if (recomposed.size() >= static_cast<size_t>(destinationCapacity) &&
            translationChanged)
        {
            recomposed = hangulOnly;
            translationChanged = false;
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

    BOOL WINAPI HookTextOutA(HDC dc, int x, int y, LPCSTR text, int length)
    {
        if (!IsPrecomposedModeEnabled() || SystemTextOutW == nullptr)
            return OriginalTextOutA(dc, x, y, text, length);

        std::wstring recomposed;
        const bool hangulChanged = KoreanRenderHook::RecomposeLegacyText(text, length, recomposed);
        const bool translationChanged = KoreanRenderHook::TryAppendChatTranslation(recomposed);
        if (!hangulChanged && !translationChanged)
            return OriginalTextOutA(dc, x, y, text, length);
        LogConversion(text, length, recomposed);
        const ScopedCompleteFont font(dc);
        return SystemTextOutW(dc, x, y, recomposed.data(), static_cast<int>(recomposed.size()));
    }

    BOOL WINAPI HookTextOutW(HDC dc, int x, int y, LPCWSTR text, int length)
    {
        if (!IsPrecomposedModeEnabled())
            return OriginalTextOutW(dc, x, y, text, length);

        std::wstring recomposed;
        const bool hangulChanged = KoreanRenderHook::RecomposeLegacyWideText(text, length, recomposed);
        const bool translationChanged = KoreanRenderHook::TryAppendChatTranslation(recomposed);
        if (!hangulChanged && !translationChanged)
            return OriginalTextOutW(dc, x, y, text, length);
        LogWideConversion(text, length, recomposed);
        const ScopedCompleteFont font(dc);
        return SystemTextOutW(dc, x, y, recomposed.data(), static_cast<int>(recomposed.size()));
    }

    BOOL WINAPI HookGetTextExtentPoint32A(HDC dc, LPCSTR text, int length, LPSIZE size)
    {
        if (!IsPrecomposedModeEnabled() || SystemGetTextExtentPoint32W == nullptr)
            return OriginalGetTextExtentPoint32A(dc, text, length, size);

        std::wstring recomposed;
        const bool hangulChanged = KoreanRenderHook::RecomposeLegacyText(text, length, recomposed);
        const bool translationChanged = KoreanRenderHook::TryAppendChatTranslation(recomposed);
        if (!hangulChanged && !translationChanged)
            return OriginalGetTextExtentPoint32A(dc, text, length, size);
        const ScopedCompleteFont font(dc);
        return SystemGetTextExtentPoint32W(
            dc,
            recomposed.data(),
            static_cast<int>(recomposed.size()),
            size);
    }

    BOOL WINAPI HookGetTextExtentPoint32W(HDC dc, LPCWSTR text, int length, LPSIZE size)
    {
        if (!IsPrecomposedModeEnabled())
            return OriginalGetTextExtentPoint32W(dc, text, length, size);

        std::wstring recomposed;
        const bool hangulChanged = KoreanRenderHook::RecomposeLegacyWideText(text, length, recomposed);
        const bool translationChanged = KoreanRenderHook::TryAppendChatTranslation(recomposed);
        if (!hangulChanged && !translationChanged)
            return OriginalGetTextExtentPoint32W(dc, text, length, size);
        const ScopedCompleteFont font(dc);
        return SystemGetTextExtentPoint32W(
            dc,
            recomposed.data(),
            static_cast<int>(recomposed.size()),
            size);
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
                ? L"install ok v18.18: client text conversion + GDI hooks + translation bridge, complete font registered"
                : L"install ok v18.18: client text conversion + GDI hooks + translation bridge, complete font registration failed")
            : L"install failed: TextOutW patch");
        return installed;
    }

    DWORD WINAPI HookWorker(void*)
    {
        wchar_t eventName[96] = {};
        swprintf_s(
            eventName,
            L"Local\\KoreanInputFontTool.RenderHook.v18_30.%lu",
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
