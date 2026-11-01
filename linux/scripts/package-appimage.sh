#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="${UFX_VERSION:-1.4.1-linux-beta11}"
STEM="Universal-FrameFX-${VERSION}-linux-x86_64"
DIST="$ROOT/dist"
APPDIR="$DIST/AppDir"
BIN="$ROOT/build/ufx-linux"
mkdir -p "$DIST" "$APPDIR/usr/bin" "$APPDIR/usr/share/ufx-linux" "$APPDIR/usr/share/icons/hicolor/256x256/apps"

if [[ ! -x "$BIN" ]]; then
  echo "missing $BIN — run scripts/build.sh first" >&2
  exit 1
fi

cp -a "$BIN" "$APPDIR/usr/bin/ufx-linux"
cp -a "$ROOT/build/shaders" "$APPDIR/usr/share/ufx-linux/shaders"
mkdir -p "$APPDIR/usr/share/ufx-linux/models"
cp -a "$ROOT/../Models/csr_ai_x2.weights.json" "$APPDIR/usr/share/ufx-linux/models/csr_ai_x2.weights.json"
cp -a "$ROOT/NOTICE.md" "$APPDIR/usr/share/ufx-linux/NOTICE.md"
cp -a "$ROOT/LICENSE" "$APPDIR/usr/share/ufx-linux/LICENSE"
cp -a "$ROOT/README.md" "$APPDIR/usr/share/ufx-linux/README.md"
mkdir -p "$APPDIR/usr/share/ufx-linux/docs" "$APPDIR/usr/share/ufx-linux/scripts"
cp -a "$ROOT/docs/." "$APPDIR/usr/share/ufx-linux/docs/"
cp -a "$ROOT/scripts/ufx-gamescope-launch.sh" "$APPDIR/usr/share/ufx-linux/scripts/"
cp -a "$ROOT/scripts/ufx-gamescope-launch.sh" "$APPDIR/usr/bin/ufx-gamescope-launch"
cp -a "$ROOT/resources/icon.png" "$APPDIR/usr/share/icons/hicolor/256x256/apps/universal-framefx.png"
cp -a "$ROOT/resources/icon.png" "$APPDIR/universal-framefx.png"

cat > "$APPDIR/usr/bin/ufx-linux-wrapper" <<'EOF'
#!/usr/bin/env bash
HERE="$(dirname "$(readlink -f "$0")")"
export UFX_SHADER_DIR="${UFX_SHADER_DIR:-$HERE/../share/ufx-linux/shaders}"
cd "$HERE/../share/ufx-linux" 2>/dev/null || true
LOGDIR="${HOME:-/tmp}/.local/share/universal-framefx"
mkdir -p "$LOGDIR"
LOG="$LOGDIR/last-run.log"
if [[ -t 1 ]]; then
  "$HERE/ufx-linux" "$@" 2>&1 | tee -a "$LOG"
  exit "${PIPESTATUS[0]}"
else
  exec "$HERE/ufx-linux" "$@" >>"$LOG" 2>&1
fi
EOF
chmod +x "$APPDIR/usr/bin/ufx-linux-wrapper"

cat > "$APPDIR/universal-framefx.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Universal-FrameFX
Comment=Vulkan/Wayland upscaler + CSR 2.0 FG; Game Mode experimental
Exec=ufx-linux-wrapper
Icon=universal-framefx
Categories=Utility;Graphics;
Terminal=false
EOF
rm -f "$APPDIR/AppRun"
cat > "$APPDIR/AppRun" <<'EOF'
#!/usr/bin/env bash
HERE="$(dirname "$(readlink -f "$0")")"
export UFX_SHADER_DIR="${UFX_SHADER_DIR:-$HERE/usr/share/ufx-linux/shaders}"
cd "$HERE/usr/share/ufx-linux" 2>/dev/null || true
LOGDIR="${HOME:-/tmp}/.local/share/universal-framefx"
mkdir -p "$LOGDIR"
LOG="$LOGDIR/last-run.log"
if [[ -t 1 ]]; then
  "$HERE/usr/bin/ufx-linux" "$@" 2>&1 | tee -a "$LOG"
  exit "${PIPESTATUS[0]}"
