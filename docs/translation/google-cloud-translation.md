# Google Cloud Translation 설정

이 앱은 API 키를 지원하는 **Cloud Translation - Basic(v2)** 엔드포인트를 사용합니다.

## 필요한 것

- [Google Cloud Console](https://console.cloud.google.com/)
- [Cloud Translation 설정 공식 안내](https://cloud.google.com/translate/docs/setup)
- [Cloud Translation API 활성화](https://console.cloud.google.com/apis/library/translate.googleapis.com)
- [API 사용자 인증 정보](https://console.cloud.google.com/apis/credentials)
- [API 키 관리 공식 안내](https://cloud.google.com/docs/authentication/api-keys)

Google Cloud 프로젝트와 결제 계정 연결이 필요할 수 있습니다. 무료 사용량, 가격 및 한도는
[Cloud Translation 가격 안내](https://cloud.google.com/translate/pricing)에서 확인하세요.

## 설정 순서

1. [Google Cloud Console](https://console.cloud.google.com/)에 로그인합니다.
2. 새 프로젝트를 만들거나 기존 프로젝트를 선택합니다.
3. 프로젝트에 결제 계정을 연결합니다.
4. [Cloud Translation API 페이지](https://console.cloud.google.com/apis/library/translate.googleapis.com)에서 API를 사용 설정합니다.
5. `API 및 서비스 > 사용자 인증 정보`로 이동합니다.
6. `사용자 인증 정보 만들기 > API 키`를 선택합니다.
7. 생성된 키를 복사합니다.
8. 키 편집 화면에서 `API 제한`을 `Cloud Translation API`로 제한하는 것을 권장합니다.
9. Google Cloud에서 할당량과 예산 알림을 설정합니다.

## 클라이언트 연동

1. 한글 입력 도구에서 `완성형 출력`을 선택합니다.
2. `설정`을 누르고 `채팅 자동 번역 사용`을 선택합니다.
3. 번역 방식에서 `Google Cloud Translation`을 선택합니다.
4. 발급받은 값을 `API 키`에 입력합니다.
5. 번역할 채널을 선택하고 `저장`을 누릅니다.

앱은 `https://translation.googleapis.com/language/translate/v2`로 영문 원문을 보내고
한국어 결과를 받습니다. Basic(v2)는 API 키 인증을 지원합니다. 자세한 요청 형식은
[Basic 텍스트 번역 공식 문서](https://cloud.google.com/translate/docs/basic/translating-text)를 참고하세요.

## 문제 해결

- `API has not been used`: 현재 프로젝트에서 Cloud Translation API를 활성화했는지 확인합니다.
- `API key not valid`: 키를 다시 복사하고 키가 삭제·재생성되지 않았는지 확인합니다.
- `403`: 결제 계정 연결과 API 제한 설정을 확인합니다.
- `429`: 프로젝트 할당량과 사용량을 확인합니다.
