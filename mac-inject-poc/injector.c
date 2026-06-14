// injector — macOS / Apple Silicon (ARM64) equivalent of the Windows
// CreateRemoteThread + LoadLibraryA technique used in TokenInjectionService.cs.
//
// Windows version                       This version
// ----------------------------------    ---------------------------------------
// OpenProcess(PROCESS_ALL_ACCESS)       task_for_pid()
// VirtualAllocEx                        mach_vm_allocate
// WriteProcessMemory                    mach_vm_write
// GetProcAddress(LoadLibraryA)          resolve dlopen() (see note below)
// CreateRemoteThread(LoadLibraryA,path) thread_create_running() with a tiny
//                                       ARM64 bootstrap that calls dlopen(path)
//
// REQUIREMENTS on Apple Silicon:
//   * SIP disabled (csrutil status -> disabled)   [your machine: OK]
//   * run as root (sudo) so task_for_pid succeeds
//
// NOTE on dlopen address: we assume libdyld/dlopen is mapped at the SAME
// address in the target as in ourselves. That holds when both processes share
// the dyld shared cache slide — true for normally-launched processes without
// per-process ASLR differences in the shared region. If it ever fails, the
// robust fix is to read the target's dyld_all_image_infos and compute the
// remote slide. For a first PoC we use the simple assumption and print the
// address so you can verify.

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <dlfcn.h>
#include <mach/mach.h>
#include <mach/mach_vm.h>
#include <mach/thread_act.h>
#include <mach/arm/thread_status.h>
#include <mach/task_info.h>
#include <mach-o/dyld_images.h>
#include <mach-o/dyld.h>
#include <pthread.h>

// SPI: spawns a real pthread from a bare mach thread. Declared here because
// it isn't in the public pthread.h. We only need its address, not to call it.
extern int pthread_create_from_mach_thread(
    pthread_t *thread, const pthread_attr_t *attr,
    void *(*start_routine)(void *), void *arg);

#define STACK_SIZE   (512 * 1024)

// Both this process and the target share the SAME dyld shared cache image, but
// each maps it at its own slide. dlopen lives in that shared cache, so:
//
//   remote_dlopen = local_dlopen - local_cache_slide + remote_cache_slide
//
// We read each process's shared-cache slide from its dyld_all_image_infos
// (TASK_DYLD_INFO). This replaces the naive "same address" assumption that
// fails for JVM processes.

static int read_cache_slide(task_t task, uint64_t *slide_out) {
    struct task_dyld_info dyld_info;
    mach_msg_type_number_t count = TASK_DYLD_INFO_COUNT;
    kern_return_t kr = task_info(task, TASK_DYLD_INFO,
                                 (task_info_t)&dyld_info, &count);
    if (kr != KERN_SUCCESS) {
        fprintf(stderr, "task_info(TASK_DYLD_INFO) failed: %s\n", mach_error_string(kr));
        return -1;
    }

    // Read the all_image_infos struct out of the target.
    struct dyld_all_image_infos infos;
    mach_vm_size_t got = 0;
    kr = mach_vm_read_overwrite(task, dyld_info.all_image_info_addr,
                                sizeof(infos), (mach_vm_address_t)&infos, &got);
    if (kr != KERN_SUCCESS || got < sizeof(infos)) {
        fprintf(stderr, "mach_vm_read_overwrite(all_image_infos) failed: %s\n",
                mach_error_string(kr));
        return -1;
    }
    *slide_out = (uint64_t)infos.sharedCacheSlide;
    return 0;
}

// Read both this process's and the target's shared-cache slide once.
// dlopen, pthread_*, etc. all live in the shared cache, so any local symbol
// pointer re-slides to the target as: local_sym - local_slide + remote_slide.
static int read_both_slides(task_t task, uint64_t *local_out, uint64_t *remote_out) {
    uint64_t local_slide = (uint64_t)_dyld_get_image_vmaddr_slide(0);
    uint64_t self_cache = 0;
    if (read_cache_slide(mach_task_self(), &self_cache) == 0 && self_cache != 0)
        local_slide = self_cache;

    uint64_t remote_slide = 0;
    if (read_cache_slide(task, &remote_slide) != 0) {
        fprintf(stderr, "[!] could not read remote cache slide\n");
        return -1;
    }
    *local_out = local_slide;
    *remote_out = remote_slide;
    return 0;
}

// Re-slide a local shared-cache symbol pointer to the target process.
static uint64_t reslide(void *local_sym, uint64_t local_slide, uint64_t remote_slide) {
    return (uint64_t)local_sym - local_slide + remote_slide;
}

