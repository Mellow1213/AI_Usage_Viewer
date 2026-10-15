# macOS 메뉴 막대 앱

`NSStatusItem`과 `NSPopover`로 메뉴 막대 앱을 만들고, `WKWebView` 안에 기존 React 화면을 표시합니다. Swift 실행부가 공식 CLI의 로그인·구독 한도 조회를 처리합니다. Windows WPF 프로젝트, 실행 파일, 계정 저장 위치는 변경하지 않습니다.

## 빌드와 실행

macOS 13 이상, Xcode Command Line Tools, Node.js `^20.19.0 || >=22.12.0`, npm이 필요합니다. 저장소 루트에서 실행합니다.

```bash
./build-macos.sh
./run-macos.command
```

출력은 `artifacts/macos/AI Usage.app`입니다. Apple Silicon Mac에서는 arm64, Intel Mac에서는 x86_64로 빌드하며 Windows의 `Release`를 덮어쓰지 않습니다. 앱을 다른 폴더로 옮길 때는 `.app` 전체를 옮깁니다. 실행 자체에는 Node.js·.NET이 필요하지 않지만, 각 서비스의 공식 CLI는 필요합니다. npm으로 설치한 CLI가 Node.js를 요구하는 것은 별개입니다.

앱은 Dock에 나타나지 않습니다. 메뉴 막대의 삼각형 아이콘을 클릭해 카드를 열고, 카드 상단의 새로고침 버튼이나 `⌘R`·`F5`로 사용량을 갱신합니다. 아이콘 우클릭 메뉴에서도 새로고침하거나 종료할 수 있습니다. 창 밖을 클릭하면 카드가 자동으로 닫힙니다. macOS에는 창 고정 기능이 없으며 이전 버전의 고정 설정도 적용하지 않습니다. Windows의 화면 모서리 배치는 메뉴 막대 모드에서 제공하지 않습니다. 자세히·간략히 보기, 여러 계정, 별명, 관측 사용량, 비교 구간은 공통 화면을 사용합니다.

톱니바퀴 설정 화면은 `계정 관리`, `사용량 측정`, `화면 설정`으로 나뉩니다. 각 하위 화면의 뒤로가기로 상위 설정에 돌아가며, 닫기 버튼은 이전 사용량 화면으로 돌아갑니다. 간략히 보기에서도 설정을 열고 닫아 보기 모드를 유지할 수 있습니다.

자세히 보기에서 간략히 보기로 바꾸면 삼각형 로고가 시계 방향으로 120도 회전하고, 자세히 보기로 돌아올 때는 반시계 방향으로 120도 회전합니다.

새로고침 시 카드와 메뉴 막대의 삼각형은 같은 방향으로 한 바퀴 회전합니다. 조회가 빨리 끝나도 회전을 마친 뒤 멈추며, 원래 각도로 역회전하지 않습니다. 카드가 닫히면 카드의 회전을 정리하고, 다시 열 때는 최신 사용량만 표시합니다. 닫혀 있는 동안의 갱신이나 회전 중 겹친 갱신은 추가 회전으로 쌓이지 않습니다.

`계정 관리`에서 계정을 펼치면 로그인·별명·삭제 기능이 함께 표시됩니다. 상단 `+` 또는 계정 관리의 `AI 계정 추가`로 서비스를 추가하면 새 계정의 로그인 항목이 열립니다. `계정 항목 삭제`를 누르면 카드와 저장된 계정 항목·별명이 목록에서 제거되며, 재실행해도 `다시 표시` 항목으로 남지 않습니다. 이전 버전에서 숨긴 계정도 같은 화면에서 삭제할 수 있습니다. 기존 CLI 로그인 파일과 로컬 기록은 보존하고, 해당 계정의 Claude 상태줄 연결은 해제합니다.

## CLI 연결과 저장

계정마다 공식 CLI를 실행해 인증합니다. 새 모델 요청이나 대화를 생성하지 않습니다.

