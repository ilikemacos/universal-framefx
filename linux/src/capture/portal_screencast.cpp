#include "capture.h"

#include <cerrno>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <string>
#include <vector>
#include <unistd.h>
#include <sys/mman.h>

#include <systemd/sd-bus.h>
#include <pipewire/pipewire.h>
#include <spa/param/video/format-utils.h>
#include <spa/param/video/type-info.h>
#include <spa/debug/types.h>
#include <spa/buffer/buffer.h>

namespace {

void set_err(char* err, size_t errlen, const char* msg) {
    if (!err || errlen == 0) return;
    std::snprintf(err, errlen, "%s", msg);
}

std::string sender_token(sd_bus* bus) {
    const char* uniq = nullptr;
    if (sd_bus_get_unique_name(bus, &uniq) < 0 || !uniq) return "u0";
    std::string s(uniq + (*uniq == ':' ? 1 : 0));
    for (char& c : s) {
        if (c == '.') c = '_';
    }
    return s;
}

struct PortalWait {
    int response = -1;
    sd_bus_message* results = nullptr;
};

int on_request_response(sd_bus_message* m, void* userdata, sd_bus_error*) {
    auto* w = static_cast<PortalWait*>(userdata);
    uint32_t response = 2;
    sd_bus_message_read(m, "u", &response);
    w->response = static_cast<int>(response);
    sd_bus_message_enter_container(m, 'a', "{sv}");
    w->results = sd_bus_message_ref(m);
    return 0;
}

int wait_request(sd_bus* bus, const char* request_path, PortalWait* wait, int timeout_ms) {
    sd_bus_slot* slot = nullptr;
    int r = sd_bus_match_signal(bus, &slot, nullptr, request_path, "org.freedesktop.portal.Request", "Response",
                                on_request_response, wait);
    if (r < 0) return r;
    const uint64_t deadline = static_cast<uint64_t>(timeout_ms) * 1000ull;
    uint64_t waited = 0;
    while (wait->response < 0 && waited < deadline) {
        r = sd_bus_process(bus, nullptr);
        if (r < 0) break;
        if (wait->response >= 0) break;
        if (r == 0) {
            r = sd_bus_wait(bus, 50 * 1000);
            waited += 50 * 1000;
            if (r < 0) break;
        }
    }
    sd_bus_slot_unref(slot);
    return wait->response < 0 ? -ETIMEDOUT : 0;
}

int portal_call(sd_bus* bus, const char* member, const char* extra_sig, auto fill, PortalWait* wait, char* err,
                size_t errlen, int timeout_ms, const char* handle_token) {
    sd_bus_message* m = nullptr;
    int r = sd_bus_message_new_method_call(bus, &m, "org.freedesktop.portal.Desktop", "/org/freedesktop/portal/desktop",
                                           "org.freedesktop.portal.ScreenCast", member);
    if (r < 0) {
        set_err(err, errlen, "ScreenCast method build failed");
        return r;
    }
    fill(m);
    sd_bus_error error = SD_BUS_ERROR_NULL;
    sd_bus_message* reply = nullptr;
    r = sd_bus_call(bus, m, timeout_ms * 1000, &error, &reply);
    sd_bus_message_unref(m);
    if (r < 0) {
        set_err(err, errlen, error.message ? error.message : "ScreenCast call failed");
        sd_bus_error_free(&error);
        return r;
    }
    const char* request_path = nullptr;
    r = sd_bus_message_read(reply, "o", &request_path);
    sd_bus_message_unref(reply);
    if (r < 0 || !request_path) {
        // Some portals return the request as a well-known path derived from the token.
        std::string fallback = "/org/freedesktop/portal/desktop/request/" + sender_token(bus) + "/" + handle_token;
        r = wait_request(bus, fallback.c_str(), wait, timeout_ms);
    } else {
        r = wait_request(bus, request_path, wait, timeout_ms);
    }
    if (r < 0) {
        set_err(err, errlen, "Timed out waiting for portal picker");
        return r;
    }
    if (wait->response != 0) {
        set_err(err, errlen, wait->response == 1 ? "Screen capture cancelled" : "Screen capture failed");
        return -EIO;
    }
    (void)extra_sig;
    return 0;
}

}  // namespace

