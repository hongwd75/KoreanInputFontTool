# KoreanInputFontTool 책임 범위

`KoreanInputFontTool.exe`는 독립적으로 실행되는 x64 WinForms 앱입니다.

## 입력 흐름

```text
물리 키 입력
  -> 전역 한글 키보드 훅
  -> 두벌식/세벌식 한글 조합
  -> KDAOC/확장된 한글/완성형 출력 모드 선택
  -> DAoC용 조합 코드 포인트
  -> DAoC 채팅 입력창
```

- 앱이 열리면 훅을 자동 시작합니다.
- 한/영키가 입력 언어를 전환합니다.
- Space는 코드 포인트 치환에 사용하지 않고 항상 공백으로 유지합니다.
- Unicode 모드와 별도 IME 입력창은 사용하지 않습니다.

## 한글 글리프 모드

- `KDAOC 모드`: 초중성·초중종성 음절이 기존 KDAOC 초성 ID를 공유합니다.
- `확장된 한글`: 초중성 음절의 초성만 Windows-1252 인쇄 가능 코드 19개로 분리합니다. 초중종성의 초성·중성·종성 코드는 기존 KDAOC와 같습니다.
- `완성형 출력`: 입력 및 네트워크 계약은 KDAOC 조합형과 같습니다. 게임 안에서 GDI 출력 직전에만 완성형 Unicode 음절로 합칩니다.
- 인코더는 raw C1 제어 문자 `U+0080..U+009F`를 직접 전송하지 않습니다.
- DAoC가 Windows-1252 문자를 내부 1바이트 ID로 바꾸는 경우를 위해, 확장 폰트는 printable/raw ID 양쪽을 같은 초성 글리프로 매핑합니다.
- 구형 렌더러가 선택하는 8비트 Macintosh/ANSI cmap에도 같은 raw ID 별칭을 둡니다.
- `U+0020`은 빈 Space 글리프이고 `U+00A0`의 초중성용 중성 글리프와도 분리되어 있습니다.
- `한글출력` 변경 이벤트는 훅의 글리프 모드를 바꾸고, 레지스트리에 저장한 뒤 대응 채팅 폰트를 즉시 적용합니다.
- 폼이 처음 표시될 때 저장된 표시방식의 폰트를 다시 적용하여 배포본에 포함된 최신 폰트로 갱신합니다.

## 폰트 적용 흐름

KDAOC 원본, 확장 폰트, KDAOC 영문에 D2Coding 완성형 한글을 합친 폰트는 `Assets\DaocLegacyFonts.br` 한 개로 공동 압축되어 EXE에 포함됩니다. 선택한 폰트만 복원하여 게임의 공통·Atlantis·custom 폰트 폴더에 `korean-font.ttf`로 배치합니다. Atlantis/custom XML의 `chat_small`, `chat_large`, `ghost_chat_font` 선언만 수정하며, XML 원본은 `.korean-input-font-tool.original`로 백업합니다.

## 완성형 출력 흐름

```text
DAoC 조합형 채팅 문자열
  -> game.dll의 ANSI→UTF-16 문자열 변환 함수
  -> KoreanRenderHook32.dll
  -> 유효한 B0-C2 + 중성 + 선택 종성 바이트열 재조합
  -> 완성형 UTF-16 문자열
  -> DirectX 글리프 캐시 및 채팅 텍스처
```

- DAoC가 32비트이므로 렌더 훅만 Win32 DLL이며 설정 앱은 계속 Release/x64입니다.
- Win32 DLL은 단일 EXE 리소스에 포함되고 실행 시 버전별 LocalAppData 폴더로 복원됩니다.
- 설정 앱은 32비트 `rundll32.exe`를 통해 DAoC에 훅을 넣고, 이름 있는 준비 이벤트로 실제 IAT 패치 성공까지 확인합니다.
- 대상 PID는 `DAoCMWC` 창 클래스와 `camelot`, `camelot_2gb`, `game`, `game_2gb` 프로세스 이름을 함께 사용해 찾습니다.
- 훅은 설치된 `korean-font.ttf`를 게임 프로세스의 private font로 등록하고 변환 문자열을 그리는 동안만 같은 높이의 완성형 폰트를 선택하므로, 실행 중 모드 전환에서도 새 글리프를 사용할 수 있습니다.
- 모드를 바꾸면 DLL을 강제로 언로드하지 않고 레지스트리 값을 1초 간격으로 확인하여 변환을 켜거나 끕니다.
- 유효한 조합형 묶음만 변환하므로 일반 영문, 숫자, 문장부호와 `U+0020` Space는 보존됩니다.
- 설치 결과와 최초 64개 변환의 바이트/Unicode 코드는 `%LOCALAPPDATA%\KoreanInputFontTool\render-hook-<PID>.log`에 기록됩니다.
