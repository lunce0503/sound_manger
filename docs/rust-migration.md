# Sound Manager Rust 마이그레이션 진행 기록

기준 계획: Rust 마이그레이션 계획서 v1.0. 대상 저장소:
`lunce0503/sound_manger`. 기존 C# 서버를 유지하면서 DSP → 네트워크 →
Windows 연동 → 음질 개선 → 배포 순서로 진행합니다.

## 1. 이번 변경 범위

2026-11-05 현재 Phase 0~3의 초기 구현과 자동 검증을 진행했습니다.
작업 브랜치는 `feature/rust-audio-server`입니다. Windows 앱 소스의 서버 경로는
Rust sidecar로 전환했으며, 기존 C# 서버·DSP는 기준선/비교 테스트용으로 보존합니다.
공개 릴리스 v0.4.1은 기존 C# 기반입니다. 1.0.0 릴리스는 아직 게시하지 않았습니다.
작업 시작 당시의 지연 측정·UI 관련 미커밋 변경을 포함해 보존했습니다.

| 단계 | 현재 상태 | 남은 완료 조건 |
| --- | --- | --- |
| 0 기준선 | C# 테스트 71개·Windows 기존 20개, 결정적 기준 출력 40개 통과; 설정 백업·브랜치 확보 | 새 변경에 따른 회귀 검증 유지 |
| 1 DSP | Rust fmt/clippy/test·C# 출력 40개 비교 통과 | 품질 개선을 별도 옵션으로 검증 |
| 2 서버 | 인증·REST·WS·PascalCase 설정 저장·세션 교체·진단 구현; 서버 테스트 8개 통과 | 부하·시간 제한·실제 클라이언트 비교 보강 |
| 3 Windows 연동 | WASAPI 출력·WPF sidecar·프로세스 종료 감지·아이콘·바로가기 구현; Windows 총 24개 테스트 통과 | 프로세스 재시작·자동 시작·단일 EXE 패키징 최종 검증 |
| 4 품질 | 미착수 | AGC·look-ahead limiter·실효성 있는 클록 보정 |
| 5 배포 | Rust 빌드/검증 CI·Rust 대상 Chrome 실제 탭 테스트 구현 | 10개 소스·30분·성능·터널·롤백·1.0.0 서명/게시 |

사용자는 Android 실기기 검증을 추후 진행하고 자동 검증 후 릴리스하도록
명시했습니다. 해당 실기기 결과를 자동 테스트로 대체해 통과했다고 기록하지 않습니다.
실제 PC의 저장된 Galaxy Buds2 출력 장치에서 Rust WASAPI 초기화와 종료를
전체 음량 0으로 확인했습니다. 실제 청취 음질 검증을 의미하지 않습니다.

현재 Rust workspace는 `sound-core`, `sound-protocol`, `sound-server`,
`sound-output`, `sound-app`을 포함합니다. Rust 도구 설치 및 SDK 복구 뒤
2026-11-05 로컬 빌드가 가능해졌습니다. 아래 2026-11-03 제한 기록은 당시 기록입니다.

## 2. 기존 코드와 대응 관계

| C# 기준 구현 | Rust 대응 | 보존하는 동작 |
| --- | --- | --- |
| `src/SoundManager.Core/Contracts.cs` | `sound-core/src/contracts.rs` | PCM16/float32 LE, 8~192 kHz, 1~2채널, 볼륨·EQ·방향 검증 |
| `AudioSource.cs` | `source.rs`, `ring_buffer.rs`, `resampler.rs`, `spatial.rs` | 모노 복제, 비정상 float 정제, 500 ms 버퍼 상한, 선형 보간, 공간 효과, 음소거 중 소비 |
| `Mixer.cs` | `mixer.rs` | 16소스, float stereo 합산, EQ → master volume → hard clip |
| `Biquad.cs` | `biquad.rs` | 120 Hz low shelf, 4 kHz high shelf, 2.5 kHz peaking |
| `RenderTiming.cs` 및 수신 진단 | Phase 2에서 연결 예정 | 단조 시계 기반 수신·렌더 지표; 현재 Rust는 프레임·패킷·trim 카운터만 제공 |
| `src/SoundManager.Server/HubServer.cs` | Phase 2 | 인증·엔드포인트·세션 생명주기 |
| `SourceSettingsStore.cs`, `WindowsConnectionStore.cs` | Phase 2/3 | 기기 설정·저장된 페어링 보존 |
| `src/SoundManager.Windows` | Phase 3 | WPF 유지, WASAPI 출력·송신 캡처·트레이·자동 시작·업데이트 |

입력 버퍼는 C#처럼 입력 샘플레이트를 유지하며, 믹싱 단계에서 48 kHz
Float32 Stereo로 변환합니다. Rust 코어의 핸들은 내부 식별자입니다.
프로토콜 `sourceId`/`deviceId` GUID와 매핑할 네트워크 계층은 별도로 필요합니다.

