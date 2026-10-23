// present.frag — fit/letterbox blit. Translated from Present.metal.
// Copyright (c) 2026 Chopsticks HQ

#version 450

layout(location = 0) in vec2 v_uv;
layout(location = 0) out vec4 outColor;

layout(std140, binding = 0) uniform PresentUniforms {
    vec2 scale;
    vec2 offset;
    vec4 background;
} u;

layout(binding = 1) uniform sampler2D src;

void main() {
    vec2 uv = v_uv * u.scale + u.offset;
    if (uv.x < 0.0 || uv.y < 0.0 || uv.x > 1.0 || uv.y > 1.0) {
        outColor = u.background;
        return;
    }
    outColor = vec4(texture(src, uv).rgb, 1.0);
}