// ARM64 bootstrap shellcode — NEW-THREAD edition (the safe one).
//
// This mirrors what the Windows TokenSwapper.dll does: it never touches the
// game's existing threads. The Windows DLL is LoadLibrary'd and its DllMain
// calls CreateThread() to spin up its OWN worker thread. Hijacking a live
// game thread (our previous approach) crashed real Minecraft because the
// borrowed render/GC thread couldn't resume cleanly.
//
// macOS equivalent: thread_create_running() makes a brand-new bare thread.
// A bare thread has no pthread/TLS context, so it must NOT call dlopen()
// directly (dlopen needs TLS/locks). Instead the bare thread calls
// pthread_create_from_mach_thread() — an SPI made exactly for this: it
// safely promotes into a real pthread. That real pthread then runs `stub`
// which calls dlopen(). The bare thread just parks in a wfe/b. loop; the
// game's own threads are never disturbed → no crash.
//
// Bootstrap (bare thread):
//   ldr x0, =tid_scratch     ; &pthread_t out
//   mov x1, #0               ; attr = NULL
//   ldr x2, =stub            ; start_routine
//   ldr x3, =path            ; arg = dylib path
//   ldr x4, =pthread_cfmt
//   blr x4
//   spin: b spin             ; park forever (harmless extra thread)
// stub (real pthread, x0 = path):
//   mov x1, #2 (RTLD_NOW)
//   ldr x2, =dlopen
//   blr x2
//   ret                      ; pthread exits cleanly

#define LIT_TID     0
#define LIT_STUB    1
#define LIT_PATH    2
#define LIT_PTHREAD 3
#define LIT_DLOPEN  4
#define BOOT_LIT_COUNT 5
#define BOOT_CODE_LEN  0x40          // generous; we use < this
#define BOOT_TOTAL     (BOOT_CODE_LEN + BOOT_LIT_COUNT * 8)

static uint32_t enc_ldr_lit(int rt, int32_t imm) {
    uint32_t imm19 = ((uint32_t)(imm >> 2)) & 0x7ffff;
    return 0x58000000u | (imm19 << 5) | (uint32_t)(rt & 0x1f);
}

// Returns the byte offset of the stub entry within the blob (for thread_state
// callers that need it; here the bootstrap references it via a literal).
static void build_bootstrap(uint8_t *out,
                            uint64_t remote_base,
                            uint64_t tid_addr,
                            uint64_t path_addr,
                            uint64_t pthread_addr,
                            uint64_t dlopen_addr_) {
    uint64_t lit_base = remote_base + BOOT_CODE_LEN;
    uint32_t *c = (uint32_t *)out;
    int n = 0;
    #define EMIT(w) do { c[n] = (uint32_t)(w); n++; } while (0)
    #define LDR(rt, li) do {                                                  \
        int32_t _imm = (int32_t)((lit_base + (uint64_t)(li) * 8)              \
                                 - (remote_base + (uint64_t)n * 4));          \
        c[n] = enc_ldr_lit((rt), _imm); n++;                                  \
    } while (0)

    // ── bootstrap (bare thread) ──
    LDR(0, LIT_TID);          // x0 = &tid
    EMIT(0xd2800001u);        // mov x1, #0
    LDR(2, LIT_STUB);         // x2 = stub addr
    LDR(3, LIT_PATH);         // x3 = path
    LDR(4, LIT_PTHREAD);      // x4 = pthread_create_from_mach_thread
    EMIT(0xd63f0080u);        // blr x4
    int spin = n;             // park forever so the bare thread never returns
    EMIT(0x14000000u | (uint32_t)((spin - n) & 0x3ffffff)); // b .  (self)

    // ── stub (runs on the real pthread; x0 already = path) ──
    int stub_off = n * 4;
    EMIT(0xd2800041u);        // mov x1, #2 (RTLD_NOW)
    LDR(2, LIT_DLOPEN);       // x2 = dlopen
    EMIT(0xd63f0040u);        // blr x2
    EMIT(0xd65f03c0u);        // ret  (pthread start_routine returns -> thread exits)

    #undef EMIT
    #undef LDR

    uint64_t lit[BOOT_LIT_COUNT];
    lit[LIT_TID]     = tid_addr;
    lit[LIT_STUB]    = remote_base + (uint64_t)stub_off;
    lit[LIT_PATH]    = path_addr;
    lit[LIT_PTHREAD] = pthread_addr;
    lit[LIT_DLOPEN]  = dlopen_addr_;
    memcpy(out + BOOT_CODE_LEN, lit, sizeof(lit));
}

static int write_remote(task_t task, mach_vm_address_t addr,
                        const void *data, size_t len) {
    kern_return_t kr = mach_vm_write(task, addr,
                                     (vm_offset_t)data, (mach_msg_type_number_t)len);
    if (kr != KERN_SUCCESS) {
        fprintf(stderr, "mach_vm_write @ 0x%llx failed: %s\n",
                addr, mach_error_string(kr));
        return -1;
    }
    return 0;
}

