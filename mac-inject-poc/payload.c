// payload.dylib — the macOS equivalent of TokenSwapper.dll.
//
// When dlopen()'d inside the target Minecraft JVM (by injector), its
// constructor runs and reproduces, 1:1, what the Windows DLL does:
//
//   Windows TokenSwapper.dll                 this payload.dylib
//   --------------------------------------   --------------------------------
//   JNI_GetCreatedJavaVMs                     JNI_GetCreatedJavaVMs (dlsym)
//   AttachCurrentThread                       AttachCurrentThread
//   find Minecraft ClassLoader, defineClass   find game loader / DefineClass the
//     cn/zhyujun/tokenswap/TokenSwapper          embedded TokenSwapper.class
//   in-JVM HTTP server                        in-process HTTP server here
//     POST /client/online   -> app:38964        same
//     POST /handshake/init  -> {success}        same
//     POST /token/swap      -> swap session     calls TokenSwapper.swap()
//
// The TokenSwapper.class bytecode is embedded at build time (see Makefile:
// javac -> xxd -i -> tokenswapper_class.h).

#include <stdio.h>
#include <stdlib.h>
#include <stdarg.h>
#include <string.h>
#include <unistd.h>
#include <pthread.h>
#include <dlfcn.h>
#include <arpa/inet.h>
#include <sys/socket.h>
#include <netinet/in.h>
#include <jni.h>

#include "tokenswapper_class.h"   // unsigned char TokenSwapper_class[]; unsigned int TokenSwapper_class_len;

#define APP_PORT 38964            // the C# host's HttpListener (MainWindow.xaml.cs:228)
#define LOG_PATH "/tmp/tokenswapper_poc.log"

static JavaVM *g_vm = NULL;
static jclass  g_swapperClass = NULL;     // global ref to cn.zhyujun.tokenswap.TokenSwapper
static int     g_port = 0;                // this payload's own HTTP port

static void log_line(const char *fmt, ...) {
    FILE *f = fopen(LOG_PATH, "a");
    if (!f) return;
    fprintf(f, "[payload pid=%d] ", getpid());
    va_list ap; va_start(ap, fmt);
    vfprintf(f, fmt, ap);
    va_end(ap);
    fputc('\n', f);
    fclose(f);
}

// ── JVM attach + class injection ───────────────────────────────────────

// Resolve JNI_GetCreatedJavaVMs from libjvm already mapped in this process.
typedef jint (*GetCreatedVMs_t)(JavaVM **, jsize, jsize *);

static int locate_jvm(void) {
    GetCreatedVMs_t fn = (GetCreatedVMs_t)dlsym(RTLD_DEFAULT, "JNI_GetCreatedJavaVMs");
    if (!fn) {
        // Not exported into the default namespace; load libjvm explicitly.
        void *h = dlopen("libjvm.dylib", RTLD_NOW | RTLD_GLOBAL);
        if (!h) h = dlopen("@rpath/libjvm.dylib", RTLD_NOW | RTLD_GLOBAL);
        if (h) fn = (GetCreatedVMs_t)dlsym(h, "JNI_GetCreatedJavaVMs");
    }
    if (!fn) { log_line("could not resolve JNI_GetCreatedJavaVMs"); return -1; }

    JavaVM *vms[8];
    jsize n = 0;
    jint rc = fn(vms, 8, &n);
    if (rc != JNI_OK || n < 1) { log_line("JNI_GetCreatedJavaVMs rc=%d n=%d", rc, n); return -1; }

    g_vm = vms[0];
    log_line("attached to JVM (%d VM(s) created)", n);
    return 0;
}

// Get a JNIEnv for the calling thread, attaching if needed.
static JNIEnv *get_env(void) {
    JNIEnv *env = NULL;
    jint rc = (*g_vm)->GetEnv(g_vm, (void **)&env, JNI_VERSION_1_8);
    if (rc == JNI_EDETACHED) {
        if ((*g_vm)->AttachCurrentThread(g_vm, (void **)&env, NULL) != JNI_OK) {
            log_line("AttachCurrentThread failed");
            return NULL;
        }
    } else if (rc != JNI_OK) {
        log_line("GetEnv rc=%d", rc);
        return NULL;
    }
    return env;
}