else
  exec "$HERE/usr/bin/ufx-linux" "$@" >>"$LOG" 2>&1
fi
EOF
chmod +x "$APPDIR/AppRun"

# Always ship a runnable zip of ELF + shaders + libs (same stem).
STAGE="$DIST/${STEM}-stage"
rm -rf "$STAGE"
mkdir -p "$STAGE/shaders" "$STAGE/lib" "$STAGE/models"
cp -a "$BIN" "$STAGE/ufx-linux"
cp -a "$ROOT/build/shaders/." "$STAGE/shaders/"
cp -a "$ROOT/../Models/csr_ai_x2.weights.json" "$STAGE/models/csr_ai_x2.weights.json"
cp -a "$ROOT/NOTICE.md" "$ROOT/LICENSE" "$ROOT/README.md" "$ROOT/resources/icon.png" "$STAGE/"
mkdir -p "$STAGE/docs" "$STAGE/scripts"
cp -a "$ROOT/docs/." "$STAGE/docs/"
cp -a "$ROOT/scripts/ufx-gamescope-launch.sh" "$STAGE/scripts/"
chmod +x "$STAGE/scripts/ufx-gamescope-launch.sh"
# Bundle non-Vulkan runtime deps next to the ELF.
ldd "$BIN" | awk '/=>/ {print $3}' | while read -r so; do
  [[ -z "$so" || ! -f "$so" ]] && continue
  base="$(basename "$so")"
  case "$base" in
    libvulkan.so*|libvulkan_lvp*|libvulkan_radeon*|libGLX*|libGL.so*|ld-linux*) continue ;;
  esac
  cp -a "$so" "$STAGE/lib/" || true
done
cat > "$STAGE/run.sh" <<'EOF'
#!/usr/bin/env bash
HERE="$(dirname "$(readlink -f "$0")")"
export LD_LIBRARY_PATH="$HERE/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
export UFX_SHADER_DIR="$HERE/shaders"
LOGDIR="${HOME:-/tmp}/.local/share/universal-framefx"
mkdir -p "$LOGDIR"
LOG="$LOGDIR/last-run.log"
if [[ -t 1 ]]; then
  "$HERE/ufx-linux" "$@" 2>&1 | tee -a "$LOG"
  exit "${PIPESTATUS[0]}"
else
  exec "$HERE/ufx-linux" "$@" >>"$LOG" 2>&1
fi
EOF
chmod +x "$STAGE/run.sh" "$STAGE/ufx-linux"
ZIP="$DIST/${STEM}.zip"
(cd "$DIST" && rm -f "$ZIP" && cd "$STAGE" && zip -qr "$ZIP" .)
echo "zip: $ZIP"
echo "zip size: $(stat -c %s "$ZIP") bytes"
echo "zip sha256: $(sha256sum "$ZIP" | awk '{print $1}')"
TGZ="$DIST/${STEM}.tar.gz"
tar -C "$STAGE" -czf "$TGZ" .
echo "tar.gz: $TGZ"
echo "tar.gz size: $(stat -c %s "$TGZ") bytes"
echo "tar.gz sha256: $(sha256sum "$TGZ" | awk '{print $1}')"

# Try linuxdeploy / AppImage (optional; zip is the guaranteed artifact).
LINUXDEPLOY="${LINUXDEPLOY:-}"
if [[ -z "$LINUXDEPLOY" ]]; then
  CAND="$DIST/linuxdeploy-x86_64.AppImage"
  if [[ ! -x "$CAND" ]]; then
    if command -v wget >/dev/null; then
      wget -q -O "$CAND" \
        "https://github.com/linuxdeploy/linuxdeploy/releases/download/continuous/linuxdeploy-x86_64.AppImage" || true
      chmod +x "$CAND" || true
    fi
  fi
  LINUXDEPLOY="$CAND"
