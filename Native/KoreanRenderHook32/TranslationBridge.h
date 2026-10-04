#pragma once

#include <string>

namespace KoreanRenderHook
{
    void ResetChatTranslationBridge();
    void PollChatTranslationResponses();
    bool TryAppendChatTranslation(std::wstring& text);
    unsigned long ChatTranslationLayoutRevision();
    bool RunChatTranslationBridgeSelfTest();
    bool RunChatSourceBoundarySelfTest(bool (*check)(const std::wstring&, const std::wstring&));
}
