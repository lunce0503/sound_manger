# 검증 기록

검증 환경: Windows x64, .NET SDK 10.0.401, JDK 21, Android SDK 35 / Build Tools 35.0.0. 코드와 실행 명령은 저장소에 포함하고, 로컬 SDK/생성물/키는 Git에서 제외합니다.

## 자동 검증

2026-10-25 v0.4.1 검증: .NET 62개, Windows 19개, Android 27개, 브라우저 단위 테스트 7개 통과. RSA 서명 변조·잘못된 키·경로·숫자 버전·비 HTTPS/외부 호스트 리다이렉트 거부, 손상된 다운로드 시 기존 EXE 보존, 해시 검증 후 교체, 최신 실행 포인터와 복구·경로 탈출 방지, 업데이트 설정·재시작 상태·페어링 파일 보존을 검사했습니다. 실제 Kestrel 음원 연결이 있으면 자동 업데이트 게이트가 대기하고, 게이트를 잡은 뒤 새 연결을 거부하는 것도 검사했습니다.

격리된 Windows 자식 프로세스로 정상 시작 응답·시작 실패·응답 없음과 이전 앱 실행을 확인했습니다. Chrome for Testing 151의 실제 탭 캡처 중 확장 파일 갱신을 보류하고, 전송 종료 후 갱신·브라우저 재시작 뒤 암호화 인증과 탭 설정을 유지하는 흐름이 통과했습니다. 테스트용 프로필에서 문서의 개발자 모드 설치 조건을 재현합니다. Android는 서명·해시·버전·APK 공유 URI 접근 제한 및 자동 확인 해제 시 연결 저장소 보존을 검사했습니다. 실제 Android 시스템 설치 승인과 실제 앱 전체 버전 간 교체, Bluetooth 청취는 미실행입니다.

2026-10-25 v0.4.0 검증: 서버 테스트 53개, Windows 테스트 14개, 브라우저 확장 단위 테스트 7개 통과. Windows 목록은 이름·음소거·⋮만 표시하고 메뉴에서 음량/이름/방향을 조절하는 WPF 테스트를 포함합니다. Android는 기기 메뉴와 PATCH 이름 변경을 포함한 20개 테스트가 통과했습니다.

격리된 Chrome for Testing 151에서 실제 두 웹 탭을 캡처해 서버의 float32 PCM 수신·믹싱을 확인했습니다. 팝업 종료 후 전송, 탭별 서버 음량·음소거·별칭, 브라우저 프로세스 종료/재시작 후 암호화 인증 및 저장한 탭 설정 복원을 검증했습니다. `tools/SoundManager.BrowserCheck`는 localhost에서만 열리고 오디오 출력 장치를 열지 않습니다. 실제 Bluetooth 청취는 별도입니다. 테스트 복사본은 로컬 테스트 호스트만 미리 허용하며 배포 확장은 사용자가 서버 호스트 권한을 승인해야 합니다.

```powershell
dotnet build SoundManager.sln -c Release
dotnet test tests/SoundManager.Tests -c Release
dotnet test tests/SoundManager.Windows.Tests -c Release
cd android
.\gradlew.bat assembleDebug lintDebug
```

테스트 항목:

- 서로 다른 레이트·채널 PCM의 합산과 공간 기능을 끈 스테레오 보존.
- 음소거 중 입력 소비, 제한 버퍼·오버플로·언더런·재버퍼링.
- 좌우 방향 레벨 차와 후방 주파수 응답 차이.
- 저음/고음/부드럽게/선명하게 프리셋의 실제 주파수 응답.
- 최종 출력 범위·전체 음량·클리핑 계수, NaN 오디오 차단.
- 유효하지 않은 형식/설정/불완전 프레임 거부, 동시 수신/제어/렌더.
- 실제 Kestrel HTTP/WebSocket에서 키 검사, 제어 값 검증, fragment 재조립, 오디오 수신, 비정상 패킷 거부, 연결 종료 후 소스 제거.
- Windows WPF 창을 닫고 숨긴 뒤에도 HTTP 제어·WebSocket PCM 수신·믹싱 유지, 같은 창 복원, 완전 종료 후 서버 접근 종료.
- 종료 중 중복 종료·복원 요청, 다른 실행 인스턴스의 복원 신호, 트레이 생성/정리 및 Windows 세션 종료 허용.

2026-10-23 v0.3.1 검증: .NET Release 빌드 경고/오류 0개. 기존 .NET 테스트 39개와 Windows 수명 주기 회귀 테스트 4개 통과. Windows 테스트는 실제 WPF 창과 Kestrel을 사용하며 하드웨어로 소리를 재생하지 않습니다. 실제 Bluetooth 장시간 청취와 트레이 메뉴의 수동 조작은 별도 점검 대상입니다.

2026-10-23 v0.3.2 검증: 기존 .NET 테스트 39개와 Windows 테스트 10개 통과. 추가 검증은 격리된 HKCU 테스트 키에서 로그인 등록/해제·인용된 실행 경로·버전 갱신·다른 값 보존, 출력 장치 JSON 저장/복원/손상 감지, 장치 대기 및 일시 오류 후 실제 서버 시작, 창을 표시하지 않은 상태에서 HTTP 접근, 대기·진행 중 시작 취소, 지연된 로그인 실행의 전용 IPC 신호를 포함합니다. 테스트는 실제 시작 프로그램 등록을 변경하지 않습니다. 실제 Windows 재부팅/로그인 및 Bluetooth 부팅 후 연결 검증은 미실행입니다.

