# SluPlayer

가볍고 빠른 Windows용 동영상 플레이어 + 다운로더.

- GPU로 재생(mpv + FFmpeg). 4K, HDR, HEVC, AV1까지
- 링크를 넣으면 YouTube·Instagram·X 영상을 MP4·MP3로 저장
- Windows 10·11 64비트. .NET 등 따로 설치할 것 없음

## 다운로드

### **[최신 버전 받기 (Releases)](https://github.com/sssslu/sluplayer/releases/latest)**

| 파일 | 설명 |
|---|---|
| `SluPlayer-Setup-<버전>.exe` | **설치 프로그램 (추천)**. 시작 메뉴 등록, 연결 프로그램 등록 |
| `SluPlayer-<버전>-win-x64.zip` | 설치 없이 쓰는 휴대용. 압축을 풀고 실행 |
| `*.sha256` | 파일 검증용 SHA-256 값 (필요할 때만) |

### 설치 순서
1. [Releases](https://github.com/sssslu/sluplayer/releases/latest) 페이지 아래 **Assets**에서 `SluPlayer-Setup-<버전>.exe`를 받는다(약 240 MB).
2. 브라우저가 "흔히 다운로드되지 않는 파일"이라고 막으면 다운로드 목록에서 **[유지]**(Edge: `…` → 유지 → 그래도 계속)를 누른다.
3. 받은 파일을 실행한다. **"Windows의 PC 보호"** 창이 뜨면 **[추가 정보] → [실행]**. 디지털 서명이 없어서 뜨는 창이다.
4. **[설치]**. 관리자 권한은 필요 없다. 설치 위치: `%LOCALAPPDATA%\Programs\SluPlayer`.
5. 동영상을 더블클릭해서 SluPlayer로 열려면: 동영상 파일 우클릭 → **연결 프로그램 → 다른 앱 선택 → SluPlayer → [항상]**. Windows는 기본 앱을 사용자가 직접 고르게 되어 있다.

- **업데이트**: 새 버전의 설치 프로그램을 실행하면 된다. 설정과 재생 기록은 유지.
- **제거**: Windows 설정 → 앱 → 설치된 앱 → SluPlayer → 제거. "설정과 재생 기록도 삭제"를 고르지 않으면 데이터는 남는다. 다운로드한 파일은 지우지 않는다.

### 휴대용(zip)
1. `SluPlayer-<버전>-win-x64.zip`을 **모두 풀고** `SluPlayer.exe` 실행. 압축 파일 안에서 바로 실행하면 동작하지 않는다.
2. `SluPlayer.exe` 옆에 빈 `portable.txt`를 만들면 설정과 기록을 그 폴더의 `data`에 저장.

### 파일 검증 (선택)
PowerShell에서 `(Get-FileHash .\SluPlayer-Setup-<버전>.exe).Hash`를 실행해서, 같이 올라온 `.sha256` 파일 앞부분 값과 같으면 정상(대소문자 무시).

## 사용법

- **재생**: 파일을 창에 끌어다 놓거나 **[열기]**(Ctrl+O). 같은 폴더 파일은 자동으로 재생목록이 된다.
- **다운로드**: **[다운로드]**(Ctrl+D) → 링크 **[붙여넣기]** → **[분석]** → 영상/음악, 화질, 형식 선택 → **[다운로드]**. 재생목록과 여러 영상 게시물은 전체 또는 골라서 받는다.
- **URL 바로 재생**: 링크를 복사하고 플레이어 창에서 Ctrl+V.
- 나머지 기능은 화면 우클릭 메뉴.

## 기능

- **재생**: MP4, MKV, AVI, MOV, WebM, TS/M2TS, FLV, WMV 등. H.264, HEVC, AV1, VP9 등. 4K, 10비트, HDR10(Windows HDR이 켜져 있으면 HDR 출력, 꺼져 있으면 톤 매핑)
- **음악**: MP3, FLAC, M4A, Opus, OGG, WAV 등. 앨범 커버 표시
- **GPU 가속**: Direct3D 11 렌더링 + 하드웨어 디코딩(d3d11va, NVDEC) 자동. GPU가 두 개면 고성능 GPU 사용
- 이어 보기, 같은 폴더 파일 자동 재생목록, 재생 속도, 반복·무작위, 챕터, 스크린샷, 회전, 화면 비율
- **자막**: SRT, SMI, ASS/SSA, VTT, 내장 자막. 한국어 우선, CP949(EUC-KR) 지원. 싱크·크기 조절
- **URL 재생**: Ctrl+U 또는 링크 붙여넣기(Ctrl+V)
- **다운로드**: 링크를 넣으면 동영상(MP4·MKV) 또는 오디오(MP3·M4A)로 저장. YouTube, Instagram(공개 게시물·릴스), X 등 [yt-dlp가 지원하는 사이트](https://github.com/yt-dlp/yt-dlp/blob/master/supportedsites.md). 화질 선택, 재생목록·여러 영상 게시물(전체·선택), 일시정지·재개·취소

## 단축키

| 동작 | 키 |
|---|---|
| 재생 / 일시정지 | Space, K, 화면 클릭 |
| 정지 | S |
| 전체 화면 / 해제 | Enter, F, F11, 더블클릭 / Esc |
| 5초 / 10초 / 30초 / 1분 이동 | ← → / J L / Ctrl+← → / Shift+← → |
| 처음 / 끝, 0~90% 위치 | Home / End, 0~9 |
| 이전·다음 프레임 | 쉼표(,) 마침표(.) |
| 볼륨 ±5 / ±1 / 음소거 | ↑ ↓, 마우스 휠 / Ctrl+↑ ↓ / M, 휠 클릭 |
| 재생 속도 +0.1 / −0.1 / 1배 | C X Z (또는 ] [ Backspace) |
| 이전·다음 파일 / 챕터 | PgUp PgDn / Ctrl+PgUp Ctrl+PgDn |
| 자막 바꾸기 / 숨기기 | V / Shift+V |
| 자막 싱크 −0.5초 / +0.5초 / 초기화 | Shift+쉼표(<) / Shift+마침표(>) / 슬래시(/) |
| 오디오 트랙 바꾸기 | B |
| 화면 비율 / 90° 회전 | A / Ctrl+R, Ctrl+Shift+R |
| 반복 모드 | Shift+L |
| 하드웨어 가속 켜기·끄기 | Ctrl+H |
| 스크린샷 | Ctrl+E, F8 |
| 파일 / 폴더 / URL 열기, 붙여넣어 재생 | Ctrl+O / Ctrl+Shift+O / Ctrl+U, Ctrl+V |
| 재생목록 / 다운로드 / 설정 | F6, Ctrl+L / Ctrl+D / F5 |
| 항상 위 / 창 크기 50·100·200% | Ctrl+T / Alt+0 Alt+1 Alt+2 |
| 종료 | Ctrl+Q |

마우스: Ctrl+휠 5초 이동, 뒤로·앞으로 버튼 10초 이동. 나머지 기능은 우클릭 메뉴.

## 자주 묻는 질문

### YouTube 로그인 필요
YouTube가 봇 확인을 요구하면 다운로드와 URL 재생이 실패한다. 연령 제한·멤버십 영상도 로그인이 필요하다.

1. 다운로드 창 아래 **[로그인]** 또는 설정 → **YouTube 계정**에서 Google 계정으로 로그인.
2. 로그인 정보(쿠키)는 이 PC의 `%LOCALAPPDATA%\SluPlayer\cookies`에만 저장하고 YouTube 요청에만 쓴다.
3. 로그인 창은 Microsoft Edge WebView2를 쓴다. 창이 열리지 않으면 [WebView2 런타임](https://developer.microsoft.com/microsoft-edge/webview2/) 설치.

### Instagram·X 영상이 안 받아짐
- 공개 게시물·릴스만 된다. 비공개 계정 영상은 받을 수 없다.
- DM 채팅방 링크(`instagram.com/direct/…`)는 안 된다. DM에 공유된 게시물을 열고 **공유 → 링크 복사**로 얻은 주소를 쓴다. DM에 직접 올린 영상은 링크가 없어서 받을 수 없다.

### GPU 가속 확인
우클릭 → 영상 → 하드웨어 가속 → "현재" 항목. `d3d11va`·`nvdec`이면 GPU 디코딩, `CPU`면 소프트웨어 디코딩. 그래픽 카드가 코덱을 지원하지 않으면(예: 오래된 GPU의 AV1) 자동으로 CPU를 쓴다.

### "vulkan-1.dll이 없어서 실행할 수 없다" 오류
그래픽 드라이버가 없는 PC(가상 머신 등)에서 난다. 그래픽 카드 제조사의 최신 드라이버나 [LunarG Vulkan Runtime](https://vulkan.lunarg.com/sdk/home) 설치.

### 파일 위치
- 다운로드: `동영상\SluPlayer`(동영상), `음악\SluPlayer`(오디오). 스크린샷: `사진\SluPlayer`. 설정에서 변경.
- 설정·재생 위치·다운로드 목록: `%APPDATA%\SluPlayer`
- 로그·캐시·로그인 정보·업데이트된 엔진: `%LOCALAPPDATA%\SluPlayer`
- 초기화: SluPlayer를 끄고 두 폴더 삭제. 휴대용 모드는 `SluPlayer.exe` 옆 `data` 폴더 삭제.

### 다운로드가 갑자기 안 됨
사이트가 바뀌면 다운로드 엔진(yt-dlp)도 새 버전이 필요하다. 다운로드 창을 열 때 하루 한 번 자동으로 확인하고 업데이트한다. 다운로드 창 아래 [엔진 업데이트]로 바로 업데이트할 수도 있다. 새 엔진은 `%LOCALAPPDATA%\SluPlayer\tools`에 저장.

### 열리지 않는 파일
손상된 파일(다운로드가 중간에 끊긴 MP4 등)과 DRM으로 보호된 영상은 재생할 수 없다.

## 문제 해결

- **로그**: `%LOCALAPPDATA%\SluPlayer\logs\sluplayer.log`(휴대용 모드: `data\local\logs`)
- **화면이 검거나 깨짐**: 그래픽 드라이버 업데이트. 그래도 같으면 Ctrl+H로 하드웨어 가속 끄기.
- **영상이 끊김**: 하드웨어 가속 확인(위 참고). 노트북은 전원 연결.
- **소리 없음**: 음소거(M)와 볼륨 확인.
- **실행 안 됨**: 압축을 모두 풀었는지 확인. 백신이 `tools` 폴더 파일(yt-dlp, deno, ffmpeg)을 지웠다면 다시 풀거나 다시 설치.
- **창이 화면 밖**: SluPlayer를 끄고 `%APPDATA%\SluPlayer\settings.json`에서 `"window"` 항목 삭제.

## 라이선스
SluPlayer는 [GPL-3.0](LICENSE). mpv, FFmpeg, yt-dlp, Deno, Vulkan 로더, .NET 등 오픈소스 포함. 라이선스와 소스 위치: 설치 폴더의 `licenses\THIRD-PARTY-NOTICES.txt`. mpv와 FFmpeg는 GPL.

- 소스: https://github.com/sssslu/sluplayer
- 버그·건의: [Issues](https://github.com/sssslu/sluplayer/issues)

<!-- developer -->

---

## 개발자용: 빌드

구조와 설계: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

```powershell
git clone https://github.com/sssslu/sluplayer.git
cd sluplayer
```

### 준비물
- Windows 10/11 x64, Windows PowerShell 5.1
- .NET SDK 10. 사용자 폴더 설치 가능(`%LOCALAPPDATA%\Microsoft\dotnet`, 스크립트가 자동으로 찾는다).
- 첫 빌드 때 인터넷(외부 바이너리 약 230 MB)

### 1. 외부 바이너리
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build\fetch-vendor.ps1
```
고정 버전만 받고 SHA-256을 검사한다. 이미 받은 파일은 검사만 한다. 저장 위치: `%LOCALAPPDATA%\SluPlayerDev\vendor`(저장소 밖).

| 구성 요소 | 버전 | 폴더 |
|---|---|---|
| libmpv | mpv v0.41.0-1227-ge471496c5 (shinchiro 빌드 20261021) | `libmpv\libmpv-2.dll` |
| yt-dlp | 2026.10.26, 폴더형(onedir) 빌드. 단일 exe보다 약 3배 빨리 시작해서 이것을 배포 | `yt-dlp-win\yt-dlp.exe` + `_internal\` |
| yt-dlp (단일 파일) | 2026.10.26, 개발 빌드용 대체 | `yt-dlp\yt-dlp.exe` |
| Deno | 2.9.8 (yt-dlp가 YouTube에 사용) | `deno\deno.exe` |
| FFmpeg | N-127043-g5a54fcf75e (yt-dlp/FFmpeg-Builds, gpl shared) | `ffmpeg-shared\` |
| Vulkan 로더 | 1.4.363.0 (LunarG, 서명됨) | `vulkan\vulkan-1.dll` |

- `-Verify`: 각 도구를 한 번씩 실행해 확인.
- `-Latest`: 상위 프로젝트의 최신 버전과 SHA-256 출력(핀 갱신용, 다운로드 안 함).
- `-Force`: 핀과 다른 파일 교체(기존 파일은 `.bak-날짜`로 보존).
- `-Only libmpv,ffmpeg,...`: 일부 구성 요소만.
- shinchiro 릴리스는 약 한 달, FFmpeg 일일 빌드는 약 2주 뒤에 삭제된다. FFmpeg는 2년간 보관되는 월말 빌드를 고정. 고정 주소가 404가 되면 `-Latest`로 새 버전을 확인하고 스크립트의 `$Pins` 수정.

### 2. 개발 빌드
```powershell
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build src\SluPlayer\SluPlayer.csproj
```
결과물: `%LOCALAPPDATA%\SluPlayerDev\artifacts\bin\SluPlayer\debug_win-x64\`.

### 3. 릴리스 빌드
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build\publish.ps1 -Installer
```
1. `fetch-vendor.ps1`로 외부 바이너리 확인
2. `dotnet publish`(Release, win-x64, self-contained, 단일 파일, ReadyToRun). 작업 폴더: `%LOCALAPPDATA%\SluPlayerDev\publish`
3. `dist\`에 결과물 생성

| 결과물 | 내용 |
|---|---|
| `dist\SluPlayer\` | 바로 실행 가능한 폴더 |
| `dist\SluPlayer-<버전>-win-x64.zip` (+ `.sha256`) | 위 폴더 압축 |
| `dist\SluPlayer-Setup-<버전>.exe` | 사용자별 설치 프로그램(`-Installer`) |

`dist\SluPlayer\` 구성:

```
SluPlayer.exe            앱 (.NET 런타임과 WPF 포함, 단일 파일)
*_cor3.dll, WebView2Loader.dll   WPF·WebView2 네이티브 DLL
libmpv-2.dll             재생 엔진
vulkan\vulkan-1.dll      Vulkan 로더 (드라이버에 없을 때만 사용)
tools\yt-dlp\            yt-dlp.exe + _internal\ (다운로드 엔진)
tools\                   deno.exe, ffmpeg.exe, ffprobe.exe, FFmpeg DLL
licenses\                오픈소스 라이선스
README.txt               이 문서의 사용자용 부분
```

옵션: `-Version 1.2.3`(버전 지정, exe에도 기록), `-OutDir <폴더>`, `-Folder`(단일 파일 대신 일반 폴더 게시), `-BundleNativeLibraries`(네이티브 DLL도 exe에 포함), `-SkipFetch`, `-SkipZip`.

- 네이티브 DLL을 exe 옆에 두는 이유: exe에 넣으면 .NET이 첫 실행 때 `%TEMP%\.net`에 풀어 쓴다. 시작 속도는 같고 임시 폴더에 파일만 쌓인다.
- `dist\SluPlayer\`에 만든 `portable.txt`와 `data\`는 다시 빌드해도 지우지 않는다.
- `dist\`(약 1 GB)는 git에 올리지 않는다(`.gitignore`). 다른 위치에 만들려면 `-OutDir`.
- 배포: `dist\`의 Setup exe, zip, `.sha256` 파일을 GitHub Releases에 올린다.

### 4. 설치 프로그램
`build\installer`의 작은 .NET Framework 4.7.2 프로그램(Windows 10 1803 이상·Windows 11에 기본 포함)에 zip을 붙여서 만든다(`build-installer.ps1`).

- 관리자 권한 없이 `%LOCALAPPDATA%\Programs\SluPlayer`에 설치. 시작 메뉴(선택: 바탕 화면) 바로 가기와 "앱 및 기능" 제거 항목(HKCU) 생성.
- 이전 버전 위에 설치하면 실행 중인 SluPlayer를 먼저 닫고, 새 버전에 없는 파일은 지운다. 사용자 데이터는 건드리지 않는다.
- 명령줄: `/S`(조용히 설치. 바탕 화면 바로 가기와 실행은 `/Desktop`·`/Launch`를 줄 때만), `/D=<폴더>`(설치 위치), `/Desktop`·`/NoDesktop`, `/NoLaunch`. 제거: `Uninstall.exe [/S] [/RemoveData]`(임시 폴더의 복사본이 이어서 진행하므로 바로 반환). 종료 코드: 0 성공, 2 잘못된 인수·위치, 3 SluPlayer 실행 중, 4 공간 부족.
- 설치 로그: `%TEMP%\SluPlayer-Setup.log`.
- 테스트용 옵션(실제 시작 메뉴·레지스트리·데이터를 건드리지 않음): `/RegKey=Software\...`, `/StartMenuDir=`, `/DesktopDir=`, `/DataDirs=a|b`, `/NoActivate`, `/WindowPos=x,y`.
- 설치 끝에 `SluPlayer.exe --register-file-types`로 파일 연결(HKCU: 연결 프로그램, 기본 앱 목록)을 등록하고, 제거할 때 지운다. 기본 앱 지정은 Windows가 사용자에게만 허용한다(연결 프로그램 › 항상). 휴대용(zip)은 설정 › 파일 연결 › 등록.

### 라이선스 메모
`build\licenses`의 파일과 `LICENSE`(→ `licenses\SluPlayer-LICENSE.txt`)가 `licenses\`로 들어간다. 새 NuGet 패키지를 추가하면 `publish.ps1`이 경고와 함께 그 패키지의 라이선스 파일을 `licenses\nuget\`에 넣는다. 이때 `build\licenses`와 `THIRD-PARTY-NOTICES.txt`도 수정. 이 빌드의 libmpv는 GPL이라 SluPlayer를 배포할 때는 소스도 GPLv3 호환 조건으로 공개해야 한다.
