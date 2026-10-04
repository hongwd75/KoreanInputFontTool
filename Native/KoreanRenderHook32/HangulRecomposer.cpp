#include "HangulRecomposer.h"

#include <Windows.h>

#include <iterator>

namespace
{
    constexpr unsigned char ExtendedInitialBytes[] =
    {
        0x80, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
        0x88, 0x89, 0x8A, 0x8B, 0x8C, 0x8E, 0x91,
        0x92, 0x93, 0x94, 0x95, 0x96,
    };

    constexpr wchar_t ExtendedInitialCodepoints[] =
    {
        0x20AC, 0x201A, 0x0192, 0x201E, 0x2026, 0x2020, 0x2021,
        0x02C6, 0x2030, 0x0160, 0x2039, 0x0152, 0x017D, 0x2018,
        0x2019, 0x201C, 0x201D, 0x2022, 0x2013,
    };

    int StandardInitialIndex(unsigned char value)
    {
        return value >= 0xB0 && value <= 0xC2 ? value - 0xB0 : -1;
    }

    int ExtendedInitialIndex(unsigned char value)
    {
        for (int index = 0; index < static_cast<int>(std::size(ExtendedInitialBytes)); ++index)
        {
            if (ExtendedInitialBytes[index] == value)
                return index;
        }
        return -1;
    }

    int NoFinalMedialIndex(unsigned char value)
    {
        if (value >= 0xA0 && value <= 0xAF)
            return value - 0xA0;
        if (value >= 0xF4 && value <= 0xF8)
            return 16 + value - 0xF4;
        return -1;
    }

    int FinalMedialIndex(unsigned char value)
    {
        return value >= 0xC3 && value <= 0xD7 ? value - 0xC3 : -1;
    }

    int FinalIndex(unsigned char value)
    {
        return value >= 0xD8 && value <= 0xF2 ? 1 + value - 0xD8 : -1;
    }

    wchar_t Compose(int initial, int medial, int finalIndex)
    {
        return static_cast<wchar_t>(0xAC00 + ((initial * 21 + medial) * 28) + finalIndex);
    }

    void AppendWindows1252(unsigned char value, std::wstring& output)
    {
        const char source = static_cast<char>(value);
        wchar_t converted = 0;
        if (::MultiByteToWideChar(1252, 0, &source, 1, &converted, 1) == 1)
            output.push_back(converted);
        else
            output.push_back(static_cast<wchar_t>(value));
    }

    bool TryLegacyByte(wchar_t value, unsigned char& byte)
    {
        if (value <= 0x00FF)
        {
            byte = static_cast<unsigned char>(value);
            return true;
        }
        for (int index = 0; index < static_cast<int>(std::size(ExtendedInitialCodepoints)); ++index)
        {
            if (ExtendedInitialCodepoints[index] == value)
            {
                byte = ExtendedInitialBytes[index];
                return true;
            }
        }
        return false;
    }
}

std::string KoreanRenderHook::EncodeLegacyChatText(const wchar_t* text, int length)
{
    std::string output;
    for (int index = 0; index < length; ++index)
    {
        const wchar_t value = text[index];
        if (value >= 0xAC00 && value <= 0xD7A3)
        {
            const int syllable = value - 0xAC00;
            const int initial = syllable / (21 * 28);
            const int medial = (syllable / 28) % 21;
            const int finalIndex = syllable % 28;
            output.push_back(static_cast<char>(0xB0 + initial));
            output.push_back(static_cast<char>(finalIndex != 0 ? 0xC3 + medial :
                medial < 16 ? 0xA0 + medial : 0xF4 + medial - 16));
            if (finalIndex != 0)
                output.push_back(static_cast<char>(0xD8 + finalIndex - 1));
        }
        else
        {
            char bytes[2] = {};
            const int count = ::WideCharToMultiByte(1252, 0, &value, 1, bytes, 2, "?", nullptr);
            output.append(bytes, count > 0 ? count : 0);
        }
    }
    return output;
}

bool KoreanRenderHook::RecomposeLegacyText(
    const char* text,
    int length,
    std::wstring& output)
{
    output.clear();
    if (text == nullptr || length <= 0)
        return false;

    output.reserve(static_cast<size_t>(length));
    bool changed = false;
    int position = 0;
    while (position < length)
    {
        const auto current = static_cast<unsigned char>(text[position]);
        int initial = StandardInitialIndex(current);
        const int extendedInitial = ExtendedInitialIndex(current);

        if (initial >= 0 && position + 2 < length)
        {
            const int medial = FinalMedialIndex(
                static_cast<unsigned char>(text[position + 1]));
            const int finalIndex = FinalIndex(
                static_cast<unsigned char>(text[position + 2]));
            if (medial >= 0 && finalIndex > 0)
            {
                output.push_back(Compose(initial, medial, finalIndex));
                position += 3;
                changed = true;
                continue;
            }
        }

        if (initial < 0)
            initial = extendedInitial;
        if (initial >= 0 && position + 1 < length)
        {
            const int medial = NoFinalMedialIndex(
                static_cast<unsigned char>(text[position + 1]));
            if (medial >= 0)
            {
                output.push_back(Compose(initial, medial, 0));
                position += 2;
                changed = true;
                continue;
            }
        }

        AppendWindows1252(current, output);
        ++position;
    }

    return changed;
}

