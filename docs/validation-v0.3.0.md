# v0.3.0 검증

- .NET: 믹싱/서버/QR 및 Windows DPAPI 인증 저장을 포함한 테스트 39개 통과.
- Android: 14개 통과. 기존 QR 파서, AES-GCM 암호화/변조 거부, 저장 후 새 저장소 인스턴스에서 복원, 앱 닫기 후 저장된 인증으로 실제 테스트 HTTP 서버에 재접속하는 테스트.
- Android UI: 기본 화면에 주소/키 입력과 EQ 슬라이더/방향 조작판이 없음, 음소거의 아이콘/문자/스위치, EQ/방향 별도 창, 조작점 밖 스크롤 제스처 비점유, 취소된 방향 드래그 미적용을 Robolectric에서 검증.
- Android Release 빌드·lint 성공, 기존 릴리스 인증서로 APK 서명 확인(인증서 SHA256 유지).
- Windows: 단일 EXE 생성 후 `--extract-only` 추출 성공, 포함된 앱 EXE/DLL·터널 파일의 SHA256이 원본과 같음 확인. 일반 실행에서 앱 창이 생성됨을 확인.

Robolectric UI 검증은 Android API 29 환경을 사용합니다. 실제 Android Keystore 하드웨어와 휴대폰 터치/카메라, Bluetooth 청취는 실기기 검증 대상입니다. 인증 저장 코드의 암호화 및 복원은 테스트용 AES 키를 주입해 검증하며, 실제 앱은 AndroidKeyStore 공급자를 사용합니다.
