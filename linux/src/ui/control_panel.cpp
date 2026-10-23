#include "control_panel.h"

#include "imgui.h"
#include "ufx_version.h"

#include <algorithm>
#include <cmath>

float quality_ratio(int quality) {
    switch (quality) {
        case 0: return 1.5f;
        case 1: return 1.7f;
        case 2: return 2.0f;
        default: return 3.0f;
    }
}

int fg_max_multiplier(float refresh_hz, float source_fps) {
    if (refresh_hz < 1.f || source_fps < 1.f) return 3;
    float r = refresh_hz / source_fps;
    int m = static_cast<int>(std::floor(r + 1e-4f));
    if (m < 2) m = 2;
    if (m > 4) m = 4;
    if (r < 4.f && m >= 4) m = 3;
    return m;
}

void control_panel_draw(ControlState* st) {
    ImGui::SetNextWindowPos(ImVec2(0, 0));
    ImGui::SetNextWindowSize(ImGui::GetIO().DisplaySize);
    ImGui::Begin("Universal-FrameFX", nullptr,
                 ImGuiWindowFlags_NoDecoration | ImGuiWindowFlags_NoMove | ImGuiWindowFlags_NoBringToFrontOnFocus);

    ImGui::TextUnformatted("Universal-FrameFX  " UFX_VERSION_STRING);
    ImGui::TextDisabled("Linux desktop preview — Wayland / Vulkan");
    ImGui::Separator();

    ImGui::TextUnformatted("Source");
    ImGui::BulletText("Uses the system screen/window picker (portal).");
    ImGui::BulletText("No custom source list — pick when you press Start.");

    ImGui::Separator();
    ImGui::TextUnformatted("Upscaler");
    const char* ups[] = {"CSR 1.2", "CSR 2.0"};
    ImGui::Combo("Upscaler", &st->upscaler_mode, ups, 2);

    const char* qualities[] = {"Quality 1.5x", "Balanced 1.7x", "Performance 2.0x", "Ultra Performance 3.0x"};
    ImGui::Combo("Quality", &st->quality, qualities, 4);
    ImGui::SliderFloat("Sharpness", &st->sharpness, 0.0f, 2.0f, "%.2f");
    ImGui::TextDisabled("0 = sharpest, 2 = least sharp. Default 0.25.");

    ImGui::Separator();
    ImGui::TextUnformatted("Frame generation");
    const char* fg[] = {"Off", "CSR 2.0"};
    ImGui::Combo("FG", &st->fg_mode, fg, 2);
    int maxm = fg_max_multiplier(st->refresh_hz, st->source_fps > 1.f ? st->source_fps : 30.f);
    if (st->fg_multiplier > maxm) st->fg_multiplier = maxm;
    if (st->fg_multiplier < 2) st->fg_multiplier = 2;
    const char* muls[] = {"2x", "3x", "4x"};
    int mi = st->fg_multiplier - 2;
    if (mi < 0) mi = 0;
    if (mi > 2) mi = 2;
    if (st->fg_mode == 0) ImGui::BeginDisabled();
    if (ImGui::Combo("Multiplier", &mi, muls, maxm >= 4 ? 3 : 2)) st->fg_multiplier = mi + 2;
    if (st->fg_mode == 0) ImGui::EndDisabled();
    if (maxm < 4) ImGui::TextDisabled("4x needs refresh / source fps >= 4.");

    if (!st->running) {
        if (ImGui::Button("Start", ImVec2(120, 0))) st->start_clicked = true;
    } else {
        if (ImGui::Button("Stop", ImVec2(120, 0))) st->stop_clicked = true;
    }
    ImGui::SameLine();
    ImGui::Text("GPU %.2f ms   out %.1f fps", st->gpu_ms, st->out_fps);
    if (st->fg_mode != 0) {
        ImGui::Text("latency +%.1fms (1 source frame)", st->added_latency_ms);
    }

    ImGui::Separator();
    ImGui::TextWrapped("%s", st->status.c_str());

    ImGui::Separator();
    if (ImGui::CollapsingHeader("About")) {
        ImGui::TextWrapped("Frame generation (CSR 2.0): Desktop Mode only. Adds ~1 source frame of latency.");
        ImGui::TextWrapped("Game Mode (gamescope) is not supported.");
        ImGui::TextWrapped("Capture-only: no process injection, no anti-cheat interaction.");
        ImGui::TextWrapped("DMA-BUF zero-copy import is best-effort. Otherwise memfd/CPU upload.");
        ImGui::TextWrapped("Numerically verified on Lavapipe. Live FG on Deck RADV still needs hardware testing.");
        ImGui::Text("https://chopstickshq.com/universal-framefx/");
        ImGui::BeginChild("notice", ImVec2(0, 180), true);
        ImGui::TextUnformatted(st->notice.c_str());
        ImGui::EndChild();
    }

    ImGui::End();
}
