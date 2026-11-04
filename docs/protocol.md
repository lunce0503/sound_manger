# Sound Manager protocol v1

LAN 기본 주소 `http://<Windows IPv4>:17842`. 외부 모드에서는 Cloudflare 임시 터널의 `https://<name>.trycloudflare.com` 주소와 WSS를 사용합니다. 모든 HTTP 요청과 WebSocket 업그레이드 요청에 `X-Sound-Key: <서버의 연결 키>`를 포함합니다. 누락/불일치는 `401`. 서버는 최초 실행 시 192-bit 무작위 키(48자 hex)를 생성하고 DPAPI로 암호화해 보관하며 다음 실행에서도 재사용합니다. Android는 성공한 연결을 Keystore AES-GCM으로 보관하고 재실행 시 인증 확인 요청을 다시 보여주지 않고 접속합니다. 외부 모드의 TLS는 Cloudflare에서 종료되고 로컬 서버로는 loopback HTTP를 사용합니다.

## QR 연결 정보 v1

`soundmanager://pair?v=1&server=<percent-encoded base http(s) URL>&key=<connection key>`

정확히 `v`, `server`, `key` 세 필드를 포함하며 중복 필드·지원하지 않는 버전·다른 호스트/경로·fragment를 거부합니다. 주소는 루트 URL만 허용합니다. HTTP는 사설/LAN/loopback 및 CGNAT VPN 주소에만 허용하며 공개 도메인/IP에는 HTTPS가 필요합니다. 연결 키는 24–128자 ASCII 영숫자입니다. QR에는 서버 제어 권한이 포함되며 공유 대상을 제한해야 합니다. Android 앱은 스캔한 주소를 표시하고 사용자가 연결한 경우에만 인증 요청을 전송합니다.

## 원본 PCM 전송

`ws://<host>:17842/v1/audio` 연결 후 첫 **텍스트 메시지**:

```json
{
  "version": 1,
  "deviceId": "cccccccc-cccc-4ccc-8ccc-cccccccccccc",
  "name": "My phone",
  "format": { "sampleRate": 48000, "channels": 2, "encoding": "pcm16" }
}
```

이름 1–64자, sampleRate 8000–192000 Hz, channels 1/2, encoding `pcm16` / `float32`. PCM little-endian, 스테레오는 L/R 인터리브. 입력 형식은 한 연결 동안 고정합니다. 형식이 변경되면 새로 연결합니다. 최대 16개 소스.

v0.3.3부터 `deviceId`는 전송 앱이 저장해 재사용하는 무작위 GUID입니다. null/생략은 이전 클라이언트 호환용이며 설정을 영구 복원하지 않습니다. 빈 GUID/잘못된 GUID는 거부합니다. 이 ID는 인증 수단이 아니며 기존 `X-Sound-Key` 검사를 그대로 적용합니다. 이름이 같아도 ID가 다르면 별도 기기로 취급합니다.

Windows 서버는 이 ID별 음량·방향·공간 효과·음소거를 로컬 파일에 저장합니다. 재연결과 서버 재시작 후 설정을 `AddSource` 시점에 적용해 첫 PCM부터 복원합니다. 동일 ID의 새 연결은 아직 남아 있는 이전 연결과 버퍼를 교체합니다. `sourceId`는 여전히 연결마다 새로 발급하며 활성 연결 제어에 사용합니다. 연결 종료는 활성 소스만 제거하며 저장된 설정은 삭제하지 않습니다.

서버 응답 **텍스트 메시지**:

```json
{ "sourceId": "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx" }
```

이후 각각의 **바이너리 메시지**는 캡처 PCM입니다. 메시지 크기는 1–65536 bytes이며 완전한 PCM 프레임으로 구성합니다. 여러 WebSocket fragment에 나누어 전송할 수 있습니다. TCP 순서로 재생하며 클라이언트는 캡처 데이터에 별도의 음량/EQ/샘플레이트 변경을 적용하지 않습니다.

무음 시 OS 캡처 콜백이 발생하지 않는 Windows 클라이언트는 3초마다 정확히 다음 텍스트 메시지를 전송합니다. 이는 PCM 데이터에 포함되지 않습니다.

```json
{"type":"keepalive"}
```

메시지 수신에 10초 이상 걸리면 연결을 정리합니다. 잘못된 형식/프레임/크기/소스 한도 초과는 WebSocket close code `1007`. 클라이언트는 정상 종료 때 `1000`을 사용하며 서버는 종료/연결 손실/오류 시 등록한 소스를 제거합니다. 수동 재연결 시 새 sourceId가 발급됩니다.

