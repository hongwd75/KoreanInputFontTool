#include "TranslationBridge.h"

#include <Windows.h>

#include <algorithm>
#include <cstdint>
#include <cstring>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace
{
    constexpr std::uint32_t MaxRecordCharacters = 4096;
    constexpr DWORD MaxReadBytes = 1024 * 1024;
    constexpr size_t PendingLayoutReservation = 96;
    constexpr size_t MaxDisplayedTranslationCharacters = 120;
    SRWLOCK TranslationLock = SRWLOCK_INIT;
    std::unordered_map<std::wstring, std::wstring> TranslationCache;
    std::unordered_set<std::wstring> TranslationPending;
    unsigned long long ResponseOffset = 0;

    std::wstring TranslationDirectory()
    {
        wchar_t localAppData[MAX_PATH] = {};
        if (::GetEnvironmentVariableW(L"LOCALAPPDATA", localAppData, MAX_PATH) == 0)
            return {};
        std::wstring directory(localAppData);
        directory += L"\\KoreanInputFontTool";
        ::CreateDirectoryW(directory.c_str(), nullptr);
        return directory;
    }

    std::wstring QueuePath(const wchar_t* kind)
    {
        std::wstring directory = TranslationDirectory();
        if (directory.empty())
            return {};
        wchar_t fileName[96] = {};
        swprintf_s(
            fileName,
            L"\\translation-%s-v1-%lu.bin",
            kind,
            ::GetCurrentProcessId());
        return directory + fileName;
    }

    bool StartsWithInsensitive(
        const std::wstring& text,
        size_t offset,
        const wchar_t* prefix)
    {
        const size_t prefixLength = wcslen(prefix);
        return offset + prefixLength <= text.size() &&
            ::CompareStringOrdinal(
                text.data() + offset,
                static_cast<int>(prefixLength),
                prefix,
                static_cast<int>(prefixLength),
                TRUE) == CSTR_EQUAL;
    }

    size_t FindInsensitive(
        const std::wstring& text,
        size_t offset,
        const wchar_t* value)
    {
        const size_t valueLength = wcslen(value);
        if (valueLength == 0 || offset >= text.size())
            return std::wstring::npos;
        for (size_t index = offset; index + valueLength <= text.size(); ++index)
        {
            if (::CompareStringOrdinal(
                text.data() + index,
                static_cast<int>(valueLength),
                value,
                static_cast<int>(valueLength),
                TRUE) == CSTR_EQUAL)
            {
                return index;
            }
        }
        return std::wstring::npos;
    }

    bool IsTranslatableChatLine(const std::wstring& text)
    {
        const size_t start = text.find_first_not_of(L" \t\r\n");
        if (start == std::wstring::npos || text.find(L"[번역]", start) != std::wstring::npos)
            return false;

        constexpr const wchar_t* Prefixes[] =
        {
            L"[Guild]",
            L"[Group]",
            L"[Whisper]",
            L"[Say]",
            L"[LFG]",
        };
        bool supportedChannel = false;
        size_t closingBracket = std::wstring::npos;
        for (const wchar_t* prefix : Prefixes)
        {
            if (StartsWithInsensitive(text, start, prefix))
            {
                supportedChannel = true;
                closingBracket = start + wcslen(prefix) - 1;
                break;
            }
        }
        size_t body = std::wstring::npos;
        if (supportedChannel)
        {
            const size_t colon = text.find(L':', closingBracket + 1);
            if (colon == std::wstring::npos)
                return false;
            body = text.find_first_not_of(L" \t", colon + 1);
        }
        else
        {
            constexpr const wchar_t* ChatDelimiters[] =
            {
                L" sends,",
                L" says,",
            };
            size_t delimiterPosition = std::wstring::npos;
            size_t delimiterLength = 0;
            for (const wchar_t* delimiter : ChatDelimiters)
            {
                const size_t position = FindInsensitive(text, start, delimiter);
                if (position != std::wstring::npos && position > start &&
                    (delimiterPosition == std::wstring::npos || position < delimiterPosition))
                {
                    delimiterPosition = position;
                    delimiterLength = wcslen(delimiter);
                }
            }
            if (delimiterPosition == std::wstring::npos)
                return false;
            body = text.find_first_not_of(
                L" \t",
                delimiterPosition + delimiterLength);
        }
        if (body == std::wstring::npos)
            return false;

        bool hasEnglish = false;
        for (size_t index = body; index < text.size(); ++index)
        {
            const wchar_t value = text[index];
            if ((value >= L'A' && value <= L'Z') ||
                (value >= L'a' && value <= L'z'))
            {
                hasEnglish = true;
            }
            if ((value >= 0x1100 && value <= 0x11FF) ||
                (value >= 0x3130 && value <= 0x318F) ||
                (value >= 0xAC00 && value <= 0xD7A3))
            {
                return false;
            }
        }
        return hasEnglish;
    }

    bool AppendRecord(
        const std::wstring& path,
        const std::wstring& key,
        const std::wstring& value)
    {
        if (path.empty() || key.empty() ||
            key.size() > MaxRecordCharacters ||
            value.size() > MaxRecordCharacters)
        {
            return false;
        }

        const std::uint32_t keyCharacters = static_cast<std::uint32_t>(key.size());
        const std::uint32_t valueCharacters = static_cast<std::uint32_t>(value.size());
        const size_t headerBytes = sizeof(keyCharacters) + sizeof(valueCharacters);
        const size_t keyBytes = key.size() * sizeof(wchar_t);
        const size_t valueBytes = value.size() * sizeof(wchar_t);
        std::vector<unsigned char> record(headerBytes + keyBytes + valueBytes);
        std::memcpy(record.data(), &keyCharacters, sizeof(keyCharacters));
        std::memcpy(record.data() + sizeof(keyCharacters), &valueCharacters, sizeof(valueCharacters));
        std::memcpy(record.data() + headerBytes, key.data(), keyBytes);
        if (valueBytes > 0)
            std::memcpy(record.data() + headerBytes + keyBytes, value.data(), valueBytes);

        HANDLE file = ::CreateFileW(
            path.c_str(),
            FILE_APPEND_DATA,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            nullptr,
            OPEN_ALWAYS,
            FILE_ATTRIBUTE_NORMAL,
            nullptr);
        if (file == INVALID_HANDLE_VALUE)
            return false;

        DWORD written = 0;
        const bool succeeded = ::WriteFile(
            file,
            record.data(),
            static_cast<DWORD>(record.size()),
            &written,
            nullptr) && written == record.size();
        ::CloseHandle(file);
        return succeeded;
    }
}

