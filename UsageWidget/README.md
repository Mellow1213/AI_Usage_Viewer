# AI Usage Widget 개발 안내

설치·사용법, 화면 예시, 문제 해결과 사용자 데이터 설명은 [루트 README](../README.md)를 참고하세요. 이 문서는 소스 빌드와 구현·검증을 다룹니다.

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
.\build-widget.ps1 -RestoreUI
.\run-widget.cmd
```

스크립트는 `WidgetUI`에서 `npm ci`와 `npm run build`를 실행한 다음 `dotnet publish`로 `Release/AIUsageWidget.exe`를 생성합니다. .NET 런타임을 포함하는 Windows x64 배포본이며 `Release` 전체가 실행에 필요합니다. 이후 빌드에서는 `-RestoreUI`를 생략할 수 있고, `node_modules`가 없으면 자동으로 의존성을 설치합니다.

AI Usage Viewer의 실행 파일은 `Release/AIUsageWidget.exe`입니다. 기존 삼각형 아이콘, 애니메이션과 `%LOCALAPPDATA%\AIUsageWidget`의 계정 저장 구조를 유지합니다.

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
| `UsageWidget/WidgetStorage.cs` | 사용자 데이터 저장과 제한된 오류 로그 |
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

네이티브 앱은 빌드된 UI를 `https://widget.local/index.html`로 매핑하고 WebView2 메시지로 상태와 사용자 동작을 전달합니다. UI 변경을 데스크톱 앱에 반영하려면 저장소 루트에서 다시 빌드합니다.

## 검증

UI를 빌드한 다음 저장소 루트에서 실행합니다.

```powershell
node --test .\WidgetUI\src\displayClock.test.js
dotnet run --project .\WidgetVerification\UsageWidget.Verification.csproj -c Release -- artifacts\widget-verification
dotnet run --project .\WidgetVerification\UsageWidget.Verification.csproj -c Release -- artifacts\performance-checks --performance-checks-only
```

단위 테스트는 표시 시각 경계를, 통합 검증은 데이터 파싱·상태줄 복원·계정 격리·계정 삭제·관측 집계·보기 전환 등을 확인합니다. 계정 삭제 검증은 기본·추가·기존 숨김 계정의 태그 제거, 재실행과 이전 형식 복원 시 재등장 방지, 다른 계정과 로그인 파일 보존을 포함합니다. 성능 변경 검증은 동일 상태 전송 생략, 새 데이터 전달, 로고 화면의 시계 해제와 복원을 확인합니다. 결과는 지정한 출력 폴더의 `results.json`에 저장합니다. 기본 검증은 예시 데이터와 별도 Claude 설정 파일을 사용합니다.

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

이 옵션은 계정·설정·관측 기록·WebView 프로필의 저장 위치를 바꿉니다. `WidgetStorage.Log`의 오류 로그는 옵션과 관계없이 기본 `%LOCALAPPDATA%\AIUsageWidget`에 기록됩니다.

## 계정 격리와 서비스 연동

첫 계정은 기존 `Accounts/Codex`, `Accounts/Claude`를 유지합니다. 추가 계정은 `Accounts/CodexProfiles/<프로필 ID>`, `Accounts/ClaudeProfiles/<프로필 ID>`로 분리합니다. Codex에는 `CODEX_HOME`, Claude에는 `CLAUDE_CONFIG_DIR`를 전달하며 공식 CLI가 인증 상태를 관리합니다. 기존 CLI 계정 공유는 서비스별 첫 계정 슬롯에서만 허용합니다.

