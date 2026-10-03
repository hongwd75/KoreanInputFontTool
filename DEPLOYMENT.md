# 독립 배포

`KoreanInputFontTool`은 단독으로 배포하는 WinForms EXE입니다.

```powershell
cd C:\project\KoreanInputFontTool
.\publish-win-x64.ps1
```

생성 파일:

```text
C:\project\KoreanInputFontTool\dist\korean-input-font-tool-win-x64\KoreanInputFontTool.exe
```

배포본은 framework-dependent single-file `Release/win-x64`입니다. 세 가지 모드의 폰트와 Win32 완성형 렌더 훅은 EXE 리소스에 포함되므로 배포 파일은 EXE 한 개입니다. 실행 PC에는 Microsoft .NET 8 Desktop Runtime(x64)이 필요합니다.

배포 스크립트는 먼저 `KoreanRenderHook32`를 `Release|Win32`로 빌드하고 Brotli로 압축한 뒤 x64 설정 앱을 게시합니다. 완성형 출력 모드에서 압축된 훅 DLL은 `%LOCALAPPDATA%\KoreanInputFontTool\<버전>`에 자동 복원됩니다.
