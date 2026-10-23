#!/usr/bin/env bash
# Probe xdg-desktop-portal ScreenCast + PipeWire. Headless hosts SKIP, not fail.
set -u
echo "portal_capability_probe"

have_bus=0
if [[ -n "${DBUS_SESSION_BUS_ADDRESS:-}" ]]; then
  have_bus=1
elif [[ -S "/run/user/$(id -u)/bus" ]]; then
  have_bus=1
fi

if [[ "$have_bus" -eq 0 ]]; then
  echo "SKIPPED: no portal on this host (no session D-Bus)"
  exit 0
fi

if ! command -v busctl >/dev/null 2>&1 && ! command -v gdbus >/dev/null 2>&1 && ! command -v dbus-send >/dev/null 2>&1; then
  echo "SKIPPED: no D-Bus client tools"
  exit 0
fi

if command -v busctl >/dev/null 2>&1; then
  if ! busctl --user introspect org.freedesktop.portal.Desktop /org/freedesktop/portal/desktop org.freedesktop.portal.ScreenCast >/dev/null 2>&1; then
    echo "SKIPPED: no portal on this host (ScreenCast interface missing)"
    exit 0
  fi
fi

pw_ok=0
if [[ -S "/run/user/$(id -u)/pipewire-0" ]]; then
  pw_ok=1
fi
if [[ "$pw_ok" -eq 0 ]]; then
  echo "SKIPPED: portal present but no PipeWire socket"
  exit 0
fi

echo "PASS: ScreenCast portal and PipeWire socket reachable"
exit 0
