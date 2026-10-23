# 릴리스와 서명 키

개발 빌드: `scripts/build.ps1`. 릴리스 패키지: `scripts/release.ps1` (Windows PowerShell 7, .NET 10 SDK, JDK/Android SDK 필요).

## Android 서명

첫 릴리스 빌드가 `%LOCALAPPDATA%/SoundManager/release-signing/release.jks`를 생성합니다. 암호는 같은 폴더의 `keystore-password.dpapi`에 Windows 사용자별 DPAPI로 암호화해 보관합니다. 다음 릴리스는 같은 키를 재사용합니다. **둘 다 Git·릴리스 자산에 올리지 않습니다.** 키를 잃으면 기존 릴리스 앱의 업데이트 서명이 달라집니다.

안전한 별도 저장소에 키를 백업하세요. DPAPI 파일은 생성한 Windows 사용자/환경에 묶이므로 다른 PC로 이관할 때는 원래 환경에서 비밀번호를 안전하게 이관하는 별도 작업이 필요합니다. 현재 빌드 도구는 평문 암호를 출력하지 않습니다.

CI나 다른 환경에서는 다음 환경 변수로 기존 키를 제공할 수 있습니다. CI 저장소의 암호화된 secret 등을 사용하며 값 자체를 커밋하지 않습니다.

- `SM_KEYSTORE_PATH`
- `SM_KEYSTORE_PASSWORD`
- `SM_KEY_ALIAS` (기본 `soundmanager`)
- `SM_KEY_PASSWORD`

```powershell
cd android
.\gradlew.bat assembleRelease lintRelease testReleaseUnitTest
```

키를 제공하지 않은 `assembleRelease` 결과는 **unsigned APK**입니다. `release.ps1`은 키를 제공하고 서명된 `app-release.apk`만 패키징합니다. Android `apksigner verify --verbose --print-certs`로 배포할 APK를 확인합니다.

## 공개 배포

Windows는 항상 **단일 EXE**를 배포합니다. `package-windows.ps1`은 내부 ZIP을 SHA256으로 확인하는 .NET 런처에 포함하여 self-contained EXE를 만듭니다. 실행 시 `%LOCALAPPDATA%/SoundManager/versions/<버전-해시>`에 풀고 앱을 시작하며 설치 도구나 관리자 권한은 필요하지 않습니다. ZIP을 Windows 다운로드 자산으로 올리지 않습니다. `--extract-only <폴더>` 옵션으로 하드웨어를 열지 않고 패키지 추출을 검증할 수 있습니다.

릴리스 APK와 Windows EXE, 체크섬을 생성·검증하고 해당 소스를 커밋·푸시한 뒤:

```powershell
python scripts/publish-release.py --version 0.3.1 --notes docs/releases/v0.3.1.md --commit <커밋SHA>
```

이 스크립트는 GitHub Releases API를 사용합니다. GitHub 커넥터에는 릴리스 생성/파일 업로드 기능이 없어 이 경로를 사용합니다. 인증 정보는 환경 변수 `GH_TOKEN`/`GITHUB_TOKEN` 또는 Git Credential Manager에서 메모리로 읽고 저장·출력하지 않습니다. 초안을 생성하고 자산 업로드·크기·digest 검증이 성공한 경우에만 시험 릴리스를 공개합니다. 같은 버전이 이미 있으면 실패하며 기존 배포를 덮어쓰지 않습니다.

Windows 바이너리 게시자 인증서 서명, Google Play 및 Microsoft Store 게시는 이 파이프라인에 포함하지 않습니다.
