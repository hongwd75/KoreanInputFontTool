# Microsoft Translator 설정

## 필요한 것

- [Azure 계정 및 Azure Portal](https://portal.azure.com/)
- [Translator 리소스 생성 공식 안내](https://learn.microsoft.com/azure/ai-services/translator/how-to-create-translator-resource)
- 사용량과 요금 확인: [Azure Translator 가격 안내](https://azure.microsoft.com/pricing/details/cognitive-services/translator/)

Azure 구독과 결제 정보가 필요할 수 있습니다. 사용 가능한 경우 학습·시험 용도로 F0
무료 계층을 선택할 수 있지만, 무료 계층 제공 여부와 한도는 Azure Portal에서 확인하세요.

## 설정 순서

1. [Azure Portal](https://portal.azure.com/)에 로그인합니다.
2. `리소스 만들기`에서 `Translator`를 검색해 Translator 리소스를 만듭니다.
3. 구독, 리소스 그룹, 이름, 지역 및 가격 계층을 선택하고 리소스를 생성합니다.
4. 생성된 리소스에서 `리소스 관리 > 키 및 엔드포인트`를 엽니다.
5. `KEY 1` 또는 `KEY 2`를 복사합니다. 키는 외부에 공개하지 마세요.
6. 지역 리소스 또는 다중 서비스 리소스를 사용한다면 같은 화면의 `Location/Region` 값도 확인합니다.

## 클라이언트 연동

1. 한글 입력 도구에서 `완성형 출력`을 선택합니다.
2. `설정`을 누르고 `채팅 자동 번역 사용`을 선택합니다.
3. 번역 방식에서 `Microsoft Translator`를 선택합니다.
4. `API 키`에 Azure의 KEY 값을 입력합니다.
5. `지역`에는 Azure에 표시된 Location 값을 입력합니다. 단일 서비스 Global 리소스라면 비워도 됩니다.
6. 번역할 채널을 선택하고 `저장`을 누릅니다.

이 앱은 영문을 한국어로 번역하기 위해 Translator REST API v3의 전역 엔드포인트를
사용합니다. 지역 또는 다중 서비스 리소스에서는 지역 헤더가 필요합니다. 자세한 인증
방식은 [Microsoft REST 빠른 시작](https://learn.microsoft.com/azure/ai-services/translator/text-translation/quickstart/rest-api)을 참고하세요.

## 문제 해결

- `401/403`: 키가 잘못됐거나 Translator 리소스의 키가 아닌지 확인합니다.
- 지역 관련 오류: Azure의 `Location/Region` 값을 앱의 `지역`에 정확히 입력합니다.
- `429`: 할당량 또는 요청 속도 제한을 확인합니다.
- 비용이 걱정된다면 Azure Portal에서 예산·경고와 사용량을 설정하세요.
