# Universal-FrameFX for Linux (desktop) — third-party notices and attribution

Universal-FrameFX Linux desktop native preview (`1.4.1-linux-preview.1`).
Wayland-first Vulkan (SDL2 + xdg-desktop-portal ScreenCast / PipeWire).
Steam Deck Desktop Mode is a supported host. Not Game Mode / gamescope.

and are not endorsed by AMD. "AMD", "FidelityFX" and "FSR" are trademarks of
Advanced Micro Devices, Inc., used here only in the licence notices.

## 1. MIT-licensed code portions in CSR 1.2 and CSR 2.0 CAS (© 2021 Advanced Micro Devices, Inc.)

Used by: `src/shaders/easu.comp` (EASU edge-adaptive upsampling),
`src/shaders/rcas.comp` (RCAS sharpening), and `src/shaders/csr2_cas.comp`
(same CAS lobe as RCAS), translated from `macos/Resources/Shaders/Spatial.metal`.

```
Copyright (c) 2021 Advanced Micro Devices, Inc. All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
```

## 2. Dear ImGui (MIT)

Vendored under `src/imgui/` (Dear ImGui, https://github.com/ocornut/imgui).

```
The MIT License (MIT)

Copyright (c) 2014-2025 Omar Cornut

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## 3. stb (MIT)

Vendored under `src/third_party/` (`stb_image.h`, `stb_image_write.h`) for
offline PNG decode/encode. https://github.com/nothings/stb

```
This software is available under 2 licenses -- choose whichever you prefer.

ALTERNATIVE A - MIT License
Copyright (c) 2017 Sean Barrett
Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
of the Software, and to permit persons to whom the Software is furnished to do
so, subject to the following conditions:
The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.
THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## 4. Frame generation is not AMD FSR 3 code

CSR 2.0 frame generation on Linux is Universal-FrameFX interpolation
(InterpFast). It is not FSR 3 and contains no FSR 3 source.

## 5. Everything else

Copyright (c) 2026 Chopsticks HQ, MIT — see LICENSE.
