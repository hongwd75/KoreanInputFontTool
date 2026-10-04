# 빌드 원칙

- 바이너리는 항상 기능과 호환성을 유지하는 최소 용량의 Release 버전으로 빌드한다.
- 앱은 Release/x64, 네이티브 훅은 Release/Win32를 사용한다. Debug 빌드는 하지 않는다.
- 배포는 `publish-win-x64.ps1`을 사용한다. 네이티브 훅을 함께 빌드할 때는 `-RebuildNativeHook`을 지정한다.
- framework-dependent single-file, Optimize=true, PublishReadyToRun=false, DebugType=none, DebugSymbols=false를 유지한다. 자체 포함 런타임과 WinForms 트리밍은 사용하지 않는다.
- 네이티브 훅의 MinSpace, 참조 제거, COMDAT 병합 및 Brotli SmallestSize 압축을 유지한다.
