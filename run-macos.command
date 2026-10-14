#!/bin/bash
set -euo pipefail
project_root="$(cd "$(dirname "$0")" && pwd)"
app_dir="$project_root/artifacts/macos/AI Usage.app"
if [[ ! -x "$app_dir/Contents/MacOS/AIUsageWidget" ]]; then
  "$project_root/build-macos.sh"
fi
open "$app_dir"
