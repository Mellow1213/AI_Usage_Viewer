# Project boundary

This repository is AI Usage Viewer only. Build with build-widget.ps1 and run Release\AIUsageWidget.exe. OpenClaw Dashboard is the separate repository at D:\Projects\OpenClaw_Dashboard, running Release\OpenClawViewer.exe. Do not apply Dashboard icon or WebView changes to AI Usage Viewer.

Keep the existing AI Usage triangle icon, animations, account storage at %LOCALAPPDATA%\AIUsageWidget, and unrelated in-progress changes when working on this project.

# Mandatory release workflow for AI agents

The production update channel is the public GitHub Releases repository https://github.com/Mellow1213/AI_Usage_Viewer. Read docs/releasing.md before any application change. Follow that guide's release pipeline whenever application behavior, UI, or distributed files change; a local build, local feed, Git commit/push, or draft release alone is not completed delivery. Unless the user explicitly requests local-only work or no deployment, finish with a verified public release. Documentation-only changes do not need a new app version.

1. Preserve unrelated changes and the user's running installed app. Build with build-widget.ps1 and run the relevant checks documented in UsageWidget/README.md. Use isolated test profiles, never real account data.
2. Increase UsageWidget/UsageWidget.csproj Version above the latest public release. Add docs/releases/<version>.md and update affected guides. Never reuse or replace a published version or tag.
3. Commit the intended source, tests, scripts and docs and push that commit to origin/main. Do not silently include unrelated in-progress work or generated Release/ binaries. If unrelated uncommitted source prevents release, report the specific blocker; do not discard it or bypass release checks.
4. Run ./publish-widget.ps1 -Version <version>. It builds an isolated Windows x64 package, binds the release to the pushed source commit, uploads installer/portable/full package/feed/checksums to a draft, verifies sizes and SHA256 digests, then publishes it as Latest. -ResumeDraft is only for resuming the exact same version and source commit after an interrupted upload.
5. Run ./verify-widget-release.ps1 -Version <version>. It checks and downloads through the production GitHub provider anonymously and verifies the real package without modifying the installed app/accounts. Report the version, release URL and verification result. Do not claim completion if upload, public visibility or the anonymous check fails.

The app checks once after startup and on the user's update-check button only. Download and restart/apply require the user's app buttons. Do not add periodic polling or silently restart/update the running production app. New PCs install Mellow.AIUsageViewer-win-Setup.exe once from Releases. Existing 1.1.0 local-feed installations can switch using use-github-updates.ps1 followed by one normal restart. publish-widget-local.ps1 is explicitly for local tests and does not fulfill production delivery.