void KoreanRenderHook::ResetChatTranslationBridge()
{
    ::AcquireSRWLockExclusive(&TranslationLock);
    TranslationCache.clear();
    TranslationPending.clear();
    ResponseOffset = 0;
    ::ReleaseSRWLockExclusive(&TranslationLock);

    const std::wstring requests = QueuePath(L"requests");
    const std::wstring responses = QueuePath(L"responses");
    if (!requests.empty())
        ::DeleteFileW(requests.c_str());
    if (!responses.empty())
        ::DeleteFileW(responses.c_str());
}

bool KoreanRenderHook::TryAppendChatTranslation(std::wstring& text)
{
    if (!IsTranslatableChatLine(text))
        return false;

    const std::wstring original = text;
    std::wstring translated;
    bool shouldWrite = false;
    ::AcquireSRWLockExclusive(&TranslationLock);
    const auto cached = TranslationCache.find(original);
    if (cached != TranslationCache.end())
    {
        translated = cached->second;
    }
    else
    {
        shouldWrite = TranslationPending.insert(original).second;
    }
    ::ReleaseSRWLockExclusive(&TranslationLock);

    if (!translated.empty())
    {
        if (translated.size() > MaxDisplayedTranslationCharacters)
        {
            translated.resize(MaxDisplayedTranslationCharacters - 1);
            translated += L'…';
        }
        text += L" [번역] : ";
        text += translated;
        return true;
    }

    if (shouldWrite && !AppendRecord(QueuePath(L"requests"), original, L""))
    {
        ::AcquireSRWLockExclusive(&TranslationLock);
        TranslationPending.erase(original);
        ::ReleaseSRWLockExclusive(&TranslationLock);
    }

    // The client allocates the chat text texture during the first conversion.
    // Reserve invisible width immediately so an asynchronous translation is not
    // clipped to the original English line's texture bounds on later redraws.
    text.append(PendingLayoutReservation, L' ');
    return true;
}

