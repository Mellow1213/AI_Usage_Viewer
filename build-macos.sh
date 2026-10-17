#!/bin/bash
set -euo pipefail
project_root="$(cd "$(dirname "$0")" && pwd)"
cd "$project_root"
if [[ "$(uname -s)" != Darwin ]]; then echo 'macOS에서 실행해주세요.' >&2; exit 1; fi
build_dir="$project_root/artifacts/macos"
app_dir="$build_dir/AI Usage.app"
mkdir -p "$build_dir/module-cache" "$app_dir/Contents/MacOS" "$app_dir/Contents/Resources"
if [[ ! -d WidgetUI/node_modules/vite ]]; then
  (cd WidgetUI && npm ci --no-audit --no-fund)
fi
(cd WidgetUI && npm run build -- --outDir "$build_dir/Web")
swift_flags=(-swift-version 5 -O)
if [[ "${AI_USAGE_DEBUG_SYMBOLS:-0}" == 1 ]]; then
  swift_flags+=(-g -debug-prefix-map "$project_root=/_/AIUsageWidget")
fi
xcrun swiftc "${swift_flags[@]}" -target "$(uname -m)-apple-macos13.0" \
  -module-cache-path "$build_dir/module-cache" \
  -framework AppKit -framework WebKit -framework CryptoKit \
  MacUsageWidget/Sources/*.swift -o "$app_dir/Contents/MacOS/AIUsageWidget"
if [[ "${AI_USAGE_DEBUG_SYMBOLS:-0}" == 1 ]]; then
  xcrun dsymutil "$app_dir/Contents/MacOS/AIUsageWidget" -o "$build_dir/AIUsageWidget.dSYM"
  # Keep symbols in CI diagnostics, outside the distributed app bundle.
  rm -rf "$app_dir/Contents/MacOS/AIUsageWidget.dSYM"
  strip -S "$app_dir/Contents/MacOS/AIUsageWidget"
fi
cp MacUsageWidget/Info.plist "$app_dir/Contents/Info.plist"
version="$(sed -nE 's/.*<Version>([^<]+)<\/Version>.*/\1/p' UsageWidget/UsageWidget.csproj)"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-beta\.[1-9][0-9]*)?$ ]] || { echo 'Invalid application version' >&2; exit 1; }
version_core="${version%%-*}"
# Apple's version fields stay numeric; retain the full SemVer separately for beta provenance.
/usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $version_core" "$app_dir/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleVersion $version_core" "$app_dir/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Add :AIUsageVersion string $version" "$app_dir/Contents/Info.plist"
rm -rf "$app_dir/Contents/Resources/Web"
cp -R "$build_dir/Web" "$app_dir/Contents/Resources/Web"
icon_dir="$build_dir/AIUsageWidget.iconset"
mkdir -p "$icon_dir"
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" UsageWidget/Assets/usage-widget.png --out "$icon_dir/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -z "$double" "$double" UsageWidget/Assets/usage-widget.png --out "$icon_dir/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$icon_dir" -o "$app_dir/Contents/Resources/AIUsageWidget.icns"
codesign --force --sign - "$app_dir"
printf 'Built: %s\n' "$app_dir"
