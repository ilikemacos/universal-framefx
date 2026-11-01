cmake_minimum_required(VERSION 3.16)
project(ufx-linux LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_C_STANDARD 11)

if(NOT CMAKE_BUILD_TYPE)
  set(CMAKE_BUILD_TYPE Release)
endif()

find_package(PkgConfig REQUIRED)
pkg_check_modules(SDL2 REQUIRED sdl2)
pkg_check_modules(VULKAN REQUIRED vulkan)
pkg_check_modules(PIPEWIRE REQUIRED libpipewire-0.3)
pkg_check_modules(SYSTEMD REQUIRED libsystemd)

find_program(GLSLC glslc REQUIRED)

set(SHADER_SRC_DIR ${CMAKE_CURRENT_SOURCE_DIR}/src/shaders)
set(SHADER_OUT_DIR ${CMAKE_BINARY_DIR}/shaders)
file(MAKE_DIRECTORY ${SHADER_OUT_DIR})

function(ufx_shader IN OUT)
  cmake_parse_arguments(S "" "" "DEFINES" ${ARGN})
  set(defs)
  foreach(d ${S_DEFINES})
    list(APPEND defs -D${d})
  endforeach()
  add_custom_command(
    OUTPUT ${SHADER_OUT_DIR}/${OUT}
    COMMAND ${GLSLC} -O --target-env=vulkan1.2 ${defs} ${SHADER_SRC_DIR}/${IN} -o ${SHADER_OUT_DIR}/${OUT}
    DEPENDS ${SHADER_SRC_DIR}/${IN}
    COMMENT "glslc ${IN} -> ${OUT}")
endfunction()

ufx_shader(easu.comp easu.spv)
ufx_shader(rcas.comp rcas.spv)
ufx_shader(present.vert present.vert.spv)
ufx_shader(present.frag present.frag.spv)
ufx_shader(fg2_luma.comp fg2_luma.spv)
ufx_shader(fg2_halve.comp fg2_halve.spv)
ufx_shader(fg2_p8_search.comp fg2_p8_search.spv)
ufx_shader(fg2_p8_prop.comp fg2_p8_prop.spv)
ufx_shader(fg2_p4_refine.comp fg2_p4_refine.spv)
ufx_shader(fg2_p2_final.comp fg2_p2_final.spv)
ufx_shader(fg2_fwd.comp fg2_fwd.spv)
ufx_shader(fg2_consistency.comp fg2_consistency.spv)
ufx_shader(fg2_static_count.comp fg2_static_count.spv)
ufx_shader(fg2_hud_mask.comp fg2_hud_mask.spv)
ufx_shader(fg2_scene_cut.comp fg2_scene_cut.spv)
ufx_shader(fg2_global_motion.comp fg2_global_motion.spv)
ufx_shader(fg2_interp_fast.comp fg2_interp_fast.spv)
ufx_shader(fg2_interp_fast.comp fg2_interp_fast_fp16.spv DEFINES UFX_FP16)
ufx_shader(csr2_upsample.comp csr2_upsample.spv)
ufx_shader(csr2_upsample.comp csr2_upsample_fp16.spv DEFINES UFX_FP16)
ufx_shader(csr2_accum.comp csr2_accum.spv)
ufx_shader(csr2_cas.comp csr2_cas.spv)
ufx_shader(csr2_cas.comp csr2_cas_fp16.spv DEFINES UFX_FP16)
ufx_shader(csr_ai_luma.comp csr_ai_luma.spv)
ufx_shader(csr_ai_conv.comp csr_ai_conv.spv)
ufx_shader(csr_ai_shuffle.comp csr_ai_shuffle.spv)
ufx_shader(csr_ai_resample.comp csr_ai_resample.spv)
ufx_shader(csr_ai_sharpen.comp csr_ai_sharpen.spv)
ufx_shader(ssgi_downsample.comp ssgi_downsample.spv)
ufx_shader(ssgi_march.comp ssgi_march.spv)
ufx_shader(ssgi_filter.comp ssgi_filter.spv)
ufx_shader(ssgi_composite.comp ssgi_composite.spv)