// Find a usable classloader: prefer the thread context loader of the calling
// thread (the game/launcher loader), mirroring the Windows DLL's
// Thread.getContextClassLoader use. Falls back to the system classloader.
static jobject find_game_classloader(JNIEnv *env) {
    jclass clThread = (*env)->FindClass(env, "java/lang/Thread");
    if (!clThread) { (*env)->ExceptionClear(env); goto system; }

    {
        jmethodID midCurrent = (*env)->GetStaticMethodID(env, clThread, "currentThread", "()Ljava/lang/Thread;");
        jmethodID midCtx     = (*env)->GetMethodID(env, clThread, "getContextClassLoader", "()Ljava/lang/ClassLoader;");
        if (midCurrent && midCtx) {
            jobject cur = (*env)->CallStaticObjectMethod(env, clThread, midCurrent);
            if (cur) {
                jobject loader = (*env)->CallObjectMethod(env, cur, midCtx);
                if (loader) return loader;
            }
        }
        (*env)->ExceptionClear(env);
    }

system:;
    // Fallback: ClassLoader.getSystemClassLoader()
    jclass clCL = (*env)->FindClass(env, "java/lang/ClassLoader");
    if (!clCL) { (*env)->ExceptionClear(env); return NULL; }
    jmethodID midSys = (*env)->GetStaticMethodID(env, clCL, "getSystemClassLoader", "()Ljava/lang/ClassLoader;");
    if (!midSys) { (*env)->ExceptionClear(env); return NULL; }
    return (*env)->CallStaticObjectMethod(env, clCL, midSys);
}

// DefineClass the embedded TokenSwapper bytecode into the game classloader.
static int inject_swapper_class(JNIEnv *env) {
    // Maybe it's already defined (re-injection): try FindClass first.
    jclass existing = (*env)->FindClass(env, "cn/zhyujun/tokenswap/TokenSwapper");
    if (existing) {
        g_swapperClass = (*env)->NewGlobalRef(env, existing);
        log_line("TokenSwapper already present");
        return 0;
    }
    (*env)->ExceptionClear(env);

    jobject loader = find_game_classloader(env);
    if (!loader) { log_line("no classloader found"); return -1; }

    jclass defined = (*env)->DefineClass(
        env, "cn/zhyujun/tokenswap/TokenSwapper", loader,
        (const jbyte *)TokenSwapper_class, (jsize)TokenSwapper_class_len);

    if ((*env)->ExceptionCheck(env)) {
        (*env)->ExceptionDescribe(env);
        (*env)->ExceptionClear(env);
        log_line("DefineClass threw");
        return -1;
    }
    if (!defined) { log_line("DefineClass returned NULL"); return -1; }

    g_swapperClass = (*env)->NewGlobalRef(env, defined);
    log_line("defined TokenSwapper class");
    return 0;
}

// Call TokenSwapper.swap(String) -> boolean ; fills msg with lastMessage().
static int jvm_swap_token(const char *accessToken, char *msg, size_t msgcap) {
    JNIEnv *env = get_env();
    if (!env || !g_swapperClass) { snprintf(msg, msgcap, "no JVM/class"); return 0; }

    jmethodID midSwap = (*env)->GetStaticMethodID(env, g_swapperClass, "swap", "(Ljava/lang/String;)Z");
    jmethodID midMsg  = (*env)->GetStaticMethodID(env, g_swapperClass, "lastMessage", "()Ljava/lang/String;");
    if (!midSwap) { (*env)->ExceptionClear(env); snprintf(msg, msgcap, "no swap method"); return 0; }

    jstring jtok = (*env)->NewStringUTF(env, accessToken);
    jboolean ok  = (*env)->CallStaticBooleanMethod(env, g_swapperClass, midSwap, jtok);
    (*env)->DeleteLocalRef(env, jtok);

    if (midMsg) {
        jstring jm = (*env)->CallStaticObjectMethod(env, g_swapperClass, midMsg);
        if (jm) {
            const char *cm = (*env)->GetStringUTFChars(env, jm, NULL);
            if (cm) { snprintf(msg, msgcap, "%s", cm); (*env)->ReleaseStringUTFChars(env, jm, cm); }
            (*env)->DeleteLocalRef(env, jm);
        }
    }
    log_line("swap(\"%s\") -> %s (%s)", accessToken, ok ? "true" : "false", msg);
    return ok ? 1 : 0;
}