struct ufx_capture {
    sd_bus* bus = nullptr;
    std::string session_path;
    int pw_fd = -1;
    uint32_t node_id = 0;

    struct pw_thread_loop* loop = nullptr;
    struct pw_context* ctx = nullptr;
    struct pw_core* core = nullptr;
    struct pw_stream* stream = nullptr;
    spa_hook stream_listener{};

    std::mutex mu;
    bool have_frame = false;
    ufx_capture_frame latest{};
    std::vector<uint8_t> cpu_copy;
    int last_fd_dup = -1;

    uint32_t width = 0;
    uint32_t height = 0;
    uint32_t stride = 0;
};

static void on_stream_process(void* data);

static void on_param_changed(void* data, uint32_t id, const struct spa_pod* param) {
    auto* cap = static_cast<ufx_capture*>(data);
    if (id != SPA_PARAM_Format || !param) return;
    spa_video_info_raw raw{};
    spa_format_video_raw_parse(param, &raw);
    if (raw.size.width && raw.size.height) {
        cap->width = raw.size.width;
        cap->height = raw.size.height;
    }
}

static pw_stream_events make_stream_events() {
    pw_stream_events e{};
    e.version = PW_VERSION_STREAM_EVENTS;
    e.param_changed = on_param_changed;
    e.process = on_stream_process;
    return e;
}

static void on_stream_process(void* data) {
    auto* cap = static_cast<ufx_capture*>(data);
    struct pw_buffer* b = pw_stream_dequeue_buffer(cap->stream);
    if (!b || !b->buffer || b->buffer->n_datas < 1) {
        if (b) pw_stream_queue_buffer(cap->stream, b);
        return;
    }
    spa_data* d = &b->buffer->datas[0];
    uint32_t w = cap->width;
    uint32_t h = cap->height;
    if (b->buffer->datas[0].chunk) {
        if (d->chunk->stride > 0) cap->stride = static_cast<uint32_t>(d->chunk->stride);
        if (d->chunk->size > 0 && cap->stride && h == 0) h = d->chunk->size / cap->stride;
    }
    if (!cap->stride && d->maxsize && h) cap->stride = d->maxsize / h;
    if (!w || !h) {
        pw_stream_queue_buffer(cap->stream, b);
        return;
    }

    std::lock_guard<std::mutex> lock(cap->mu);
    if (cap->last_fd_dup >= 0) {
        close(cap->last_fd_dup);
        cap->last_fd_dup = -1;
    }
    cap->latest = {};
    cap->latest.width = w;
    cap->latest.height = h;
    cap->latest.stride = cap->stride ? cap->stride : w * 4;
    cap->latest.modifier = 0;
    cap->latest.fourcc = 0;

    if (d->type == SPA_DATA_DmaBuf && d->fd >= 0) {
        cap->latest.kind = UFX_CAPTURE_KIND_DMABUF;
        cap->last_fd_dup = dup(d->fd);
        cap->latest.fd = cap->last_fd_dup;
        cap->latest.mapped = nullptr;
        cap->latest.mapped_size = 0;
        if (d->flags & SPA_DATA_FLAG_READABLE && d->data) {
            cap->latest.mapped = d->data;
            cap->latest.mapped_size = d->maxsize;
        }
    } else {
        cap->latest.kind = UFX_CAPTURE_KIND_MEMFD;
        size_t nbytes = static_cast<size_t>(cap->latest.stride) * h;
        const void* srcp = d->data;
        if (!srcp && d->fd >= 0) {
            srcp = mmap(nullptr, d->maxsize ? d->maxsize : nbytes, PROT_READ, MAP_PRIVATE, d->fd, d->mapoffset);
            if (srcp == MAP_FAILED) srcp = nullptr;
        }
        if (srcp) {
            cap->cpu_copy.resize(nbytes);
            std::memcpy(cap->cpu_copy.data(), srcp, nbytes);
            cap->latest.mapped = cap->cpu_copy.data();
            cap->latest.mapped_size = nbytes;
        }
        if (!d->data && srcp) munmap(const_cast<void*>(srcp), d->maxsize ? d->maxsize : nbytes);
        cap->latest.fd = -1;
    }
    cap->have_frame = true;
    pw_stream_queue_buffer(cap->stream, b);
}