Codex 전용 프로필은 인증 저장소를 파일로 지정합니다. 앱은 기본 `%APPDATA%/npm/node_modules/@openai/codex` 아래의 x64 native 실행 파일, PATH의 `codex.exe`, 기본 npm 설치의 `bin/codex.js` 순서로 찾습니다. JS 진입점을 사용하는 경우 `node.exe`가 필요합니다. 로그인·계정 조회는 `account/login/start`, `account/login/completed`, `account/login/cancel`, `account/logout`, `account/read`, `account/rateLimits/read`를 사용합니다. [공식 app-server 문서](https://learn.chatgpt.com/docs/app-server)를 참고하세요.

Claude는 `%USERPROFILE%/.local/bin/claude.exe`를 먼저 찾고 PATH의 `claude.exe`를 확인합니다. `claude.cmd`나 WSL의 Linux CLI는 이 탐색 경로에 포함되지 않습니다. 전용 프로필에서는 상속된 API 키·다른 공급자 환경변수가 선택한 구독 계정을 덮어쓰지 않도록 제거합니다.

Claude 인증은 공식 CLI의 `auth login --claudeai`, `auth status --json`, `auth logout`에 위임합니다. 구독 한도는 스트림 JSON의 `initialize`, `get_usage` 제어 요청으로 받으며 사용자 메시지·모델 요청·도구 요청을 보내지 않습니다. 공식 TypeScript SDK의 `usage_EXPERIMENTAL_MAY_CHANGE_DO_NOT_RELY_ON_THIS_API_YET()`에 대응하는 실험 기능입니다. [공식 추가 기록](https://github.com/anthropics/claude-agent-sdk-typescript/releases/tag/v0.3.169)에 따라 이후 CLI 변경 가능성을 고려해야 합니다.

Claude 상태줄 브리지는 한도 사용률·리셋 시각·수신 시각만 추출하고, 자동 조회 실패 시 마지막 수신값을 유지합니다. 계정 이메일은 별도 `usage-account.json`에 저장해 캐시의 계정을 구분합니다. 상태줄 설치 시 전체 설정과 이전 `statusLine`을 백업하며, 해제 시 상태줄만 복원해 이후 변경된 다른 설정을 유지합니다. 기존 상태줄 명령은 Git Bash로 실행해 출력도 보존합니다.

## 갱신과 화면 전환

네이티브 호스트는 2초마다 표시 중인 Claude 계정의 로컬 수신 파일을 확인하고 상태를 집계합니다. 서비스 계정 확인·갱신은 60초 주기로 진행하며, Claude의 실제 자동 사용량 제어 요청은 세션별 5분 간격으로 제한합니다. 수동 새로고침은 이 간격을 기다리지 않고 요청합니다. 계정 조회는 최대 3개까지 동시에 처리합니다.

시간 표시는 App 전체의 매초 렌더링 대신 카드별 표시 시계를 사용합니다. 분 표시·수신 경과 시간·리셋 만료가 바뀌는 시점에 갱신하고 로고만 보기에서는 카드 시계를 해제합니다. 같은 JSON 상태의 반복 전송은 생략하며, 새 수신 시각·계정·설정·화면 전환과 새 `ready` 요청은 계속 전달합니다. 조회·집계와 애니메이션은 유지합니다.

창 드래그는 WebView2의 native 제목줄 영역으로 처리합니다. 삼각형 버튼은 드래그 영역에서 제외하고 글자 영역을 통해 이동합니다. 드래그 종료 시 작업 영역 내 창 중심의 사분면으로 접기 기준 모서리를 정하고, 전환 중에는 기준점을 유지합니다.

### 애니메이션 구현

- 펼치기·접기는 내용 정리(약 0.09초) → 삼각형을 유지하며 실제 창의 폭·높이 변형(약 0.38초) → 새 내용 표시(약 0.13초) 순서입니다. 크기가 바뀔 때 카드 내용이 잘려 보이지 않도록 다음 내용을 숨긴 상태로 미리 측정하고, 창 변형 완료 신호를 받은 후 표시합니다. 선택된 모서리를 유지하고, 전환 중 다시 누르면 현재 크기에서 새 목표로 이어집니다. `설정 → 화면 설정 → 부드러운 화면 전환`로 끌 수 있고 이 설정도 저장합니다. 요청된 전환 효과는 Windows 애니메이션 옵션과 별도로 기본 활성화합니다.
- 전환 중 삼각형 세 개로 구성된 로고가 약 0.48초 동안 easeInOut으로 120도만 회전합니다. 접기는 시계 방향, 펼치기는 반시계 방향입니다. 정지 로고는 끝난 각도를 이어받습니다. 회전과 새 카드의 페이드가 끝날 때까지 정지 로고를 숨긴 뒤 페이드 겹침 없이 교체하며, 크기와 위치도 맞춥니다. 펼치기·접기 효과를 끄면 전환 회전도 생략합니다.
- 창 크기 전환은 WPF 합성 프레임에 맞춰 0.38초 동안 cosine easeInOut으로 진행합니다. 너비·높이·위치를 한 번의 네이티브 호출로 바꾸고, 같은 합성 프레임과 같은 픽셀 크기는 중복 적용하지 않습니다. 카드가 숨겨진 전환 구간에는 WebView 크기를 고정해 웹 화면의 반복 재배치를 피하고, 최종 크기로 맞춘 뒤 내용을 표시합니다. 일반 설정창 크기 변경과 드래그 중에는 정상적인 화면 크기 갱신을 유지합니다.
- 선택한 AI가 모두 연결된 동안에만 삼각형 세 조각의 밝기가 3초 주기로 부드럽게 오르내리며, 1초씩 위상을 나눠 느린 물결처럼 흐릅니다. 연결이 끊기거나 조회 오류가 있는 AI가 하나라도 있으면 로고가 회색으로 바뀌고 밝기 효과가 멈춥니다. 빈 대시보드도 회색입니다. 숨긴 AI는 연결 판단에 포함하지 않습니다. 연결된 상태의 갱신 중에는 물결이 약 0.85초 주기로 빨라지고 로고가 약 1.6초에 한 바퀴씩 easeInOut으로 회전합니다. 연결 확인·로그인 및 펼치기·접기 회전은 회색에서도 유지합니다. 밝기 효과는 추가 사용량 조회를 발생시키지 않으며, Windows 동작 줄이기 설정에서는 생략합니다.
- 잔여량이 감소하면 밝은 잔상이 약 0.65초 유지된 뒤 1.25초 동안 따라 줄어드는 지연 HP 효과를 표시합니다. 한도 초기화와 계정·플랜 변경은 감소 애니메이션의 기준을 새로 잡습니다.

## 삼각형 아이콘

실행 파일·작업 표시줄·시작 화면에는 기존 삼각형 세 개 로고를 사용합니다. 원본은 [usage-widget.svg](Assets/usage-widget.svg)이며 저장소 루트에서의 경로는 `UsageWidget/Assets/usage-widget.svg`입니다. SVG를 변경한 경우 루트에서 다음 명령으로 ICO와 시작 화면 PNG를 다시 생성합니다.

```powershell
powershell.exe -NoProfile -STA -File .\UsageWidget\Tools\generate-icon.ps1
```

Windows 아이콘은 16~256px의 10개 해상도를 포함하며, 벡터 원본을 각 크기로 렌더링합니다.

## 서비스 확장

계정 목록, 카드 추가·숨김, 로그인·로그아웃과 관측 집계는 서비스 공통으로 처리합니다. 새 AI를 지원하려면 다음 부분을 연결합니다.

1. `AiAccountProfiles.cs`의 `AiServices.Catalog`에 서비스 정보를 추가합니다.
2. `AccountSessions.cs`의 `IAccountSession` 어댑터로 공식 인증과 사용량 조회를 구현합니다.
3. `MainWindow.xaml.cs`의 `CreateSession`에 세션 생성을 연결합니다.
4. 서비스별 프로필 경로와 `UsageModels.cs`의 응답 파싱, UI의 한도 표시를 확인합니다.
5. 계정 격리·없는 한도·조회 실패·로그아웃·표시 복원 등을 통합 검증에 포함합니다.

새 서비스를 연결할 때도 사용자 계정의 인증 정보와 다른 계정의 캐시가 섞이지 않도록 합니다.

## 배포와 소스 공유

- `UsageWidget`, `WidgetUI`, `WidgetVerification`, `docs`는 소스·검증·문서입니다.
- `Release`는 런타임을 포함한 실행용 EXE·DLL·웹 파일이며 이 저장소에서는 Git에 함께 추가합니다.
- 소스를 수정해 실행본을 갱신할 때는 `build-widget.ps1`을 사용합니다.
- `artifacts`, `bin`, `obj`, `node_modules`, `WidgetUI/dist`는 Git에서 제외합니다.
- 공개 실행 배포에는 publish 결과인 `Release` 폴더 전체를 포함합니다. 사용자 프로필·계정 파일·관측 기록은 포함하지 않습니다.

프로젝트는 `.pdb`의 publish 복사를 막고, publish 완료 시 이전 빌드에서 남은 `.pdb`도 제거합니다. 컴파일러 소스 경로는 가상 경로로 매핑하며 로컬 개발용 심볼은 `bin`에 유지합니다.
