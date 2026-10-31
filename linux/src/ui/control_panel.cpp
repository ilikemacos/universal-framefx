#include "control_panel.h"

#include "imgui.h"
#include "platform/larkquill_permission.h"
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

static void larkquill_open(ControlState* st) {
    if (larkquill::open_web()) st->status = "Opened LarkquillAI in your browser.";
    else st->status = std::string("Could not open a browser. LarkquillAI is at ") + larkquill::kUrl;
}

static void larkquill_draw(ControlState* st) {
    if (st->larkquill_clicked) {
        st->larkquill_clicked = false;
        std::string root = larkquill::agent_root();
        larkquill::Answer a = larkquill::load(root);
        if (a == larkquill::Answer::Yes) larkquill_open(st);
        else if (a == larkquill::Answer::No) ImGui::OpenPopup("LarkquillAI##off");
        else ImGui::OpenPopup("LarkquillAI##ask");
    }

    bool ask_no = false;
    ImGui::SetNextWindowPos(ImGui::GetMainViewport()->GetCenter(), ImGuiCond_Appearing, ImVec2(0.5f, 0.5f));
    if (ImGui::BeginPopupModal("LarkquillAI##ask", nullptr, ImGuiWindowFlags_AlwaysAutoResize)) {
        ImGui::TextUnformatted(larkquill::kQuestion);
        ImGui::Spacing();
        if (ImGui::Button("Yes", ImVec2(90, 0))) {
            larkquill::save(larkquill::agent_root(), larkquill::Answer::Yes);
            ImGui::CloseCurrentPopup();
            larkquill_open(st);
        }
        ImGui::SameLine();
        if (ImGui::IsWindowAppearing()) ImGui::SetKeyboardFocusHere();
        if (ImGui::Button("No", ImVec2(90, 0)) || ImGui::IsKeyPressed(ImGuiKey_Escape)) {
            larkquill::save(larkquill::agent_root(), larkquill::Answer::No);
            ImGui::CloseCurrentPopup();
            ask_no = true;
        }
        ImGui::EndPopup();
    }
    if (ask_no) ImGui::OpenPopup("LarkquillAI##off");

    ImGui::SetNextWindowPos(ImGui::GetMainViewport()->GetCenter(), ImGuiCond_Appearing, ImVec2(0.5f, 0.5f));
    if (ImGui::BeginPopupModal("LarkquillAI##off", nullptr, ImGuiWindowFlags_AlwaysAutoResize)) {
        ImGui::TextUnformatted(larkquill::kUnavailable);
        ImGui::Spacing();
        if (ImGui::IsWindowAppearing()) ImGui::SetKeyboardFocusHere();
        if (ImGui::Button("OK", ImVec2(90, 0)) || ImGui::IsKeyPressed(ImGuiKey_Escape)) ImGui::CloseCurrentPopup();
        ImGui::EndPopup();
    }
}

void control_panel_draw(ControlState* st) {
    ImGui::SetNextWindowPos(ImVec2(0, 0));
    ImGui::SetNextWindowSize(ImGui::GetIO().DisplaySize);
    ImGui::Begin("Universal-FrameFX", nullptr,
                 ImGuiWindowFlags_NoDecoration | ImGuiWindowFlags_NoMove | ImGuiWindowFlags_NoBringToFrontOnFocus);

    ImGui::TextUnformatted("Universal-FrameFX  " UFX_VERSION_STRING);
    if (st->game_mode) {
        ImGui::TextColored(ImVec4(1.f, 0.85f, 0.2f, 1.f), "Game Mode (experimental, hardware-unverified)");
    } else {
        ImGui::TextDisabled("Linux desktop — Wayland / Vulkan");
    }
    ImGui::TextDisabled("Session: %s   Capture: %s", st->session_label.c_str(), st->capture_backend.c_str());
    ImGui::Separator();

    ImGui::TextUnformatted("Source");
    if (st->game_mode) {
        ImGui::BulletText("Prefers gamescope PipeWire node, then portal.");
        ImGui::BulletText("See docs/GAMEMODE.md — Deck QA still needed.");
    } else {
        ImGui::BulletText("Uses the system screen/window picker (portal).");
        ImGui::BulletText("No custom source list — pick when you press Start.");
    }

    ImGui::Separator();
    ImGui::TextUnformatted("Upscaler");
    const char* ups[] = {"CSR 1.2", "CSR 2.0"};
    ImGui::Combo("Upscaler", &st->upscaler_mode, ups, 2);
    ImGui::TextDisabled("CSR AI / FSR2+ / XeSS: not on Linux yet (docs/BLOCKERS.md).");

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
    if (ImGui::Button("LarkquillAI", ImVec2(120, 0))) st->larkquill_clicked = true;
    ImGui::SameLine();
    ImGui::TextDisabled("Asks first, then opens in your browser.");
    larkquill_draw(st);

    ImGui::Separator();
    ImGui::TextWrapped("%s", st->status.c_str());

    ImGui::Separator();
    if (ImGui::CollapsingHeader("About")) {
        ImGui::TextWrapped("Frame generation (CSR 2.0 InterpFast) adds ~1 source frame of latency.");
        if (st->game_mode) {
            ImGui::TextWrapped("Game Mode: experimental. Capture via gamescope PipeWire (preferred) or portal. Not hardware-verified on this preview.");
        } else {
            ImGui::TextWrapped("Desktop Mode: portal ScreenCast. Nested Game Mode: scripts/ufx-gamescope-launch.sh.");
        }
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