int ufx_capture_open(ufx_capture** out, char* err, size_t errlen) {
    if (!out) return -EINVAL;
    *out = nullptr;

    sd_bus* bus = nullptr;
    int r = sd_bus_open_user(&bus);
    if (r < 0) {
        set_err(err, errlen, "SKIPPED: no session D-Bus (no portal on this host)");
        return r;
    }

    sd_bus_error error = SD_BUS_ERROR_NULL;
    sd_bus_message* prop = nullptr;
    r = sd_bus_get_property(bus, "org.freedesktop.portal.Desktop", "/org/freedesktop/portal/desktop",
                            "org.freedesktop.portal.ScreenCast", "AvailableSourceTypes", &error, &prop, "u");
    if (r < 0) {
        set_err(err, errlen, "SKIPPED: no portal on this host");
        sd_bus_error_free(&error);
        sd_bus_unref(bus);
        return r;
    }
    sd_bus_message_unref(prop);

    auto* cap = new ufx_capture();
    cap->bus = bus;
    cap->latest.fd = -1;

    char handle_token[64];
    char session_token[64];
    std::snprintf(handle_token, sizeof(handle_token), "ufx%d", getpid());
    std::snprintf(session_token, sizeof(session_token), "ufxsess%d", getpid());

    PortalWait wait{};
    r = portal_call(
        bus, "CreateSession", nullptr,
        [&](sd_bus_message* m) {
            sd_bus_message_append(m, "a{sv}", 2, "handle_token", "s", handle_token, "session_handle_token", "s",
                                  session_token);
        },
        &wait, err, errlen, 15000, handle_token);
    if (r < 0) {
        ufx_capture_close(cap);
        return r;
    }
    // session_handle in results
    if (wait.results) {
        const char* key = nullptr;
        while (sd_bus_message_enter_container(wait.results, 'e', "sv") > 0) {
            sd_bus_message_read(wait.results, "s", &key);
            if (key && std::strcmp(key, "session_handle") == 0) {
                const char* path = nullptr;
                sd_bus_message_enter_container(wait.results, 'v', "o");
                sd_bus_message_read(wait.results, "o", &path);
                if (path) cap->session_path = path;
                sd_bus_message_exit_container(wait.results);
            } else {
                sd_bus_message_skip(wait.results, "v");
            }
            sd_bus_message_exit_container(wait.results);
        }
        sd_bus_message_unref(wait.results);
        wait.results = nullptr;
    }
    if (cap->session_path.empty()) {
        cap->session_path = "/org/freedesktop/portal/desktop/session/" + sender_token(bus) + "/" + session_token;
    }

    wait = {};
    char sel_token[64];
    std::snprintf(sel_token, sizeof(sel_token), "ufxsel%d", getpid());
    r = portal_call(
        bus, "SelectSources", nullptr,
        [&](sd_bus_message* m) {
            sd_bus_message_append(m, "oa{sv}", cap->session_path.c_str(), 4, "handle_token", "s", sel_token, "types",
                                  "u", 1u | 2u, "multiple", "b", 0, "cursor_mode", "u", 2u);
        },
        &wait, err, errlen, 120000, sel_token);
    if (wait.results) {
        sd_bus_message_unref(wait.results);
        wait.results = nullptr;
    }
    if (r < 0) {
        ufx_capture_close(cap);
        return r;
    }

    wait = {};
    char start_token[64];
    std::snprintf(start_token, sizeof(start_token), "ufxstart%d", getpid());
    r = portal_call(
        bus, "Start", nullptr,
        [&](sd_bus_message* m) {
            sd_bus_message_append(m, "osa{sv}", cap->session_path.c_str(), "", 1, "handle_token", "s", start_token);
        },
        &wait, err, errlen, 120000, start_token);
    if (r < 0) {
        if (wait.results) sd_bus_message_unref(wait.results);
        ufx_capture_close(cap);
        return r;
    }
    if (wait.results) {
        const char* key = nullptr;
        while (sd_bus_message_enter_container(wait.results, 'e', "sv") > 0) {
            sd_bus_message_read(wait.results, "s", &key);
            if (key && std::strcmp(key, "streams") == 0) {
                sd_bus_message_enter_container(wait.results, 'v', "a(ua{sv})");
                sd_bus_message_enter_container(wait.results, 'a', "(ua{sv})");
                if (sd_bus_message_enter_container(wait.results, 'r', "ua{sv}") > 0) {
                    uint32_t node = 0;
                    sd_bus_message_read(wait.results, "u", &node);
                    cap->node_id = node;
                    sd_bus_message_skip(wait.results, "a{sv}");
                    sd_bus_message_exit_container(wait.results);
                }
                sd_bus_message_exit_container(wait.results);
                sd_bus_message_exit_container(wait.results);
            } else {
                sd_bus_message_skip(wait.results, "v");
            }
            sd_bus_message_exit_container(wait.results);
        }
        sd_bus_message_unref(wait.results);
    }

    int fd = -1;
    sd_bus_message* fdmsg = nullptr;
    sd_bus_error fderr = SD_BUS_ERROR_NULL;
    r = sd_bus_call_method(bus, "org.freedesktop.portal.Desktop", "/org/freedesktop/portal/desktop",
                           "org.freedesktop.portal.ScreenCast", "OpenPipeWireRemote", &fderr, &fdmsg, "oa{sv}",
                           cap->session_path.c_str(), 0);
    if (r < 0) {
        set_err(err, errlen, fderr.message ? fderr.message : "OpenPipeWireRemote failed");
        sd_bus_error_free(&fderr);
        ufx_capture_close(cap);
        return r;
    }
    r = sd_bus_message_read(fdmsg, "h", &fd);
    if (r >= 0 && fd >= 0) cap->pw_fd = dup(fd);
    sd_bus_message_unref(fdmsg);
    if (cap->pw_fd < 0) {
        set_err(err, errlen, "PipeWire remote fd missing");
        ufx_capture_close(cap);
        return -EIO;
    }

    pw_init(nullptr, nullptr);
    cap->loop = pw_thread_loop_new("ufx-capture", nullptr);
    if (!cap->loop) {
        set_err(err, errlen, "PipeWire loop failed");
        ufx_capture_close(cap);
        return -EIO;
    }
    pw_thread_loop_lock(cap->loop);
    cap->ctx = pw_context_new(pw_thread_loop_get_loop(cap->loop), nullptr, 0);
    cap->core = pw_context_connect_fd(cap->ctx, cap->pw_fd, nullptr, 0);
    cap->pw_fd = -1;  // owned by context
    if (!cap->core) {
        pw_thread_loop_unlock(cap->loop);
        set_err(err, errlen, "PipeWire connect failed");
        ufx_capture_close(cap);
        return -EIO;
    }
    cap->stream = pw_stream_new(cap->core, "Universal-FrameFX",
                                pw_properties_new(PW_KEY_MEDIA_TYPE, "Video", PW_KEY_MEDIA_CATEGORY, "Capture",
                                                  PW_KEY_MEDIA_ROLE, "Screen", nullptr));
    static const pw_stream_events stream_events = make_stream_events();
    pw_stream_add_listener(cap->stream, &cap->stream_listener, &stream_events, cap);

    uint8_t buffer[1024];
    struct spa_pod_builder b = SPA_POD_BUILDER_INIT(buffer, sizeof(buffer));
    const struct spa_pod* params[1];
    spa_rectangle def_sz = SPA_RECTANGLE(1280, 800);
    spa_rectangle min_sz = SPA_RECTANGLE(1, 1);
    spa_rectangle max_sz = SPA_RECTANGLE(4096, 4096);
    spa_fraction def_fr = SPA_FRACTION(0, 1);
    spa_fraction min_fr = SPA_FRACTION(0, 1);
    spa_fraction max_fr = SPA_FRACTION(240, 1);
    params[0] = static_cast<struct spa_pod*>(spa_pod_builder_add_object(
        &b, SPA_TYPE_OBJECT_Format, SPA_PARAM_EnumFormat, SPA_FORMAT_mediaType, SPA_POD_Id(SPA_MEDIA_TYPE_video),
        SPA_FORMAT_mediaSubtype, SPA_POD_Id(SPA_MEDIA_SUBTYPE_raw), SPA_FORMAT_VIDEO_format,
        SPA_POD_CHOICE_ENUM_Id(4, SPA_VIDEO_FORMAT_BGRA, SPA_VIDEO_FORMAT_BGRA, SPA_VIDEO_FORMAT_RGBA,
                               SPA_VIDEO_FORMAT_BGRx),
        SPA_FORMAT_VIDEO_size, SPA_POD_CHOICE_RANGE_Rectangle(&def_sz, &min_sz, &max_sz), SPA_FORMAT_VIDEO_framerate,
        SPA_POD_CHOICE_RANGE_Fraction(&def_fr, &min_fr, &max_fr)));

    r = pw_stream_connect(cap->stream, PW_DIRECTION_INPUT, cap->node_id,
                          static_cast<pw_stream_flags>(PW_STREAM_FLAG_AUTOCONNECT | PW_STREAM_FLAG_MAP_BUFFERS), params,
                          1);
    pw_thread_loop_unlock(cap->loop);
    if (r < 0) {
        set_err(err, errlen, "PipeWire stream connect failed");
        ufx_capture_close(cap);
        return r;
    }
    pw_thread_loop_start(cap->loop);
    *out = cap;
    return 0;
}

