#!/bin/bash
# Exports the Windows build into out/Nitrogenesis/ and zips it as out/Nitrogenesis-X.Y.Z-win64.zip.
# The version comes only from VERSION; this script writes it everywhere and fails on a mismatch.
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
source <(grep -E '^[A-Z_]+=' "$ROOT/build/versions.txt")
export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1
export WINEPREFIX=$HOME/.wine-nitrogenesis WINEDEBUG=-all XDG_RUNTIME_DIR=${XDG_RUNTIME_DIR:-/tmp/xdg-$(id -u)}
mkdir -p -m700 "$XDG_RUNTIME_DIR"

# Pinned tools only.
[ "$("$GODOT_BIN" --version | tr -d '\r' | cut -d. -f1-5)" = "$GODOT" ] || { echo "Godot is not $GODOT" >&2; exit 1; }
dotnet --list-sdks | grep -q "^$DOTNET_SDK " || { echo ".NET SDK $DOTNET_SDK missing" >&2; exit 1; }

V=$(tr -d '[:space:]' < "$ROOT/VERSION")
[[ $V =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "VERSION must be X.Y.Z, got '$V'" >&2; exit 1; }
sed -i "s/^config\/version=.*/config\/version=\"$V\"/" "$ROOT/app/project.godot"
sed -i -E "s/^application\/(file|product)_version=.*/application\/\1_version=\"$V.0\"/" "$ROOT/app/export_presets.cfg"
grep -q "^config/version=\"$V\"$" "$ROOT/app/project.godot" || { echo "project.godot version mismatch" >&2; exit 1; }
[ "$(grep -c "_version=\"$V.0\"$" "$ROOT/app/export_presets.cfg")" = 2 ] || { echo "export_presets version mismatch" >&2; exit 1; }

dotnet test "$ROOT/tests/Sim.Tests" --nologo -v q

OUT=$ROOT/out/Nitrogenesis
rm -rf "$OUT" && mkdir -p "$OUT"
"$GODOT_BIN" --headless --path "$ROOT/app" --import >/dev/null 2>&1 || true
LOG=$ROOT/out/export.log
"$GODOT_BIN" --headless --path "$ROOT/app" --export-release "Windows Desktop" "$OUT/Nitrogenesis.exe" >"$LOG" 2>&1
! grep -q "ERROR:" "$LOG" || { grep -A3 "ERROR:" "$LOG" >&2; echo "export failed, see $LOG" >&2; exit 1; }
[ -f "$OUT/Nitrogenesis.exe" ] || { echo "export produced no exe" >&2; exit 1; }

wine "$RCEDIT" "$OUT/Nitrogenesis.exe" --set-icon "$(winepath -w "$ROOT/app/icon.ico")" \
  --set-file-version "$V.0" --set-product-version "$V.0" \
  --set-version-string ProductName Nitrogenesis --set-version-string FileDescription Nitrogenesis \
  --set-version-string CompanyName Husarp --set-version-string OriginalFilename Nitrogenesis.exe

ZIP=$ROOT/out/Nitrogenesis-$V-win64.zip
rm -f "$ZIP" && (cd "$ROOT/out" && zip -qr "$ZIP" Nitrogenesis)
echo "Built $ZIP ($(du -h "$ZIP" | cut -f1))"