add_custom_target(ufx_shaders ALL DEPENDS
  ${SHADER_OUT_DIR}/easu.spv
  ${SHADER_OUT_DIR}/rcas.spv
  ${SHADER_OUT_DIR}/present.vert.spv
  ${SHADER_OUT_DIR}/present.frag.spv
  ${SHADER_OUT_DIR}/fg2_luma.spv
  ${SHADER_OUT_DIR}/fg2_halve.spv
  ${SHADER_OUT_DIR}/fg2_p8_search.spv
  ${SHADER_OUT_DIR}/fg2_p8_prop.spv
  ${SHADER_OUT_DIR}/fg2_p4_refine.spv
  ${SHADER_OUT_DIR}/fg2_p2_final.spv
  ${SHADER_OUT_DIR}/fg2_fwd.spv
  ${SHADER_OUT_DIR}/fg2_consistency.spv
  ${SHADER_OUT_DIR}/fg2_static_count.spv
  ${SHADER_OUT_DIR}/fg2_hud_mask.spv
  ${SHADER_OUT_DIR}/fg2_scene_cut.spv
  ${SHADER_OUT_DIR}/fg2_global_motion.spv
  ${SHADER_OUT_DIR}/fg2_interp_fast.spv
  ${SHADER_OUT_DIR}/fg2_interp_fast_fp16.spv
  ${SHADER_OUT_DIR}/csr2_upsample.spv
  ${SHADER_OUT_DIR}/csr2_upsample_fp16.spv
  ${SHADER_OUT_DIR}/csr2_accum.spv
  ${SHADER_OUT_DIR}/csr2_cas.spv
  ${SHADER_OUT_DIR}/csr2_cas_fp16.spv
  ${SHADER_OUT_DIR}/csr_ai_luma.spv
  ${SHADER_OUT_DIR}/csr_ai_conv.spv
  ${SHADER_OUT_DIR}/csr_ai_shuffle.spv
  ${SHADER_OUT_DIR}/csr_ai_resample.spv
  ${SHADER_OUT_DIR}/csr_ai_sharpen.spv
  ${SHADER_OUT_DIR}/ssgi_downsample.spv
  ${SHADER_OUT_DIR}/ssgi_march.spv
  ${SHADER_OUT_DIR}/ssgi_filter.spv
  ${SHADER_OUT_DIR}/ssgi_composite.spv)

set(UFX_MODEL_SRC ${CMAKE_CURRENT_SOURCE_DIR}/../Models/csr_ai_x2.weights.json)
set(UFX_MODEL_OUT ${CMAKE_BINARY_DIR}/models/csr_ai_x2.weights.json)
add_custom_command(
  OUTPUT ${UFX_MODEL_OUT}
  COMMAND ${CMAKE_COMMAND} -E make_directory ${CMAKE_BINARY_DIR}/models
  COMMAND ${CMAKE_COMMAND} -E copy_if_different ${UFX_MODEL_SRC} ${UFX_MODEL_OUT}
  DEPENDS ${UFX_MODEL_SRC}
  COMMENT "copying csr_ai_x2.weights.json")
add_custom_target(ufx_models ALL DEPENDS ${UFX_MODEL_OUT})

set(IMGUI_DIR ${CMAKE_CURRENT_SOURCE_DIR}/src/imgui)
add_library(imgui STATIC
  ${IMGUI_DIR}/imgui.cpp
  ${IMGUI_DIR}/imgui_draw.cpp
  ${IMGUI_DIR}/imgui_tables.cpp
  ${IMGUI_DIR}/imgui_widgets.cpp
  ${IMGUI_DIR}/backends/imgui_impl_sdl2.cpp
  ${IMGUI_DIR}/backends/imgui_impl_vulkan.cpp)
target_include_directories(imgui PUBLIC ${IMGUI_DIR} ${IMGUI_DIR}/backends ${SDL2_INCLUDE_DIRS})
target_link_libraries(imgui PUBLIC ${SDL2_LIBRARIES} ${VULKAN_LIBRARIES})

set(UFX_COMMON
  src/gpu/vk_context.cpp
  src/gpu/vk_dmabuf_import.cpp
  src/gpu/csr_spatial.cpp
  src/gpu/fg2_affine.cpp
  src/gpu/fg2_pipeline.cpp
  src/gpu/csr2_upscale.cpp
  src/gpu/csr_ai_upscale.cpp
  src/gpu/ssgi.cpp
  src/capture/portal_screencast.cpp
  src/platform/session_detect.cpp
  src/platform/larkquill_permission.cpp)