// ── tiny HTTP plumbing ──────────────────────────────────────────────────

static void send_json(int c, int status, const char *json) {
    char hdr[256];
    int n = snprintf(hdr, sizeof(hdr),
        "HTTP/1.1 %d %s\r\nContent-Type: application/json\r\nContent-Length: %zu\r\nConnection: close\r\n\r\n",
        status, status == 200 ? "OK" : "Error", strlen(json));
    write(c, hdr, n);
    write(c, json, strlen(json));
}

// Extract a "access_token":"..." value from a small JSON body. Good enough
// for the fixed payload the C# host sends.
static int extract_access_token(const char *body, char *out, size_t cap) {
    const char *k = strstr(body, "access_token");
    if (!k) return 0;
    const char *colon = strchr(k, ':');
    if (!colon) return 0;
    const char *q1 = strchr(colon, '"');
    if (!q1) return 0;
    q1++;
    const char *q2 = strchr(q1, '"');
    if (!q2) return 0;
    size_t len = (size_t)(q2 - q1);
    if (len >= cap) len = cap - 1;
    memcpy(out, q1, len);
    out[len] = 0;
    return 1;
}

// Report this payload's port back to the C# host: POST /client/online.
static void post_client_online(int my_port) {
    int s = socket(AF_INET, SOCK_STREAM, 0);
    if (s < 0) return;
    struct sockaddr_in addr = {0};
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    addr.sin_port = htons(APP_PORT);
    if (connect(s, (struct sockaddr *)&addr, sizeof(addr)) < 0) {
        log_line("client/online: host :%d not reachable (ok if no host yet)", APP_PORT);
        close(s);
        return;
    }
    char body[128];
    int blen = snprintf(body, sizeof(body), "{\"pid\":%d,\"port\":%d}", getpid(), my_port);
    char req[256];
    // Host must match the IP we actually connected to (127.0.0.1). .NET's
    // HttpListener routes by the Host header against its registered prefixes;
    // sending "localhost" while connecting to 127.0.0.1 made it return 404.
    int rlen = snprintf(req, sizeof(req),
        "POST /client/online HTTP/1.1\r\nHost: 127.0.0.1:%d\r\nContent-Type: application/json\r\n"
        "Content-Length: %d\r\nConnection: close\r\n\r\n%s",
        APP_PORT, blen, body);
    // Write the full request, then read the response before closing. Closing
    // immediately after write() can tear the connection down before the kernel
    // flushes the bytes / before the HTTP server finishes reading the body —
    // which is why the host never saw our POST. Reading the reply forces a
    // graceful, fully-flushed exchange.
    ssize_t off = 0;
    while (off < rlen) {
        ssize_t w = write(s, req + off, (size_t)(rlen - off));
        if (w <= 0) break;
        off += w;
    }
    shutdown(s, SHUT_WR);          // signal end-of-request
    char resp[512];
    ssize_t r = read(s, resp, sizeof(resp) - 1);   // wait for the host's reply
    if (r > 0) { resp[r] = 0; }
    log_line("reported to host :%d -> %s (reply %zd bytes)", APP_PORT, body, r);
    close(s);
}

static const char *body_of(const char *buf) {
    const char *p = strstr(buf, "\r\n\r\n");
    return p ? p + 4 : "";
}

