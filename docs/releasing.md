# GitHub Releases 배포 가이드

AI Usage Viewer의 정식 배포 경로는 [GitHub Releases](https://github.com/Mellow1213/AI_Usage_Viewer/releases)입니다. AI가 앱의 코드·UI·배포 파일을 수정한 경우, 사용자가 로컬 작업만 요청한 경우를 제외하고 아래 절차를 끝까지 수행합니다. 로컬 빌드, 커밋·푸시 또는 릴리스 초안 생성만으로 배포를 완료했다고 보고하지 않습니다. 문서만 바뀌면 앱 릴리스는 필요하지 않습니다.

## 필수 배포 라인

**소스 수정 → 버전 증가·릴리스 노트 → 빌드·관련 검증 → 소스 커밋·CI 브랜치 푸시 → Windows·Mac CI 통과 → 동일 커밋을 origin/main에 반영 → GitHub 초안에 패키징·업로드·SHA256 검증 → Latest 공개 → 비로그인 업데이트 확인·다운로드 검증**

1. 기존 삼각형 아이콘·애니메이션과 `%LOCALAPPDATA%\AIUsageWidget` 데이터를 유지합니다. 개발·검증은 격리된 프로필에서 수행하며, 관계없는 미완료 변경은 보존합니다.
2. GitHub의 최신 버전보다 큰 번호를 `UsageWidget/UsageWidget.csproj`의 `Version`에 넣고 `docs/releases/<버전>.md`에 사용자에게 보일 변경 내용을 작성합니다. 이미 공개한 버전·태그·파일은 덮어쓰지 않습니다.
3. `build-widget.ps1`로 빌드하고 [개발 안내의 검증](../UsageWidget/README.md#검증) 중 변경에 필요한 항목을 통과시킵니다. 업데이트 동작 변경에는 `--updates-only`와 `verify-widget-update.ps1`을 포함합니다. 개발판은 `run-dev-widget.cmd`로 열고 실사용 설치본과 데이터를 분리합니다.
4. 해당 변경의 소스·테스트·스크립트·문서를 명시적으로 선택해 커밋하고 `codex/` 브랜치 또는 PR로 푸시합니다. `Desktop verification` CI에서 Windows 빌드·Portable 검사와 Apple Silicon·Intel Mac의 네이티브 검사·WebKit 화면·번들 서명 검증이 통과해야 합니다. 동일 커밋을 `origin/main`에 반영합니다. 관계없는 변경이나 생성된 `Release/` 바이너리를 함께 커밋하지 않습니다. 다른 미완료 소스 변경 때문에 배포할 수 없다면 이를 버리거나 검사에서 제외하지 말고 구체적인 차단 사유를 알립니다.
5. 저장소 루트에서 다음을 실행합니다. 아래 번호는 예시이며 후속 배포마다 올립니다.

   ```powershell
   .\publish-widget.ps1 -Version 1.2.0
   .\verify-widget-release.ps1 -Version 1.2.0
   ```

6. 최종 보고에 실제 공개된 버전·릴리스 링크·익명 다운로드 검증 결과를 포함합니다. 인증·네트워크·업로드·검증 오류가 있으면 완료로 보고하지 않습니다. 사용자 앱은 임의로 재시작하거나 업데이트하지 않습니다.

## 스크립트가 확인하는 것

`publish-widget.ps1`에는 Git, GitHub CLI(`gh auth login`), .NET 8 SDK와 Node.js가 필요합니다. 토큰은 GitHub CLI의 인증 저장소에서만 사용하며 앱·설정 파일·배포물에 넣지 않습니다. 저장소는 공개여야 하며, 최신 `origin/main`과 로컬 커밋이 같고 생성 파일 외의 미커밋 변경이 없어야 합니다.

스크립트는 `build-widget.ps1`을 사용하는 `package-widget.ps1`을 호출합니다. 버전별 `artifacts/publish/<버전>`의 별도 폴더에서 빌드하고 `artifacts/releases/<버전>`에 패키징하므로 실행 중인 설치본과 기존 `Release/`를 덮어쓰지 않습니다. 버전·패키지 ID·크기·SHA256을 검사하고, 지정된 소스 커밋으로 GitHub 릴리스 초안을 만든 뒤 모든 파일을 업로드합니다. GitHub가 반환한 업로드 파일의 크기·SHA256까지 확인한 후에만 공개하고 Latest로 지정합니다.

업로드가 중단되면 초안과 로컬 출력물을 보존합니다. 같은 소스 커밋·버전의 출력물임을 확인한 뒤 `-ResumeDraft`를 붙여 재시도할 수 있습니다. 소스가 바뀌었다면 새 버전을 사용합니다. 공개된 릴리스는 이 옵션으로도 수정할 수 없습니다.

Mac 배포물은 동일 커밋의 성공한 `Desktop verification` 실행에서 내려받습니다. 소스 커밋·버전·아키텍처가 일치하는 두 ZIP만 게시하며 해당 실행 번호를 `release-build.json`에 기록합니다. 성공한 CI가 없으면 게시를 거부합니다. Mac은 현재 수동 번들 교체 방식이며 Windows 앱 내 업데이트는 그대로 유지합니다. 공개 후 검증 스크립트는 두 Mac ZIP도 인증 없이 다운로드해 SHA256을 확인합니다.

`verify-widget-release.ps1`은 실제 앱의 GitHub 업데이트 공급자를 토큰 없이 사용해 이전 설치 버전에서 새 버전을 찾고, 공개 패키지를 내려받아 크기·SHA256을 검증합니다. 1.1.0이 사용하는 HTTPS 전환 경로도 확인합니다. 테스트용 설치 경로를 모의하므로 사용자 설치본이나 계정 데이터는 건드리지 않습니다. 결과는 `artifacts/github-release-checks/`에 남습니다.

## 필수 릴리스 파일

| 파일 | 용도 |
|---|---|
| `Mellow.AIUsageViewer-win-Setup.exe` | 다른 PC의 첫 설치 |
| `Mellow.AIUsageViewer-win-Portable.zip` | 설치 없이 실행할 수 있는 전체 앱 |
| `Mellow.AIUsageViewer-<버전>-full.nupkg` | 앱에서 내려받는 업데이트 패키지 |
| `releases.win.json`, `RELEASES` | Velopack 업데이트 피드 |
| `release-build.json` | 버전과 소스 커밋 연결 |
| `SHA256SUMS` | 배포 파일의 SHA256 목록 |
| `AIUsageWidget-<버전>-macos-arm64.zip`, `AIUsageWidget-<버전>-macos-x86_64.zip` | 검증된 Apple Silicon·Intel Mac 앱 번들 |

설정이 없는 새 설치본은 `GithubSource`를 사용해 정식 공개 릴리스를 확인합니다. GitHub 토큰이나 로그인이 필요하지 않으며 초안과 사전 릴리스는 업데이트 대상으로 사용하지 않습니다. 코드 서명 인증서는 현재 구성하지 않았습니다. SHA256은 파일 무결성 확인이며 게시자 코드 서명과는 별개입니다.

## 기존 설치본 전환과 앱 업데이트

기존 1.1.0 설치본이 로컬 폴더를 사용한다면 `use-github-updates.ps1`을 한 번 실행하고 앱을 정상 종료·재실행합니다. 이 스크립트는 `update-source.json`만 변경하며 이전 설정은 `.previous`로 보관합니다. 예전 버전에서도 읽을 수 있는 `https://github.com/Mellow1213/AI_Usage_Viewer/releases/latest/download`를 사용합니다. 새 버전은 같은 주소를 GitHub 공급자로 인식합니다. 별도로 설정한 로컬·HTTPS 테스트 피드도 계속 지원합니다.

다른 PC는 Releases의 설치 파일을 한 번 실행합니다. 이후 **설정 → 앱 업데이트 → 업데이트 확인 → 다운로드 및 업데이트 → 재시작하여 적용**으로 갱신합니다. 시작 직후 한 번 확인하며 실행 중 주기적 확인·자동 다운로드·자동 적용은 하지 않습니다. 계정과 설정은 업데이트 후에도 같은 폴더에 유지됩니다.

`package-widget.ps1`은 패키징만 하며, `publish-widget-local.ps1`은 로컬 테스트 피드를 게시하는 보조 도구입니다. 둘 다 정식 GitHub 배포를 대신하지 않습니다. 수동 ZIP 배포본은 설치 파일로 한 번 전환해야 설치본 업데이트를 사용할 수 있습니다.

관련 공식 문서: [Velopack 배포 형식](https://docs.velopack.io/distributing/overview), [업데이트 공급자](https://docs.velopack.io/integrating/update-sources).
