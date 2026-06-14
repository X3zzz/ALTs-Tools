// target — a stand-in for the Minecraft JVM process. Does nothing but stay
// alive so we can practice injecting into it without risking a real game.
#include <stdio.h>
#include <unistd.h>

int main(void) {
    printf("target running, pid=%d — waiting to be injected\n", getpid());
    fflush(stdout);
    for (;;) sleep(1);
    return 0;
}
