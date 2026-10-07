#!/usr/bin/env bash
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if command -v python3 &>/dev/null; then
    exec python3 "$SCRIPT_DIR/build.py" "$@"
elif command -v python &>/dev/null; then
    exec python "$SCRIPT_DIR/build.py" "$@"
else
    echo "[ERROR] Python 3 was not found in PATH."
    echo "Please install Python 3 to run the LMP Build System."
    exit 1
fi