# LibreTranslate 설정

LibreTranslate는 로컬 PC에서 직접 실행하거나 외부에서 운영되는 서버에 연결할 수 있습니다.
Windows에서는 공식 안내에 따라 Docker Desktop 사용을 권장합니다.

## 1. 로컬 서버

### 필요한 도구

- [Docker Desktop for Windows 설치](https://docs.docker.com/desktop/setup/install/windows-install/)
- Docker Desktop 기본 백엔드인 WSL 2가 없다면 [WSL 설치 안내](https://learn.microsoft.com/windows/wsl/install)
- [LibreTranslate 공식 설치 문서](https://docs.libretranslate.com/guides/installation/)
- [LibreTranslate 공식 GitHub 저장소](https://github.com/LibreTranslate/LibreTranslate)

Docker Desktop의 라이선스 조건은 사용 환경에 따라 다를 수 있으므로 설치 페이지에서
현재 조건을 확인하세요.

### Step 1: WSL 2와 Docker Desktop 설치

1. 작업 관리자의 `성능 → CPU → 가상화`가 `사용`인지 확인합니다. 꺼져 있다면 BIOS/UEFI에서 AMD `SVM Mode` 또는 Intel `Virtualization Technology(VT-x)`를 활성화하고 Windows를 다시 시작합니다.
2. 관리자 PowerShell에서 `wsl --install`을 실행하고 Windows를 다시 시작합니다. 이미 WSL 2가 있다면 건너뜁니다.
3. Docker Desktop을 설치합니다.
4. Docker Desktop을 처음 실행하면 라이선스 약관을 직접 확인하고 동의합니다.
5. Docker 계정 로그인은 기본적으로 필수가 아닙니다. 조직 관리자가 로그인을 강제한 환경은 조직 정책을 따릅니다.
6. Linux 컨테이너 모드와 Docker 엔진이 준비될 때까지 기다립니다.
7. PowerShell에서 `docker version`을 실행해 서버 정보가 표시되는지 확인합니다.

### Step 2: LibreTranslate 설치 및 실행

한글 입력 도구에서 번역 방식을 `LibreTranslate (사용자 서버)`로 선택하고 서버 주소가
`http://localhost:5000`인 경우, 연결되지 않은 상태에서는 `로컬 번역 서버 켜기` 버튼이
표시됩니다. Docker Desktop이 설치되어 있다면 이 버튼이 Docker Desktop과 기존
`libretranslate` 컨테이너를 시작합니다. 컨테이너가 없으면 영어·한국어 모델 구성으로
검증된 `LibreTranslate 1.9.6` 이미지로 자동 생성합니다. 태그 없는 `latest` 이미지는
업데이트에 따라 동작이 달라질 수 있으므로 사용하지 않습니다. Docker 이미지와 번역 모델을 준비하는 동안 프로그래스바와 현재 단계가
표시되며, 서버 준비가 완료되면 프로그래스바가 자동으로 사라집니다.
`초기화` 버튼은 저장 전 입력된 API 키를 지우고 서버 주소를 `http://localhost:5000`으로
되돌립니다.

Docker Desktop이 설치되어 있지 않으면 자동 설치 여부를 묻습니다. `예`를 선택하면 Windows
패키지 관리자(winget)로 공식 Docker Desktop 패키지를 설치한 뒤 서버 시작을 다시 시도합니다.
설치 직후에는 Docker Desktop 첫 실행 화면이 열립니다. 라이선스 약관을 직접 확인하고 동의한
뒤 엔진 준비가 끝나면 앱으로 돌아와 `Docker 준비 후 다시 누르기`를 누릅니다. 앱은 약관에
대신 동의하지 않습니다.
앱은 `WSL 2 설치 → Windows 재시작 → Docker Desktop 설치·실행 → LibreTranslate 생성`
순서로 준비합니다. WSL이 준비되지 않은 PC에서는 Docker Desktop보다 먼저 WSL 설치 여부를
묻고, 동의하면 관리자 권한으로 `wsl --install`을 실행한 뒤 Windows 재시작 필요 상태를
표시합니다. Windows를 다시 시작한 뒤 앱에서 `로컬 번역 서버 켜기`를 다시 누르세요.
CPU 가상화가 BIOS/UEFI에서 꺼져 있으면 앱은 WSL·Docker 설치 전에 이를 감지하고 중단합니다.
펌웨어 설정은 PC마다 다르며 앱에서 자동 변경할 수 없습니다.

최초 실행은 이미지와 모델 다운로드로 시간이 걸릴 수 있습니다. 자동 실행이 실패하거나
상태를 직접 확인하려면 아래 수동 명령을 사용하세요.

PowerShell에서 다음 명령을 한 줄로 실행합니다.

```powershell
docker run -d --name libretranslate -p 127.0.0.1:5000:5000 --restart unless-stopped libretranslate/libretranslate:v1.9.6 --load-only en,ko
```

최초 실행에서는 Docker 이미지와 영어·한국어 번역 모델을 내려받기 때문에 시간이 걸릴 수 있습니다.

진행 상태 확인:

```powershell
docker logs -f libretranslate
```

로그 확인을 끝내려면 `Ctrl+C`를 누릅니다. 컨테이너는 계속 실행됩니다.

### Step 3: 서버 확인

브라우저에서 다음 주소를 엽니다.

```text
http://localhost:5000
```

또는 PowerShell에서 언어 목록을 확인합니다.

```powershell
Invoke-RestMethod http://localhost:5000/languages
```

`en`과 `ko`가 표시되면 준비된 것입니다.

### Step 4: 클라이언트 연동

1. 한글 입력 도구에서 `완성형 출력`을 선택합니다.
2. `설정`을 누르고 `채팅 자동 번역 사용`을 선택합니다.
3. 번역 방식에서 `LibreTranslate (사용자 서버)`를 선택합니다.
4. `서버 주소`에 `http://localhost:5000`을 입력합니다.
5. 로컬 기본 구성에서는 `API 키`를 비워 둡니다.
6. 번역할 채널을 선택하고 `저장`을 누릅니다.

앱이 서버 주소 뒤에 `/translate`를 자동으로 붙입니다. 서버 주소에 `/translate`까지 입력해도 됩니다.

### 시작·중지·삭제

```powershell
docker stop libretranslate
docker start libretranslate
docker logs --tail 100 libretranslate
```

컨테이너를 완전히 삭제하려면 먼저 중지한 뒤 삭제합니다. 번역 모델을 다시 받을 수 있으므로
문제 해결 목적이 아니라면 삭제할 필요는 없습니다.

```powershell
docker stop libretranslate
docker rm libretranslate
```

기존 `libretranslate` 컨테이너가 `v1.9.6`이 아닌 이미지로 만들어졌다면 앱은 버전 오류를
표시하고 자동으로 삭제하지 않습니다. 필요한 데이터가 없는지 확인한 다음 위의 중지·삭제
명령을 실행하고 `로컬 번역 서버 켜기`를 다시 누르면 `v1.9.6`으로 재생성됩니다.

## 2. 외부 LibreTranslate 서버

외부 서버는 직접 운영하는 원격 서버 또는 LibreTranslate 호스팅 서비스를 의미합니다.

- 공식 관리형 서비스의 API 키는 [LibreTranslate Portal](https://portal.libretranslate.com/)에서 확인합니다.
- 다른 운영자의 서버라면 운영자에게 기본 URL, API 키 필요 여부, 한국어 지원 여부를 확인합니다.
- 서버 주소는 예를 들어 `https://translate.example.com`처럼 입력합니다.
- API 키가 필요하면 `API 키 (선택)`에 입력하고, 필요 없으면 비워 둡니다.
- 개인 정보와 API 키를 보호하려면 외부 서버는 HTTPS 사용을 권장합니다.
- 공개 무료 서버는 요청 제한, 장애 또는 한국어 모델 미지원으로 동작하지 않을 수 있습니다.

직접 외부 공개 서버를 운영한다면 단순히 5000 포트를 인터넷에 노출하지 말고 HTTPS
리버스 프록시, 인증, 방화벽과 요청 제한을 구성하세요.

## 문제 해결

- 연결 거부: Docker Desktop과 `libretranslate` 컨테이너가 실행 중인지 확인합니다.
- 첫 요청이 느림: 초기 모델 다운로드와 로딩이 완료될 때까지 로그를 확인합니다.
- 한국어 번역 실패: `/languages` 결과에 `en`, `ko`가 모두 있는지 확인합니다.
- 외부 서버의 `401/403`: API 키 필요 여부와 키 값을 확인합니다.
- 외부 서버의 인증서 오류: 주소와 인증서가 올바른지 서버 운영자에게 확인합니다.
