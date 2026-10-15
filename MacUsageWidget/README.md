# macOS 개발 안내

이 문서는 Swift + AppKit + WebKit 호스트의 빌드·구현·검증을 다룹니다. 설치·메뉴 막대 조작·업데이트는 [macOS 사용 안내](../README.md#macos-버전), 계정·관측 사용량의 공통 설명은 [루트 README](../README.md)를 참고하세요. Windows WPF + WebView2 호스트는 [Windows 개발 안내](../UsageWidget/README.md)에 분리했습니다.

## 개발 환경과 빌드

| 항목 | 요구 사항 |
|---|---|
| 운영체제 | macOS 13 이상 |
| 네이티브 도구 | Xcode Command Line Tools: `xcode-select --install` |
| UI 도구 | npm과 Node.js `^20.19.0 \|\| >=22.12.0` |
| 실제 계정 조회 | macOS에서 실행할 수 있는 공식 Codex CLI 또는 Claude Code CLI와 해당 구독 계정 |

저장소 루트에서 실행합니다.

```bash
./build-macos.sh
./run-macos.command
```

`build-macos.sh`는 공통 React UI를 `artifacts/macos/Web`에 빌드하고 Swift 실행 파일·웹 UI·아이콘을 **`artifacts/macos/AI Usage.app`**에 담은 뒤 임시 코드 서명을 적용합니다. Windows의 `Release`는 변경하지 않습니다. `run-macos.command`는 앱이 없을 때만 빌드하며, 이미 있으면 그대로 실행합니다. 소스 변경 뒤에는 앱을 종료하고 빌드 스크립트를 다시 실행하세요.

| 빌드 결과 | 값 |
|---|---|
| 앱 이름 | `AI Usage.app` |
| 번들 ID | `com.aiusagewidget.macos` |
| 최소 macOS | `13.0` |
| 아키텍처 | 빌드한 Mac의 `uname -m`: Apple Silicon `arm64`, Intel `x86_64` |
| 화면 엔진 | macOS 기본 WebKit |
| Dock 표시 | `LSUIElement` 메뉴 막대 앱이므로 표시하지 않음 |

앱 자체 실행에는 Node.js·.NET·WebView2가 필요하지 않습니다. 공식 CLI와 로그인 상태는 번들에 포함되지 않으며, npm으로 설치한 CLI의 Node.js 요구 사항은 별도입니다.

## 앱 배포와 이동

배포 단위는 **`.app` 번들 전체**입니다. `AI Usage.app`을 응용 프로그램 폴더 등으로 옮겨 실행할 수 있으며 저장소의 `Web` 폴더를 따로 복사할 필요가 없습니다. 번들 내부 실행 파일만 분리하면 웹 UI와 리소스를 찾을 수 없습니다.

현재 스크립트는 호스트 아키텍처 하나만 빌드하며 Universal Binary를 만들지 않습니다. 공개 릴리즈에는 대상 아키텍처별 검증, Developer ID 서명과 공증을 별도로 준비해야 합니다. 로컬의 ad-hoc 서명 검증 성공이 다른 Mac에서의 배포 신뢰성 검증을 대신하지 않습니다.

업데이트는 앱을 종료한 뒤 `.app` 전체를 교체합니다. 사용자 데이터는 `~/Library/Application Support/AIUsageWidget`에 있으므로 앱 파일 이동·교체와 별도로 유지됩니다. Claude 상태줄은 앱의 실행 파일 경로를 사용하며, 새 위치에서 새로고침하면 앱이 설치한 기존 명령의 경로를 갱신합니다. 사용자가 이후 직접 바꾼 상태줄은 주기적 갱신으로 덮어쓰지 않습니다.

## 소스 구조

| 경로 | 역할 |
|---|---|
| `Sources/main.swift` | 앱 진입점과 `--statusline` 실행 모드 |
| `Sources/App.swift` | 메뉴 막대 아이콘, 팝오버, WebKit 메시지, 표시 상태와 화면 검증 |
| `Sources/Store.swift` | 계정 목록·동작, 자동 조회, 상태 전달과 보기 전환 방향 |
| `Sources/Core.swift` | 계정·환경설정·응답 모델, 한도 파서, 관측 집계와 저장 |
| `Sources/CLI.swift` | CLI 탐색·프로필별 환경·RPC·로그인·Terminal 실행 |
| `Sources/ClaudeBridge.swift` | 상태줄 설치·복원, 원래 출력 보존, 계정별 캐시 |
| `Sources/RefreshRotation.swift` | 메뉴 막대 삼각형의 한 바퀴 회전 상태 |
| `Tests/Verification.swift`, `verify.sh` | 로컬 격리 검증과 실행 스크립트 |
| `Info.plist` | 번들 정보, 최소 macOS, 메뉴 막대 앱 설정 |
| `../WidgetUI` | Windows·macOS 공통 React 카드와 설정 화면 |

## 메뉴 막대와 공통 UI

`NSStatusItem` 클릭으로 `NSPopover`를 열고 `WKWebView`에 공통 UI를 표시합니다. `platform: "macos"` 상태에 따라 카드 상단에 새로고침을 배치하고 Windows의 핀·모서리 배치·로고만 보기 조작은 제외합니다. 하단 실행 상태 문구도 표시하지 않습니다.

카드는 바깥 클릭, 상단 삼각형 또는 `Escape`로 닫힙니다. 열린 채로 유지하는 설정은 없습니다. 메뉴 막대 아이콘 우클릭 메뉴는 `사용량 보기`·`새로고침`·`종료`이며, 카드에 포커스가 있을 때 `⌘R`·`F5`로 갱신합니다.

설정은 톱니바퀴에서 `계정 관리`·`사용량 측정`·`화면 설정`으로 나뉩니다. 계정을 펼쳐 로그인·별명·삭제를 처리하며, 설정을 닫으면 이전 자세히·간략히 보기로 돌아갑니다. 버튼의 SVG 자동 축소와 기본 여백을 제거했고 Codex `⌘`는 중앙 정렬한 SVG를 사용합니다.

### 조회와 회전

- 앱 실행 중 60초마다 표시 중인 연결 계정을 확인합니다. Codex는 이때 한도를 조회하고, Claude의 자동 사용량 요청은 계정별 5분 간격으로 제한합니다. 수동 새로고침은 제한 간격을 기다리지 않으며 동시에 최대 3개 계정을 조회합니다.
- 계정·사용량 조회 묶음이 끝나면 앱이 시작한 Codex `app-server`를 종료합니다. 다음 조회 때 새로 초기화하며 로그인 중이거나 응답 대기 중인 연결은 종료하지 않습니다. 인증 파일과 수신값은 그대로 유지합니다.
- 팝오버를 닫아도 조회와 집계는 계속합니다. 화면을 처음 열기 전에는 WebKit을 만들지 않으며, 닫은 뒤 5초가 지나면 웹 화면과 메시지 연결을 해제합니다. 다시 열 때 최신 상태와 설정 페이지·펼친 계정·저장 전 별명·스크롤을 복원합니다. 짧은 닫기·열기는 같은 화면을 재사용합니다.
- 자세히 → 간략히는 시계 방향 120도, 간략히 → 자세히는 반시계 방향 120도입니다. `logoRotation`과 `logoRotationStep`으로 공통 UI에 방향과 도착 각도를 전달합니다.
- 새로고침·로그인 시작 시 삼각형은 약 1.6초 동안 한 바퀴만 회전합니다. 조회가 빨리 끝나도 회전을 마치고, 느리게 끝나도 회전을 계속 추가하지 않습니다. 회전 중 겹친 갱신은 새 회전을 예약하지 않습니다.
- 팝오버를 닫을 때 카드의 회전을 정리하며, 다시 열 때 지나간 회전을 재생하지 않습니다. 메뉴 막대 회전은 단조 증가 시간으로 계산해 잠자기 후에도 밀린 프레임을 따라 돌지 않습니다.
- 메뉴 막대의 20×20pt 아이콘은 세 삼각형의 도형 중심 `(20, 70/3)`을 이미지 중앙에 맞춰 그립니다. 도형 모양은 유지하면서 중심 이탈과 가장자리 잘림을 방지합니다.

메뉴 막대 회전·밝기와 팝오버 열기·닫기는 앱의 전환 효과 설정과 macOS 동작 줄이기를 확인합니다. 공통 UI의 조회·숫자·막대 효과는 `prefers-reduced-motion`, 보기 전환은 호스트의 `animateTransitions` 상태를 따릅니다.

## CLI 탐색과 로그인

공식 설치 안내: [Codex CLI](https://learn.chatgpt.com/docs/codex/cli), [Claude Code](https://code.claude.com/docs/en/setup).

CLI는 `PATH`, `~/.local/bin`, `/opt/homebrew/bin`, `/usr/local/bin`, npm-global·Volta·asdf·Bun·nvm 경로에서 찾습니다. Codex는 `/Applications/Codex.app`과 `/Applications/ChatGPT.app`의 알려진 포함 CLI 경로도 확인합니다.

사용자 지정 실행 파일은 `AI_USAGE_CODEX_PATH`, `AI_USAGE_CLAUDE_PATH`에 절대 경로로 지정할 수 있습니다. 기존 앱을 종료한 뒤 해당 환경변수를 전달해 직접 실행합니다. 아래 경로는 설치 위치에 맞게 바꾸세요.

```bash
AI_USAGE_CODEX_PATH=/absolute/path/to/codex \
AI_USAGE_CLAUDE_PATH=/absolute/path/to/claude \
"artifacts/macos/AI Usage.app/Contents/MacOS/AIUsageWidget"
```

Codex는 공식 [app-server](https://learn.chatgpt.com/docs/app-server)의 초기화, 계정·한도 조회, 로그인·취소·로그아웃 RPC를 사용합니다. 브라우저 로그인은 확인된 공식 HTTPS 주소만 엽니다. Claude는 `auth status --json`, `auth login --claudeai`, `auth logout`을 사용하고 로그인은 Terminal에서 사용자가 직접 완료합니다. 두 서비스 모두 로그인 대기 시간은 5분입니다.

Claude 사용량은 `initialize`·`get_usage` 제어 요청으로 조회하며 메시지·모델·도구 요청을 보내지 않습니다. 이 기능은 실험적이므로 CLI 버전에 따라 달라질 수 있습니다. 공식 [상태줄](https://code.claude.com/docs/en/statusline)에서도 한도와 수신 시각을 추출해 자동 조회의 보조 수신 경로로 사용합니다. 원래 상태줄 출력은 `/bin/sh`로 실행해 보존하며 전체 입력·대화 내용을 사용량 파일에 저장하지 않습니다.

## 저장 파일과 계정 격리

기본 위치는 `~/Library/Application Support/AIUsageWidget`입니다. 데이터 폴더를 분리해 실행하려면 다음 옵션을 사용합니다.

```bash
"artifacts/macos/AI Usage.app/Contents/MacOS/AIUsageWidget" \
  --data-dir "$PWD/artifacts/macos/dev-data"
```

| 파일·폴더 | 내용 |
|---|---|
| `mac-settings.json` | 계정 프로필·표시 순서·별명·연결 방식·보기 모드·전환 효과·집계 설정 |
| `daily-usage-mac.json` | 계정 식별 해시, 한도별 관측 기준·누적 변화량·집계 상태와 최대 48시간의 변화 기록 |
| `Accounts/Codex`, `Accounts/Claude` | 각 서비스 첫 계정의 전용 CLI 홈 |
| `Accounts/CodexProfiles/<프로필 ID>`, `Accounts/ClaudeProfiles/<프로필 ID>` | 추가 계정별 전용 CLI 홈 |
| Claude 연결 폴더의 `UsageBridge` 또는 루트의 `SystemClaudeBridge` | 전용 계정 또는 기존 CLI 계정의 상태줄 연결 파일 |
| 브리지 폴더의 `bridge.json`, `settings-backup.json` | 앱이 설치한 명령, 이전 상태줄과 Claude 설정 백업 |
| 브리지 폴더의 `snapshot.json`, `usage-cache.json` | 상태줄 수신값과 자동 조회 캐시 |
| 브리지 폴더의 `identity.json` | 캐시를 구분하는 계정 식별값. 이메일이 포함될 수 있습니다. |
| `Terminal` | Claude 로그인·실행용 스크립트와 진행 상태 파일 |
| `mac-app.lock` | 동일 데이터 폴더에 대한 중복 앱 실행 방지 |

Codex 전용 프로필에는 `CODEX_HOME`과 파일 인증 저장소를, Claude에는 `CLAUDE_CONFIG_DIR`를 사용합니다. 인증 상태는 공식 CLI가 관리합니다. 기존 CLI 계정 사용은 서비스별 첫 계정 슬롯에서만 허용하며 연결 해제는 CLI 자체를 로그아웃하지 않습니다.

계정 항목 삭제는 표시 목록·별명·연결 메타데이터를 정리합니다. 첫 서비스 슬롯은 비활성 기본값으로 되돌리고 추가 프로필은 목록에서 제거해 삭제한 태그가 다시 나타나지 않도록 합니다. CLI 로그인 폴더와 관측 기록은 보존하며 해당 Claude 상태줄은 해제합니다. 전체 데이터 폴더에는 인증 상태·계정 식별값·설정 백업이 포함될 수 있으므로 배포에 넣지 않습니다.

Windows의 `window-state.json`, `dashboard.json` 등과는 별도 형식이며 두 플랫폼 사이의 설정·계정 자동 동기화는 없습니다.

## 검증

### 로컬 검증

저장소 루트에서 실행합니다.

```bash
./MacUsageWidget/verify.sh
```

임시 계정 폴더와 가짜 CLI로 응답 파싱, 다중 계정 격리, 집계·초기화·재시작, 상태줄 복원, RPC 왕복, 시간 초과·취소, 설정 저장, 계정 삭제와 한 바퀴 회전을 확인합니다. 이어서 공통 UI의 표시 시계 단위 테스트를 실행합니다. 실제 사용자 인증이나 네트워크는 사용하지 않습니다. 결과 경로는 스크립트가 출력합니다.

### 실제 WebKit 화면 검증

앱을 빌드한 뒤 실행합니다. 별도의 데이터 폴더와 예시 계정만 사용합니다.

```bash
open -n 'artifacts/macos/AI Usage.app' --args \
  --data-dir "$PWD/artifacts/macos/ui-test-data" \
  --fixture --smoke-test "$PWD/artifacts/macos/ui-test" --verify-memory
```

`smoke.json`의 `passed: true`와 `menu-bar.png`를 확인합니다. 카드를 표시하고 자동 검증 후 테스트 앱만 종료합니다.

- ready 메시지, 메뉴 막대 항목, 카드 수·화면 높이, 상단 새로고침·하단 문구 제거
- 자세히 → 간략히의 시계 방향, 간략히 → 자세히의 반시계 방향 120도 회전
- 100ms·2400ms 조회 상태에서 역회전 없이 한 바퀴 회전
- 닫힌 동안 12회 갱신하거나 회전 도중 닫았다 다시 열 때 밀린 회전 없음
- 다시 연 뒤 연속 갱신이 한 바퀴로 합쳐짐
- 웹 화면 해제 후 설정·미저장 별명·보기 모드 복원 및 새로고침·전환 회전 유지

검증 팝오버는 다른 앱에 포커스가 옮겨져도 유지됩니다. 일반 앱의 팝오버는 바깥 클릭으로 닫힙니다. 이 검증은 실제 Claude OAuth 로그인이나 배포 대상 모든 Mac에서의 실행을 보장하지 않습니다.

### 메모리 측정

빌드 후 새 출력 폴더를 지정합니다. 예시 계정으로 앱과 관련 보조 프로세스 전체의 physical footprint를 수집하고, 여러 번 닫고 열면서 화면 복원 시간도 기록합니다.

```bash
python3 MacUsageWidget/Tests/measure-memory.py \
  'artifacts/macos/AI Usage.app' artifacts/macos/memory-check
```

`measurement.json`에 화면별 평균과 원시 표본, `memory-benchmark.json`에 반복 복원 결과를 저장합니다. 이 측정은 예시 UI만 사용하므로 실제 CLI 조회 프로세스의 비용은 별도로 확인합니다.

변경 전후 수치와 측정 범위는 [메모리 비교 기록](../docs/memory.md)에 있습니다.

### 실제 Codex 조회 검증

`verify.sh` 실행 후 현재 Codex CLI 로그인으로 계정·한도를 조회하려면 별도로 실행합니다. 로그인 변경이나 모델 요청 없이 실제 조회를 3회 수행하며, 각 조회가 끝난 뒤 프로세스를 종료하고 다시 연결되는지 확인합니다. 계정 이메일·한도 수치는 콘솔에 출력하지 않습니다.

```bash
artifacts/macos/verification/verify --live-codex "$PWD/artifacts/macos/live-codex-check"
```

## WebKit 경계

앱은 번들 안의 정적 파일만 `aiusage://widget`으로 제공합니다. 외부 페이지 탐색·새 창·카메라·마이크 접근을 차단하며 네이티브 메시지는 해당 출처의 최상위 프레임만 받습니다. 웹 데이터 저장소는 비영구적이고 계정 설정·관측 기록은 네이티브 저장 파일로 관리합니다. CLI 오류 원문은 화면에 노출하지 않습니다.