타임스탬프·sequence·압축은 v1에 없습니다. 서로 다른 기기에서 같은 음원을 보내는 경우 샘플 단위 동기화를 보장하지 않습니다. 48 kHz stereo float는 약 3.072 Mbps, PCM16은 약 1.536 Mbps(헤더 제외)입니다.

## 상태 및 제어

`GET /v1/state`

```json
{
  "settings": { "masterVolume": 0.7, "bassDb": 0, "trebleDb": 0, "presenceDb": 0 },
  "sources": [
    {
      "id": "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
      "name": "My phone",
      "format": { "sampleRate": 48000, "channels": 2, "encoding": "pcm16" },
      "settings": { "volume": 1, "azimuth": 0, "spatial": false, "muted": false },
      "bufferedMs": 60,
      "droppedFrames": 0,
      "underruns": 0
    }
  ],
  "clippedSamples": 0
}
```

`PUT /v1/mix`, `Content-Type: application/json`:

```json
{ "masterVolume": 0.7, "bassDb": 6, "trebleDb": 0, "presenceDb": 0 }
```

volume 0–1, 각 EQ -12–12 dB. 전체 필드를 전송합니다. 성공 시 `200`과 갱신된 상태, 잘못된 값/JSON은 `400`. Windows/Android UI에서 동시에 조작하면 마지막 요청을 적용합니다.

`PUT /v1/sources/{sourceId}`:

```json
{ "volume": 1, "azimuth": 90, "spatial": true, "muted": false }
```

volume 0–2, azimuth 0 이상 360 미만. 0 앞, 90 오른쪽, 180 뒤, 270 왼쪽. 성공 `200`, 존재하지 않는 ID `404`, 잘못된 값 `400`. 전체 필드를 전송합니다. 공간 처리와 EQ는 항상 서버에서 수행합니다.

안정된 `deviceId`가 있는 기기는 저장이 성공한 뒤에만 변경을 적용하고 `200`을 반환합니다. 파일 저장 실패는 `503`이며 이전 설정을 유지합니다. 클라이언트의 이름·IP·연결 키를 설정 조회 키로 사용하지 않습니다. 비활성 기기의 설정은 서버에 보관하지만 `/v1/state`의 `sources`에는 현재 연결된 기기만 포함합니다.

v0.4.0은 `settings.displayName`에 사용자 지정 이름을 저장하고 `sources[].name`에 표시할 최종 이름을 반환합니다. null이면 캡처 원래 이름을 사용합니다. 이름은 공백만이 아닌 1–64자이며 제어 문자를 거부합니다. 이전 저장 파일은 null로 읽습니다. PUT에서 null/생략한 이름은 기존 별칭을 유지합니다.

`PATCH /v1/sources/{sourceId}`는 전송한 항목만 원자적으로 변경·저장합니다. 지원 항목은 `volume`, `azimuth`, `spatial`, `muted`, `displayName`, `resetName`입니다. 예: `{"muted":true}`, `{"volume":0.5}`, `{"displayName":"음악 탭"}`. `{"resetName":true}`로 별칭을 지웁니다. resetName과 displayName을 함께 지정하면 `400`입니다. Windows 서버 화면도 같은 저장/검증 경로를 사용합니다.

## 브라우저 탭 PCM

브라우저 확장은 `ws(s)://<server>/v1/browser-audio`에 연결합니다. 일반 웹페이지 Origin은 `403`이며 Chromium 확장 Origin만 허용합니다. 브라우저 WebSocket은 사용자 지정 헤더를 지원하지 않으므로 이 경로만 첫 텍스트 프레임의 `key`로 인증합니다. 주소/쿼리에 키를 넣지 않습니다. 첫 메시지는 기존 AudioHello 필드와 key이며 실제 AudioContext의 sampleRate, 1/2 채널, float32 LE를 선언합니다.

인증 실패는 소스를 등록하지 않고 close `1008`로 종료합니다. 인증 전 제한 시간·최대 메시지는 기존과 같습니다. 인증 후 ready sourceId와 바이너리 PCM·keepalive·설정 제어는 동일합니다. 기존 `/v1/audio`와 HTTP 제어는 계속 X-Sound-Key를 요구합니다. 확장 Origin 검사 자체는 인증을 대신하지 않습니다.

## 버퍼 복구와 서버 계측

