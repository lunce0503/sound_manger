# v1.0.0 자동 검증 기록

날짜: 2026-11-05 (KST). Windows x64, Rust 1.99.1, .NET 10.
개인 녹음 대신 합성 PCM과 격리된 브라우저 프로필을 사용했습니다.

## 기능·호환성

| 검증 | 결과 |
| --- | --- |
| C# 코어·서버 회귀 | 71개 통과 |
| Windows UI·트레이·자동 시작·업데이트·Rust 제어 | 27개 통과 |
| Rust 코어·DSP 품질·REST/WebSocket | 27개 통과; fmt/clippy 경고 없음 |
| C# 결정적 PCM 기준 출력 | 40개 일치; PCM 허용 오차 0.00002, 프레임·언더런·trim 카운터 정확 일치 |
| 브라우저 단위 | 7개 통과 |
| 실제 Chrome 탭 캡처 | 독립 2탭 원본 PCM, 팝업 닫기, 음량·음소거·이름, idle 확장 갱신·페어링 복원 통과 |
| Android 릴리스 JVM/Robolectric | 28개 통과; native Rust API/OkHttp PCM 전송·제어·재접속 포함; skipped 0 |
| Android release lint | No issues found |
| 실제 외부 연결 | HTTPS 200·무인증 401, 일반·브라우저 WSS, Rust DSP 수신과 EQ·음량 제어 통과 |
| 출력 장치 | 저장된 Galaxy Buds2의 Rust WASAPI 초기화·종료를 음량 0으로 확인; 실제 청취 평가는 아님 |

원래 40개 binary golden fixture는 바뀌지 않았습니다. 새 AutoGain/Processing 및
출력 큐 부족 진단 필드를 C# 계약에 추가하면서 Contracts.cs의 기준 해시만 갱신했습니다.

## 장시간 검증

같은 PC의 별도 Rust/C# 서버 프로세스에 각각 10개의 합성 소스를 전송합니다.
송신 샘플 클록 편차는 ±1000 ppm, 메시지는 약 20 ms, headless 출력은 10 ms입니다.
각 프로세스의 CPU·private memory·버퍼·언더런·폐기·trim을 10초마다 측정합니다.
CPU는 전체 PC 비율이 아닌 코어 하나를 100%로 계산한 값입니다.
Bluetooth 실제 출력 지연이나 청취 품질을 측정하는 시험은 아닙니다.

첫 시험은 빌드·패키지 준비를 병행한 상태에서 1804.25초를 완료했습니다.
25분 무렵 두 backend 모두 약 71 ms의 출력 요청 간격을 기록했고, Rust의 10개
소스가 한 차례씩 재버퍼링했습니다. Rust 누적 underrun 10, 폐기 35,700 프레임,
trim 14회였습니다. 이후 남은 구간에는 추가 underrun이 없었습니다.
같은 시험의 C# 기준은 underrun 268, 폐기 243,496 프레임, trim 99회였습니다.
Rust private memory 증가는 348,160 bytes로 제한적이었으나 **무손실 기준을
통과한 시험으로 처리하지 않았습니다**. 원인을 단정하지 않고 PC 전체의 스케줄링
지연 가능성을 확인한 뒤, 모든 큰 로컬 빌드를 끝내고 최종 패키지로 재검증합니다.

최종 패키지의 Rust 서버로 큰 로컬 빌드 없이 재검증한 결과 **1804.15초 통과**입니다. 판정 스크립트는
`scripts/summarize-soak.py`입니다. 1800초 이상·10소스 유지·지속 PCM 수신,
warm-up 이후 private memory 증가 16 MiB 이하, Rust underrun·폐기·trim 0을 요구합니다.
C# 결과는 기존 구현과의 비교값이며 이 구현의 알려진 정수 클록 보정 동작을 보존합니다.

| 최종 재검증 지표 | Rust | C# 기준 |
| --- | ---: | ---: |
| 유지한 소스 | 10 | 10 |
| 수신 PCM 메시지 | 902,410 | 902,080 |
| underrun / 폐기 프레임 / trim | 0 / 0 / 0 | 226 / 241,972 / 100 |
| CPU 중앙값 (코어 하나=100%) | 4.53% | 7.11% |
| 최종 private memory | 6.33 MiB | 25.26 MiB |
| warm-up 이후 private memory 증가 | 176,128 bytes | 462,848 bytes |
| 샘플링된 입력 버퍼 범위 | 15.65–91.40 ms | 11.75–120.06 ms |
| 최대 믹싱 처리 시간 | 0.885 ms | 2.178 ms |

프로세스 단위·합성 입력·해당 PC의 한 번 측정입니다. 전체 WPF 앱 메모리나 다른 PC의
성능 개선 비율로 일반화하지 않습니다. 서로 다른 소스의 합계 카운터입니다.
[원시 최종 기록](validation/v1.0.0/soak-final-30min.jsonl),
[요약 JSON](validation/v1.0.0/soak-final-summary.json),
[첫 시험 기록](validation/v1.0.0/soak-busy-30min.jsonl)을 함께 보존합니다.

측정한 최종 native 서버 SHA256은
`55680bc88dc0b1c54599689568ba2360727562943bfb3ca39c2c38350b0c1eff`입니다.
그 뒤 변경한 WPF 제어 상태 순서 보장·외부 터널 보존은 native DSP를 변경하지 않습니다.
최종 재패키징 때 native SHA256이 이 값과 일치하는지 다시 검사합니다.

## 패키지·업데이트

Windows EXE를 `--extract-only`로 실행하고 647개 payload 파일을 배포 폴더와
SHA256으로 비교했습니다. native 서버는 `backend=rust, version=1.0.0`으로
시작하며 부모 stdin EOF 후 정상 종료했습니다. EXE 아이콘도 추출해 확인했습니다.
바탕화면·시작 메뉴의 Sound Manager 바로가기는 검증한 EXE와 전용 ICO를 가리킵니다.

Android package는 `com.lunce.soundmanager`, versionCode 9, versionName 1.0.0입니다.
`apksigner verify`로 기존 RSA 3072-bit 인증서와 APK v2 서명을 확인했습니다.
인증서 SHA256:
`7217017ad413a98cc79e74bf44a6d4e13c93852ca32ef538e75881db1cda7687`.

앱에 고정된 공개키로 update.json 서명과 EXE·APK 파일 해시·크기를 검증했습니다.
게시한 공개 feed는 다음 명령으로 기존 0.4.1 사용자의 업데이트 경로와 동일하게 검증합니다.

```powershell
dotnet run --project tools/SoundManager.UpdateCheck -c Release -- --feed artifacts/verified-public-v1.0.0
```

Rust sidecar만 종료·재시작한 뒤에도 기존 Cloudflare 터널 주소와 같은 키로 HTTPS와
WSS 연결을 복원하는 실제 네트워크 검사도 통과했습니다. 늦은 상태 응답이 완료된
음소거를 되돌리지 않는 Windows 회귀 테스트를 포함합니다.
로컬 테스트와 별도로 GitHub Windows·Android·Rust CI를 실행합니다.

사용자 지시에 따라 Android 실기기 캡처·재생 검증은 추후 진행합니다.
자동 음량 보정과 리미터는 디지털 신호 처리이며 실제 60 dB SPL 측정·제한은 아닙니다.
