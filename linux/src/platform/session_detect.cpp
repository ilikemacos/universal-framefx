#include "session_detect.h"

#include <cctype>
#include <cstdlib>
#include <cstring>

namespace {

bool env_nonempty(const char* name) {
    const char* v = std::getenv(name);
    return v && v[0];
}

bool env_eq_ci(const char* name, const char* expect) {
    const char* v = std::getenv(name);
    if (!v || !expect) return false;
    while (*v && *expect) {
        if (std::tolower(static_cast<unsigned char>(*v)) !=
            std::tolower(static_cast<unsigned char>(*expect)))
            return false;
        ++v;
        ++expect;
    }
    return *v == 0 && *expect == 0;
}

bool env_truthy(const char* name) {
    const char* v = std::getenv(name);
    if (!v || !v[0]) return false;
    if (v[0] == '0' && v[1] == 0) return false;
    if ((v[0] == 'n' || v[0] == 'N') && (v[1] == 'o' || v[1] == 'O') && v[2] == 0) return false;
    if ((v[0] == 'f' || v[0] == 'F') && v[1] == 0) return false;
    return true;
}

}  // namespace

int ufx_is_gamescope_session(void) {
    if (env_truthy("UFX_FORCE_GAMEMODE")) return 1;
    if (env_nonempty("GAMESCOPE_WAYLAND_DISPLAY")) return 1;
    if (env_eq_ci("XDG_CURRENT_DESKTOP", "gamescope")) return 1;
    if (env_eq_ci("XDG_SESSION_DESKTOP", "gamescope")) return 1;
    if (env_eq_ci("STEAMOS_SESSION", "gamescope")) return 1;
    return 0;
}

ufx_session_kind ufx_detect_session(void) {
    if (ufx_is_gamescope_session()) return UFX_SESSION_GAMEMODE;
    if (env_nonempty("WAYLAND_DISPLAY") || env_nonempty("DISPLAY")) return UFX_SESSION_DESKTOP;
    return UFX_SESSION_UNKNOWN;
}

const char* ufx_session_name(ufx_session_kind kind) {
    switch (kind) {
        case UFX_SESSION_GAMEMODE: return "Game Mode (gamescope)";
        case UFX_SESSION_DESKTOP: return "Desktop";
        default: return "Unknown";
    }
}
