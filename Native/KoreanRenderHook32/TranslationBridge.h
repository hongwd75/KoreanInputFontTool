#pragma once

#include <string>

namespace KoreanRenderHook
{
    void ResetChatTranslationBridge();
    void PollChatTranslationResponses();
    bool TryAppendChatTranslation(std::wstring& text);
    bool RunChatTranslationBridgeSelfTest();
}
