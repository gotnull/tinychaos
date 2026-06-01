#!/usr/bin/env bash
# Build a standalone macOS app bundle: analysis/dist/tinychaos.app
#
#   ./macos/package-mac.sh         build the bundle
#   ./macos/package-mac.sh open    build, then launch it
#
# The bundle is self-contained (no .NET install required to run it), so it
# doubles as the shareable build. It is what gives the app its proper macOS
# identity - Dock icon, "tinychaos" menu title, and a real About box - which a
# plain `dotnet run` cannot, because those come from the .app's Info.plist and
# .icns rather than from Window.Icon.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"          # analysis/
PROJ="$ROOT/src/TinyChaos.Gui/TinyChaos.Gui.csproj"
ICNS="$ROOT/src/TinyChaos.Gui/Assets/tinychaos.icns"
PLIST="$HERE/Info.plist"
DIST="$ROOT/dist"
APP="$DIST/Tiny Chaos.app"               # display name on disk; Dock title comes from CFBundleName
EXEC="tinychaos-gui"                     # must match CFBundleExecutable / AssemblyName

# Pick the runtime identifier for this Mac.
case "$(uname -m)" in
  arm64)  RID="osx-arm64" ;;
  x86_64) RID="osx-x64" ;;
  *) echo "unsupported arch: $(uname -m)" >&2; exit 1 ;;
esac

echo "=== publishing ($RID, self-contained) ==="
PUB="$DIST/publish-$RID"
rm -rf "$PUB"
dotnet publish "$PROJ" -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=false -o "$PUB"

echo "=== assembling $APP ==="
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$PLIST" "$APP/Contents/Info.plist"
cp "$ICNS"  "$APP/Contents/Resources/tinychaos.icns"
cp -R "$PUB"/* "$APP/Contents/MacOS/"
chmod +x "$APP/Contents/MacOS/$EXEC"

# Ad-hoc sign so Gatekeeper lets it launch locally (no Developer ID needed).
echo "=== ad-hoc codesign ==="
codesign --force --deep --sign - "$APP" >/dev/null 2>&1 || \
  echo "warning: codesign failed (app may still run)."

echo "built: $APP"
if [ "${1:-}" = "open" ]; then
  open "$APP"
fi
