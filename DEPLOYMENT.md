# 독립 배포

`KoreanInputFontTool`은 단독으로 배포하는 WinForms EXE입니다.

```powershell
cd <KoreanInputFontTool 프로젝트 폴더>
.\publish-win-x64.ps1
```

생성 파일:

```text
dist\korean-input-font-tool-win-x64\KoreanInputFontTool.exe
```

배포본은 framework-dependent single-file `Release/win-x64`입니다. 세 가지 모드의 폰트와 Win32 완성형 렌더 훅은 EXE 리소스에 포함되므로 배포 파일은 EXE 한 개입니다. 실행 PC에는 Microsoft .NET 8 Desktop Runtime(x64)이 필요합니다.

일반 배포 빌드는 호환 버전 `18_28`의 기존 Win32 훅 자산을 그대로 재사용하고 x64 설정 앱만 게시합니다. 따라서 앱 버전이 올라가도 네이티브 훅 구현이 바뀌지 않으면 같은 DLL 및 준비 이벤트와 호환됩니다. 완성형 출력 모드에서 압축된 훅 DLL은 `%LOCALAPPDATA%\KoreanInputFontTool\NativeHook\18_28`에 자동 복원됩니다.

네이티브 훅 소스를 실제로 변경한 경우에만 새 호환 버전을 정한 뒤 프로젝트의 훅 버전 상수를 함께 갱신하고 다음 명령으로 다시 빌드합니다.

```powershell
.\publish-win-x64.ps1 -RebuildNativeHook
```