int ufx_capture_poll_frame(ufx_capture* cap, ufx_capture_frame* frame, int timeout_ms) {
    if (!cap || !frame) return -EINVAL;
    const int slice = 10;
    int waited = 0;
    while (waited <= timeout_ms) {
        {
            std::lock_guard<std::mutex> lock(cap->mu);
            if (cap->have_frame) {
                *frame = cap->latest;
                cap->have_frame = false;
                return 0;
            }
        }
        if (timeout_ms == 0) break;
        usleep(static_cast<useconds_t>(slice * 1000));
        waited += slice;
    }
    return -ETIMEDOUT;
}

void ufx_capture_release_frame(ufx_capture* cap, ufx_capture_frame* frame) {
    (void)cap;
    (void)frame;
}

void ufx_capture_close(ufx_capture* cap) {
    if (!cap) return;
    if (cap->loop) pw_thread_loop_stop(cap->loop);
    if (cap->stream) {
        pw_stream_destroy(cap->stream);
        cap->stream = nullptr;
    }
    if (cap->core) {
        pw_core_disconnect(cap->core);
        cap->core = nullptr;
    }
    if (cap->ctx) {
        pw_context_destroy(cap->ctx);
        cap->ctx = nullptr;
    }
    if (cap->loop) {
        pw_thread_loop_destroy(cap->loop);
        cap->loop = nullptr;
    }
    if (cap->pw_fd >= 0) close(cap->pw_fd);
    if (cap->last_fd_dup >= 0) close(cap->last_fd_dup);
    if (cap->bus) {
        if (!cap->session_path.empty()) {
            sd_bus_call_method(cap->bus, "org.freedesktop.portal.Desktop", cap->session_path.c_str(),
                               "org.freedesktop.portal.Session", "Close", nullptr, nullptr, nullptr);
        }
        sd_bus_unref(cap->bus);
    }
    delete cap;
}
