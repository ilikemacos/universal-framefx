#include "control_panel.h"
#include "output_window.h"

#include "capture/capture.h"
#include "platform/session_detect.h"
#include "fg2_pacing.h"
#include "gpu/csr2_upscale.h"
#include "gpu/csr_spatial.h"
#include "gpu/fg2_pipeline.h"
#include "gpu/vk_context.h"
#include "gpu/vk_dmabuf_import.h"
#include "gpu/vk_window.h"
#include "ufx_version.h"

#include "imgui.h"
#include "backends/imgui_impl_sdl2.h"
#include "backends/imgui_impl_vulkan.h"

#include <algorithm>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <ctime>
#include <fstream>
#include <sstream>
#include <string>
#include <unistd.h>
#include <linux/limits.h>
#include <libgen.h>
#include <vector>
#include <sys/stat.h>

int ufx_run_selftest();
int ufx_run_offline_fg(int argc, char** argv);
int ufx_run_offline_upscale(int argc, char** argv);

static std::string ufx_log_path() {
    const char* home = std::getenv("HOME");
    std::string base = home ? std::string(home) : "/tmp";
    std::string dir = base + "/.local/share/universal-framefx";
    // mkdir -p (two levels under HOME is enough; HOME and .local/share already exist)
    mkdir((base + "/.local").c_str(), 0755);
    mkdir((base + "/.local/share").c_str(), 0755);
    mkdir(dir.c_str(), 0755);
    return dir + "/last-run.log";
}

static void ufx_fatal(const std::string& msg, bool sdl_initialized) {
    std::string path = ufx_log_path();
    std::ofstream f(path, std::ios::app);
    if (f) {
        std::time_t t = std::time(nullptr);
        char tbuf[64];
        std::strftime(tbuf, sizeof(tbuf), "%Y-%m-%d %H:%M:%S", std::localtime(&t));
        f << "[" << tbuf << "] " << UFX_PRODUCT_NAME << " " << UFX_VERSION_STRING
          << " FATAL: " << msg << "\n";
        f.close(); // flush before the (blocking) message box
    }
    std::fprintf(stderr, "%s\n", msg.c_str());
    std::string box_msg = msg + "\n\nLog: " + path +
                          "\nPaste this file's contents when reporting the issue.";
    if (!sdl_initialized) {
        // Try a minimal SDL_Init just for the message box; ignore failure.
        if (SDL_Init(SDL_INIT_VIDEO) == 0) sdl_initialized = true;
    }
    if (sdl_initialized) {
        SDL_ShowSimpleMessageBox(SDL_MESSAGEBOX_ERROR,
                                 UFX_PRODUCT_NAME " failed to start", box_msg.c_str(), nullptr);
    }
}

static std::string load_notice() {
    char self[PATH_MAX];
    ssize_t n = readlink("/proc/self/exe", self, sizeof(self) - 1);
    std::vector<std::string> cands;
    if (n > 0) {
        self[n] = 0;
        char buf[PATH_MAX];
        std::strncpy(buf, self, sizeof(buf));
        std::string dir = dirname(buf);
        cands.push_back(dir + "/NOTICE.md");
        cands.push_back(dir + "/../NOTICE.md");
        cands.push_back(dir + "/../../NOTICE.md");
    }
    cands.push_back("NOTICE.md");
    cands.push_back("linux/NOTICE.md");
    for (const auto& p : cands) {
        std::ifstream f(p);
        if (!f) continue;
        std::ostringstream ss;
        ss << f.rdbuf();
        return ss.str();
    }
    return "NOTICE.md not found next to the binary.";
}

static void check_vk(VkResult r) {
    if (r != VK_SUCCESS) std::fprintf(stderr, "imgui vulkan error %d\n", static_cast<int>(r));
}