fi

APPIMAGE="$DIST/${STEM}.AppImage"
if [[ -x "$LINUXDEPLOY" ]]; then
  export LINUXDEPLOY_OUTPUT_VERSION="$VERSION"
  # Do not bundle Vulkan loader / GPU ICDs.
  export LINUXDEPLOY_PLUGIN_APPIMAGE_EXTRACT_AND_RUN=1
  set +e
  if [[ ! -e /dev/fuse ]]; then
    # AppImages often need FUSE; linuxdeploy itself is an AppImage — extract it.
    EXTRACT="$DIST/linuxdeploy-extracted"
    rm -rf "$EXTRACT"
    (cd "$DIST" && "$LINUXDEPLOY" --appimage-extract >/dev/null 2>&1)
    if [[ -x "$DIST/squashfs-root/AppRun" ]]; then
      mv "$DIST/squashfs-root" "$EXTRACT"
      LD="$EXTRACT/AppRun"
    else
      LD=""
    fi
  else
    LD="$LINUXDEPLOY"
  fi
  if [[ -n "${LD:-}" ]]; then
    # Host Vulkan loader/driver must be used (Deck RADV / Mesa).
    rm -f "$APPDIR"/usr/lib/libvulkan.so* || true
    "$LD" --appdir "$APPDIR" \
      --executable "$APPDIR/usr/bin/ufx-linux" \
      --desktop-file "$APPDIR/universal-framefx.desktop" \
      --icon-file "$ROOT/resources/icon.png" \
      --exclude-library 'libvulkan.so.1' \
      --exclude-library 'libvulkan.so*' \
      --output appimage
    rm -f "$APPDIR"/usr/lib/libvulkan.so* || true
    # linuxdeploy writes next to CWD
    FOUND="$(ls -1 "$PWD"/Universal-FrameFX*.AppImage 2>/dev/null | tail -n1 || true)"
    if [[ -z "$FOUND" ]]; then
      FOUND="$(ls -1 "$DIST"/Universal-FrameFX*.AppImage 2>/dev/null | tail -n1 || true)"
    fi
    if [[ -n "$FOUND" ]]; then
      mv -f "$FOUND" "$APPIMAGE"
    fi
  fi
  set -e
fi

# Fallback: package AppDir as a fake-but-runnable AppImage-named squash? Prefer real AppImage.
# If linuxdeploy failed, copy a self-extracting tar labeled .AppImage is misleading.
# Instead, still emit the zip (done) and if no AppImage, skip.

if [[ -f "$APPIMAGE" ]]; then
  # Verify Vulkan loader was not bundled.
  if command -v python3 >/dev/null; then
    :
  fi
  echo "appimage: $APPIMAGE"
  echo "appimage size: $(stat -c %s "$APPIMAGE") bytes"
  echo "appimage sha256: $(sha256sum "$APPIMAGE" | awk '{print $1}')"
  if command -v appimagetool >/dev/null; then
    :
  fi
  # Extract and scan for vulkan loader
  TMP="$DIST/appimage-extract"
  rm -rf "$TMP"
  if (cd "$DIST" && "$APPIMAGE" --appimage-extract >/dev/null 2>&1); then
    mv "$DIST/squashfs-root" "$TMP" || true
  fi
  if [[ -d "$TMP" ]]; then
    if find "$TMP" -name 'libvulkan.so*' | grep -q .; then
      echo "WARNING: bundled libvulkan.so — remove from AppImage" >&2
    else
      echo "AppImage exclude check: no bundled libvulkan.so"
    fi
  fi
else
  echo "AppImage not produced (linuxdeploy/FUSE unavailable). Zip is the shippable artifact."
fi
