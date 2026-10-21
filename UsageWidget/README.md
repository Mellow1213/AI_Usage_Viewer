# Windows 개발 안내

이 문서는 Windows WPF + WebView2 호스트의 빌드·구현·검증을 다룹니다. 설치·화면 조작·업데이트는 [Windows 사용 안내](../README.md#windows-버전), 공통 기능은 [루트 README](../README.md)를 참고하세요. macOS의 Swift + WebKit 호스트는 [macOS 개발 안내](../MacUsageWidget/README.md)에 분리했습니다.

## 개발 환경과 빌드

| 항목 | 요구 사항 |
|---|---|
| 운영체제 | Windows. 배포 대상은 `win-x64`입니다. |
| .NET | .NET 8 SDK. WPF 프로젝트는 `net8.0-windows10.0.17763.0`을 대상으로 합니다. |
| UI 도구 | npm과 Node.js. 잠금 파일의 Vite 7.3.7은 `^20.19.0 \|\| >=22.12.0`을 요구합니다. |
| 화면 검증 | Microsoft Edge WebView2 Runtime |
| 실제 계정 조회 | 연결할 서비스의 공식 Windows CLI와 해당 구독 계정 |

아래 명령은 모두 저장소 루트 기준입니다.

```powershell
.\build-dev-widget.ps1 -RestoreUI
.\run-dev-widget.cmd
```

개발 스크립트는 `build-widget.ps1 -OutputDirectory artifacts/dev-build`를 호출합니다. `WidgetUI`에서 `npm ci`와 `npm run build`를 실행한 다음 `dotnet publish`로 `artifacts/dev-build/AIUsageWidget.exe`를 생성하며, 기존 `Release`와 설치본은 덮어쓰지 않습니다. .NET 런타임을 포함하는 Windows x64 빌드이며 출력 폴더 전체가 실행에 필요합니다. 이후 빌드에서는 `-RestoreUI`를 생략할 수 있고, `node_modules`가 없으면 자동으로 의존성을 설치합니다.

기본 `build-widget.ps1`은 기존처럼 `Release/AIUsageWidget.exe`를 생성합니다. 기존 삼각형 아이콘, 애니메이션과 `%LOCALAPPDATA%\AIUsageWidget`의 계정 저장 구조를 유지합니다.

### 실사용 설치본과 개발 환경

정식 배포는 [GitHub Releases 배포 가이드](../docs/releasing.md)의 필수 배포 라인을 따릅니다. 앱 변경 시 버전과 릴리스 노트를 올리고, 빌드·검증 후 소스를 커밋·푸시한 다음 실행합니다. 일반 빌드는 설치본이나 배포 피드를 갱신하지 않습니다.

```powershell
.\publish-widget.ps1 -Version 1.1.1
.\verify-widget-release.ps1 -Version 1.1.1
```

설치 폴더는 `%LOCALAPPDATA%\Mellow.AIUsageViewer`이며 기존 `%LOCALAPPDATA%\AIUsageWidget` 계정 데이터는 옮기지 않습니다. `publish-widget.ps1`은 버전별 격리 빌드·패키징, 소스 커밋 연결, GitHub 초안 업로드와 SHA256 검증 후 Latest 공개까지 수행합니다. 이미 공개된 버전은 재사용할 수 없습니다. 다른 PC는 Releases의 `Mellow.AIUsageViewer-win-Setup.exe`를 한 번 설치한 후 앱에서 다운로드·적용합니다.

`run-widget.cmd`는 설치본만 실행하며 설치본이 없으면 설치 안내 후 종료합니다. 개발 빌드로 대신 실행하지 않습니다. `run-dev-widget.cmd`는 `artifacts/dev-build/AIUsageWidget.exe --data-dir artifacts/dev-profile`로 개발 데이터를 분리합니다. 계정·설정·관측 기록·WebView·오류 로그와 로그 회전 파일이 모두 개발 프로필에 저장됩니다. 단일 실행 제한은 기본 데이터 폴더에서 이전 버전과 호환되고, 별도 개발 데이터 폴더에서는 독립적입니다.

실사용은 Windows 시작 메뉴의 설치본을 사용합니다. Windows 패키지 환경에서 실행한 자식 프로세스는 AppData가 해당 패키지의 `LocalCache`로 리디렉션될 수 있으므로, 개발 도구에서 실사용 앱을 직접 실행하지 않습니다. 별도 프로필의 인증을 자동 병합하지 않으며 개발판에는 실제 계정 대신 예시 데이터나 테스트 전용 계정을 사용합니다. Docker나 VM은 이 실행·데이터 분리에 필요하지 않습니다.

Velopack SDK와 `vpk`는 1.2.161로 고정합니다. WPF 시작점에서 UI·단일 실행 검사 전에 Velopack 훅을 처리하고 자동 적용은 끕니다. 업데이트 서비스는 시작 시 한 번과 사용자 요청 때만 실행하며 주기 타이머를 만들지 않습니다. 적용 시 `Update.exe`를 대기 상태로 실행한 뒤 정상 종료하여 창 상태 저장·CLI 정리를 마칩니다. 설치·제거·업데이트 훅에서 계정 데이터 삭제나 로그아웃을 실행하지 않습니다.

`update-source.json`의 `source`는 로컬 폴더 또는 HTTPS 피드입니다. 설정이 없거나 정식 저장소·Latest 다운로드 주소이면 공개 GitHub Releases 공급자를 사용합니다. 기존 로컬 피드 설치본은 `use-github-updates.ps1` 실행 후 앱을 한 번 재시작합니다. `publish-widget-local.ps1`은 로컬 테스트용이며 정식 배포를 대신하지 않습니다. 패키지 ID 일치와 Velopack의 크기·해시 검증을 사용합니다. 코드 서명 인증서는 현재 구성하지 않았으며 해시 검증은 게시자 코드 서명과 별개입니다.

베타는 [베타 배포 절차](../docs/releasing.md#베타-배포)를 따릅니다. `-beta.<번호>` 빌드만 사전 릴리스 업데이트를 포함하며 정식 설치본의 Latest 경로는 유지합니다.

## 소스 구조

| 경로 | 역할 |
|---|---|
| `UsageWidget/App.xaml.cs` | 앱 시작·종료와 단일 실행 제어 |
| `UsageWidget/MainWindow.xaml.cs` | WPF 호스트, WebView2, 계정 세션, 상태 전달·창 이동·보기 전환 |
| `UsageWidget/AiAccountProfiles.cs`, `AccountSessions.cs` | 서비스 카탈로그, 공통 계정 목록과 서비스별 세션 어댑터 |
| `UsageWidget/CodexClient.cs` | 공식 Codex app-server의 계정·로그인·한도 RPC |
| `UsageWidget/ClaudeAuthClient.cs`, `ClaudeUsageControl.cs` | 공식 Claude Code 인증과 사용량 제어 요청 |
| `UsageWidget/ClaudeBridge.cs`, `Bridge/claude-statusline.ps1` | Claude 상태줄 설치·복원과 사용량 수신 |
| `UsageWidget/UsageModels.cs` | 사용량·한도 모델과 서비스 응답 파서 |
| `UsageWidget/DailyUsageTracker.cs`, `MeasurementPreferences.cs` | 관측 변화 기록, 로컬 집계와 측정 설정 |
| `UsageWidget/UsageHistory.cs`, `WidgetUI/src/UsageHistory.jsx` | 위젯 집계와 분리된 90일 기록·관측 구간과 선택형 분석 화면 |
| `UsageWidget/WidgetStorage.cs` | 사용자 데이터 저장과 제한된 오류 로그 |
| `UsageWidget/WidgetUpdates.cs`, `WidgetUI/src/Updates.jsx` | 업데이트 상태·Velopack 연동과 설정 화면 |
| `WidgetUI/src/main.jsx`, `style.css` | React 카드·설정 화면과 애니메이션 |
| `WidgetUI/src/displayClock.js` | 카드별 수신 경과 시간·리셋 표시 갱신 일정 |
| `WidgetVerification` | WPF·WebView2 통합 검증과 성능 측정 |
| `docs/screenshots`, `docs/performance.md` | 검증용 화면 예시와 성능 비교 기록 |
| `build-widget.ps1`, `run-widget.cmd` | 배포 빌드와 실행 스크립트 |

## UI 개발과 미리보기

```powershell
Set-Location .\WidgetUI
npm run dev
```

개발 서버는 `127.0.0.1`에 바인딩합니다. 출력된 URL에 `?preview=1`을 붙이면 예시 데이터라는 표시가 있는 디자인 미리보기를 볼 수 있습니다. 실제 계정 연결은 데스크톱 앱에서 처리합니다. 예시 값은 데스크톱 앱의 실제 수신값을 대체하지 않습니다.

네이티브 앱은 빌드된 UI를 `https://widget.local/index.html`로 매핑하고 WebView2 메시지로 상태와 사용자 동작을 전달합니다. 공통 React UI는 macOS에서도 사용하며, 호스트가 전달한 `platform: "macos"`에 따라 상단 새로고침과 메뉴 막대용 조작을 표시합니다. Windows UI 변경을 실행본에 반영하려면 저장소 루트에서 `build-widget.ps1`로 다시 빌드합니다. macOS 빌드는 Windows의 `Release`를 갱신하지 않습니다.

## 검증

UI를 빌드한 다음 저장소 루트에서 실행합니다.

```powershell
node --test .\WidgetUI\src\*.test.js
dotnet run --project .\WidgetVerification\UsageWidget.Verification.csproj -c Release -- artifacts\connection-checks --connection-only
dotnet run --project .\WidgetVerification\UsageWidget.Verification.csproj -c Release -- artifacts\history-checks --history-only
dotnet run --project .\WidgetVerification\UsageWidget.Verification.csproj -c Release -- artifacts\widget-verification
dotnet run --project .\WidgetVerification\UsageWidget.Verification.csproj -c Release -- artifacts\performance-checks --performance-checks-only
dotnet run --project .\WidgetVerification\UsageWidget.Verification.csproj -c Release -- artifacts\presence-checks --presence-only
dotnet run --project .\WidgetVerification\UsageWidget.Verification.csproj -c Release -- artifacts\update-checks --updates-only
.\verify-widget-update.ps1
```

단위 테스트는 표시 시각 경계를, 통합 검증은 데이터 파싱·상태줄 복원·계정 격리·계정 삭제·관측 집계·보기 전환 등을 확인합니다. 계정 삭제 검증은 기본·추가·기존 숨김 계정의 태그 제거, 재실행과 이전 형식 복원 시 재등장 방지, 다른 계정과 로그인 파일 보존을 포함합니다. 성능 변경 검증은 동일 상태 전송 생략, 새 데이터 전달, 로고 화면의 시계 해제와 복원을 확인합니다. 결과는 지정한 출력 폴더의 `results.json`에 저장합니다. 기본 검증은 예시 데이터와 별도 Claude 설정 파일을 사용합니다.

자동 연결 검증은 저장된 계정의 첫 화면, 느린 인증·사용량 응답, 계정별 완료 표시, 실제 미연결·오류·재시도와 기존 수신값 보존을 격리된 CLI로 확인합니다.

Codex 프로세스의 종료·재연결·로그인 보존은 Windows UI에 의존하지 않는 동일 C# 소스로도 검증할 수 있습니다. 가짜 CLI와 별도 계정 폴더를 사용합니다.

```powershell
dotnet run --project .\WidgetVerification\Portable\UsageWidget.ProcessVerification.csproj -c Release -- artifacts\process-verification
```

로고 표시 검증은 카드·로고 고정 분리, 이전 설정 파일 호환, 비활성화 창 스타일, 우클릭 메뉴, 트레이 복원, 임시 숨김 만료와 설정 저장을 확인합니다. 별도 프로세스의 전체 화면 창을 잠시 열어 전체 화면 숨김·자동 복원 시 입력 포커스 유지도 확인합니다.

업데이트 검증은 실패·재시도, 중복 확인 방지, 다운로드 후 명시적 적용, 종료 시 취소, 계정 설정 보존과 실제 React 버튼을 확인합니다. `verify-widget-update.ps1`은 별도 패키지 ID·격리된 설치 폴더를 사용해 두 버전을 실제 설치·업데이트하고 손상된 패키지 거부와 데이터 보존을 검증합니다. 실제 사용자 계정이나 기본 설치본은 사용하지 않습니다.

실제 Codex CLI의 현재 로그인 계정을 조회하는 검증은 별도로 실행합니다.

```powershell
dotnet run --project .\WidgetVerification\UsageWidget.Verification.csproj -c Release -- artifacts\widget-verification-live --live
```

CPU·메모리 측정은 다음 명령으로 실행하며 `performance.json`에 결과를 저장합니다.

```powershell
dotnet run --project .\WidgetVerification\UsageWidget.Verification.csproj -c Release -- artifacts\performance --performance-only
```

측정 조건과 변경 전후 수치는 [성능 비교 기록](../docs/performance.md)에 있습니다. 고정 예시 데이터의 짧은 측정이므로 실제 계정·네트워크 조회를 포함한 장시간 운용의 보장값으로 사용하지 않습니다.

개발용 저장 위치를 분리하려면 다음처럼 실행합니다.

```powershell
.\Release\AIUsageWidget.exe --data-dir D:\Temp\AIUsageWidget-Dev
```

이 옵션은 계정·설정·관측 기록·WebView 프로필과 오류 로그의 저장 위치를 함께 바꿉니다. `widget.log`와 `widget.log.old`도 지정한 프로필에 저장합니다. 검증 호스트는 임시 출력 폴더를 로그 위치로 지정합니다.

## 계정 격리와 서비스 연동

첫 계정은 기존 `Accounts/Codex`, `Accounts/Claude`를 유지합니다. 추가 계정은 `Accounts/CodexProfiles/<프로필 ID>`, `Accounts/ClaudeProfiles/<프로필 ID>`로 분리합니다. Codex에는 `CODEX_HOME`, Claude에는 `CLAUDE_CONFIG_DIR`를 전달하며 공식 CLI가 인증 상태를 관리합니다. 기존 CLI 계정 공유는 서비스별 첫 계정 슬롯에서만 허용합니다.

Codex 전용 프로필은 인증 저장소를 파일로 지정합니다. 앱은 기본 `%APPDATA%/npm/node_modules/@openai/codex` 아래의 x64 native 실행 파일, PATH의 `codex.exe`, 기본 npm 설치의 `bin/codex.js` 순서로 찾습니다. JS 진입점을 사용하는 경우 `node.exe`가 필요합니다. 로그인·계정 조회는 `account/login/start`, `account/login/completed`, `account/login/cancel`, `account/logout`, `account/read`, `account/rateLimits/read`를 사용합니다. [공식 app-server 문서](https://learn.chatgpt.com/docs/app-server)를 참고하세요.

Claude는 `%USERPROFILE%/.local/bin/claude.exe`를 먼저 찾고 PATH의 `claude.exe`를 확인합니다. `claude.cmd`나 WSL의 Linux CLI는 이 탐색 경로에 포함되지 않습니다. 전용 프로필에서는 상속된 API 키·다른 공급자 환경변수가 선택한 구독 계정을 덮어쓰지 않도록 제거합니다.

Claude 인증은 공식 CLI의 `auth login --claudeai`, `auth status --json`, `auth logout`에 위임합니다. 구독 한도는 스트림 JSON의 `initialize`, `get_usage` 제어 요청으로 받으며 사용자 메시지·모델 요청·도구 요청을 보내지 않습니다. 공식 TypeScript SDK의 `usage_EXPERIMENTAL_MAY_CHANGE_DO_NOT_RELY_ON_THIS_API_YET()`에 대응하는 실험 기능입니다. [공식 추가 기록](https://github.com/anthropics/claude-agent-sdk-typescript/releases/tag/v0.3.169)에 따라 이후 CLI 변경 가능성을 고려해야 합니다.

Claude 상태줄 브리지는 한도 사용률·리셋 시각·수신 시각만 추출하고, 자동 조회 실패 시 마지막 수신값을 유지합니다. 계정 이메일은 별도 `usage-account.json`에 저장해 캐시의 계정을 구분합니다. 상태줄 설치 시 전체 설정과 이전 `statusLine`을 백업하며, 해제 시 상태줄만 복원해 이후 변경된 다른 설정을 유지합니다. 기존 상태줄 명령은 Git Bash로 실행해 출력도 보존합니다.

## 갱신과 화면 전환

네이티브 호스트는 2초마다 표시 중인 Claude 계정의 로컬 수신 파일을 확인하고 상태를 집계합니다. 서비스 계정 확인·갱신은 60초 주기로 진행하며, Claude의 실제 자동 사용량 제어 요청은 세션별 5분 간격으로 제한합니다. 수동 새로고침은 이 간격을 기다리지 않고 요청합니다. 계정 조회는 최대 3개까지 동시에 처리합니다.

Codex 계정·사용량 조회 묶음이 완료되면 해당 계정의 조회 프로세스를 종료하고 다음 갱신 때 다시 연결합니다. 진행 중인 로그인과 요청은 유지하고, 인증 파일·계정 정보·마지막 수신값은 보존합니다. 종료된 이전 프로세스의 응답 처리가 새 프로세스의 요청을 취소하지 않도록 구분합니다.

시간 표시는 App 전체의 매초 렌더링 대신 카드별 표시 시계를 사용합니다. 분 표시·수신 경과 시간·리셋 만료가 바뀌는 시점에 갱신하고 로고만 보기에서는 카드 시계를 해제합니다. 같은 JSON 상태의 반복 전송은 생략하며, 새 수신 시각·계정·설정·화면 전환과 새 `ready` 요청은 계속 전달합니다. 조회·집계와 애니메이션은 유지합니다.

창 드래그는 WebView2의 native 제목줄 영역으로 처리합니다. 삼각형 버튼은 드래그 영역에서 제외하고 글자 영역을 통해 이동합니다. 드래그 종료 시 작업 영역 내 창 중심의 사분면으로 접기 기준 모서리를 정하고, 전환 중에는 기준점을 유지합니다.

### 애니메이션 구현

- 펼치기·접기는 내용 정리(약 0.09초) → 삼각형을 유지하며 실제 창의 폭·높이 변형(약 0.38초) → 새 내용 표시(약 0.13초) 순서입니다. 크기가 바뀔 때 카드 내용이 잘려 보이지 않도록 다음 내용을 숨긴 상태로 미리 측정하고, 창 변형 완료 신호를 받은 후 표시합니다. 선택된 모서리를 유지하고, 전환 중 다시 누르면 현재 크기에서 새 목표로 이어집니다. `설정 → 화면 설정 → 부드러운 화면 전환`로 끌 수 있고 이 설정도 저장합니다. 요청된 전환 효과는 Windows 애니메이션 옵션과 별도로 기본 활성화합니다.
- 전환 중 삼각형 세 개로 구성된 로고가 약 0.48초 동안 easeInOut으로 120도만 회전합니다. 접기는 시계 방향, 펼치기는 반시계 방향입니다. 정지 로고는 끝난 각도를 이어받습니다. 회전과 새 카드의 페이드가 끝날 때까지 정지 로고를 숨긴 뒤 페이드 겹침 없이 교체하며, 크기와 위치도 맞춥니다. 펼치기·접기 효과를 끄면 전환 회전도 생략합니다.
- 창 크기 전환은 WPF 합성 프레임에 맞춰 0.38초 동안 cosine easeInOut으로 진행합니다. 너비·높이·위치를 한 번의 네이티브 호출로 바꾸고, 같은 합성 프레임과 같은 픽셀 크기는 중복 적용하지 않습니다. 카드가 숨겨진 전환 구간에는 WebView 크기를 고정해 웹 화면의 반복 재배치를 피하고, 최종 크기로 맞춘 뒤 내용을 표시합니다. 일반 설정창 크기 변경과 드래그 중에는 정상적인 화면 크기 갱신을 유지합니다.
- 선택한 AI가 모두 연결된 동안에만 삼각형 세 조각의 밝기가 3초 주기로 부드럽게 오르내리며, 1초씩 위상을 나눠 느린 물결처럼 흐릅니다. 연결이 끊기거나 조회 오류가 있는 AI가 하나라도 있으면 로고가 회색으로 바뀌고 밝기 효과가 멈춥니다. 빈 대시보드도 회색입니다. 표시 목록에 없는 계정은 연결 판단에 포함하지 않습니다. 연결된 상태의 갱신 중에는 물결이 약 0.85초 주기로 빨라집니다. 조회·로그인 시작 시 삼각형은 약 1.6초 동안 한 바퀴만 회전하며, 일찍 완료되어도 끝까지 돌고 역회전하지 않습니다. 회전 중 겹친 갱신은 추가 회전으로 쌓지 않습니다. 웹 화면의 표시 상태가 바뀌거나 로고가 교체되면 진행 중 회전을 정리해 다시 보일 때 밀린 회전을 재생하지 않습니다. 연결 확인·로그인 및 펼치기·접기 회전은 회색에서도 유지합니다. 밝기 효과는 추가 사용량 조회를 발생시키지 않으며, Windows 동작 줄이기 설정에서는 생략합니다.
- 잔여량이 감소하면 밝은 잔상이 약 0.65초 유지된 뒤 1.25초 동안 따라 줄어드는 지연 HP 효과를 표시합니다. 한도 초기화와 계정·플랜 변경은 감소 애니메이션의 기준을 새로 잡습니다.

## 삼각형 아이콘

실행 파일·작업 표시줄·시작 화면에는 기존 삼각형 세 개 로고를 사용합니다. 원본은 [usage-widget.svg](Assets/usage-widget.svg)이며 저장소 루트에서의 경로는 `UsageWidget/Assets/usage-widget.svg`입니다. SVG를 변경한 경우 루트에서 다음 명령으로 ICO와 시작 화면 PNG를 다시 생성합니다.

```powershell
powershell.exe -NoProfile -STA -File .\UsageWidget\Tools\generate-icon.ps1
```

Windows 아이콘은 16~256px의 10개 해상도를 포함하며, 벡터 원본을 각 크기로 렌더링합니다.

공통 UI의 아이콘 버튼은 26×26px로 통일하고 기본 안쪽 여백과 SVG 자동 축소를 제거했습니다. Codex의 `⌘`는 글꼴의 기준선 영향을 받지 않도록 `lucide-react`의 `Command` SVG로 표시합니다. 사용량 카드와 계정 추가 목록 모두 컨테이너 중앙에 정렬하며 기존 삼각형 로고는 유지합니다.

## 저장 파일과 이전 버전 호환

기본 저장 위치는 **`%LOCALAPPDATA%\AIUsageWidget`**입니다. 실행 파일 옆에 저장하지 않으므로 같은 Windows 사용자라면 실행 폴더를 옮기거나 배포본을 교체해도 설정과 계정 연결을 유지합니다.

| 파일·폴더 | 보관하는 내용 |
|---|---|
| `window-state.json` | 창 위치·고정·보기 모드·접기 기준 모서리·전환 효과 설정 |
| `dashboard.json` | 표시할 계정 카드 ID와 순서. 빈 목록도 유지하며 삭제한 계정 ID는 제거합니다. |
| `account-profiles.json` | 카드 ID·서비스·기본 이름·별명·연결 사용 여부와 기존 CLI 사용 여부. 이메일·토큰은 넣지 않습니다. |
| `connections.json`, `codex-accounts.json` | 이전 버전 연결·계정 메타데이터와의 호환 정보 |
| `measurement.json` | 집계 주기와 비교 구간 |
| `daily-usage.json` | 현재 날짜, 계정·플랜의 해시, 한도별 관측 기준·누적 변화량·마지막 집계 상태와 최대 48시간의 관측 변화 기록. 이메일·토큰·대화 내용은 넣지 않습니다. |
| `usage-history.json` | 최근 90일의 수신 시각별 %p 증가분과 관측 구간. 계정·플랜·한도는 해시로 구분하며 위젯의 초기화 가능한 누적치와 독립적으로 보존합니다. |
| `Accounts/Codex`, `Accounts/CodexProfiles/<프로필 ID>` | 첫 Codex 계정과 추가 계정별 전용 CLI 홈. 공식 CLI가 인증 상태를 보관·갱신합니다. |
| `Accounts/Claude`, `Accounts/ClaudeProfiles/<프로필 ID>` | 첫 Claude 계정과 추가 계정별 전용 CLI 설정·인증 상태와 상태줄 연결 파일 |
| Claude 연결 폴더의 `claude-usage.json` | 두 한도의 사용률·리셋 시각, 수신 시각과 조회 출처. 대화 내용·세션 경로·인증 토큰은 넣지 않습니다. |
| Claude 연결 폴더의 `usage-account.json` | 캐시가 어느 계정의 값인지 구분하는 이메일 |
| Claude 연결 폴더의 `claude-bridge.json`, `claude-settings-backup-*.json` | 이전 상태줄과 전체 Claude 설정의 백업 |
| `WebView` | WebView2 화면 엔진의 사용자 프로필 |
| `widget.log`, `widget.log.old` | 제한된 오류 로그와 이전 로그 |

전용 Claude 연결 파일은 해당 계정의 `UsageBridge` 폴더에, 기존 CLI 연결 파일은 `SystemClaudeBridge` 폴더에 보관합니다. 이전 버전의 로그인 폴더와 관측 기록은 유지하면서 공통 계정 목록으로 이관합니다. 표시 목록에 없는 계정은 주기적으로 조회하지 않습니다. 계정 항목 삭제는 목록과 별명을 제거하며 CLI 로그인 파일과 관측 기록을 지우지 않습니다.

위젯은 Claude OAuth 토큰을 직접 읽거나 비공개 HTTP API로 전송하지 않습니다. **공식 CLI가 관리하는 인증 상태는 전용 계정 폴더에 남으며, 전체 데이터 폴더에는 이메일과 사용자 설정 백업도 포함됩니다.** 따라서 배포·문제 보고에는 데이터 폴더 전체를 포함하지 마세요. 기존 CLI 계정을 사용하면 인증 상태는 해당 CLI의 기존 저장소에서 관리합니다.

이전 버전에서 데이터 폴더 루트에 설치한 전역 Claude 상태줄을 복원하는 호환 명령은 다음과 같습니다. 현재 계정별 연결은 `설정 → 계정 관리`에서 연결 해제 또는 계정 항목 삭제로 정리합니다. 전용 계정 로그아웃도 원한다면 항목 삭제 전에 진행합니다.

```powershell
.\Release\AIUsageWidget.exe --disconnect-claude
```

## 서비스 확장

계정 목록, 카드 추가·계정 항목 삭제, 로그인·로그아웃과 관측 집계는 서비스 공통으로 처리합니다. 삭제 시 `dashboard.json`, 공통 계정 목록과 이전 형식 호환 메타데이터를 함께 갱신해 재실행 시 삭제한 태그가 되살아나지 않도록 합니다. 기본 서비스 슬롯은 비활성 초기 상태로 되돌리고 추가 프로필은 목록에서 제거합니다. CLI 로그인 폴더와 관측 기록은 보존하며 Claude 상태줄 연결은 해제합니다. 이전 버전의 숨김 계정만 계정 관리에서 다시 표시하거나 삭제할 수 있습니다. 새 AI를 지원하려면 다음 부분을 연결합니다.

1. `AiAccountProfiles.cs`의 `AiServices.Catalog`에 서비스 정보를 추가합니다.
2. `AccountSessions.cs`의 `IAccountSession` 어댑터로 공식 인증과 사용량 조회를 구현합니다.
3. `MainWindow.xaml.cs`의 `CreateSession`에 세션 생성을 연결합니다.
4. 서비스별 프로필 경로와 `UsageModels.cs`의 응답 파싱, UI의 한도 표시를 확인합니다.
5. 계정 격리·없는 한도·조회 실패·로그아웃·계정 삭제·재시작·이전 숨김 계정 복원 등을 통합 검증에 포함합니다.

새 서비스를 연결할 때도 사용자 계정의 인증 정보와 다른 계정의 캐시가 섞이지 않도록 합니다.

## 배포와 소스 공유

- `UsageWidget`, `WidgetUI`, `WidgetVerification`, `docs`는 소스·검증·문서입니다.
- `Release`는 런타임을 포함한 개발 실행용 EXE·DLL·웹 파일입니다. 이전 배포본이 Git에 추적되어 있지만 새 배포에서는 생성된 바이너리를 소스 커밋에 추가하지 않습니다.
- 소스를 수정해 실행본을 갱신할 때는 `build-widget.ps1`을 사용합니다.
- `artifacts`, `bin`, `obj`, `node_modules`, `WidgetUI/dist`는 Git에서 제외합니다.
- 정식 배포는 [배포 가이드](../docs/releasing.md)에 따라 GitHub Releases에 설치 파일·업데이트 패키지·피드를 게시합니다. 사용자 프로필·계정 파일·관측 기록은 포함하지 않습니다.

프로젝트는 `.pdb`의 publish 복사를 막고, publish 완료 시 이전 빌드에서 남은 `.pdb`도 제거합니다. 컴파일러 소스 경로는 가상 경로로 매핑하며 로컬 개발용 심볼은 `bin`에 유지합니다.
