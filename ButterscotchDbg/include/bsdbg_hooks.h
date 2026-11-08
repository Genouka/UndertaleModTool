#ifndef _BSDBG_HOOKS_H_
#define _BSDBG_HOOKS_H_

/*
 * Hook points compiled into Butterscotch when the BSDBG compile definition is
 * set (the ButterscotchDbg DLL target). Without BSDBG every hook is an inline
 * no-op, so the stock executable/other platforms are unaffected.
 */

#include "common.h"

struct Runner;
struct VMContext;

#ifdef BSDBG

void BsdbgHook_LoopStart(struct Runner* runner, struct VMContext* vm);
int  BsdbgHook_ShouldExit(void);
/* Called once per frame right after Runner_step returns. May block the game
 * thread (pause at frame boundary, frame-step stop, hot reload apply). */
void BsdbgHook_FrameBoundary(struct Runner* runner);
/* Called before every VM instruction dispatch. Fast path: two volatile loads. */
void BsdbgHook_VmInstruction(struct VMContext* ctx, uint32_t ip);
/* Called from the log layer for every log line. level: 0=debug 1=info 2=warn 3=error. */
void BsdbgHook_Log(int level, const char* message);

#else

static inline void BsdbgHook_LoopStart(struct Runner* runner, struct VMContext* vm) { (void)runner; (void)vm; }
static inline int  BsdbgHook_ShouldExit(void) { return 0; }
static inline void BsdbgHook_FrameBoundary(struct Runner* runner) { (void)runner; }
static inline void BsdbgHook_VmInstruction(struct VMContext* ctx, uint32_t ip) { (void)ctx; (void)ip; }
static inline void BsdbgHook_Log(int level, const char* message) { (void)level; (void)message; }

#endif

#endif /* _BSDBG_HOOKS_H_ */
