#!/bin/bash
# =============================================================
# WinOptimizer Build Script
# Компілює Agent + Main App → publish/
# =============================================================

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PUBLISH_DIR="$SCRIPT_DIR/publish"
SRC_DIR="$SCRIPT_DIR/src"

RUNTIME="win-x86"
CONFIG="Release"

echo "=== WinOptimizer Build ==="
echo "Output: $PUBLISH_DIR"
echo "Runtime: $RUNTIME"
echo ""

# Очистити publish
rm -rf "$PUBLISH_DIR"
mkdir -p "$PUBLISH_DIR"

# ── Step 1: Build Agent ──
echo "[1/3] Building WinOptimizerAgent..."
dotnet publish "$SRC_DIR/WinOptimizerAgent" \
    -c $CONFIG \
    -r $RUNTIME \
    --self-contained \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:DebugType=none \
    -o "$PUBLISH_DIR/agent-temp"

echo ""

# ── Step 2: Copy Agent to Assets (for embedding) ──
echo "[2/3] Embedding Agent into Assets..."
mkdir -p "$SRC_DIR/WinOptimizer/Assets"
cp "$PUBLISH_DIR/agent-temp/WinOptimizerAgent.exe" "$SRC_DIR/WinOptimizer/Assets/WinOptimizerAgent.exe"
echo "  Copied to: src/WinOptimizer/Assets/WinOptimizerAgent.exe"
echo ""

# ── Step 3: Build Main App ──
echo "[3/3] Building WinOptimizer..."
dotnet publish "$SRC_DIR/WinOptimizer" \
    -c $CONFIG \
    -r $RUNTIME \
    --self-contained \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:DebugType=none \
    -o "$PUBLISH_DIR"

# Cleanup temp agent folder
rm -rf "$PUBLISH_DIR/agent-temp"

echo ""
echo "=== Build Complete ==="
echo "Output: $PUBLISH_DIR/WinOptimizer.exe"
ls -lh "$PUBLISH_DIR/WinOptimizer.exe" 2>/dev/null || echo "(file not found — check build errors)"