2026-10-25 v0.3.3 검증: .NET 테스트 47개, Windows 테스트 11개 통과. 실제 HTTP/WebSocket에서 정상/강제 연결 종료 후 음량·방향·공간 효과·음소거 복원, 음소거 상태의 첫 PCM 렌더, 같은 이름의 다른 기기, 기기 이름 변경, 서버 재시작, 이전 연결을 대체하는 재연결, 동시 변경 시 파일/메모리 일치, 저장 실패 시 이전 설정 유지, 손상 파일 보존을 검사했습니다. Windows ID 동시 생성/재사용과 Android ID 저장/재사용/앱 데이터 초기화/손상 감지도 검사했습니다. Android debug 테스트 19개 및 lint 통과. 재연결 후 새 기기 카드의 음량·방향·음소거 표시 복원도 검사했습니다. 실제 Android/Bluetooth 다중 기기 청취는 미실행입니다.

2026-10-22 최종 검증:

| 항목 | 결과 |
| --- | --- |
| .NET Release 빌드 | 성공, 경고/오류 0개 |
| .NET DSP/HTTP/WebSocket/QR 테스트 | 38개 통과, 실패/건너뜀 0개 |
| Windows x64 self-contained publish | 성공 |
| Android `assembleDebug` | 성공, 디버그 APK 생성 |
| Android `lintDebug` | 성공, `No issues found` |
| Android `assembleRelease` / `lintRelease` | 성공, 릴리스 APK 생성, `No issues found` |
| Android QR 연결 정보 테스트 | JVM 테스트 5개 통과 |
| Android APK 서명 | `apksigner verify` 성공, APK Signature Scheme v2, RSA 3072 |
| Windows 실행 화면 | 실행·출력 장치 열거 확인 |
| Windows 서버 시작/제어 | 선택한 WASAPI 출력 초기화, 키 발급, 상태 API `200`, UI의 저음 강조 설정이 API에 +6 dB로 반영됨 확인 |
| Windows 서버 중지 | 중지 및 연결 키 제거 확인 |
| Windows QR/외부 모드 UI | LAN QR 표시, 외부 모드 시작 후 HTTPS 주소 자동 선택·QR 갱신, 해당 공개 주소 상태 API 200, 끄기 후 자식 프로세스 종료·LAN QR 복귀 확인 |
| 외부 HTTPS/WSS | 실제 공개 Cloudflare 터널에서 상태 200, 미인증 401, 믹서 제어 성공, 합성 PCM 19,200 bytes 수신·960 samples 렌더 확인 |
| Android/Bluetooth 실기기 청취 | 미실행. 연결된 Android 테스트 기기 없음 |

GUI 검증은 무음 상태에서 진행했습니다. Bluetooth 출력 장치를 열 수 있음은 확인했으나 실제 음질·지연·다중 기기 수신의 실기기 검증을 대체하지 않습니다. GitHub Actions의 원격 실행 결과는 별도 확인 대상입니다.

외부 경로 검증 명령(17842 포트 사용):

```powershell
dotnet run --project tools/SoundManager.NetworkCheck -c Release -- --tunnel-exe artifacts/windows/cloudflared.exe
```

이 검증은 테스트용 서버와 터널을 열고 인증/제어 및 합성 PCM을 시험한 뒤 둘 다 정리합니다. 실제 사용자 오디오를 캡처하거나 하드웨어로 재생하지 않습니다. 모바일 통신사 경로와 카메라 스캔은 검증하지 않습니다.

v0.2.0 Android 릴리스 인증서 SHA256(공개 식별자): `7217017ad413a98cc79e74bf44a6d4e13c93852ca32ef538e75881db1cda7687`.

## 실기기 점검 절차 (아직 미실행)

1. Windows 서버에서 Bluetooth 스테레오 출력 장치를 선택합니다. 앱이 표시하는 IP/키로 Android를 연결합니다.
2. 캡처를 허용한 테스트 앱에서 좌/우 서로 다른 톤을 재생합니다. 공간 기능을 끈 상태에서 채널이 보존되는지 확인합니다.
3. 다른 Windows PC를 추가하고 두 소리가 동시에 출력되는지 확인합니다. 시스템 볼륨을 낮게 시작하여 클리핑을 피합니다.
4. 음원 하나씩 음소거/음량 조정, 전체 음량, 각 EQ, 방향 0/90/180/270°를 확인합니다. 전후 방향 구별은 장치·개인차를 기록합니다.
5. Android 화면 끄기, 앱 전환, 회전, 시스템 캡처 중지, 알림에서 중지, 권한 거부를 확인합니다. 재시작은 새 캡처 승인을 요구해야 합니다.
6. 무음 30초 후 재생, Wi-Fi 단절/재연결, 서버 재시작, 잘못된 연결 키, Bluetooth 연결 해제/출력 장치 재선택을 확인합니다.
7. 30분 연속 재생 중 버퍼 지연, underruns, droppedFrames, clippedSamples와 실제 소리 끊김을 기록합니다. 두 기기 동시 출력의 시간 정렬을 보장하는 테스트는 아닙니다.

## 후속 개발

- 실기기에서 출력/캡처 장치 변경, 지연과 클럭 드리프트 측정.
- HRTF 기반 binaural 렌더링, 효과 전환 smoothing, 고품질 서버 리샘플러와 look-ahead limiter.
- 고정 주소 외부 연결, 기기별 페어링/권한, 자동 검색, 재연결, 설정 저장.
- Android 서버/출력 역할과 절대 시간 기반 다중 소스 동기화.
