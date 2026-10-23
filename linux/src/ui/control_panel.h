#pragma once

#include <string>

struct ControlState {
    bool running = false;
    int quality = 1;
    float sharpness = 0.25f;
    float gpu_ms = 0.0f;
    float out_fps = 0.0f;
    int fg_mode = 0;
    int fg_multiplier = 2;
    int upscaler_mode = 0;
    float added_latency_ms = 0.0f;
    float refresh_hz = 60.0f;
    float source_fps = 0.0f;
    std::string status = "Desktop Mode preview. Click Start to pick a screen or window.";
    bool start_clicked = false;
    bool stop_clicked = false;
    bool show_about = false;
    std::string notice;
};

void control_panel_draw(ControlState* st);
float quality_ratio(int quality);
int fg_max_multiplier(float refresh_hz, float source_fps);