add_library(ufx_core STATIC ${UFX_COMMON})
target_include_directories(ufx_core PUBLIC
  ${CMAKE_CURRENT_SOURCE_DIR}/src
  ${SDL2_INCLUDE_DIRS}
  ${VULKAN_INCLUDE_DIRS}
  ${PIPEWIRE_INCLUDE_DIRS}
  ${SYSTEMD_INCLUDE_DIRS})
target_link_libraries(ufx_core PUBLIC
  ${VULKAN_LIBRARIES}
  ${PIPEWIRE_LIBRARIES}
  ${SYSTEMD_LIBRARIES}
  pthread)
target_compile_options(ufx_core PUBLIC ${PIPEWIRE_CFLAGS_OTHER} ${SYSTEMD_CFLAGS_OTHER})

add_executable(ufx-linux
  src/ui/main.cpp
  src/ui/control_panel.cpp
  src/ui/output_window.cpp
  src/gpu/vk_window.cpp
  src/selftest.cpp
  src/offline_fg.cpp
  src/offline_upscale.cpp)
add_dependencies(ufx-linux ufx_shaders ufx_models)
target_link_libraries(ufx-linux PRIVATE ufx_core imgui ${SDL2_LIBRARIES})
target_include_directories(ufx-linux PRIVATE ${SDL2_INCLUDE_DIRS})
set_target_properties(ufx-linux PROPERTIES
  BUILD_RPATH "$ORIGIN"
  INSTALL_RPATH "$ORIGIN")

add_custom_command(TARGET ufx-linux POST_BUILD
  COMMAND ${CMAKE_COMMAND} -E copy_directory ${SHADER_OUT_DIR} $<TARGET_FILE_DIR:ufx-linux>/shaders
  COMMAND ${CMAKE_COMMAND} -E copy_if_different ${CMAKE_CURRENT_SOURCE_DIR}/NOTICE.md $<TARGET_FILE_DIR:ufx-linux>/NOTICE.md
  COMMAND ${CMAKE_COMMAND} -E copy_if_different ${CMAKE_CURRENT_SOURCE_DIR}/LICENSE $<TARGET_FILE_DIR:ufx-linux>/LICENSE)

add_executable(easu_rcas_numeric_test tests/easu_rcas_numeric_test.cpp)
add_dependencies(easu_rcas_numeric_test ufx_shaders)
target_link_libraries(easu_rcas_numeric_test PRIVATE ufx_core)

add_executable(pacing_numeric_test tests/pacing_numeric_test.cpp)
target_include_directories(pacing_numeric_test PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/src)

add_executable(affine_numeric_test tests/affine_numeric_test.cpp)
target_link_libraries(affine_numeric_test PRIVATE ufx_core)

add_executable(fg2_numeric_test tests/fg2_numeric_test.cpp)
add_dependencies(fg2_numeric_test ufx_shaders)
target_link_libraries(fg2_numeric_test PRIVATE ufx_core)

add_executable(gen_synthetic tests/gen_synthetic.cpp)
target_include_directories(gen_synthetic PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/src)
target_link_libraries(gen_synthetic PRIVATE m)

add_executable(csr2_numeric_test tests/csr2_numeric_test.cpp)
add_dependencies(csr2_numeric_test ufx_shaders)
target_link_libraries(csr2_numeric_test PRIVATE ufx_core)

add_executable(csr_ai_numeric_test tests/csr_ai_numeric_test.cpp)
add_dependencies(csr_ai_numeric_test ufx_shaders ufx_models)
target_link_libraries(csr_ai_numeric_test PRIVATE ufx_core)

add_executable(ssgi_numeric_test tests/ssgi_numeric_test.cpp)
add_dependencies(ssgi_numeric_test ufx_shaders)
target_link_libraries(ssgi_numeric_test PRIVATE ufx_core)

add_executable(larkquill_permission_test tests/larkquill_permission_test.cpp src/platform/larkquill_permission.cpp)
target_include_directories(larkquill_permission_test PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/src)

install(TARGETS ufx-linux RUNTIME DESTINATION bin)
install(DIRECTORY ${SHADER_OUT_DIR}/ DESTINATION share/ufx-linux/shaders)
install(FILES ${UFX_MODEL_SRC} DESTINATION share/ufx-linux/models)