int main(int argc, char **argv) {
    if (argc != 3) {
        fprintf(stderr, "usage: %s <pid> <absolute-path-to-dylib>\n", argv[0]);
        return 2;
    }

    pid_t pid = (pid_t)atoi(argv[1]);
    const char *dylib = argv[2];
    size_t pathlen = strlen(dylib) + 1;

    // ── 1. task_for_pid (the OpenProcess equivalent) ───────────────────────
    task_t task;
    kern_return_t kr = task_for_pid(mach_task_self(), pid, &task);
    if (kr != KERN_SUCCESS) {
        fprintf(stderr,
            "task_for_pid(%d) failed: %s\n"
            "  -> are you running as root, with SIP disabled?\n",
            pid, mach_error_string(kr));
        return 1;
    }
    printf("[+] got task port for pid %d\n", pid);

    // Resolve dlopen's address inside the target by re-sliding our own (it
    // lives in the dyld shared cache).
    uint64_t local_slide, remote_slide;
    if (read_both_slides(task, &local_slide, &remote_slide) != 0) return 1;
    uint64_t dlopen_addr  = reslide((void *)&dlopen, local_slide, remote_slide);
    uint64_t pthread_addr = reslide((void *)&pthread_create_from_mach_thread,
                                    local_slide, remote_slide);
    printf("[+] cache slide: local=0x%llx remote=0x%llx\n", local_slide, remote_slide);
    printf("[+] dlopen @ 0x%llx  pthread_create_from_mach_thread @ 0x%llx\n",
           dlopen_addr, pthread_addr);

    // ── 2. allocate + write the dylib path string ──────────────────────────
    mach_vm_address_t remote_path = 0;
    kr = mach_vm_allocate(task, &remote_path, pathlen, VM_FLAGS_ANYWHERE);
    if (kr != KERN_SUCCESS) {
        fprintf(stderr, "alloc path failed: %s\n", mach_error_string(kr));
        return 1;
    }
    if (write_remote(task, remote_path, dylib, pathlen)) return 1;
    printf("[+] wrote dylib path @ 0x%llx\n", remote_path);

    // ── 3. allocate the bootstrap code region ──────────────────────────────
    mach_vm_address_t remote_code = 0;
    kr = mach_vm_allocate(task, &remote_code, BOOT_TOTAL, VM_FLAGS_ANYWHERE);
    if (kr != KERN_SUCCESS) {
        fprintf(stderr, "alloc code failed: %s\n", mach_error_string(kr));
        return 1;
    }
    // ── 4. allocate a tid scratch slot + a stack for the new bare thread ───
    mach_vm_address_t remote_tid = 0;
    kr = mach_vm_allocate(task, &remote_tid, sizeof(uint64_t), VM_FLAGS_ANYWHERE);
    if (kr != KERN_SUCCESS) { fprintf(stderr, "alloc tid failed: %s\n", mach_error_string(kr)); return 1; }

    mach_vm_address_t remote_stack = 0;
    kr = mach_vm_allocate(task, &remote_stack, STACK_SIZE, VM_FLAGS_ANYWHERE);
    if (kr != KERN_SUCCESS) { fprintf(stderr, "alloc stack failed: %s\n", mach_error_string(kr)); return 1; }
    mach_vm_address_t sp = (remote_stack + STACK_SIZE - 16) & ~0xFULL;
    printf("[+] new-thread stack @ 0x%llx (sp=0x%llx)\n", remote_stack, sp);

    // ── 5. build + write the bootstrap ─────────────────────────────────────
    uint8_t boot[BOOT_TOTAL];
    memset(boot, 0, sizeof(boot));
    build_bootstrap(boot, (uint64_t)remote_code, (uint64_t)remote_tid,
                    (uint64_t)remote_path, pthread_addr, dlopen_addr);
    if (write_remote(task, remote_code, boot, BOOT_TOTAL)) return 1;
    kr = mach_vm_protect(task, remote_code, BOOT_TOTAL, FALSE, VM_PROT_READ | VM_PROT_EXECUTE);
    if (kr != KERN_SUCCESS) {
        fprintf(stderr, "mach_vm_protect(code) failed: %s\n", mach_error_string(kr));
        return 1;
    }
    printf("[+] wrote bootstrap code @ 0x%llx\n", remote_code);

    // ── 6. create a BRAND-NEW thread (never touches the game's threads) ────
    // This is the macOS equivalent of the Windows DLL's CreateThread: the new
    // bare thread calls pthread_create_from_mach_thread() to promote to a real
    // pthread, which then dlopen()s the payload. The game's own threads are
    // never suspended, hijacked, or modified → no crash.
    arm_thread_state64_t state;
    memset(&state, 0, sizeof(state));
    __darwin_arm_thread_state64_set_pc_fptr(state, (void *)remote_code);
    __darwin_arm_thread_state64_set_sp(state, (void *)sp);

    thread_act_t newthread = MACH_PORT_NULL;
    kr = thread_create_running(task, ARM_THREAD_STATE64,
                               (thread_state_t)&state, ARM_THREAD_STATE64_COUNT,
                               &newthread);
    if (kr != KERN_SUCCESS) {
        fprintf(stderr, "thread_create_running failed: %s\n", mach_error_string(kr));
        return 1;
    }
    printf("[+] spawned new thread; it will pthread_create -> dlopen(\"%s\") in pid %d\n",
           dylib, pid);
    printf("[+] check the payload log for proof it loaded.\n");
    return 0;
}