struct LiveImages {
    uint32_t in_w = 0, in_h = 0, out_w = 0, out_h = 0;
    VkImage src = VK_NULL_HANDLE, mid = VK_NULL_HANDLE, dst = VK_NULL_HANDLE;
    VkDeviceMemory src_mem = VK_NULL_HANDLE, mid_mem = VK_NULL_HANDLE, dst_mem = VK_NULL_HANDLE;
    VkImageView src_view = VK_NULL_HANDLE, mid_view = VK_NULL_HANDLE, dst_view = VK_NULL_HANDLE;
    bool dst_shader_read = false;
};

static void live_destroy(VkContext* ctx, LiveImages* L) {
    if (!ctx || !ctx->device) return;
    if (L->src_view) vkDestroyImageView(ctx->device, L->src_view, nullptr);
    if (L->mid_view) vkDestroyImageView(ctx->device, L->mid_view, nullptr);
    if (L->dst_view) vkDestroyImageView(ctx->device, L->dst_view, nullptr);
    if (L->src) vkDestroyImage(ctx->device, L->src, nullptr);
    if (L->mid) vkDestroyImage(ctx->device, L->mid, nullptr);
    if (L->dst) vkDestroyImage(ctx->device, L->dst, nullptr);
    if (L->src_mem) vkFreeMemory(ctx->device, L->src_mem, nullptr);
    if (L->mid_mem) vkFreeMemory(ctx->device, L->mid_mem, nullptr);
    if (L->dst_mem) vkFreeMemory(ctx->device, L->dst_mem, nullptr);
    *L = {};
}

static bool live_ensure(VkContext* ctx, LiveImages* L, uint32_t in_w, uint32_t in_h, uint32_t out_w, uint32_t out_h) {
    if (L->in_w == in_w && L->in_h == in_h && L->out_w == out_w && L->out_h == out_h && L->src) return true;
    live_destroy(ctx, L);
    const VkFormat fmt = VK_FORMAT_R32G32B32A32_SFLOAT;
    const VkImageUsageFlags usage = VK_IMAGE_USAGE_STORAGE_BIT | VK_IMAGE_USAGE_SAMPLED_BIT |
                                    VK_IMAGE_USAGE_TRANSFER_SRC_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT;
    if (!vk_create_image_2d(ctx, in_w, in_h, fmt, usage, &L->src, &L->src_mem, &L->src_view)) return false;
    if (!vk_create_image_2d(ctx, out_w, out_h, fmt, usage, &L->mid, &L->mid_mem, &L->mid_view)) return false;
    if (!vk_create_image_2d(ctx, out_w, out_h, fmt, usage, &L->dst, &L->dst_mem, &L->dst_view)) return false;
    L->in_w = in_w;
    L->in_h = in_h;
    L->out_w = out_w;
    L->out_h = out_h;
    return true;
}

static void img_barrier(VkCommandBuffer cmd, VkImage img, VkImageLayout from, VkImageLayout to, VkAccessFlags srcA,
                        VkAccessFlags dstA, VkPipelineStageFlags srcS, VkPipelineStageFlags dstS) {
    VkImageMemoryBarrier b{VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER};
    b.srcAccessMask = srcA;
    b.dstAccessMask = dstA;
    b.oldLayout = from;
    b.newLayout = to;
    b.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    b.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    b.image = img;
    b.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    b.subresourceRange.levelCount = 1;
    b.subresourceRange.layerCount = 1;
    vkCmdPipelineBarrier(cmd, srcS, dstS, 0, 0, nullptr, 0, nullptr, 1, &b);
}

