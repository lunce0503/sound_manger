# Sound Manager protocol v1

LAN 기본 주소 `http://<Windows IPv4>:17842`. 외부 모드에서는 Cloudflare 임시 터널의 `https://<name>.trycloudflare.com` 주소와 WSS를 사용합니다. 모든 HTTP 요청과 WebSocket 업그레이드 요청에 `X-Sound-Key: <서버의 연결 키>`를 포함합니다. 누락/불일치는 `401`. 서버 재시작마다 192-bit 무작위 키(48자 hex)가 생성됩니다. 외부 모드의 TLS는 Cloudflare에서 종료되고 로컬 서버로는 loopback HTTP를 사용합니다.

## QR 연결 정보 v1

`soundmanager://pair?v=1&server=<percent-encoded base http(s) URL>&key=<connection key>`

정확히 `v`, `server`, `key` 세 필드를 포함하며 중복 필드·지원하지 않는 버전·다른 호스트/경로·fragment를 거부합니다. 주소는 루트 URL만 허용합니다. HTTP는 사설/LAN/loopback 및 CGNAT VPN 주소에만 허용하며 공개 도메인/IP에는 HTTPS가 필요합니다. 연결 키는 24–128자 ASCII 영숫자입니다. QR에는 서버 제어 권한이 포함되며 공유 대상을 제한해야 합니다. Android 앱은 스캔한 주소를 표시하고 사용자가 연결한 경우에만 인증 요청을 전송합니다.

## 원본 PCM 전송

`ws://<host>:17842/v1/audio` 연결 후 첫 **텍스트 메시지**:

```json
{
  "version": 1,
  "name": "My phone",
  "format": { "sampleRate": 48000, "channels": 2, "encoding": "pcm16" }
}
```

이름 1–64자, sampleRate 8000–192000 Hz, channels 1/2, encoding `pcm16` / `float32`. PCM little-endian, 스테레오는 L/R 인터리브. 입력 형식은 한 연결 동안 고정합니다. 형식이 변경되면 새로 연결합니다. 최대 16개 소스.

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

## DSP 순서

`입력별 PCM decode → bounded buffer → 서버 레이트 변환 → 선택적 가상 방향 → 소스 음량/음소거 → 합산 → 저음/고음/명료도 EQ → 전체 음량 → hard clip → WASAPI → OS Bluetooth 출력`

방향 기능은 ITD/ILD 및 후방 필터를 사용한 근사입니다. HRTF 모델·가상 스피커 배열·공간 오디오 코덱을 구현하지 않습니다. Bluetooth A2DP 등 무선 전송 자체는 Windows가 담당합니다.
