#!/bin/bash
set -euo pipefail
project_root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$project_root"
verification_dir="$project_root/artifacts/macos/verification"
mkdir -p "$verification_dir" "$project_root/artifacts/macos/module-cache"
xcrun swiftc -swift-version 5 -parse-as-library -target "$(uname -m)-apple-macos13.0" \
  -module-cache-path "$project_root/artifacts/macos/module-cache" -framework AppKit -framework CryptoKit \
  MacUsageWidget/Sources/Core.swift MacUsageWidget/Sources/CLI.swift MacUsageWidget/Sources/ClaudeBridge.swift \
  MacUsageWidget/Sources/Store.swift MacUsageWidget/Sources/RefreshRotation.swift MacUsageWidget/Sources/UsageHistory.swift MacUsageWidget/Tests/Verification.swift -o "$verification_dir/verify"
test_root="$(mktemp -d "$verification_dir/run.XXXXXX")"
"$verification_dir/verify" "$test_root"
node --test WidgetUI/src/*.test.js
printf 'Results: %s\n' "$test_root/results.json"