static bool csr12_from_view(VkContext* ctx, CsrSpatial* csr, LiveImages* live, VkImageView src_view, float sharpness) {
    SpatialUniformsCPU u{};
    csr_fill_uniforms(&u, live->in_w, live->in_h, live->out_w, live->out_h, sharpness);
    void* mapped = nullptr;
    vkMapMemory(ctx->device, csr->ubo_mem, 0, sizeof(u), 0, &mapped);
    std::memcpy(mapped, &u, sizeof(u));
    vkUnmapMemory(ctx->device, csr->ubo_mem);
    vkResetDescriptorPool(ctx->device, csr->pool, 0);
    VkDescriptorSet easu_set = VK_NULL_HANDLE, rcas_set = VK_NULL_HANDLE;
    VkDescriptorSetAllocateInfo dai{VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO};
    dai.descriptorPool = csr->pool;
    dai.descriptorSetCount = 1;
    dai.pSetLayouts = &csr->easu_layout;
    vkAllocateDescriptorSets(ctx->device, &dai, &easu_set);
    dai.pSetLayouts = &csr->rcas_layout;
    vkAllocateDescriptorSets(ctx->device, &dai, &rcas_set);
    VkDescriptorBufferInfo ubo{csr->ubo, 0, sizeof(SpatialUniformsCPU)};
    VkDescriptorImageInfo easu_src{csr->nearest, src_view, VK_IMAGE_LAYOUT_GENERAL};
    VkDescriptorImageInfo easu_dst{VK_NULL_HANDLE, live->mid_view, VK_IMAGE_LAYOUT_GENERAL};
    VkDescriptorImageInfo rcas_src{VK_NULL_HANDLE, live->mid_view, VK_IMAGE_LAYOUT_GENERAL};
    VkDescriptorImageInfo rcas_dst{VK_NULL_HANDLE, live->dst_view, VK_IMAGE_LAYOUT_GENERAL};
    VkWriteDescriptorSet w[6]{};
    auto fill = [](VkWriteDescriptorSet& wr, VkDescriptorSet set, uint32_t b, VkDescriptorType t,
                   const VkDescriptorBufferInfo* bi, const VkDescriptorImageInfo* ii_) {
        wr.sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET;
        wr.dstSet = set;
        wr.dstBinding = b;
        wr.descriptorCount = 1;
        wr.descriptorType = t;
        wr.pBufferInfo = bi;
        wr.pImageInfo = ii_;
    };
    fill(w[0], easu_set, 0, VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER, &ubo, nullptr);
    fill(w[1], easu_set, 1, VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, nullptr, &easu_src);
    fill(w[2], easu_set, 2, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, nullptr, &easu_dst);
    fill(w[3], rcas_set, 0, VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER, &ubo, nullptr);
    fill(w[4], rcas_set, 1, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, nullptr, &rcas_src);
    fill(w[5], rcas_set, 2, VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, nullptr, &rcas_dst);
    vkUpdateDescriptorSets(ctx->device, 6, w, 0, nullptr);
    VkCommandBuffer ccmd = vk_begin_once(ctx);
    VkImageLayout from = live->dst_shader_read ? VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL : VK_IMAGE_LAYOUT_UNDEFINED;
    img_barrier(ccmd, live->dst, from, VK_IMAGE_LAYOUT_GENERAL, VK_ACCESS_SHADER_READ_BIT, VK_ACCESS_SHADER_WRITE_BIT,
                VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT, VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT);
    vkCmdBindPipeline(ccmd, VK_PIPELINE_BIND_POINT_COMPUTE, csr->easu_pipe);
    vkCmdBindDescriptorSets(ccmd, VK_PIPELINE_BIND_POINT_COMPUTE, csr->easu_pl, 0, 1, &easu_set, 0, nullptr);
    vkCmdDispatch(ccmd, (live->out_w + 7) / 8, (live->out_h + 7) / 8, 1);
    img_barrier(ccmd, live->mid, VK_IMAGE_LAYOUT_GENERAL, VK_IMAGE_LAYOUT_GENERAL, VK_ACCESS_SHADER_WRITE_BIT,
                VK_ACCESS_SHADER_READ_BIT, VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT, VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT);
    vkCmdBindPipeline(ccmd, VK_PIPELINE_BIND_POINT_COMPUTE, csr->rcas_pipe);
    vkCmdBindDescriptorSets(ccmd, VK_PIPELINE_BIND_POINT_COMPUTE, csr->rcas_pl, 0, 1, &rcas_set, 0, nullptr);
    vkCmdDispatch(ccmd, (live->out_w + 7) / 8, (live->out_h + 7) / 8, 1);
    img_barrier(ccmd, live->dst, VK_IMAGE_LAYOUT_GENERAL, VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL,
                VK_ACCESS_SHADER_WRITE_BIT, VK_ACCESS_SHADER_READ_BIT, VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,
                VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT);
    if (!vk_submit_once(ctx, ccmd)) return false;
    live->dst_shader_read = true;
    return true;
}

