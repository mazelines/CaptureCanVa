# CaptureCanva

[![CI/CD](https://github.com/mazelines/CaptureCanVa/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/mazelines/CaptureCanVa/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/mazelines/CaptureCanVa)](https://github.com/mazelines/CaptureCanVa/releases/latest)

[![CaptureCanva 프로모션 배너 — 필요한 화면만. 소리까지 함께.](docs/assets/capturecanva-promotion-banner.png)](https://github.com/mazelines/CaptureCanVa/releases/latest)

Windows용 화면 녹화 앱입니다. 전체 화면, 특정 창, 지정한 영역을 MP4로 녹화하고, 녹화가 끝나면 블로그·보고서에 사용할 GIF도 함께 생성합니다. MP4에는 시스템 소리와 마이크 음성을 포함할 수 있습니다.

## 주요 기능

- **녹화 대상 선택**: 모니터 전체, 특정 창, 마우스로 지정한 영역
- **영상 설정**: 15 / 24 / 30 / 60 fps, 높음 / 보통 / 낮음 화질
- **오디오 녹음**: 시스템 소리와 기본 마이크를 각각 선택하거나 함께 녹음
- **인코더 자동 선택**: NVIDIA NVENC → Intel Quick Sync → AMD AMF 순으로 사용 가능 여부를 확인하고, 사용할 수 없으면 소프트웨어 x264 사용
- **녹화 제어**: 시작·중지, 일시 정지·재개, 전역 단축키
- **화면 옵션**: 마우스 커서 포함 여부와 CaptureCanva 창 숨기기
- **GIF 자동 저장**: 녹화 완료 후 같은 이름의 GIF 생성, 용도별 프리셋 선택, 변환 취소
- **메이즈라인 홈페이지**: 앱 하단 홍보 배너에서 [메이즈라인](https://www.mazeline.tech/) 홈페이지 열기
- **설정 저장**: 녹화 옵션, 저장 폴더, 마지막으로 지정한 영역을 다음 실행에 복원
- **YouTube 업로드 (실험적)**: 환경설정에서 계정을 연결하고, 녹화 완료 후 MP4 업로드·취소·완료 링크 확인
- **단축키 변경**: 환경설정에서 키 조합 지정·해제, 중복 및 다른 앱과의 충돌 확인

영상은 H.264로 저장하며, 오디오를 포함하면 AAC로 인코딩합니다.

## 다운로드 및 실행

### 실행 환경

- Windows 10/11 x64
- Windows.Graphics.Capture와 Direct3D 11을 지원하는 환경

[최신 릴리스](https://github.com/mazelines/CaptureCanVa/releases/latest)의 `CaptureCanva-v<버전>-win-x64.zip`을 내려받아 압축을 풀고 `CaptureCanva.exe`를 실행합니다.

YouTube 업로드와 단축키 설정은 [v0.3.0 릴리스](https://github.com/mazelines/CaptureCanVa/releases/tag/v0.3.0)에서 사용할 수 있습니다. 실제 Google 계정 검증이 남아 있어 YouTube 연동은 실험적 기능으로 제공합니다.

**v0.1.2부터 .NET 런타임과 FFmpeg를 함께 제공합니다.** 별도 설치나 `PATH` 설정 없이 바로 사용할 수 있습니다. ZIP의 파일과 폴더를 함께 유지하세요.

```text
CaptureCanva/
├── CaptureCanva.exe
├── ffmpeg.exe
├── THIRD-PARTY-NOTICES.md
└── licenses/
    └── ffmpeg/
```

앱은 실행 파일과 같은 폴더의 FFmpeg를 먼저 찾습니다. 함께 제공하는 FFmpeg의 라이선스와 소스 정보는 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)와 ZIP의 `licenses/ffmpeg` 폴더에 있습니다.

## 사용 방법

1. `CaptureCanva.exe`를 실행하고 인코더 확인이 끝날 때까지 기다립니다.
2. 녹화 대상을 선택합니다.
   - **전체 화면**: 녹화할 모니터를 선택합니다.
   - **특정 창**: 목록에서 창을 선택합니다. 필요한 경우 **새로고침**을 누릅니다.
   - **영역 지정**: **영역 선택…**을 누르고 한 모니터 안에서 드래그합니다. `Esc` 또는 마우스 오른쪽 버튼으로 선택을 취소할 수 있습니다.
3. 프레임, 화질, 마우스 커서, 시스템 소리, 마이크 옵션과 GIF 저장 프리셋을 설정합니다.
4. 저장 폴더를 확인하고 **녹화 시작**을 누릅니다.
5. 필요하면 **일시정지**와 **계속**을 사용합니다. **녹화 중지**를 누른 뒤 저장이 끝날 때까지 기다립니다.
6. MP4 저장이 끝나면 GIF 변환이 자동으로 진행됩니다. 완료 후 MP4·GIF 파일 링크 또는 저장 폴더의 **열기** 버튼으로 결과를 확인합니다.

기본 저장 위치는 Windows의 **동영상** 폴더 아래 `CaptureCanva`이며, 파일 이름은 `CaptureCanva_yyyyMMdd_HHmmss.mp4` 형식입니다.

### GIF 함께 저장

v0.2.0부터 **녹화 완료 후 GIF 함께 저장**은 기본으로 켜져 있습니다. MP4를 먼저 저장한 뒤, 같은 폴더에 `CaptureCanva_yyyyMMdd_HHmmss.gif`를 생성합니다. MP4만 필요하면 이 옵션을 끄면 됩니다.

`movie2gif`의 2단계 팔레트 변환 방식과 웹용 프리셋을 통합했습니다.

| 프리셋 | 최대 가로 크기 | 프레임 | 용도 |
| --- | --- | --- | --- |
| 문서용 | 360px | 10 fps | 보고서·문서 삽입 |
| 블로그용 (기본) | 480px | 12 fps | 블로그·SNS 업로드 |
| 고화질 | 640px | 15 fps | 제품 시연·포트폴리오 |
| 원본 크기 | 녹화 해상도 유지 | 15 fps | 화면 글자·세부 정보 보존 |

화면 비율을 유지하며, 프리셋보다 작은 영상은 확대하지 않습니다. 전체 녹화 구간을 소리 없는 반복 GIF로 변환하므로 길이와 화면 변화에 따라 파일 크기가 달라집니다. 완료 메시지에 GIF 용량을 표시합니다.

변환 중 **GIF 변환 취소**를 누르면 MP4만 남깁니다. GIF 생성에 실패해도 MP4는 보존하며, 다시 녹화할 수 있습니다. GIF 옵션과 프리셋은 다음 실행에도 유지됩니다.

### 단축키

| 동작 | 기본 단축키 |
| --- | --- |
| 녹화 시작 / 중지 | `F12` |
| 일시 정지 / 재개 | `Shift+F12` |

다른 프로그램이 단축키를 사용 중이면 다음 조합을 순서대로 시도합니다.

| 녹화 시작 / 중지 | 일시 정지 / 재개 |
| --- | --- |
| `F9` | `Shift+F9` |
| `F9` | `Ctrl+F9` |
| `Ctrl+Shift+F9` | `Ctrl+Shift+F10` |

앱 하단에 표시된 단축키가 실제 등록된 조합입니다. 두 단축키를 함께 등록할 수 없으면 녹화 시작·중지 단축키만 등록할 수 있으며, 등록에 실패한 경우 앱의 버튼으로 제어합니다.

**환경설정 → 단축키**에서 입력 상자를 클릭하고 키 조합을 누른 뒤 **단축키 적용**을 선택합니다. **지우기**로 해제한 키는 다음 실행에도 해제 상태를 유지합니다. 등록이나 저장에 실패하면 기존 설정을 유지하고 오류를 표시합니다.

### YouTube 계정 연결과 업로드 (v0.3.0, 실험적)

v0.3.0에 계정 연결과 업로드를 추가했습니다. 모의 HTTP·로컬 인증 콜백·Windows UI 검사를 수행했으며, 실제 Google 계정으로 로그인·갱신·업로드하는 검증은 OAuth 클라이언트 준비 후 진행해야 합니다.

Google 계정 연결에는 [Google의 데스크톱 앱 OAuth 클라이언트](https://developers.google.com/identity/protocols/oauth2/native-app)가 필요합니다. Google Cloud 프로젝트에서 YouTube Data API v3를 활성화하고 **데스크톱 앱** 유형의 OAuth 클라이언트 JSON을 내려받습니다. 파일 이름을 `google-oauth-client.json`으로 바꿔 실행 파일 폴더 또는 `%APPDATA%\CaptureCanva`에 넣고 앱을 다시 실행합니다. Google이 제공하는 `installed.client_id` 형식과 `client_id`를 바로 넣는 형식을 지원합니다.

1. **환경설정 → 계정 연동**에서 YouTube **연결**을 누르고 기본 브라우저에서 승인합니다.
2. 메인 화면에서 **YouTube 업로드**를 선택합니다. YouTube는 기본으로 **비공개**입니다.
3. 녹화가 끝나면 MP4·GIF 저장 후 선택한 서비스로 MP4를 업로드합니다. 전송량·취소 버튼·완료 링크를 표시합니다.

업로드 실패나 취소 시 로컬 MP4·GIF를 보존합니다. 계정 토큰은 Windows 현재 사용자 범위(DPAPI)로 암호화해 저장합니다. OAuth 클라이언트가 없어도 녹화·GIF 기능을 사용할 수 있으며, 연결 버튼은 설정 안내를 표시합니다. 실제 계정 연결·업로드는 OAuth 클라이언트 준비 후 별도로 확인해야 합니다.

#### 개발자 참고: 비활성화된 Google Drive 연동

Google Drive 업로드 구현(`GoogleDriveUploader`, 관련 토큰 처리와 오프라인 검사)은 코드베이스에 유지되지만 `DriveFeatureGate.Enabled = false`(src/CaptureCanva/Accounts/DriveFeatureGate.cs)로 실행 경로가 차단되어 있어 UI에 노출되지 않습니다. 다시 켜려면 세 가지를 모두 변경해야 합니다: (1) `DriveFeatureGate.Enabled = true`, (2) MainWindow.xaml의 `UploadDriveCheck`를 `Visibility="Visible"`, (3) Accounts/SettingsWindow.xaml의 `DriveSection`을 `Visibility="Visible"`. Drive 업로드를 실제로 사용하려면 Google Cloud에서 Drive API도 활성화해야 합니다.

## 소스에서 빌드

Windows에 [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)와 Git을 설치합니다. 소스에서 실행할 때는 FFmpeg를 별도로 준비합니다. [FFmpeg 다운로드 페이지](https://ffmpeg.org/download.html)에서 Windows 빌드를 내려받아 `ffmpeg.exe`가 들어 있는 폴더를 `PATH`에 추가하거나, 아래 배포 명령으로 FFmpeg가 포함된 패키지를 생성합니다.

저장소를 복제하고 빌드합니다.

```powershell
git clone https://github.com/mazelines/CaptureCanVa.git
cd CaptureCanVa
dotnet restore CaptureCanva.slnx
dotnet build CaptureCanva.slnx -c Debug --no-restore
dotnet run --project src/CaptureCanva/CaptureCanva.csproj
```

### 배포용 실행 파일 생성

다음 명령으로 .NET 런타임을 포함한 Windows x64 실행 파일과 FFmpeg를 `publish` 폴더에 생성합니다. PowerShell에서 실행하세요.

```powershell
dotnet publish src/CaptureCanva/CaptureCanva.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish
./scripts/Bundle-Ffmpeg.ps1 -OutputDirectory publish
Compress-Archive -Path publish/* -DestinationPath CaptureCanva-win-x64.zip
```

FFmpeg 준비 스크립트는 고정 버전의 다운로드와 SHA-256 검증을 수행하고, 라이선스 안내를 복사한 뒤 H.264·AAC 인코딩을 확인합니다. 생성된 ZIP을 풀면 별도 설정 없이 실행할 수 있습니다.

## 프로젝트 구성

| 경로 | 역할 |
| --- | --- |
| [`src/CaptureCanva/MainWindow.xaml`](src/CaptureCanva/MainWindow.xaml) | 녹화 대상, 옵션, 저장 폴더와 녹화 제어 UI |
| [`src/CaptureCanva/Capture/`](src/CaptureCanva/Capture/) | 화면 캡처, GPU 색 변환, 모니터·창 목록 |
| [`src/CaptureCanva/Recording/`](src/CaptureCanva/Recording/) | 녹화 세션, FFmpeg 인코딩, WASAPI 오디오 녹음 |
| [`src/CaptureCanva/UI/`](src/CaptureCanva/UI/) | 영역 선택과 녹화 영역 테두리 |
| [`src/CaptureCanva/Interop/`](src/CaptureCanva/Interop/) | Win32 및 Windows 캡처 API 연동 |
| [`src/CaptureCanva/AppSettings.cs`](src/CaptureCanva/AppSettings.cs) | 설정 저장 및 복원 |
| [`.github/workflows/ci.yml`](.github/workflows/ci.yml) | Debug·Release 빌드와 태그 기반 릴리스 배포 |
| [`scripts/Bundle-Ffmpeg.ps1`](scripts/Bundle-Ffmpeg.ps1) | 배포용 FFmpeg 다운로드·검증·패키징 |
| [`src/CaptureCanva/Recording/GifConverter.cs`](src/CaptureCanva/Recording/GifConverter.cs) | 2단계 GIF 변환과 취소 처리 |
| [`tests/CaptureCanva.GifChecks/`](tests/CaptureCanva.GifChecks/) | 실제 FFmpeg를 사용하는 GIF 동작 검사 |
| [`src/CaptureCanva/Accounts/`](src/CaptureCanva/Accounts/) | 계정 연결, 암호화 저장, 업로드와 환경설정 |
| [`tests/CaptureCanva.UploadChecks/`](tests/CaptureCanva.UploadChecks/) | 외부 계정 없이 업로드·콜백·취소·저장 검사 |

C# / WPF / .NET 10을 사용합니다. Windows.Graphics.Capture와 Direct3D 11로 화면을 캡처하고, Vortice로 GPU 처리를 수행하며, NAudio로 오디오를 녹음합니다. GPU 색 변환을 사용할 수 없으면 FFmpeg의 CPU 변환으로 진행합니다.

GitHub Actions는 `main` 브랜치에 대한 push와 pull request에서 Debug·Release 빌드를 확인합니다. `v*` 태그를 push하면 Windows x64 실행 파일과 FFmpeg, 라이선스 안내를 ZIP으로 묶어 GitHub Release에 올립니다.

### 데일리 빌드와 캐시 점검

매일 **새벽 3시(한국 시간, `Asia/Seoul`)**에 기본 브랜치의 최신 코드로 다음 작업을 실행합니다.

- NuGet 패키지 캐시를 복원하고, 배포용 Windows x64 런타임 패키지까지 준비
- Debug·Release 빌드 검사
- FFmpeg 캐시의 SHA-256 검증과 H.264·AAC 인코딩 확인
- GIF 프리셋, 결과 파일 디코딩, 변환 취소와 MP4 보존 검사
- Google 외부 접속 없이 업로드·인증 콜백·취소·저장 경로 격리 검사
- 캐시가 없으면 내려받아 저장하고, 있으면 재사용

NuGet 캐시는 .NET SDK 버전과 프로젝트 의존성이 바뀌면 새로 생성됩니다. FFmpeg 캐시는 준비 스크립트가 바뀌면 새로 생성되며, 태그 기반 릴리스에서도 같은 캐시를 사용합니다.

[Actions의 CI/CD 워크플로](https://github.com/mazelines/CaptureCanVa/actions/workflows/ci.yml)에서 **Run workflow**로 같은 점검을 수동 실행할 수 있습니다. 데일리 작업의 결과와 캐시 재사용 여부는 실행 요약에 표시됩니다. 데일리 실행에서는 릴리스를 게시하지 않습니다.

## 문제 해결

- **`ffmpeg 없음`이 표시될 때**: 릴리스 ZIP을 다시 풀어 `CaptureCanva.exe`와 `ffmpeg.exe`가 같은 폴더에 있는지 확인합니다. 소스 빌드나 v0.1.1 및 이전 버전은 FFmpeg를 별도로 준비해야 합니다.
- **특정 창을 녹화할 수 없을 때**: 최소화된 창을 복원하고 목록을 새로고침한 뒤 다시 선택합니다.
- **영역 선택 후 모니터 구성을 바꿨을 때**: 녹화할 영역을 다시 선택합니다.
- **녹화 지연이 발생할 때**: 프레임이나 녹화 영역 크기를 줄이고, 앱에 표시된 인코더를 확인합니다.
- **소리가 녹음되지 않을 때**: 시스템 소리·마이크 옵션과 Windows의 기본 오디오 장치, 마이크 접근 권한을 확인합니다. 오디오 녹음 시작에 실패하면 앱에 경고가 표시됩니다.

설정은 `%APPDATA%\CaptureCanva\settings.json`, 실행 및 예외 로그는 `%APPDATA%\CaptureCanva\logs`에 저장됩니다. 녹화 중에는 저장 폴더에 임시 영상과 오디오 파일을 만들며, 최종 MP4 저장이 성공하면 임시 파일을 삭제합니다.