void KoreanRenderHook::PollChatTranslationResponses()
{
    const std::wstring path = QueuePath(L"responses");
    if (path.empty())
        return;

    HANDLE file = ::CreateFileW(
        path.c_str(),
        GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);
    if (file == INVALID_HANDLE_VALUE)
        return;

    LARGE_INTEGER size = {};
    if (!::GetFileSizeEx(file, &size))
    {
        ::CloseHandle(file);
        return;
    }
    if (static_cast<unsigned long long>(size.QuadPart) < ResponseOffset)
        ResponseOffset = 0;
    const unsigned long long remaining =
        static_cast<unsigned long long>(size.QuadPart) - ResponseOffset;
    if (remaining == 0)
    {
        ::CloseHandle(file);
        return;
    }

    const DWORD requested = static_cast<DWORD>(
        (std::min)(remaining, static_cast<unsigned long long>(MaxReadBytes)));
    std::vector<unsigned char> bytes(requested);
    LARGE_INTEGER offset = {};
    offset.QuadPart = static_cast<LONGLONG>(ResponseOffset);
    if (!::SetFilePointerEx(file, offset, nullptr, FILE_BEGIN))
    {
        ::CloseHandle(file);
        return;
    }
    DWORD read = 0;
    const bool readSucceeded = ::ReadFile(file, bytes.data(), requested, &read, nullptr) != FALSE;
    ::CloseHandle(file);
    if (!readSucceeded)
        return;

    size_t cursor = 0;
    while (read - cursor >= sizeof(std::uint32_t) * 2)
    {
        std::uint32_t keyCharacters = 0;
        std::uint32_t valueCharacters = 0;
        std::memcpy(&keyCharacters, bytes.data() + cursor, sizeof(keyCharacters));
        std::memcpy(
            &valueCharacters,
            bytes.data() + cursor + sizeof(keyCharacters),
            sizeof(valueCharacters));
        if (keyCharacters > MaxRecordCharacters || valueCharacters > MaxRecordCharacters)
        {
            ResponseOffset = static_cast<unsigned long long>(size.QuadPart);
            return;
        }

        const size_t headerBytes = sizeof(keyCharacters) + sizeof(valueCharacters);
        const size_t keyBytes = static_cast<size_t>(keyCharacters) * sizeof(wchar_t);
        const size_t valueBytes = static_cast<size_t>(valueCharacters) * sizeof(wchar_t);
        const size_t recordBytes = headerBytes + keyBytes + valueBytes;
        if (read - cursor < recordBytes)
            break;

        std::wstring key(keyCharacters, L'\0');
        std::wstring value(valueCharacters, L'\0');
        if (keyBytes > 0)
            std::memcpy(&key[0], bytes.data() + cursor + headerBytes, keyBytes);
        if (valueBytes > 0)
        {
            std::memcpy(
                &value[0],
                bytes.data() + cursor + headerBytes + keyBytes,
                valueBytes);
        }

        ::AcquireSRWLockExclusive(&TranslationLock);
        TranslationPending.erase(key);
        if (!value.empty())
            TranslationCache[key] = value;
        ::ReleaseSRWLockExclusive(&TranslationLock);
        cursor += recordBytes;
    }
    ResponseOffset += cursor;
}

bool KoreanRenderHook::RunChatTranslationBridgeSelfTest()
{
    const bool parsingSucceeded =
        IsTranslatableChatLine(L"[Guild] Character : Need healer") &&
        IsTranslatableChatLine(L"[LFG] Character : RvR group needs tank") &&
        IsTranslatableChatLine(L"[Group] 이름 : Meet at north gate") &&
        IsTranslatableChatLine(L"nodeoccu sends, \"hi\"") &&
        IsTranslatableChatLine(L"NodeOccu SENDS, \"meet at north gate\"") &&
        IsTranslatableChatLine(L"nodeoccu says, \"hello everyone\"") &&
        IsTranslatableChatLine(L"NodeOccu SAYS, \"meet at the keep\"") &&
        !IsTranslatableChatLine(L"[Guild] Character : 안녕하세요") &&
        !IsTranslatableChatLine(L"nodeoccu sends, \"안녕하세요\"") &&
        !IsTranslatableChatLine(L"nodeoccu says, \"안녕하세요\"") &&
        !IsTranslatableChatLine(L"[Advice] Character : Need help");
    if (!parsingSucceeded)
        return false;

    ResetChatTranslationBridge();
    const std::wstring original = L"[LFG] Character : Meet at north gate";
    std::wstring firstRender = original;
    if (!TryAppendChatTranslation(firstRender) ||
        firstRender.size() != original.size() + PendingLayoutReservation)
        return false;
    if (!AppendRecord(QueuePath(L"responses"), original, L"북문에서 만나기"))
        return false;

    PollChatTranslationResponses();
    std::wstring translatedRender = original;
    const bool appended = TryAppendChatTranslation(translatedRender) &&
        translatedRender == original + L" [번역] : 북문에서 만나기";
    ResetChatTranslationBridge();
    return appended;
}
