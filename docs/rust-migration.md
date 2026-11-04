# Sound Manager Rust 마이그레이션

기준: Rust 마이그레이션 계획서 v1.0. 작업 브랜치: `feature/rust-audio-server`.
Windows의 서버 실행 경로를 Rust로 전환했습니다. WPF 화면, Windows loopback 송신,
Android Java, Chrome·Edge 확장과 v1 PCM 프로토콜은 유지합니다.

## 구현과 자동 검증

| 단계 | 구현 | 검증 |
| --- | --- | --- |
| 0 기준선 | 설정 백업, 전용 브랜치, 결정적 C# 합성 음원 fixture 40개 | C# 코어·서버 71개 회귀 테스트 |
| 1 DSP | 입력 형식·링 버퍼·리샘플링·방향·EQ·48 kHz stereo 믹싱 | 기존 처리 모드에서 fixture 40개 PCM·버퍼·카운터 일치 |
| 2 네트워크 | Tokio/Axum 인증 REST·WebSocket, 세션 교체, 기기 설정의 원자적 저장 | 인증·브라우저 Origin·keepalive·크기/시간 제한·16소스 상한·업데이트 gate·재접속·저장 실패 등 서버 테스트 12개 |
| 3 Windows | Rust WASAPI sidecar, private pipe 제어, 종료 감지·재시작, 트레이·자동 시작·업데이트·아이콘 | Windows 테스트 27개; 실제 저장된 출력 장치의 음량 0 초기화·종료 확인 |
| 4 품질 | 입력별 AGC, 5 ms look-ahead 리미터, fractional 클록 보정과 개별 해제 옵션 | 입력 레벨·stereo 비율·무음 gate·피크 상한·연속성·±1500 ppm 가상 90초 시험 |
| 5 클라이언트·배포 | 실 Chrome 탭 캡처, Android API/OkHttp 합성 PCM, 외부 HTTPS/WSS, CI·서명 배포 | 아래 진행/완료 기록과 릴리스 검증 결과 참고 |

Android 자동 테스트는 기존 페어링 암호화·자동 인증·UI·업데이트 테스트에
Rust API/OkHttp 연결·PCM 전송·제어·재접속 테스트를 추가했습니다.
사용자는 **Android 실기기 검증을 추후 진행하고 자동 검증 후 릴리스**하도록
명시했습니다. 자동 테스트를 실제 AudioRecord 캡처나 Bluetooth 청취 시험으로
간주하지 않습니다. 실제 저장된 Galaxy Buds2 장치의 Rust WASAPI 초기화는
전체 음량 0으로 확인했으며 청취 음질 평가는 아닙니다.

실 Chrome 테스트는 독립된 2개 탭의 원본 PCM 전송, 팝업 닫기 후 전송 유지,
서버 음량·음소거·이름 변경, 확장 idle 업데이트와 페어링·프로필 복원을 확인했습니다.
실 Cloudflare 터널은 HTTPS 200, 무인증 401, 일반/브라우저 WSS 경로,
Rust DSP의 비영 출력과 음량·EQ 제어를 확인했습니다. 합성 음원만 사용했습니다.

## 런타임 구조

Rust workspace는 `sound-core`, `sound-protocol`, `sound-server`, `sound-output`,
`sound-app`으로 구성합니다. 오디오 상태는 DSP 전용 스레드가 단독 소유합니다.
네트워크는 소스별 제한된 큐로 PCM을 넘기고, DSP 결과는 고정 크기 블록 큐를 거쳐
WASAPI 전용 COM 스레드로 전달됩니다. 장치 callback에는 할당·잠금·네트워크·파일
작업이 없습니다. 제어 명령·상태 조회와 디스크 저장은 callback 밖에서 수행합니다.

WPF는 Rust 서버만 제어하며 C# 믹서와 WASAPI 출력기를 동시에 실행하지 않습니다.
원래 C# 코어·서버는 기준선·비교 테스트 도구와 이전 배포의 복구 경로를 위해 남깁니다.
개발용 native 서버 기본 포트는 17843, WPF가 시작하는 서버는 기존 17842입니다.
서버 키는 명령줄에 넣지 않고 부모·자식 사이의 private stdin pipe로 전달합니다.
부모 pipe가 닫히면 서버도 종료하며, 예기치 않은 자식 종료는 저장된 장치로 재시도합니다.
이때 기존 외부 터널을 유지해 주소·인증을 바꾸지 않고 재접속할 수 있습니다.
구조화된 생명주기 로그는 `%LOCALAPPDATA%/SoundManager/logs/backend.jsonl`에
크기를 제한해 저장하고 키·주소·원본 자식 출력을 기록하지 않습니다.

기존 `source-settings.json`의 PascalCase v1 구조를 유지하고 AutoGain 필드만
선택적으로 추가합니다. `deviceId` 설정은 첫 PCM 처리 전에 적용하고 같은 기기의
재접속은 이전 세션을 교체합니다. 저장 실패는 503을 반환하고 현재 설정을 유지합니다.
출력 보호·AGC 기준은 인접한 `processing-settings.json`에 원자적으로 저장합니다.

## 품질 설정과 호환성

