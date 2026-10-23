#!/bin/bash
# Compile every SSGI shader entry point with the real Windows SDK fxc.exe (10.0.28000) under Wine, SM5.0 targets
# (what Kepler / Maxwell GTX run), /O3 /WX-free. Writes .cso files to ../build/cso (used by the C API / runtime loader).
# Usage: bash fxc-check-ssgi.sh   (needs /home/box/fxc/fxc.exe and a Wine prefix at /home/box/.wine-fxc)
set -u
export WINEDEBUG=-all WINEPREFIX=${WINEPREFIX:-/home/box/.wine-fxc}
FXC=${FXC:-/home/box/fxc/fxc.exe}
cd "$(dirname "$0")/../shaders"
OUT=../build/cso; mkdir -p "$OUT"
ok=0; fail=0
run() {  # entry target file
  local log; log=$(wine "$FXC" /nologo /O3 /T "$2" /E "$1" /Fo "$(winepath -w "$OUT")\\$1.cso" "$3" 2>&1 | tr -d '\r')
  if echo "$log" | grep -q "error X"; then echo "FAIL $1 ($2):"; echo "$log"; fail=$((fail+1));
  else ok=$((ok+1)); w=$(echo "$log" | grep -c "warning X"); echo "ok   $1 ($2) warnings: $w"; [ "$w" -gt 0 ] && echo "$log" | grep "warning X"; fi
}
run CSDownsample    cs_5_0 ssgi_downsample.hlsl
run CSMarch         cs_5_0 ssgi_march.hlsl
run CSFilter        cs_5_0 ssgi_march.hlsl
run CSShift         cs_5_0 ssgi_march.hlsl
run CSPanGate       cs_5_0 ssgi_march.hlsl
run VSFullscreen    vs_5_0 ssgi_composite.hlsl
run PSComposite     ps_5_0 ssgi_composite.hlsl
run CSDepth         cs_5_0 ssrt_depth.hlsl
run CSDepthBlur     cs_5_0 ssrt_depth.hlsl
run CSMarchDepth    cs_5_0 ssrt_march.hlsl
run CSReflect       cs_5_0 ssrt_reflect.hlsl
run CSLightDir      cs_5_0 ssrt_contact.hlsl
run CSContactShadow cs_5_0 ssrt_contact.hlsl
run CSPack          cs_5_0 ssrt_composite.hlsl
run PSCompositeSsrt ps_5_0 ssrt_composite.hlsl
run CSSsrtCut       cs_5_0 ssrt_temporal.hlsl
run CSSsrtAccum     cs_5_0 ssrt_temporal.hlsl
echo "fxc: $ok/$((ok+fail)) SSGI entry points compiled, $fail failed"
[ $fail -eq 0 ]
