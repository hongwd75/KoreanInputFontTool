#pragma once

#include <string>

namespace KoreanRenderHook
{
    bool RecomposeLegacyText(const char* text, int length, std::wstring& output);
    bool RecomposeLegacyWideText(const wchar_t* text, int length, std::wstring& output);
    bool RunRecomposerSelfTest();
}
