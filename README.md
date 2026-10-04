# KoreanInputFontTool

DAoC 채팅용 한글 조합 입력과 전용 채팅 폰트를 제공하는 독립 WinForms 도구입니다.

## 현재 기능

- 앱 시작 시 한글 키보드 훅 자동 실행
- 한/영키로 한글·영문 입력 전환
- 삼성 SKG-3000UB 등 독립 한/영키를 사용하는 106키 키보드 지원
- 두벌식 조합 및 세벌식 최종 조합 선택
- `KDAOC 모드`: 기존 초중종 조합 코드와 KDAOC 원본 폰트 사용
- `확장된 한글`: D2CodingLigature Bold 구성요소를 사용하며, 종성 없는 초성 19개는 가독성이 좋은 KDAOC 세로 확대 글리프로 처리
- `완성형 출력`: 입력·서버 전송은 KDAOC 조합형을 유지하고, 게임 내부의 문자열 변환 단계에서 완성형 Unicode 한글로 재조합
- `완성형 출력`에서는 길드·그룹·귓속말·일반 대화·LFG의 영문 메시지를 선택한 번역 서비스로 번역해 원문 뒤에 `[번역] : 내용`으로 표시
- 수신 귓속말의 `캐릭터 sends, "내용"` 형식도 귓속말 채널로 인식
- 일반 대화의 `캐릭터 says, "내용"` 형식도 일반 대화 채널로 인식
- 완성형 훅 오류 분석 로그: 실행 파일과 같은 폴더의 `hook-errors.log`
- 번역 서비스 설치 도움말: [Microsoft Translator](docs/translation/microsoft-translator.md), [Google Cloud Translation](docs/translation/google-cloud-translation.md), [LibreTranslate](docs/translation/libretranslate.md)
- 로컬 LibreTranslate 연결이 끊겨 있으면 설정창의 `로컬 번역 서버 켜기`로 Docker 컨테이너를 시작하거나 생성하고, 이미지·번역 모델 준비 상태를 프로그래스바와 문구로 표시
- LibreTranslate 설정의 `초기화` 버튼으로 API 키를 삭제하고 서버 주소를 `http://localhost:5000`으로 복원
- Docker Desktop이 없으면 `로컬 번역 서버 켜기`에서 설치 여부를 묻고, 동의 시 Windows 패키지 관리자(winget)로 자동 설치
- 로컬 서버 준비 순서를 `WSL 2 설치 → Windows 재시작 → Docker Desktop 설치·실행 → LibreTranslate 생성`으로 강제하고, WSL이 없으면 `wsl --install` 실행 여부를 먼저 확인
- 선택한 모드의 채팅 폰트를 Atlantis/custom UI에 적용
- DAoC 폴더, 입력 모드(두벌식/세벌식), `한글출력` 설정을 레지스트리에 저장하고 다음 실행 시 복원
- 앱 시작 시 저장된 `한글출력` 설정의 내장 폰트를 다시 적용하여 새 버전의 폰트가 즉시 반영됨
- 원본 XML 백업 및 폰트 원복
- x64 전용 빌드
- 일반 권한으로 시작하면 UAC 관리자 권한을 요청하여 자동 재실행
- Release 구성 전용 빌드
- 내장 32비트 렌더 훅은 Brotli 압축 상태로 포함하고 실행 시 자동 복원
- DAoC가 실행 중이거나 나중에 실행되면 32비트 렌더 훅을 자동 적용
- DAoC 창 클래스와 `camelot`, `camelot_2gb`, `game`, `game_2gb` 프로세스 이름을 모두 탐색

Unicode 입력 모드는 지원하지 않습니다.

## 프로젝트 구조

```text
KoreanInputFontTool\
  Assets\            KDAOC/확장 한글 폰트와 압축 배포 번들
  Native\            게임 내부 GDI 문자열 변환용 Win32 렌더 훅
  archive\           이전 소스·폰트 도구와 V11~V15 빌드 보관
  tools\              확장 폰트 및 압축 번들 생성기
  dist\               Release 배포 스크립트 실행 시 생성
  KoreanInputFontTool.csproj
```

## 한글출력

- `KDAOC 모드`는 기존 KDAOC 사용자와 동일한 코드·폰트 계약입니다.
- `확장된 한글`은 D2CodingLigature Bold의 일반 초·중·종성 윤곽을 사용합니다. 종성이 없는 음절의 초성은 Windows-1252 인쇄 가능 확장 ID로 분리하고, 기존 KDAOC 초성의 가로 모양은 유지한 채 세로만 확대합니다. DAoC 조합 위치와 advance는 KDAOC 메트릭을 유지합니다.
- `완성형 출력`은 입력창과 서버에는 기존 조합형 바이트를 그대로 사용합니다. DAoC 내부의 ANSI→UTF-16 변환 함수를 Win32 훅으로 가로채 유효한 초성+중성(+종성) 묶음을 `U+AC00..U+D7A3`으로 합칩니다. DirectX 글리프 캐시는 변환된 완성형 글자를 사용합니다.
- 완성형 폰트는 KDAOC의 영문·숫자·공백과 자간을 유지하면서 D2Coding 완성형 한글 11,172자만 추가한 하이브리드입니다.
- DAoC 폴더, 입력 모드(두벌식/세벌식), `한글출력` 선택값은 `HKCU\Software\KoreanInputFontTool`에 저장되어 다음 실행 때 복원됩니다. `한글출력`을 변경하면 대응 폰트도 자동 적용됩니다.
- 저장된 표시방식은 앱 시작 시에도 자동 적용되므로, 같은 모드를 유지한 채 실행 파일만 업데이트해도 새 폰트가 설치됩니다.
- Space는 어느 모드에서도 한글 글리프 ID로 사용하지 않으며 항상 `U+0020` 공백입니다.

## 개발 실행

```powershell
dotnet run --project .\KoreanInputFontTool.csproj -c Release -p:Platform=x64
```
