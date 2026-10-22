# Sound Manager

여러 Windows·Android 기기의 재생음을 Windows 서버에서 합쳐 Bluetooth 이어폰·스피커로 출력하는 초기 MVP입니다. Android 앱에서 QR로 연결하고 전체 음량, EQ와 음원별 방향을 조정합니다. 외부 네트워크에서는 앱의 Cloudflare HTTPS 터널을 사용할 수 있습니다.

[v0.3.0 릴리스 및 다운로드](https://github.com/lunce0503/sound_manger/releases/tag/v0.3.0) · [변경 사항](docs/releases/v0.3.0.md)

```mermaid
flowchart LR
    A[Windows 클라이언트\nWASAPI loopback] -->|원본 캡처 PCM + 형식| S
    B[Android 클라이언트\nAudioPlaybackCapture] -->|48 kHz PCM16 stereo| S
    C[Android 제어 화면] -->|음량 · EQ · 방향| S
    S[Windows 서버\n버퍼 · 형식 변환 · 공간 처리\n믹싱 · EQ · 출력 제한] --> O[Windows 출력 장치]
    O --> H[Bluetooth 이어폰 / 스피커]
```

## 구현 범위

- **Windows**: 서버/클라이언트 중 한 역할 선택. WASAPI 캡처/출력, 연결 QR·연결 코드, 선택적 외부 HTTPS 터널, 음량 및 EQ 프리셋.
- **Android 10 이상**: QR로 한 번 인증한 뒤 자동 재연결. 전체 음량과 기기 목록이 있는 기본 화면, 별도 EQ·방향 설정 창, 스피커 아이콘·음소거 글자·스위치. 주소/키는 연결 관리 안에서만 입력합니다. 재생 오디오 전송과 알림에서 전송 중지를 지원합니다.
- **서버 코어**: 최대 16개 음원, PCM16/float32 LE, 모노/스테레오, 8–192 kHz 입력. 서버에서 48 kHz float stereo로 변환하고 혼합. 입력별 제한된 버퍼, 언더런 재버퍼링, 소규모 클럭 드리프트 보정, 최종 출력 ±1 제한과 클리핑 계수.
- **EQ**: 120 Hz 저음 shelf, 4 kHz 고음 shelf, 2.5 kHz presence peaking, 각 ±12 dB. 기본/저음 강조/고음 강조/부드럽게/선명하게 프리셋.
- **가상 방향**: 좌우 레벨 차, 최대 약 0.63 ms 귀간 지연, 후방 고주파 감쇠. HRTF·머리 추적·실제 다채널 360° 렌더러는 아직 구현하지 않았습니다. 스피커와 청취 환경에 따라 전후 구별이 제한됩니다. 방향 사용 시 해당 스테레오 입력을 모노로 합쳐 배치하며, 꺼두면 좌우 채널을 유지합니다.

## 실행

1. Windows 서버 PC에 Bluetooth 이어폰/스피커를 페어링합니다.
2. 배포한 `SoundManager-0.3.0-windows-x64.exe`를 실행합니다. 필요한 파일이 사용자 앱 폴더에 자동으로 풀리고 앱이 열립니다. 출력 장치를 선택한 뒤 **서버 시작**을 누릅니다.
3. Windows 방화벽 알림에서는 사용하는 **개인 네트워크** 접근을 허용합니다. 수동 설정이 필요한 경우 서버 TCP `17842`를 같은 LAN에 허용합니다.
4. Windows의 **새 기기 연결 · QR 인증**을 펼쳐 휴대폰과 같은 네트워크의 주소를 선택합니다. Android의 **연결 관리 → QR로 서버 연결**에서 스캔하고 주소를 확인하여 연결합니다. 카메라 없이 연결 코드를 붙여넣거나 연결 관리의 **주소와 키로 연결**을 사용할 수도 있습니다.
5. Android에서 **재생음 전송 시작**을 누르고 오디오 권한 및 시스템 캡처 승인을 합니다. 캡처 가능한 앱에서 음악을 재생합니다. 서버의 출력 장치로 합산된 음원이 재생됩니다.
6. 다른 Windows PC는 **복사한 연결 코드 적용**으로 주소/키를 입력한 뒤 **음원 전송 시작**을 누릅니다. Android에서 전체 EQ와 각 음원의 가상 방향을 조정합니다.

인증이 성공하면 Android는 주소와 키를 Android Keystore AES-GCM으로 암호화해 저장합니다. 앱을 닫았다 열면 저장된 인증으로 연결하며, 일시적인 연결 실패 시에도 인증을 지우지 않고 재시도합니다. **연결 관리 → 저장된 연결 지우기**로 해제합니다. Windows 서버 키도 사용자별 DPAPI로 보관해 재시작 시 재사용합니다. Windows 클라이언트 연결도 암호화해 보관합니다. OS의 재생 오디오 캡처 승인은 서버 인증과 별개로 필요합니다.

### 다른 Wi-Fi / 모바일 데이터로 연결

1. Windows 서버를 시작한 뒤 **외부 네트워크 연결 켜기**를 누릅니다.
2. 앱이 HTTPS 주소의 실제 접속을 확인하고 QR을 갱신할 때까지 기다립니다.
3. 휴대폰의 연결 관리에서 새 QR을 스캔합니다. 같은 LAN에 있지 않아도 제어와 PCM 전송이 가능합니다. 별도 도메인·Cloudflare 계정·공유기 포트포워딩은 필요하지 않습니다.
4. 휴대폰 앱만 닫았다 열면 같은 주소로 자동 재연결합니다. **외부 네트워크 연결 끄기** 또는 Windows 서버 중지/종료 후 터널을 다시 열면 임시 주소가 바뀌므로 새 주소 QR이 필요합니다. LAN 주소가 바뀌었을 때도 새 주소를 연결해야 합니다.

배포 EXE에는 `cloudflared.exe`가 포함되어 자동으로 함께 풀립니다. 앱은 고정한 버전과 SHA256을 확인한 뒤 숨겨진 자식 프로세스로 실행하며 종료 시 정리합니다. 소스에서 `dotnet run`으로 실행할 때는 `scripts/prepare-tunnel.ps1 -OutputDirectory <실행 폴더>`로 도구를 준비해야 합니다.

외부 모드는 Cloudflare **Quick Tunnel**을 사용한 시험용 연결입니다. 임시 주소이며 서비스 가용성을 보장하지 않습니다. 오디오는 Cloudflare를 경유하고 Cloudflare가 HTTPS를 종료하므로 기기 간 종단간 암호화는 아닙니다. QR/연결 코드를 가진 사람은 서버를 제어할 수 있으므로 연결할 기기에만 공유합니다. [공식 Quick Tunnel 안내](https://developers.cloudflare.com/tunnel/get-started/quick-tunnels/)

## 개발 및 빌드

Windows 빌드에는 .NET 10 SDK, Android 빌드에는 JDK 17 또는 21, Android SDK 35 / Build Tools 35.0.0이 필요합니다. Android Studio에서 `android` 폴더를 열어도 됩니다. Gradle Wrapper 8.11.1과 Android Gradle Plugin 8.9.2를 고정했습니다.

```powershell
dotnet build SoundManager.sln -c Release
dotnet test tests/SoundManager.Tests -c Release
dotnet run --project src/SoundManager.Windows

# Android: ANDROID_HOME 설정 또는 android/local.properties에 sdk.dir 지정
cd android
.\gradlew.bat assembleDebug lintDebug testDebugUnitTest
```

전체 빌드 및 배포 폴더 생성:

```powershell
.\scripts\build.ps1
# Windows만 빌드하려면 .\scripts\build.ps1 -SkipAndroid
```

- Windows: `artifacts/windows/SoundManager.Windows.exe` (x64, .NET 런타임 포함). 배포할 때 **폴더 전체**를 전달합니다.
- Android 개발용: `artifacts/android/SoundManager-debug.apk`.
- 릴리스: `scripts/release.ps1`로 **단일 Windows EXE**와 **릴리스 서명 APK**, `SHA256SUMS.txt`를 `artifacts/release/v0.3.0`에 생성합니다. Windows 배포는 앞으로 항상 EXE이며 ZIP은 내부 패키징에만 사용합니다. EXE는 구성 파일 무결성을 확인하고 `%LOCALAPPDATA%/SoundManager/versions`에 풀어 실행합니다. Android 서명 키는 `%LOCALAPPDATA%/SoundManager/release-signing`에 보관하며 업데이트를 위해 보존해야 합니다. [릴리스·서명 안내](docs/releasing.md)
- CI: push/PR마다 Windows 빌드·테스트, Android APK 빌드·lint를 실행하고 GitHub Actions artifact로 저장합니다.

## 현재 제약

- Android는 재생 앱의 캡처 허용 정책을 따릅니다. DRM, 통화, 캡처 금지 앱의 소리는 전송되지 않을 수 있습니다. 마이크를 대신 녹음하지 않습니다. [Android 공식 문서](https://developer.android.com/media/platform/av-capture)
- 클라이언트는 캡처 이후 EQ/음량/리샘플링을 하지 않습니다. OS가 제공한 캡처 PCM을 전송하므로 **원본 음원 파일과 비트 단위 동일함을 보장하지는 않습니다**. Android 캡처 형식은 API에 48 kHz/16-bit/stereo를 요청합니다. Windows는 기본 출력 장치의 캡처 형식을 사용하며, 현재 16-bit PCM 또는 32-bit float의 모노/스테레오만 지원합니다.
- 초기 구성에서 서버는 Windows입니다. Android 서버와 Android Bluetooth 출력 호스트는 후속 범위입니다. 서버와 Windows loopback 클라이언트를 같은 PC에서 함께 실행하면 피드백이 발생할 수 있으므로 서로 다른 PC를 사용합니다. 클라이언트의 로컬 재생음은 자동으로 음소거하지 않습니다.
- 버퍼 시작 기준은 40 ms, 목표 약 60 ms, 최대 약 500 ms이며 Bluetooth/OS/네트워크 지연이 추가됩니다. 장치 간 절대 시간 동기화·동일 음원에 대한 샘플 단위 정렬은 아직 없습니다. 입력 레이트 변환은 선형 보간이며 스튜디오 품질 변환기는 아닙니다. 과부하 시 Windows 송신 큐와 서버 버퍼는 오래된 패킷/프레임을 버리며, Android는 200 ms 이상 송신 큐가 누적되면 전송을 중지합니다.
- 여러 음원이나 EQ 증폭으로 합산이 ±1을 넘으면 최종 출력이 잘립니다. 클리핑 수가 늘면 음원 또는 전체 음량을 낮추세요. 현재 제한기는 hard clip이며 look-ahead limiter는 후속 작업입니다.
- LAN은 HTTP/WS, 외부 터널은 HTTPS/WSS와 공유 연결 키를 사용합니다. 앱은 외부 HTTP 주소와 주소의 계정·경로·쿼리를 거부합니다. Android는 인증 키가 다른 주소로 전송되지 않도록 HTTP 리다이렉트를 따르지 않습니다. 기기별 권한·자동 검색은 아직 없습니다. HTTP 포트를 직접 인터넷에 공개하는 방식은 지원하지 않습니다.
- 외부 연결은 인터넷·중계 지연이 추가되며 실시간성이 LAN보다 떨어질 수 있습니다. 48 kHz PCM16 stereo는 소스당 약 1.536 Mbps(분당 약 11.5 MB, 헤더 제외)를 사용합니다.
- 실제 Android 기기·Bluetooth 이어폰/스피커에서의 지연·끊김·음질 검증은 별도 필요합니다. 아래 문서에 검증 상태와 절차를 기록합니다.

## 구조

| 경로 | 역할 |
| --- | --- |
| `src/SoundManager.Core` | 플랫폼 독립 PCM 버퍼, 리샘플링, 공간 처리, 믹서 및 EQ |
| `src/SoundManager.Server` | 인증된 REST 제어 및 WebSocket PCM 수신 |
| `src/SoundManager.Windows` | WPF 앱, WASAPI 출력/캡처 |
| `src/SoundManager.Launcher` | 구성 파일을 포함한 단일 EXE 실행기 |
| `android/app` | Android 제어 화면 및 재생음 캡처 서비스 |
| `tests/SoundManager.Tests` | DSP 및 실제 HTTP/WebSocket 통합 테스트 |
| `tools/SoundManager.NetworkCheck` | 공개 HTTPS/WSS 터널의 합성 PCM 수신 검증 |
| `docs/protocol.md` | 통신 규격 |
| `docs/validation.md` | 빌드/테스트 결과와 실기기 점검 절차 |

MIT 라이선스. 현재 버전 `0.3.0` (시험 배포). 포함한 라이브러리·터널 도구의 라이선스는 `THIRD-PARTY-NOTICES.md`를 참조하세요.
