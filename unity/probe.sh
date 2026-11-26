#!/usr/bin/env bash
# Runs the issue #35 probe in a given Unity Editor version, headless.
# Usage: unity/probe.sh <unity-version>     e.g. unity/probe.sh 6000.0.85f2
# Needs Centrifugo on ws://localhost:8000 (override with PROBE_WS), see unity/README.md.
set -euo pipefail
VER=${1:?usage: probe.sh <unity-version>}
HERE=$(cd "$(dirname "$0")" && pwd)
WORK="$HERE/work/$VER"   # a project copy per version, so versions don't re-import each other's Library
LOG="$HERE/work/probe-$VER.log"

case "$(uname)" in
  Darwin) UNITY="/Applications/Unity/Hub/Editor/$VER/Unity.app/Contents/MacOS/Unity" ;;
  *)      UNITY="$HOME/Unity/Hub/Editor/$VER/Editor/Unity" ;;
esac
UNITY=${UNITY_EDITOR:-$UNITY}
[ -x "$UNITY" ] || { echo "Unity $VER not found at $UNITY (set UNITY_EDITOR to override)"; exit 2; }

dotnet publish "$HERE/../src/Centrifugal.Centrifuge/Centrifugal.Centrifuge.csproj" \
  -c Release -f netstandard2.1 -o "$HERE/work/publish" -v q --nologo
mkdir -p "$WORK/Assets/Plugins"
rsync -a --delete --exclude Plugins --exclude "*.meta" "$HERE/CentrifugeProbe/Assets/" "$WORK/Assets/"
rsync -a "$HERE/CentrifugeProbe/Packages/" "$WORK/Packages/"
# Google.Protobuf needs System.Runtime.CompilerServices.Unsafe, which Unity does not ship.
for d in Centrifugal.Centrifuge Google.Protobuf Microsoft.Extensions.Logging.Abstractions System.Threading.Channels System.Runtime.CompilerServices.Unsafe; do
  cp "$HERE/work/publish/$d.dll" "$WORK/Assets/Plugins/"
done

echo "Running Unity $VER (the first run imports the project and takes a few minutes)..."
code=0
"$UNITY" -batchmode -nographics -projectPath "$WORK" -executeMethod CentrifugeProbe.RunBatch -logFile "$LOG" || code=$?
grep -F '[probe]' "$LOG" | grep -v 'sdk Trace' || true
echo "exit code: $code (full Unity log: $LOG)"
if [ "$code" -ne 0 ] && ! grep -qF '[probe]' "$LOG"; then
  echo "--- no probe output; errors from the Unity log ---"
  grep -iE 'error|exception|license' "$LOG" | head -30 || true
fi
exit "$code"
