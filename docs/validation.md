# 검증 기록

검증 환경: Windows x64, .NET SDK 10.0.401, JDK 21, Android SDK 35 / Build Tools 35.0.0. 코드와 실행 명령은 저장소에 포함하고, 로컬 SDK/생성물/키는 Git에서 제외합니다.

## 자동 검증

```powershell
dotnet build SoundManager.sln -c Release
dotnet test tests/SoundManager.Tests -c Release
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

2026-10-22 최종 검증:

| 항목 | 결과 |
| --- | --- |
| .NET Release 빌드 | 성공, 경고/오류 0개 |
| .NET DSP/HTTP/WebSocket 테스트 | 20개 통과, 실패/건너뜀 0개 |
| Windows x64 self-contained publish | 성공 |
| Android `assembleDebug` | 성공, 디버그 APK 생성 |
| Android `lintDebug` | 성공, `No issues found` |
| Windows 실행 화면 | 실행·출력 장치 열거 확인 |
| Windows 서버 시작/제어 | 선택한 WASAPI 출력 초기화, 키 발급, 상태 API `200`, UI의 저음 강조 설정이 API에 +6 dB로 반영됨 확인 |
| Windows 서버 중지 | 중지 및 연결 키 제거 확인 |
| Android/Bluetooth 실기기 청취 | 미실행. 연결된 Android 테스트 기기 없음 |

GUI 검증은 무음 상태에서 진행했습니다. Bluetooth 출력 장치를 열 수 있음은 확인했으나 실제 음질·지연·다중 기기 수신의 실기기 검증을 대체하지 않습니다. GitHub Actions의 원격 실행 결과는 별도 확인 대상입니다.

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
- TLS 및 기기별 페어링, 자동 검색, 재연결, 설정 저장.
- Android 서버/출력 역할과 절대 시간 기반 다중 소스 동기화.
