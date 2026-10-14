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
xcrun swiftc -swift-version 5 -O -target "$(uname -m)-apple-macos13.0" \
  -module-cache-path "$build_dir/module-cache" \
  -framework AppKit -framework WebKit -framework CryptoKit \
  MacUsageWidget/Sources/*.swift -o "$app_dir/Contents/MacOS/AIUsageWidget"
cp MacUsageWidget/Info.plist "$app_dir/Contents/Info.plist"
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