bool KoreanRenderHook::RecomposeLegacyWideText(
    const wchar_t* text,
    int length,
    std::wstring& output)
{
    output.clear();
    if (text == nullptr || length <= 0)
        return false;

    output.reserve(static_cast<size_t>(length));
    bool changed = false;
    int position = 0;
    while (position < length)
    {
        unsigned char current = 0;
        if (!TryLegacyByte(text[position], current))
        {
            output.push_back(text[position++]);
            continue;
        }

        int initial = StandardInitialIndex(current);
        const int extendedInitial = ExtendedInitialIndex(current);
        if (initial >= 0 && position + 2 < length)
        {
            unsigned char medialByte = 0;
            unsigned char finalByte = 0;
            if (TryLegacyByte(text[position + 1], medialByte) &&
                TryLegacyByte(text[position + 2], finalByte))
            {
                const int medial = FinalMedialIndex(medialByte);
                const int finalIndex = FinalIndex(finalByte);
                if (medial >= 0 && finalIndex > 0)
                {
                    output.push_back(Compose(initial, medial, finalIndex));
                    position += 3;
                    changed = true;
                    continue;
                }
            }
        }

        if (initial < 0)
            initial = extendedInitial;
        if (initial >= 0 && position + 1 < length)
        {
            unsigned char medialByte = 0;
            if (TryLegacyByte(text[position + 1], medialByte))
            {
                const int medial = NoFinalMedialIndex(medialByte);
                if (medial >= 0)
                {
                    output.push_back(Compose(initial, medial, 0));
                    position += 2;
                    changed = true;
                    continue;
                }
            }
        }

        output.push_back(text[position]);
        ++position;
    }
    return changed;
}

bool KoreanRenderHook::RunRecomposerSelfTest()
{
    std::wstring output;
    for (int initial = 0; initial < 19; ++initial)
    {
        for (int medial = 0; medial < 21; ++medial)
        {
            const unsigned char noFinalMedial = medial < 16
                ? static_cast<unsigned char>(0xA0 + medial)
                : static_cast<unsigned char>(0xF4 + medial - 16);
            const char noFinal[] =
            {
                static_cast<char>(0xB0 + initial),
                static_cast<char>(noFinalMedial),
            };
            if (!RecomposeLegacyText(noFinal, 2, output) ||
                output.size() != 1 || output[0] != Compose(initial, medial, 0))
            {
                return false;
            }
            const wchar_t wideNoFinal[] =
            {
                static_cast<wchar_t>(0xB0 + initial),
                static_cast<wchar_t>(noFinalMedial),
            };
            if (!RecomposeLegacyWideText(wideNoFinal, 2, output) ||
                output.size() != 1 || output[0] != Compose(initial, medial, 0))
            {
                return false;
            }

            const char extendedNoFinal[] =
            {
                static_cast<char>(ExtendedInitialBytes[initial]),
                static_cast<char>(noFinalMedial),
            };
            if (!RecomposeLegacyText(extendedNoFinal, 2, output) ||
                output.size() != 1 || output[0] != Compose(initial, medial, 0))
            {
                return false;
            }
            const wchar_t extendedWideNoFinal[] =
            {
                ExtendedInitialCodepoints[initial],
                static_cast<wchar_t>(noFinalMedial),
            };
            if (!RecomposeLegacyWideText(extendedWideNoFinal, 2, output) ||
                output.size() != 1 || output[0] != Compose(initial, medial, 0))
            {
                return false;
            }

            for (int finalIndex = 1; finalIndex < 28; ++finalIndex)
            {
                const char withFinal[] =
                {
                    static_cast<char>(0xB0 + initial),
                    static_cast<char>(0xC3 + medial),
                    static_cast<char>(0xD8 + finalIndex - 1),
                };
                if (!RecomposeLegacyText(withFinal, 3, output) ||
                    output.size() != 1 ||
                    output[0] != Compose(initial, medial, finalIndex))
                {
                    return false;
                }
                const wchar_t wideWithFinal[] =
                {
                    static_cast<wchar_t>(0xB0 + initial),
                    static_cast<wchar_t>(0xC3 + medial),
                    static_cast<wchar_t>(0xD8 + finalIndex - 1),
                };
                if (!RecomposeLegacyWideText(wideWithFinal, 3, output) ||
                    output.size() != 1 ||
                    output[0] != Compose(initial, medial, finalIndex))
                {
                    return false;
                }
            }
        }
    }

    const char mixed[] =
    {
        'A', ' ', static_cast<char>(0xB0), static_cast<char>(0xA0), ' ', 'B'
    };
    return RecomposeLegacyText(mixed, 6, output) && output == L"A \xAC00 B";
}
