#pragma once

#include <string>

namespace larkquill {

enum class Answer { Unset, Yes, No };

extern const char* const kQuestion;
extern const char* const kUnavailable;
extern const char* const kFileName;
extern const char* const kUrl;

std::string agent_root();
std::string path_for(const std::string& root);
Answer load(const std::string& root);
bool save(const std::string& root, Answer a);
void clear(const std::string& root);
bool open_web();

}