static void *server_thread(void *arg) {
    (void)arg;

    // Do all JVM work on THIS thread (a clean pthread), not the hijacked one.
    // AttachCurrentThread here gives a valid JNIEnv for the lifetime of this
    // thread, which is exactly what we want for the swap calls.
    if (locate_jvm() == 0) {
        JNIEnv *env = get_env();
        if (env) {
            if (inject_swapper_class(env) != 0)
                log_line("class injection failed; /token/swap will report failure");
        }
    } else {
        log_line("no JVM in this process; running as plain HTTP echo");
    }

    int s = socket(AF_INET, SOCK_STREAM, 0);
    if (s < 0) { log_line("socket() failed"); return NULL; }
    int yes = 1;
    setsockopt(s, SOL_SOCKET, SO_REUSEADDR, &yes, sizeof(yes));

    struct sockaddr_in addr = {0};
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    addr.sin_port = htons(0);              // ephemeral port, like the real DLL
    if (bind(s, (struct sockaddr *)&addr, sizeof(addr)) < 0) { log_line("bind failed"); close(s); return NULL; }

    socklen_t alen = sizeof(addr);
    getsockname(s, (struct sockaddr *)&addr, &alen);
    g_port = ntohs(addr.sin_port);
    listen(s, 8);
    log_line("HTTP server listening on 127.0.0.1:%d", g_port);

    post_client_online(g_port);

    for (;;) {
        int c = accept(s, NULL, NULL);
        if (c < 0) continue;
        char buf[8192];
        // A single read() returns only the first TCP segment. A ~750-char JWT
        // POST arrives in several segments, so one read truncated the token
        // mid-string (→ invalid token written to the session). Read until we
        // have the full body: parse Content-Length once headers are in, then
        // keep reading until header_len + content_length bytes are received.
        ssize_t total = 0;
        long content_len = -1, header_len = -1;
        for (;;) {
            ssize_t n = read(c, buf + total, sizeof(buf) - 1 - (size_t)total);
            if (n <= 0) break;
            total += n;
            buf[total] = 0;
            if (header_len < 0) {
                char *hdr_end = strstr(buf, "\r\n\r\n");
                if (hdr_end) {
                    header_len = (hdr_end - buf) + 4;
                    char *cl = strcasestr(buf, "Content-Length:");
                    content_len = cl ? strtol(cl + 15, NULL, 10) : 0;
                }
            }
            if (header_len >= 0 && total >= header_len + content_len) break;
            if ((size_t)total >= sizeof(buf) - 1) break;
        }
        if (total <= 0) { close(c); continue; }
        buf[total] = 0;
        ssize_t n = total;  (void)n;

        if (strncmp(buf, "POST /handshake/init", 20) == 0) {
            send_json(c, 200, "{\"success\":true,\"message\":\"ready\"}");
        } else if (strncmp(buf, "POST /token/swap", 16) == 0) {
            char tok[4096] = {0};
            char msg[512]  = {0};
            int ok = 0;
            if (extract_access_token(body_of(buf), tok, sizeof(tok))) {
                ok = jvm_swap_token(tok, msg, sizeof(msg));
            } else {
                snprintf(msg, sizeof(msg), "missing access_token");
            }
            char resp[768];
            // msg is our own controlled text; no JSON escaping needed here.
            snprintf(resp, sizeof(resp), "{\"success\":%s,\"message\":\"%s\"}",
                     ok ? "true" : "false", msg);
            send_json(c, 200, resp);
        } else {
            send_json(c, 404, "{\"success\":false,\"message\":\"not found\"}");
        }
        close(c);
    }
    return NULL;
}

// ── entry ────────────────────────────────────────────────────────────────

__attribute__((constructor))
static void on_load(void) {
    log_line("payload loaded inside target process");
    // IMPORTANT: do NOT touch JNI here. The constructor runs on the HIJACKED
    // thread, whose state we are about to restore — calling AttachCurrentThread
    // on it corrupts both that thread and the JVM. All JVM work happens on the
    // server thread (a clean, freshly-created pthread).
    pthread_t t;
    pthread_create(&t, NULL, server_thread, NULL);
    pthread_detach(t);
}
