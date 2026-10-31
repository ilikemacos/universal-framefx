#include "platform/larkquill_permission.h"

#include <cerrno>
#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <spawn.h>
#include <sstream>
#include <sys/stat.h>
#include <sys/wait.h>

extern char** environ;

namespace larkquill {

const char* const kQuestion = "Do we have permission to run LarkquillAI locally for FrameFX purposes?";
const char* const kUnavailable = "LarkquillAI is unavailable.";
const char* const kFileName = "larkquill-local-permission.json";
const char* const kUrl = "https://larkquillai.vercel.app/";

static bool make_dirs(const std::string& dir) {
    if (dir.empty()) return false;
    std::string cur;
    std::stringstream ss(dir);
    std::string part;
    if (dir[0] == '/') cur = "/";
    while (std::getline(ss, part, '/')) {
        if (part.empty()) continue;
        cur += part + "/";
        if (mkdir(cur.c_str(), 0700) != 0 && errno != EEXIST) return false;
    }
    return true;
}

std::string agent_root() {
    const char* env = std::getenv("FRAMEFX_AGENT_DIR");
    if (env && *env) return env;
    const char* xdg = std::getenv("XDG_DATA_HOME");
    std::string base;
    if (xdg && *xdg) base = xdg;
    else {
        const char* home = std::getenv("HOME");
        base = std::string(home && *home ? home : "/tmp") + "/.local/share";
    }
    return base + "/FrameFX/agent";
}

std::string path_for(const std::string& root) {
    if (!root.empty() && root.back() == '/') return root + kFileName;
    return root + "/" + kFileName;
}

Answer load(const std::string& root) {
    std::ifstream f(path_for(root));
    if (!f) return Answer::Unset;
    std::stringstream ss;
    ss << f.rdbuf();
    std::string s = ss.str();
    size_t k = s.find("\"Allowed\"");
    if (k == std::string::npos) return Answer::Unset;
    size_t c = s.find(':', k);
    if (c == std::string::npos) return Answer::Unset;
    size_t v = s.find_first_not_of(" \t\r\n", c + 1);
    if (v == std::string::npos) return Answer::Unset;
    if (s.compare(v, 4, "true") == 0) return Answer::Yes;
    if (s.compare(v, 5, "false") == 0) return Answer::No;
    return Answer::Unset;
}

bool save(const std::string& root, Answer a) {
    if (!make_dirs(root)) return false;
    std::ofstream f(path_for(root), std::ios::trunc);
    if (!f) return false;
    f << "{\"Allowed\":" << (a == Answer::Yes ? "true" : a == Answer::No ? "false" : "null") << "}";
    return static_cast<bool>(f);
}

void clear(const std::string& root) {
    std::remove(path_for(root).c_str());
}

bool open_web() {
    pid_t pid = 0;
    char cmd[] = "xdg-open";
    char* url = const_cast<char*>(kUrl);
    char* argv[] = {cmd, url, nullptr};
    if (posix_spawnp(&pid, cmd, nullptr, nullptr, argv, environ) != 0) return false;
    int status = 0;
    waitpid(pid, &status, WNOHANG);
    return true;
}

}
