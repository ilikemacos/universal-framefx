#pragma once

#ifdef __cplusplus
extern "C" {
#endif

typedef enum ufx_session_kind {
    UFX_SESSION_UNKNOWN = 0,
    UFX_SESSION_DESKTOP = 1,
    UFX_SESSION_GAMEMODE = 2,
} ufx_session_kind;

/* Detect SteamOS / gamescope Game Mode vs desktop Wayland/X11. */
ufx_session_kind ufx_detect_session(void);
const char* ufx_session_name(ufx_session_kind kind);

/* True when Game Mode / gamescope env is present (or UFX_FORCE_GAMEMODE=1). */
int ufx_is_gamescope_session(void);

#ifdef __cplusplus
}
#endif
