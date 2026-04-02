#!/usr/bin/env bash
# build.sh — cross-compile ts_voice_forwarder.dll (Windows x64) from WSL2/Linux
#
# Requirements:
#   sudo apt install gcc-mingw-w64-x86-64
#
# Usage:
#   ./build.sh                     # builds DLL in ./out/
#   ./build.sh --deploy            # also copies DLL to TS3 plugin dir on Windows
#   ./build.sh --deploy --ts3-dir "/mnt/c/Users/Pc/AppData/Roaming/TS3Client/plugins"
#
# After the build, enable in TS3:  Plugins → ts_voice_forwarder → Activate
# Then create the config file (see README.md) with your WSL2 IP and restart TS3.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT_DIR="$SCRIPT_DIR/out"
DLL_NAME="ts_voice_forwarder.dll"

# Default TS3 per-user plugin directory on Windows (accessible from WSL2)
DEFAULT_TS3_PLUGIN_DIR="/mnt/c/Users/Pc/AppData/Roaming/TS3Client/plugins"

# ── Argument parsing ──────────────────────────────────────────────────────
DEPLOY=false
TS3_PLUGIN_DIR="$DEFAULT_TS3_PLUGIN_DIR"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --deploy)    DEPLOY=true;            shift ;;
        --ts3-dir)   TS3_PLUGIN_DIR="$2";   shift 2 ;;
        *) echo "Unknown argument: $1"; exit 1 ;;
    esac
done

# ── Check cross-compiler ──────────────────────────────────────────────────
if ! command -v x86_64-w64-mingw32-gcc &>/dev/null; then
    echo "ERROR: MinGW cross-compiler not found."
    echo "Install with:  sudo apt install gcc-mingw-w64-x86-64"
    exit 1
fi

# ── Compile ───────────────────────────────────────────────────────────────
echo "==> Compiling $DLL_NAME ..."
mkdir -p "$OUT_DIR"

x86_64-w64-mingw32-gcc \
    -shared \
    -O2 \
    -Wall \
    -Wno-unused-parameter \
    -I "$SCRIPT_DIR/include" \
    -o "$OUT_DIR/$DLL_NAME" \
    "$SCRIPT_DIR/plugin.c" \
    -lws2_32

echo "==> Built: $OUT_DIR/$DLL_NAME"
ls -lh "$OUT_DIR/$DLL_NAME"

# ── (Optional) Deploy ─────────────────────────────────────────────────────
if [[ "$DEPLOY" == "true" ]]; then
    if [[ ! -d "$TS3_PLUGIN_DIR" ]]; then
        echo "WARNING: TS3 plugin directory not found: $TS3_PLUGIN_DIR"
        echo "  Create it first or specify with --ts3-dir"
        exit 1
    fi
    cp "$OUT_DIR/$DLL_NAME" "$TS3_PLUGIN_DIR/$DLL_NAME"
    echo "==> Deployed to: $TS3_PLUGIN_DIR/$DLL_NAME"
    echo
    echo "Next steps:"
    echo "  1. Restart TS3 (or reload plugins via Plugins menu)"
    echo "  2. Enable the plugin: Plugins → ts_voice_forwarder → Activate"
    echo "  3. Ensure ts_voice_forwarder.cfg exists in the plugin dir (see README)"
fi

echo "==> Done."
