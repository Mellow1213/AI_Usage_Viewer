# 반복 갱신 최적화 전후 비교

이 문서는 2026-10-14의 Windows 반복 갱신 최적화 기록입니다. 이후의 조회 프로세스 정리와 macOS 화면 해제·복원은 [메모리 비교 기록](memory.md)을 참고하세요.

측정일: 2026-10-14. 변경 전 기준은 `da58ef3`의 앱 코드이며, 변경 후에는 아래 두 부분만 최적화했습니다.

- 매초 App 전체를 다시 렌더링하던 시계를 카드별 표시 시계로 이동했습니다. 분 표시·수신 경과 시간·5분 경과·한도 만료가 바뀌는 시점에만 깨어나며, 로고 화면에서는 카드 시계를 해제합니다. 새 데이터와 화면 복원 시에는 즉시 갱신합니다.
- 네이티브 호스트가 이전과 같은 JSON 상태를 WebView2로 전달하지 않도록 했습니다. 새 수신 시각, 계정, 설정, 동기화 및 화면 전환 상태는 계속 전달하며, 새 `ready` 요청에는 전체 상태를 보냅니다.

원본 조회·집계·캐시 읽기 주기, 삼각형 물결과 회전, 숫자·막대·창 펼치기 애니메이션은 유지했습니다. WebView2를 종료하거나 메모리 축소 모드를 설정하지 않았습니다.

## 측정 조건

- 동일 PC, 논리 프로세서 16개. WPF 호스트와 해당 WebView2 프로세스 그룹 전체(각 측정 6개 프로세스)를 포함했습니다.
- Codex·Claude 두 카드에 같은 고정 예시 사용량을 표시하고, 변경 없는 상태를 2초마다 네이티브 `Publish`에 전달했습니다. 개인 계정·실제 AI CLI·네트워크 조회는 포함하지 않습니다.
- 각 화면에서 10초 준비 후 30초 측정했습니다. 자세히 보기 → 로고 → 로고 → 자세히 보기 순서로 각각 두 번 측정했습니다. 전후 측정 중 다른 빌드나 검증은 실행하지 않았습니다.
- CPU는 포함된 프로세스들의 CPU 시간 증가량을 실제 경과 시간과 논리 프로세서 수로 나눈 백분율입니다. 작업 관리자의 전체 CPU 용량 기준과 같은 척도입니다.
- RAM은 5초마다 표본을 수집한 private working set의 평균입니다. [QueryWorkingSet](https://learn.microsoft.com/en-us/windows/win32/api/psapi/nf-psapi-queryworkingset)의 [공유 가능 페이지 플래그](https://learn.microsoft.com/en-us/windows/win32/api/psapi/ns-psapi-psapi_working_set_block)를 이용해 공유 페이지의 중복 합산을 피했습니다. 표의 MB는 1,048,576바이트 기준입니다.
- CPU·RAM 표는 두 측정의 평균입니다. Chromium `Performance.getMetrics`의 `TaskDuration`은 렌더러 주 스레드 작업 시간으로, 전체 CPU나 JavaScript 실행 시간만을 뜻하지 않습니다.

## 결과

| 화면 | CPU 변경 전 | CPU 변경 후 | 평균 RAM 변경 전 | 평균 RAM 변경 후 |
|---|---:|---:|---:|---:|
| 자세히 보기 | 0.152% | 0.104% | 137.5MB | 119.9MB |
| 로고만 보기 | 0.118% | 0.109% | 126.7MB | 113.6MB |

이번 측정에서 자세히 보기의 CPU는 약 31.5%, RAM은 약 12.8% 감소했습니다. 로고의 평균 CPU는 약 8.0%, RAM은 약 10.4% 감소했습니다. 로고 CPU 표본 범위는 변경 전 0.115~0.122%, 변경 후 0.096~0.122%로 겹치므로, 로고 CPU 감소율을 안정적인 보장 수치로 해석할 수는 없습니다.

| 30초 구간 지표 | 자세히 보기 변경 전 → 후 | 로고 변경 전 → 후 |
|---|---:|---:|
| 상태 메시지 수, 두 번 각각 | 15 / 27 → 0 / 0 | 15 / 16 → 0 / 0 |
| 렌더러 주 스레드 작업 시간 평균 | 0.273초 → 0.033초 | 0.063초 → 0.030초 |

상태 메시지 수는 화면 크기 등의 호스트 상태 전달도 포함합니다. 수치가 변하지 않는 상태에서도 전송하던 동작은 별도 통합 검증으로 확인했으며, 최적화 후에는 반복 `Publish` 호출이 메시지를 생성하지 않습니다.

두 번의 짧은 고정 데이터 측정이며, 장시간 운용이나 실제 계정 조회를 포함한 소비량의 보장값은 아닙니다. 이전에 사용자 앱에서 관측한 약 183MB에는 실제 Codex CLI 등이 포함되어 있으므로 위 표와 직접 비교하지 않습니다. WebView2와 애니메이션의 기본 비용은 남아 있습니다.

## 재현 및 검증

UI를 빌드한 다음 아래 명령을 실행합니다. 결과는 지정한 `artifacts` 폴더의 `performance.json`에 저장합니다. 별도 데이터 폴더와 연결 예시를 사용하며 개인 계정으로 로그인하지 않습니다.

```powershell
dotnet run --project .\WidgetVerification\UsageWidget.Verification.csproj -c Release -- artifacts\performance --performance-only
dotnet run --project .\WidgetVerification\UsageWidget.Verification.csproj -c Release -- artifacts\performance-checks --performance-checks-only
node --test .\WidgetUI\src\displayClock.test.js
```

적용 전후 원시 측정값: [변경 전 JSON](performance-before.json), [변경 후 JSON](performance-after.json).

추가 검증은 리셋·수신 시각 경계, 새 데이터 전달, 로고 화면의 시계 해제, 화면 복원, 동기화 애니메이션 및 전체 상태 재전달을 확인합니다. 기존 위젯 통합 검증도 함께 실행합니다.

최종 검증 결과: 표시 시각 경계 단위 테스트 8개, 최적화 통합 검증 11개, 기존 위젯 통합 검증 223개를 통과했습니다. 삼각형 애니메이션 코드는 기존 HEAD와 동일하고 CSS 빌드 자산도 동일합니다.