- Codex: 공식 `app-server`의 `initialize`, `account/read`, `account/rateLimits/read`, 로그인·취소·로그아웃 RPC를 사용합니다. 브라우저 로그인은 공식 HTTPS 주소만 엽니다. [공식 app-server 문서](https://learn.chatgpt.com/docs/app-server)
- Claude: `auth status --json`, `auth login --claudeai`, `auth logout`을 사용합니다. 로그인은 Terminal에서 사용자가 직접 완료합니다. 사용량 조회는 Windows판과 같은 실험적 `get_usage` 제어 요청이며, CLI 버전에 따라 지원 여부가 달라질 수 있습니다.
- Claude 상태줄: 연결 시 기존 `statusLine`을 백업하고 앱의 `--statusline` 모드로 필요한 한도 필드만 수신합니다. 기존 명령의 출력은 보존합니다. 연결 해제 시 이후 변경된 다른 설정을 유지하며 원래 상태줄을 복원합니다. [공식 상태줄 문서](https://code.claude.com/docs/en/statusline)

CLI는 PATH, `~/.local/bin`, Homebrew 경로, npm-global·Volta·asdf·nvm 경로에서 찾습니다. Codex는 설치된 Codex/ChatGPT 앱의 포함 CLI도 확인합니다. 사용자 지정 경로가 필요하면 앱 실행 전에 `AI_USAGE_CODEX_PATH`, `AI_USAGE_CLAUDE_PATH`를 절대 경로로 지정할 수 있습니다.

기본 데이터 위치는 `~/Library/Application Support/AIUsageWidget`입니다. 첫 서비스 계정은 `Accounts/Codex`, `Accounts/Claude`, 추가 계정은 서비스별 `Profiles` 하위 폴더로 구분합니다. Codex에는 전용 `CODEX_HOME`과 파일 인증 저장소, Claude에는 전용 `CLAUDE_CONFIG_DIR`를 전달합니다. 인증 토큰은 앱이 직접 읽거나 복사하지 않으며 공식 CLI가 관리합니다. 기존 CLI 계정 연결 해제는 CLI 자체를 로그아웃하지 않습니다.

`mac-settings.json`에 선택한 계정·보기·집계 설정을, `daily-usage-mac.json`에 관측 기록을 저장합니다. 관측 기록은 수신한 잔여 비율의 변화량이며 토큰 수나 하루 전체 사용량이 아닙니다. 실행 중 모니터링하지 못한 사용량은 복원하지 않습니다.

## 검증

```bash
./MacUsageWidget/verify.sh
```

이 검증은 임시 계정 폴더와 가짜 CLI 프로세스를 사용합니다. 응답 파싱, 다중 계정 경로, 집계·초기화·재시작, 상태줄 복원, RPC 왕복, 요청 시간 초과·취소, 설정 저장을 검사합니다. 실제 사용자 인증이나 네트워크는 사용하지 않습니다.

실제 WebKit에서 카드와 간략히 보기를 검증하고 스크린샷을 저장하려면 다음을 실행합니다. 예시 데이터만 사용하는 별도 인스턴스입니다.

```bash
open -n 'artifacts/macos/AI Usage.app' --args \
  --data-dir "$PWD/artifacts/macos/ui-test-data" \
  --fixture --smoke-test "$PWD/artifacts/macos/ui-test"
```

결과는 `smoke.json`, 화면은 `menu-bar.png`입니다. `passed: true`를 확인합니다. 100ms·2400ms 조회 상태에서 회전 프레임도 측정해 역회전 없이 정확히 한 바퀴 도는지 검사합니다. 닫힌 카드에서 12회 갱신하거나 회전 도중 닫았다가 다시 열 때 밀린 회전이 없는지, 다시 연 뒤 연속 갱신이 한 바퀴로 합쳐지는지도 확인합니다. 검증 창은 다른 앱에 포커스가 옮겨져도 검사를 계속하도록 유지되며, 실제 앱은 바깥을 클릭하면 닫힙니다. 이 검증은 실제 서비스의 OAuth 로그인 성공까지 보장하지 않습니다.

설치된 Codex CLI의 현재 로그인으로 실제 조회만 검증하려면 `verify.sh` 실행 후 아래 명령을 사용합니다. 로그인 변경이나 모델 요청은 하지 않으며, 계정 이메일·한도 수치는 콘솔에 출력하지 않습니다.

```bash
artifacts/macos/verification/verify --live-codex "$PWD/artifacts/macos/live-codex-check"
```

앱은 번들 내부 정적 파일만 제공하는 `aiusage://widget`을 사용하고, 외부 페이지 탐색·새 창·카메라·마이크 접근을 차단합니다. 네이티브 메시지는 해당 출처의 최상위 프레임만 받습니다. CLI 오류 원문은 인증 정보 노출을 피하기 위해 화면·로그에 출력하지 않습니다.

로컬 빌드는 임시 코드 서명(ad-hoc signing)을 사용합니다. 공개 배포 시에는 Developer ID 서명, 공증 및 배포 대상 아키텍처 검증을 별도로 수행해야 합니다.