오디오 상태는 단일 소유자가 관리합니다. `render`에는 동적 할당·잠금·I/O를
넣지 않았습니다. 향후 수신 스레드와 출력 스레드를 연결할 제한된 큐,
제어 명령 전달, 상태 스냅샷 전달 및 자원 해제 정책은 별도 검증 대상입니다.
현재 버퍼를 SPSC 동기화 구현으로 간주하면 안 됩니다.

## 3. 호환성 기준과 발견 사항

`tools/SoundManager.Golden`은 현재 C# 믹서에 합성 PCM과 설정 변경을 입력해
40개 `.smg` 파일을 생성합니다. 같은 시나리오를 두 번 생성해 결정성을
확인하며, 기준 소스의 SHA-256도 기록합니다. 개인 녹음·주소·연결 키·설정
파일은 포함하지 않습니다.

Rust 테스트는 동일 입력을 재생하여 샘플 절대 오차 ≤ 0.00002,
버퍼 시간 오차 ≤ 0.000000001 ms, 프레임·패킷·클리핑·언더런·trim 카운터의
정확한 일치를 요구합니다. 자세한 형식은
[`rust/tests/compatibility/README.md`](../rust/tests/compatibility/README.md)를 참고하세요.

**클록 드리프트 주의:** 현재 C#의 `(count - reserve) / (format.SampleRate * 20)`은
정수끼리 나눕니다. 버퍼가 약 500 ms로 제한되므로 해당 보정값은 사실상 0입니다.
초기 Rust 이식에서는 이 동작도 유지했습니다. Phase 4에서 부동소수점 나눗셈으로
변경하고, 송수신 클록 편차에 대한 별도 시험과 기준 출력 검토를 수행해야 합니다.

그 외 Phase 2에서 반드시 보존할 계약:

- `WS /v1/audio`, `WS /v1/browser-audio`, `GET /v1/state`, `PUT /v1/mix`, `PUT/PATCH /v1/sources/{id}`.
- 일반 경로의 `X-Sound-Key`와 브라우저 첫 hello의 `key`, 확장 Origin 검증,
  constant-time 키 비교. Android는 일반 오디오 경로를 사용합니다.
- hello 최대 4096 bytes, 전체 WebSocket 메시지 최대 65536 bytes,
  10초 메시지 수신 제한, 정확한 keepalive 메시지, fragment 합치기.
- `deviceId` 저장 설정을 첫 PCM 처리 전에 복원하고, 재연결 시 이전 세션을
  교체합니다. 이전 세션 종료가 새 세션을 제거하지 않아야 합니다.
- 최신 상태 응답의 진단 필드를 포함합니다. 업데이트 중 신규 접속 제한,
  저장 실패 시 기존 설정 유지 및 HTTP 상태 코드도 비교합니다.

## 4. 실행 기록 및 제한

2026-11-03 작업 환경에서 C# 코어·서버·Windows·테스트 프로젝트와
Golden 생성기 빌드를 확인했습니다. Golden 40개 생성과 검증은 성공했습니다.
표준 C# 테스트는 VSTest와 testhost 간 연결이 90초 후 시간 초과하여 중단됐습니다.
기존 `MixerTests`·`LatencyTests`의 23개 테스트 케이스는 테스트 실행기를
거치지 않고 직접 호출하여 모두 통과했습니다. 이는 전체 xUnit/WPF/서버
테스트 완료를 의미하지 않습니다.

Rust/Cargo가 설치되지 않았고 `static.rust-lang.org` DNS 요청이 실패하여
로컬 Rust fmt/clippy/test는 실행하지 못했습니다. `.git` 쓰기도 거부되어
`feature/rust-audio-server` 브랜치를 만들거나 커밋하지 못했습니다.
Rust 테스트 통과와 Phase 0/1 완료를 주장하지 않습니다.

검증 가능한 일반 Windows 개발 환경에서는 다음을 실행합니다.

```powershell
dotnet test tests/SoundManager.Tests -c Release
dotnet test tests/SoundManager.Windows.Tests -c Release
./scripts/test-rust.ps1
```

새 CI는 위 Rust 검증 스크립트를 실행하도록 추가했으며, 아직 원격 실행 결과는
없습니다. 현재 C# 기준 소스를 함께 커밋해야 기준 해시 검증이 일치합니다.
검증 실패 시 baseline을 자동 갱신하지 않고 원인과 변경 의도를 먼저 확인합니다.

다음 작업은 Rust 검증을 통과시킨 뒤 `sound-protocol`과 `sound-server`를 추가하는
Phase 2입니다. 개발 서버는 17843을 사용하고, 기존 17842 C# 서버는 유지합니다.
Windows 릴리스는 단일 `.exe`를 유지하며, Android 서명 키는 변경하지 않습니다.
서버 주소·키와 360도 방향 조작은 연결/설정 화면 안에 두고, 주 기기 목록의
스피커 아이콘·‘음소거’·토글과 저장된 페어링/자동 재연결을 유지합니다.
