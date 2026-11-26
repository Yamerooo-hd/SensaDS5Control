#ifndef SENSA_RUNTIME_FAIL_FAST_H
#define SENSA_RUNTIME_FAIL_FAST_H

#ifdef __cplusplus
extern "C" {
#endif

[[noreturn]] void sensa_runtime_panic(char const *message, ...);

#ifdef __cplusplus
}
#endif

#endif
