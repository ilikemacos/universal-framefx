#include "platform/larkquill_permission.h"

#include <cstdio>
#include <cstdlib>
#include <string>
#include <unistd.h>

static int fails = 0;

static void check(bool ok, const char* what) {
    if (!ok) {
        std::fprintf(stderr, "FAIL %s\n", what);
        ++fails;
    }
}

int main() {
    char tmpl[] = "/tmp/framefx-perm-XXXXXX";
    char* dir = mkdtemp(tmpl);
    if (!dir) return 1;
    std::string root = std::string(dir) + "/nested/agent";

    check(larkquill::load(root) == larkquill::Answer::Unset, "missing file is unset");
    check(larkquill::save(root, larkquill::Answer::Yes), "save yes");
    check(larkquill::load(root) == larkquill::Answer::Yes, "yes roundtrip");
    check(larkquill::save(root, larkquill::Answer::No), "save no");
    check(larkquill::load(root) == larkquill::Answer::No, "no roundtrip");
    larkquill::clear(root);
    check(larkquill::load(root) == larkquill::Answer::Unset, "clear resets");

    FILE* f = std::fopen(larkquill::path_for(root).c_str(), "w");
    if (f) {
        std::fputs("{ \"Allowed\" : true }", f);
        std::fclose(f);
    }
    check(larkquill::load(root) == larkquill::Answer::Yes, "reads the Windows file shape");

    std::string p = larkquill::path_for("/x/agent");
    check(p == "/x/agent/larkquill-local-permission.json", "file name matches Windows");

    setenv("FRAMEFX_AGENT_DIR", "/opt/fx", 1);
    check(larkquill::agent_root() == "/opt/fx", "FRAMEFX_AGENT_DIR wins");
    unsetenv("FRAMEFX_AGENT_DIR");
    setenv("XDG_DATA_HOME", "/data", 1);
    check(larkquill::agent_root() == "/data/FrameFX/agent", "XDG_DATA_HOME root");

    larkquill::clear(root);
    std::string cmd = std::string("rm -rf ") + dir;
    if (std::system(cmd.c_str()) != 0) ++fails;
    if (fails) return 1;
    std::puts("larkquill_permission_test: ok");
    return 0;
}
