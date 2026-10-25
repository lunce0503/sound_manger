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
python scripts/publish-release.py --version 0.4.1 --notes docs/releases/v0.4.1.md --commit <커밋SHA>
```

이 스크립트는 GitHub Releases API를 사용합니다. GitHub 커넥터에는 릴리스 생성/파일 업로드 기능이 없어 이 경로를 사용합니다. 인증 정보는 환경 변수 `GH_TOKEN`/`GITHUB_TOKEN` 또는 Git Credential Manager에서 메모리로 읽고 저장·출력하지 않습니다. 초안을 생성하고 자산 업로드·크기·digest 검증이 성공한 경우에만 시험 릴리스를 공개합니다. 같은 버전이 이미 있으면 실패하며 기존 배포를 덮어쓰지 않습니다.

Windows 바이너리 게시자 인증서 서명, Google Play 및 Microsoft Store 게시는 이 파이프라인에 포함하지 않습니다.

## 자동 업데이트 배포 계약

v0.4.1부터 `release.ps1`은 EXE·APK를 만든 뒤 파일명·SHA256·크기·Android 버전 코드를 담은 `update.json`과 Base64 `update.json.sig`를 생성합니다. 기존 Android JKS의 RSA/SHA256 키로 정확한 JSON 바이트를 서명합니다. `SignUpdate.java`는 원래 인증서 지문과 두 앱에 고정한 공개키를 확인하며 개인키를 내보내지 않습니다. JKS·DPAPI 암호 파일을 교체하지 마세요.

버전을 올릴 때 `Directory.Build.props`, `android/app/build.gradle`의 versionName·versionCode, `browser-extension/manifest.json`, 두 패키징 스크립트 기본 버전을 함께 갱신합니다. 커밋한 소스에서 릴리스 빌드를 만들고 검증한 뒤 푸시·CI 성공을 확인하여 게시합니다. `publish-release.py`는 서명 정보 두 파일도 필수 자산으로 업로드한 뒤 공개합니다. 숫자 `vMAJOR.MINOR.PATCH` 태그의 현재 시험 릴리스 채널을 사용하며 초안·낮은 버전·서명 정보가 없는 릴리스는 업데이트 후보에서 제외합니다.

앱은 고정 공식 GitHub 저장소에서만 메타데이터를 받고, GitHub HTTPS 자산 호스트로의 리다이렉트만 허용합니다. 변조되거나 불완전한 다운로드는 실행하지 않습니다. Windows는 이전 앱이 살아 있는 동안 새 EXE를 검증·추출한 다음 음원 연결 상태를 다시 검사하고 종료를 요청합니다. 새 앱의 시작 응답이 없으면 자신이 시작한 자식만 종료하고 이전 앱을 실행합니다. 이전 버전 폴더는 복구용으로 보존합니다. 다운로드한 v0.4.1 이상 EXE를 다시 실행하면 기록된 더 최신 버전을 엽니다.

Android는 동일 패키지·증가한 versionCode·원래 인증서를 추가 검사하고, 앱 전용 APK만 읽을 수 있는 일회성 URI 읽기 권한으로 시스템 설치 화면에 전달합니다. 설치 승인·알 수 없는 앱 설치 허용은 사용자가 시스템 화면에서 수행합니다. 스토어를 거치지 않는 APK 배포입니다.
