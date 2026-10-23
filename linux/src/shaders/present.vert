// present.vert — fullscreen triangle. Translated from Present.metal.
// Copyright (c) 2026 Chopsticks HQ

#version 450

layout(location = 0) out vec2 v_uv;

void main() {
    // Vulkan NDC y-down: uv (0,0) at top-left of the visible window.
    vec2 pos[3] = vec2[](vec2(-1.0, -1.0), vec2(-1.0, 3.0), vec2(3.0, -1.0));
    vec2 uvs[3] = vec2[](vec2( 0.0,  0.0), vec2( 0.0, 2.0), vec2(2.0,  0.0));
    gl_Position = vec4(pos[gl_VertexIndex], 0.0, 1.0);
    v_uv = uvs[gl_VertexIndex];
}