`bufferedMs`는 입력 PCM 링 버퍼 프레임 수를 입력 샘플레이트로 나눈 서버 내부 대기량입니다. 네트워크 왕복 시간이나 종단 간 오디오 지연이 아닙니다. 시작/재버퍼링 기준은 40 ms이며, 목표 대기량은 60 ms입니다. 렌더 시작 시 `max(120 ms, 이번 요청에 필요한 입력 프레임 + 60 ms)`를 넘으면 오래된 프레임을 버리고 이번 요청 분량과 약 60 ms를 남깁니다. ±0.2% 속도 보정은 작은 클럭 드리프트에만 사용합니다. 출력 정지 중에도 메모리는 약 500 ms로 제한됩니다. 버퍼 축소는 이전 오디오를 건너뛰므로 해당 구간은 연속 재생되지 않습니다.

`GET /v1/state`에는 기존 필드를 유지하면서 다음 읽기 전용 객체를 추가합니다. 모든 시간은 ms이며 단조 증가하는 서버 시계를 사용합니다. 측정 전의 최근 간격/경과 시간은 `null`, 누적 최대값과 횟수는 0입니다.

| 위치 | 필드 | 의미 |
| --- | --- | --- |
| `sources[].diagnostics` | `receivedPackets`, `lastPacketAudioMs` | 완성된 PCM 메시지 수와 최근 메시지의 오디오 분량. 핸드셰이크/keepalive/개별 fragment는 제외 |
| 동일 | `lastReceiveIntervalMs`, `maxReceiveIntervalMs` | PCM 메시지가 `Mixer.Push`에 도착한 간격 최근/최대 |
| 동일 | `maxReceiveExcessMs` | `수신 간격 - 이번 패킷 오디오 분량`의 양수 최대. 송신 타임스탬프가 없어 순수 네트워크 지터와 다름 |
| 동일 | `lastReceiveAgoMs` | 최근 PCM 도착 이후 경과 시간. 무음으로 전송이 없는 경우에도 증가 |
| 동일 | `lastIngestMs`, `maxIngestMs` | 입력 잠금 대기와 PCM 디코딩/링 기록을 합친 처리 시간 최근/최대 |
| 동일 | `peakBufferedMs` | 수신 직후 관측한 서버 링 버퍼 대기량 최대 (메모리 상한 적용 후) |
| 동일 | `latencyTrims`, `latencyTrimmedFrames` | 지연 복구 횟수/의도적으로 버린 입력 프레임 수. 기존 `droppedFrames`에도 포함되므로 용량 초과 폐기는 두 값의 차이 |
| 동일 | `targetBufferMs`, `trimThresholdMs` | 목표 60 ms와 기본 축소 기준 120 ms. 큰 출력 요청에는 위 보정 적용 |
| 최상위 `diagnostics` | `renderCalls`, `lastRequestedAudioMs` | 전체 믹서 렌더 호출 수/최근 요청 오디오 분량 |
| 동일 | `lastRenderIntervalMs`, `maxRenderIntervalMs` | 출력 요청 시작 간격 최근/최대 |
| 동일 | `maxRenderDelayMs`, `lateRenderCalls` | 이전 요청 오디오 분량을 초과한 양수 공백 최대/그 공백이 20 ms를 초과한 횟수 |
| 동일 | `lastRenderAgoMs` | 최근 출력 요청 시작 이후 경과 시간 |
| 동일 | `lastRenderWorkMs`, `maxRenderWorkMs` | 소스 믹싱/EQ/클리핑을 포함한 렌더 처리 시간 최근/최대 |

소스 계측은 소스 연결마다, 출력 계측은 `Mixer` 생성마다 초기화합니다 (Windows에서는 앱 실행마다). 진단 조회는 카운터를 초기화하지 않습니다. 출력 요청은 WASAPI가 요청한 블록을 믹서가 채우는 시점으로, OS/Bluetooth의 실제 청취 시점이 아닙니다. 파일 기록이나 네트워크 전송은 오디오 처리 스레드에 추가하지 않습니다. 진단 화면의 **진단 복사**로 장애 전후 값을 보관할 수 있습니다.

## DSP 순서

`입력별 PCM decode → bounded buffer → 서버 레이트 변환 → 선택적 가상 방향 → 소스 음량/음소거 → 합산 → 저음/고음/명료도 EQ → 전체 음량 → hard clip → WASAPI → OS Bluetooth 출력`

방향 기능은 ITD/ILD 및 후방 필터를 사용한 근사입니다. HRTF 모델·가상 스피커 배열·공간 오디오 코덱을 구현하지 않습니다. Bluetooth A2DP 등 무선 전송 자체는 Windows가 담당합니다.
