// minpayload.dylib — a diagnostic payload that does NOTHING but prove it ran.
// Its constructor writes one line to /tmp/minpayload.log. Used to isolate the
// injection mechanism (mach thread -> pthread -> dlopen) from any JVM/JNI code.
#include <stdio.h>
#include <unistd.h>

__attribute__((constructor))
static void boot(void) {
    FILE *f = fopen("/tmp/minpayload.log", "a");
    if (f) { fprintf(f, "MINIMAL payload loaded in pid=%d\n", getpid()); fclose(f); }
}