int main(int argc, char** argv) {
    {
        std::ofstream f(ufx_log_path(), std::ios::app);
        if (f) f << "---- launch " << UFX_PRODUCT_NAME << " " << UFX_VERSION_STRING << " ----\n";
    }
    bool capture_fail_soft = false;
    for (int i = 1; i < argc; ++i) {
        if (std::strcmp(argv[i], "--version") == 0) {
            std::printf("%s %s\n", UFX_PRODUCT_NAME, UFX_VERSION_STRING);
            return 0;
        }
        if (std::strcmp(argv[i], "--selftest") == 0) {
            return ufx_run_selftest();
        }
        if (std::strcmp(argv[i], "--offline-fg") == 0) {
            return ufx_run_offline_fg(argc, argv);
        }
        if (std::strcmp(argv[i], "--offline-upscale") == 0) {
            return ufx_run_offline_upscale(argc, argv);
        }
        if (std::strcmp(argv[i], "--session-info") == 0) {
            ufx_session_kind sk = ufx_detect_session();
            std::printf("session=%s gamescope=%s\n", ufx_session_name(sk),
                        ufx_is_gamescope_session() ? "yes" : "no");
            return 0;
        }
        if (std::strcmp(argv[i], "--capture-fail-soft") == 0) {
            capture_fail_soft = true;
            continue;
        }
        if (std::strcmp(argv[i], "--help") == 0) {
            std::printf("Usage: ufx-linux [--version] [--selftest] [--session-info] [--capture-fail-soft]\n"
                        "                 [--offline-fg IN OUT] [--offline-upscale IN OUT]\n");
            return 0;
        }
    }

    if (SDL_Init(SDL_INIT_VIDEO | SDL_INIT_EVENTS) != 0) {
        ufx_fatal(std::string("SDL_Init failed: ") + SDL_GetError(), false);
        return 1;
    }
    SDL_Vulkan_LoadLibrary(nullptr);

    unsigned int n_ext = 0;
    SDL_Vulkan_GetInstanceExtensions(nullptr, &n_ext, nullptr);
    std::vector<const char*> inst_exts(n_ext);
    SDL_Vulkan_GetInstanceExtensions(nullptr, &n_ext, inst_exts.data());

    std::string err;
    VkContext ctx{};
    if (!vk_find_shader_dir(&ctx.shader_dir)) {
        ufx_fatal("Shaders not found. Set UFX_SHADER_DIR or reinstall.", true);
        return 1;
    }
    if (!vk_instance_create(&ctx, inst_exts.data(), n_ext, &err)) {
        ufx_fatal("Vulkan instance: " + err, true);
        return 1;
    }

    SdlVkWindow control{};
    if (!vk_window_create(ctx.instance, "Universal-FrameFX", 500, 720, 0, &control)) {
        ufx_fatal(std::string("Window creation failed: ") + SDL_GetError(), true);
        return 1;
    }
    if (!vk_device_create(&ctx, control.surface, &err)) {
        ufx_fatal("Vulkan device: " + err, true);
        return 1;
    }
    if (!vk_window_init_swapchain(&ctx, &control, false, false)) {
        ufx_fatal("Swapchain creation failed (see log for GPU/compositor details).", true);
        return 1;
    }

    CsrSpatial csr{};
    if (!csr_spatial_create(&ctx, &csr, &err)) {
        ufx_fatal("CSR 1.2 pipeline: " + err, true);
        return 1;
    }
    Csr2Upscale csr2{};
    if (!csr2_upscale_create(&ctx, &csr2, &err)) {
        ufx_fatal("CSR 2.0 pipeline: " + err, true);
        return 1;
    }
    Fg2Pipeline fg{};
    if (!fg2_pipeline_create(&ctx, &fg, &err)) {
        ufx_fatal("Frame-gen pipeline: " + err, true);
        return 1;
    }

    IMGUI_CHECKVERSION();
    ImGui::CreateContext();
    ImGui::StyleColorsDark();
    ImGui_ImplSDL2_InitForVulkan(control.window);

    VkDescriptorPoolSize imgui_sizes[] = {
        {VK_DESCRIPTOR_TYPE_SAMPLER, 100},
        {VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, 100},
        {VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE, 100},
        {VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, 100},
        {VK_DESCRIPTOR_TYPE_UNIFORM_TEXEL_BUFFER, 100},
        {VK_DESCRIPTOR_TYPE_STORAGE_TEXEL_BUFFER, 100},
        {VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER, 100},
        {VK_DESCRIPTOR_TYPE_STORAGE_BUFFER, 100},
        {VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER_DYNAMIC, 100},
        {VK_DESCRIPTOR_TYPE_STORAGE_BUFFER_DYNAMIC, 100},
        {VK_DESCRIPTOR_TYPE_INPUT_ATTACHMENT, 100},
    };
    VkDescriptorPoolCreateInfo dpool{VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO};
    dpool.flags = VK_DESCRIPTOR_POOL_CREATE_FREE_DESCRIPTOR_SET_BIT;
    dpool.maxSets = 100;
    dpool.poolSizeCount = 11;
    dpool.pPoolSizes = imgui_sizes;
    VkDescriptorPool imgui_pool = VK_NULL_HANDLE;
    vkCreateDescriptorPool(ctx.device, &dpool, nullptr, &imgui_pool);

    ImGui_ImplVulkan_InitInfo ii{};
    ii.Instance = ctx.instance;
    ii.PhysicalDevice = ctx.phys;
    ii.Device = ctx.device;
    ii.QueueFamily = ctx.queue_family;
    ii.Queue = ctx.queue;
    ii.DescriptorPool = imgui_pool;
    ii.MinImageCount = 2;
    ii.ImageCount = static_cast<uint32_t>(control.images.size());
    ii.MSAASamples = VK_SAMPLE_COUNT_1_BIT;
    ii.RenderPass = control.render_pass;
    ii.CheckVkResultFn = check_vk;
    ImGui_ImplVulkan_Init(&ii);
    ImGui_ImplVulkan_CreateFontsTexture();

    ControlState st;
    st.notice = load_notice();
    {
        ufx_session_kind sk = ufx_detect_session();
        st.game_mode = ufx_is_gamescope_session() != 0;
        st.session_label = ufx_session_name(sk);
        st.capture_fail_soft = capture_fail_soft;
        if (st.game_mode) {
            st.status = "Game Mode detected (experimental). Start tries gamescope PipeWire, then portal.";
        } else {
            st.status = "Desktop Mode. Click Start to pick a screen or window.";
        }
    }

    ufx_capture* cap = nullptr;
    SdlVkWindow output{};
    PresentPass present{};
    LiveImages live{};
    bool output_ready = false;

    bool running = true;
    auto last = std::chrono::steady_clock::now();
    int frames = 0;
    float fps_acc = 0;
    double prev_cap = 0.0, curr_cap = 0.0;
    bool have_prev_cap = false;
    double last_gen_at = 0.0;
    bool have_last_gen = false;
    bool have_src = false;
    bool have_presentable = false;
    bool csr2_shader_read = false;
    int last_fg_mode = -1;
    int last_upscaler = -1;
    auto t_origin = std::chrono::steady_clock::now();

    while (running) {
        SDL_Event ev;
        while (SDL_PollEvent(&ev)) {
            ImGui_ImplSDL2_ProcessEvent(&ev);
            if (ev.type == SDL_QUIT) running = false;
            if (ev.type == SDL_KEYDOWN && ev.key.keysym.sym == SDLK_ESCAPE) {
                if (st.running) st.stop_clicked = true;
                else running = false;
            }
        }

        ImGui_ImplVulkan_NewFrame();
        ImGui_ImplSDL2_NewFrame();
        ImGui::NewFrame();
        SDL_DisplayMode dm{};
        if (SDL_GetCurrentDisplayMode(0, &dm) == 0 && dm.refresh_rate > 0) st.refresh_hz = static_cast<float>(dm.refresh_rate);
        control_panel_draw(&st);
        ImGui::Render();

        if (st.start_clicked) {
            st.start_clicked = false;
            char cap_err[512] = {};
            ufx_capture_backend used = UFX_CAPTURE_BACKEND_NONE;
            const int prefer_pw = st.game_mode ? 1 : 0;
            if (ufx_capture_open_auto(&cap, cap_err, sizeof(cap_err), prefer_pw, st.capture_fail_soft ? 1 : 0,
                                     &used) != 0) {
                st.status = cap_err[0] ? cap_err : "Could not start capture. See docs/GAMEMODE.md";
                st.capture_backend = ufx_capture_backend_name(UFX_CAPTURE_BACKEND_NONE);
                if (cap) {
                    ufx_capture_close(cap);
                    cap = nullptr;
                }
            } else {
                st.capture_backend = ufx_capture_backend_name(used);
                if (!output.window) {
                    vk_window_create(ctx.instance, "Universal-FrameFX output", 1280, 800,
                                     SDL_WINDOW_BORDERLESS, &output);
                    vk_window_init_swapchain(&ctx, &output, true, true);
                    if (present_pass_create(&ctx, output.render_pass, &present, &err)) output_ready = true;
                }
                st.running = true;
                if (used == UFX_CAPTURE_BACKEND_NULL) {
                    st.status = "Null capture (fail-soft). Esc stops. See docs/GAMEMODE.md";
                } else if (used == UFX_CAPTURE_BACKEND_GAMESCOPE_PW) {
                    st.status = "Capturing gamescope PipeWire. Esc stops.";
                } else {
                    st.status = "Capturing (portal). Esc stops.";
                }
            }
        }
        if (st.stop_clicked) {
            st.stop_clicked = false;
            st.running = false;
            if (cap) {
                ufx_capture_close(cap);
                cap = nullptr;
            }
            fg.has_history = false;
            csr2_upscale_reset_history(&csr2);
            have_src = false;
            have_prev_cap = false;
            have_last_gen = false;
            have_presentable = false;
            st.status = "Stopped.";
            st.capture_backend = "None";
        }

        if (st.fg_mode != last_fg_mode || st.upscaler_mode != last_upscaler) {
            csr2_upscale_reset_history(&csr2);
            if (st.fg_mode == 0) fg.has_history = false;
            last_fg_mode = st.fg_mode;
            last_upscaler = st.upscaler_mode;
            csr2_shader_read = false;
        }

        if (st.running && cap) {
            ufx_capture_frame fr{};
            int wait_ms = st.fg_mode ? 0 : 8;
            if (ufx_capture_poll_frame(cap, &fr, wait_ms) == 0 && fr.width && fr.height) {
                uint32_t out_w = static_cast<uint32_t>(fr.width * quality_ratio(st.quality) + 0.5f);
                uint32_t out_h = static_cast<uint32_t>(fr.height * quality_ratio(st.quality) + 0.5f);
                out_w = std::max(1u, out_w);
                out_h = std::max(1u, out_h);
                uint32_t old_in_w = live.in_w, old_in_h = live.in_h, old_out_w = live.out_w, old_out_h = live.out_h;
                if (live_ensure(&ctx, &live, fr.width, fr.height, out_w, out_h)) {
                    if (old_in_w != live.in_w || old_in_h != live.in_h || old_out_w != live.out_w ||
                        old_out_h != live.out_h) {
                        csr2_upscale_reset_history(&csr2);
                        fg.has_history = false;
                        have_presentable = false;
                        csr2_shader_read = false;
                    }
                    if (fr.mapped) {
                        vk_upload_rgba8_staging(&ctx, fr.mapped, fr.width, fr.height, fr.stride, true, live.src);
                        have_src = true;
                    } else if (fr.kind == UFX_CAPTURE_KIND_DMABUF) {
                        ImportedImage imp{};
                        if (vk_import_dmabuf(&ctx, &fr, VK_FORMAT_B8G8R8A8_UNORM, &imp)) {
                            vk_imported_image_destroy(&ctx, &imp);
                            st.status = "DMA-BUF import available; using mapped fallback when present.";
                        }
                    }
                    auto cap_now = std::chrono::steady_clock::now();
                    double tcap = std::chrono::duration<double>(cap_now - t_origin).count();
                    if (have_prev_cap && tcap > curr_cap) {
                        double dt = tcap - curr_cap;
                        if (dt > 1e-4) st.source_fps = static_cast<float>(1.0 / dt);
                    }
                    prev_cap = have_prev_cap ? curr_cap : tcap;
                    curr_cap = tcap;
                    have_prev_cap = true;
                    if (st.fg_mode && have_src) {
                        fg2_process_real_image(&fg, live.src, live.in_w, live.in_h, &err);
                    }
                    double src_iv = st.source_fps > 1.f ? 1.0 / static_cast<double>(st.source_fps) : 1.0 / 60.0;
                    st.added_latency_ms = static_cast<float>(fg2_pacing::hold_delay(src_iv, 0.004) * 1000.0);
                }
                ufx_capture_release_frame(cap, &fr);
            }
        }

        if (st.running && output_ready && have_src && live.src) {
            auto t0 = std::chrono::steady_clock::now();
            double now_s = std::chrono::duration<double>(t0 - t_origin).count();
            double src_iv = st.source_fps > 1.f ? 1.0 / static_cast<double>(st.source_fps) : 1.0 / 60.0;
            double hold = fg2_pacing::hold_delay(src_iv, 0.004);
            st.added_latency_ms = static_cast<float>(hold * 1000.0);
            int capn = st.fg_mode ? fg_max_multiplier(st.refresh_hz, st.source_fps > 1.f ? st.source_fps : 30.f) : 1;
            capn = std::min(capn, st.fg_multiplier);
            bool show_curr = true;
            float t = 1.f;
            bool do_gen = false;
            if (st.fg_mode && fg.has_history) {
                double pb = fg2_pacing::playback_time(now_s, hold, false);
                t = fg2_pacing::interp_t(pb, prev_cap, curr_cap, &show_curr);
                do_gen = !show_curr && fg2_pacing::should_generate(now_s, last_gen_at, have_last_gen, src_iv, capn);
            }
            VkImage src_img = live.src;
            VkImageView src_view = live.src_view;
            bool feeds_history = st.fg_mode != 0;
            bool need_upscale = true;
            if (st.fg_mode && fg.has_history) {
                if (show_curr) {
                    src_img = live.src;
                    src_view = live.src_view;
                    feeds_history = true;
                } else if (do_gen && fg2_generate_fast_gpu(&fg, t, &err)) {
                    last_gen_at = now_s;
                    have_last_gen = true;
                    src_img = fg.generated.image;
                    src_view = fg.generated.view;
                    feeds_history = false;
                } else if (have_presentable) {
                    need_upscale = false;
                }
            }
            VkImageView present_view = live.dst_view;
            uint32_t pw = live.out_w, ph = live.out_h;
            if (need_upscale) {
                if (st.upscaler_mode == 1) {
                    if (csr2_shader_read) {
                        VkCommandBuffer bcmd = vk_begin_once(&ctx);
                        img_barrier(bcmd, csr2.dst.image, VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL,
                                    VK_IMAGE_LAYOUT_GENERAL, VK_ACCESS_SHADER_READ_BIT, VK_ACCESS_SHADER_WRITE_BIT,
                                    VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT, VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT);
                        vk_submit_once(&ctx, bcmd);
                        csr2_shader_read = false;
                    }
                    bool temporal = st.fg_mode != 0;
                    csr2_upscale_from_image(&csr2, src_img, live.in_w, live.in_h, live.out_w, live.out_h, st.sharpness,
                                            temporal, feeds_history, &err);
                    VkCommandBuffer bcmd = vk_begin_once(&ctx);
                    img_barrier(bcmd, csr2.dst.image, VK_IMAGE_LAYOUT_GENERAL, VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL,
                                VK_ACCESS_SHADER_WRITE_BIT, VK_ACCESS_SHADER_READ_BIT,
                                VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT, VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT);
                    vk_submit_once(&ctx, bcmd);
                    csr2_shader_read = true;
                    present_view = csr2.dst.view;
                } else {
                    csr12_from_view(&ctx, &csr, &live, src_view, st.sharpness);
                    present_view = live.dst_view;
                }
                have_presentable = true;
            } else {
                present_view = st.upscaler_mode == 1 ? csr2.dst.view : live.dst_view;
            }
            present_submit(&ctx, &output, &present, present_view, pw, ph);
            auto t1 = std::chrono::steady_clock::now();
            st.gpu_ms = std::chrono::duration<float, std::milli>(t1 - t0).count();
            ++frames;
            fps_acc += st.gpu_ms;
            auto now = std::chrono::steady_clock::now();
            if (std::chrono::duration<float>(now - last).count() >= 1.0f) {
                st.out_fps = static_cast<float>(frames);
                frames = 0;
                last = now;
            }
            if (st.fg_mode) {
                char lat[192];
                std::snprintf(lat, sizeof(lat), "Capturing. Esc stops. latency +%.1fms (1 source frame)",
                              st.added_latency_ms);
                st.status = lat;
            }
        }

        if (vk_window_acquire(&ctx, &control)) {
            VkCommandBuffer cmd = control.cmds[control.frame % 2];
            vkResetCommandBuffer(cmd, 0);
            VkCommandBufferBeginInfo bi{VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO};
            vkBeginCommandBuffer(cmd, &bi);
            VkClearValue clear{};
            clear.color = {{0.10f, 0.11f, 0.13f, 1.0f}};
            VkRenderPassBeginInfo rp{VK_STRUCTURE_TYPE_RENDER_PASS_BEGIN_INFO};
            rp.renderPass = control.render_pass;
            rp.framebuffer = control.framebuffers[control.image_index];
            rp.renderArea.extent = control.extent;
            rp.clearValueCount = 1;
            rp.pClearValues = &clear;
            vkCmdBeginRenderPass(cmd, &rp, VK_SUBPASS_CONTENTS_INLINE);
            ImGui_ImplVulkan_RenderDrawData(ImGui::GetDrawData(), cmd);
            vkCmdEndRenderPass(cmd);
            vkEndCommandBuffer(cmd);
            vk_window_submit_present(&ctx, &control, cmd);
        }
    }

    vkDeviceWaitIdle(ctx.device);
    if (cap) ufx_capture_close(cap);
    live_destroy(&ctx, &live);
    present_pass_destroy(&ctx, &present);
    vk_window_destroy_swapchain(&ctx, &output);
    vk_window_destroy(ctx.instance, &output);
    ImGui_ImplVulkan_Shutdown();
    ImGui_ImplSDL2_Shutdown();
    ImGui::DestroyContext();
    vkDestroyDescriptorPool(ctx.device, imgui_pool, nullptr);
    csr_spatial_destroy(&csr);
    csr2_upscale_destroy(&csr2);
    fg2_pipeline_destroy(&fg);
    vk_window_destroy_swapchain(&ctx, &control);
    vk_window_destroy(ctx.instance, &control);
    vk_context_shutdown(&ctx);
    SDL_Quit();
    return 0;
}