입력 형식은 PCM16/float32 LE, 8–192 kHz, 모노·스테레오입니다. 시작 버퍼는
40 ms, 목표는 60 ms이며 최종 출력은 48 kHz float stereo입니다.
기본값은 클록 보정과 리미터 활성화, 입력별 AGC 비활성화입니다.

AGC 기본 목표는 -18 dBFS RMS, 최대 증폭 12 dB, attack 10 ms, release 500 ms,
무음 gate -60 dBFS입니다. 소스의 음량 비율은 AGC 이후에 적용됩니다.
출력 리미터는 양 채널에 같은 gain을 적용하고 기본 sample peak ceiling은 0.98입니다.
5 ms 지연이 추가됩니다. 이는 디지털 sample peak 제한으로 실제 음압이나 true peak를
보장하지 않습니다. 실제 60 dB SPL 측정·제한 기능은 포함하지 않습니다.

기존 C#의 클록 보정은 정수 나눗셈 때문에 실질적으로 적용되지 않았습니다.
호환 모드는 이를 보존하고 새 클록 보정은 부동소수점 계산을 사용합니다.
`/v1/processing`에서 리미터와 클록 보정을 개별 해제할 수 있습니다.
입력별 `autoGain`은 기존 source PATCH에 추가했으며 이를 생략한 기존 클라이언트가
다른 설정을 조절해도 저장된 AGC 값을 지우지 않습니다.

Golden 비교 허용 오차는 PCM 절대값 0.00002, 버퍼 시간 0.000000001 ms이며
프레임·패킷·클리핑·언더런·trim 카운터는 정확히 일치해야 합니다.
C# 계약에 AutoGain/Processing 및 출력 큐 부족 필드를 추가하면서 manifest의 Contracts.cs 해시만
갱신했습니다. 기존 40개 binary fixture의 내용과 SHA256은 바뀌지 않았습니다.

## 재현

Windows: Rust 1.99.1, Visual Studio C++ 도구·Windows SDK, .NET 10 SDK,
Python, Node.js 24, JDK 17/21과 Android SDK 35가 필요합니다.

```powershell
./scripts/build-rust.ps1
dotnet test tests/SoundManager.Tests -c Release
dotnet test tests/SoundManager.Windows.Tests -c Release
node --test browser-extension/tests/*.test.js
# Playwright Chromium 설치 후
node browser-extension/tests/e2e/run.mjs
# 실제 Android API -> native 서버 통합 자동 검증
$env:SM_RUST_SERVER=(Resolve-Path rust/target/release/sound-manager-server.exe).Path
cd android
./gradlew.bat testDebugUnitTest lintDebug assembleDebug
```

`SM_RUST_SERVER`가 없으면 Android native 통합 테스트 하나만 skipped로 명시됩니다.
CI Android job은 Linux headless Rust 서버를 빌드해 해당 테스트도 실행합니다.

```powershell
dotnet build tools/SoundManager.BrowserCheck -c Release
dotnet build tools/SoundManager.SoakCheck -c Release
dotnet tools/SoundManager.SoakCheck/bin/Release/net10.0/SoundManager.SoakCheck.dll rust/target/release/sound-manager-server.exe tools/SoundManager.BrowserCheck/bin/Release/net10.0/SoundManager.BrowserCheck.dll 1800 artifacts/validation-v1.0.0/soak-30min.jsonl

dotnet run --project tools/SoundManager.NetworkCheck -c Release -- --tunnel-exe artifacts/windows/cloudflared.exe --rust-exe rust/target/release/sound-manager-server.exe
```

장시간 비교는 각 backend에 합성 10소스를 보내며 ±1000 ppm 송신 샘플 클록 편차를
적용합니다. 10초마다 CPU·메모리·버퍼·언더런·폐기·렌더 지표를 기록합니다.
별도 서버 프로세스의 지표이며 headless 10 ms 출력입니다. 같은 PC에서 동시에
실행하고 개발 빌드 작업도 병행하므로 엄격히 격리된 성능 벤치마크는 아닙니다.
Bluetooth 전송 지연이나 실제 장치 안정성 수치를 뜻하지 않습니다.

## 릴리스 진행 기록

2026-11-05: 기능·클라이언트 자동 검증과 최종 패키지 native 서버의 10소스
1804초 연속 시험을 통과했습니다. 최종 시험의 underrun·폐기·trim은 모두 0입니다.
Windows EXE 추출·APK 원래 인증서·업데이트 서명을 확인했습니다.
첫 부하 병행 시험의 일시적 끊김도 숨기지 않고
[전체 검증 기록](validation-v1.0.0.md)에 최종 결과와 함께 보존했습니다.
v1.0.0은 자동 검증 후 EXE·APK·서명 메타데이터로 배포합니다.

Windows 다운로드는 단일 EXE이며 ZIP을 배포 자산으로 올리지 않습니다.
Android 원래 서명 키와 업데이트 공개키를 유지합니다. 서버 주소·키는 인증 설정에만
표시하고 메인 기기 목록의 이름·스피커 아이콘·‘음소거’·토글 및 별도 방향 조절창,
저장된 페어링과 자동 재접속을 유지합니다.
