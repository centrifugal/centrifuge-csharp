#!/usr/bin/env bash
# Runs the SDK in a Unity Editor, headless.
#
#   unity/run.sh test  [unity-version]   smoke tests against the docker-compose Centrifugo
#   unity/run.sh probe [unity-version]   connection probe (PROBE_WS, PROBE_TOKEN, PROBE_CHANNEL to configure)
#
# Without a version, the newest Editor installed through Unity Hub is used. See unity/README.md.
set -euo pipefail
MODE=${1:-}
case "$MODE" in
  test)  METHOD=CentrifugeUnityTests.RunBatch; FILTER='\[unity-test\]|^  (PASS|FAIL)|^        ' ;;
  probe) METHOD=CentrifugeProbe.RunBatch; FILTER='\[probe\]' ;;
  *) echo "usage: unity/run.sh test|probe [unity-version]"; exit 2 ;;
esac
HERE=$(cd "$(dirname "$0")" && pwd)

case "$(uname)" in
  Darwin) EDITORS="/Applications/Unity/Hub/Editor"; BIN="Unity.app/Contents/MacOS/Unity" ;;
  *)      EDITORS="$HOME/Unity/Hub/Editor";         BIN="Editor/Unity" ;;
esac
VER=${2:-$(ls "$EDITORS" 2>/dev/null | sort -t. -k1,1n -k2,2n -k3,3n | tail -1)}
[ -n "$VER" ] || { echo "No Unity Editor found in $EDITORS"; exit 2; }
UNITY=${UNITY_EDITOR:-"$EDITORS/$VER/$BIN"}
[ -x "$UNITY" ] || { echo "Unity $VER not found at $UNITY (set UNITY_EDITOR to override)"; exit 2; }

WORK="$HERE/work/$VER"   # a project copy per version, so versions don't re-import each other's Library
LOG="$HERE/work/$MODE-$VER.log"

dotnet publish "$HERE/../src/Centrifugal.Centrifuge/Centrifugal.Centrifuge.csproj" \
  -c Release -f netstandard2.1 -o "$HERE/work/publish" -v q --nologo
mkdir -p "$WORK/Assets/Plugins"
rsync -a --delete --exclude Plugins --exclude "*.meta" "$HERE/Project/Assets/" "$WORK/Assets/"
rsync -a "$HERE/Project/Packages/" "$WORK/Packages/"
# The SDK's netstandard2.1 dependencies. Unity doesn't resolve NuGet dependencies, and it doesn't ship
# System.Runtime.CompilerServices.Unsafe (Google.Protobuf fails to load without it).
for d in Centrifugal.Centrifuge Google.Protobuf Microsoft.Extensions.Logging.Abstractions System.Threading.Channels System.Runtime.CompilerServices.Unsafe; do
  cp "$HERE/work/publish/$d.dll" "$WORK/Assets/Plugins/"
done

echo "Unity $VER: running $MODE (the first run per version imports the project and takes a few minutes)..."
code=0
"$UNITY" -batchmode -nographics -projectPath "$WORK" -executeMethod "$METHOD" -logFile "$LOG" || code=$?
grep -E "$FILTER" "$LOG" | grep -v 'sdk Trace' || true
echo "exit code: $code (full Unity log: $LOG)"
if [ "$code" -ne 0 ] && ! grep -qE "$FILTER" "$LOG"; then
  echo "--- no output from the run; errors from the Unity log ---"
  grep -iE 'error|exception|license' "$LOG" | head -30 || true
fi
exit "$code"
