# ARM64 macOS injection PoC — Mac equivalent of the Windows DLL injection.
#
#   make            build everything (injector, payload.dylib, FakeMinecraft)
#   make swaptest   full end-to-end: start a fake host on :38964, launch the
#                   FakeMinecraft JVM, inject, then POST /token/swap and show
#                   the JVM's session token actually change.
#   make run        legacy echo-only PoC against the dumb C target
#   make clean
#
# Injection itself must run as root with SIP disabled.

CC      := clang
CFLAGS  := -Wall -Wextra -O2 -arch arm64
SIGN    := codesign -s - --force            # ad-hoc sign
# injector needs the debugger entitlement for task_for_pid on another process.
SIGN_INJ := codesign -s - --force --entitlements injector.entitlements

# JDK used to compile/embed the injected class, find JNI headers, and run the
# test JVM. MUST be a single arm64 JDK so the compiled bytecode version matches
# the runtime and the JVM is arm64 (not Rosetta x86_64). Pin to arm64 Zulu 21;
# override with `make JAVA_HOME=...` if needed.
JAVA_HOME ?= $(shell /usr/libexec/java_home -a arm64 -v 21 2>/dev/null)
JNI_INC   := -I$(JAVA_HOME)/include -I$(JAVA_HOME)/include/darwin
JAVAC     := $(JAVA_HOME)/bin/javac
JAVA      := $(JAVA_HOME)/bin/java
# target a bytecode level both javac and the test JVM accept
JAVAC_REL := --release 17

all: target injector payload.dylib FakeMinecraft.class

target: target.c
	$(CC) $(CFLAGS) -o $@ $<

injector: injector.c injector.entitlements
	$(CC) $(CFLAGS) -o $@ $<
	$(SIGN_INJ) $@

# 1) compile the injected Java class, 2) embed its bytecode as a C header.
classes/cn/zhyujun/tokenswap/TokenSwapper.class: TokenSwapper.java
	$(JAVAC) $(JAVAC_REL) -d classes TokenSwapper.java

tokenswapper_class.h: classes/cn/zhyujun/tokenswap/TokenSwapper.class
	cd classes && xxd -i cn/zhyujun/tokenswap/TokenSwapper.class > ../tokenswapper_class.h
	# normalise the xxd-generated symbol names to TokenSwapper_class[/_len]
	sed -i '' -E 's/unsigned char .*\[\]/unsigned char TokenSwapper_class[]/; s/unsigned int .*_len/unsigned int TokenSwapper_class_len/' tokenswapper_class.h

payload.dylib: payload.c tokenswapper_class.h
	$(CC) $(CFLAGS) $(JNI_INC) -dynamiclib -o $@ payload.c
	$(SIGN) $@

FakeMinecraft.class: FakeMinecraft.java
	$(JAVAC) $(JAVAC_REL) FakeMinecraft.java

# ── end-to-end token-swap test against a real JVM ───────────────────────
swaptest: all FakeMinecraft.class
	@rm -f $(LOG) /tmp/tokenswapper_poc.log
	@echo "=== starting fake C# host on :38964 ==="
	@python3 fakehost.py & echo $$! > /tmp/poc_host.pid; sleep 1
	@echo "=== starting FakeMinecraft JVM ==="
	@$(JAVA) -cp . FakeMinecraft > /tmp/poc_fakemc.log 2>&1 & echo $$! > /tmp/poc_mc.pid; sleep 2
	@MCPID=$$(cat /tmp/poc_mc.pid); \
	  echo "=== injecting payload into FakeMinecraft pid $$MCPID (needs sudo) ==="; \
	  sudo ./injector $$MCPID "$(CURDIR)/payload.dylib"; \
	  sleep 2; \
	  echo "=== /tmp/tokenswapper_poc.log ==="; cat /tmp/tokenswapper_poc.log 2>/dev/null; \
	  PORT=$$(sed -n 's/.*listening on 127.0.0.1:\([0-9]*\).*/\1/p' /tmp/tokenswapper_poc.log | tail -1); \
	  echo "=== payload port = $$PORT ==="; \
	  echo "=== POST /handshake/init ==="; \
	  curl -s -X POST http://127.0.0.1:$$PORT/handshake/init -d '{}'; echo; \
	  echo "=== POST /token/swap ==="; \
	  curl -s -X POST http://127.0.0.1:$$PORT/token/swap -d '{"access_token":"SWAPPED_BY_INJECTOR"}'; echo; \
	  sleep 2; \
	  echo "=== FakeMinecraft stdout (token should have changed) ==="; cat /tmp/poc_fakemc.log; \
	  echo "=== fake host received ==="; cat /tmp/poc_host.log 2>/dev/null; \
	  kill $$MCPID 2>/dev/null; kill $$(cat /tmp/poc_host.pid) 2>/dev/null

minpayload.dylib: minpayload.c
	$(CC) $(CFLAGS) -dynamiclib -o $@ minpayload.c
	$(SIGN) $@

# ── isolate the injection mechanism from any JVM code ───────────────────
# Inject the do-nothing minimal payload into the plain (non-JVM) target.
# If /tmp/minpayload.log appears, mach-thread -> pthread -> dlopen works and
# the problem is JVM-side. If it does NOT, the injector bootstrap itself is
# the problem (PAC / thread state).
diag: target injector minpayload.dylib
	@rm -f /tmp/minpayload.log
	@echo "=== starting plain target ==="
	@./target & echo $$! > /tmp/poc_target.pid; sleep 1
	@TPID=$$(cat /tmp/poc_target.pid); \
	  echo "=== injecting MINIMAL payload into pid $$TPID (needs sudo) ==="; \
	  sudo ./injector $$TPID "$(CURDIR)/minpayload.dylib"; \
	  sleep 2; \
	  echo "=== /tmp/minpayload.log ==="; \
	  cat /tmp/minpayload.log 2>/dev/null || echo "(EMPTY -> injector bootstrap failed, not JVM)"; \
	  kill $$TPID 2>/dev/null

run: all
	@echo "=== starting target ==="
	@./target & echo $$! > /tmp/poc_target.pid; sleep 1
	@TPID=$$(cat /tmp/poc_target.pid); \
	  echo "=== injecting into pid $$TPID (needs sudo) ==="; \
	  sudo ./injector $$TPID "$(CURDIR)/payload.dylib"; \
	  sleep 1; \
	  cat /tmp/tokenswapper_poc.log 2>/dev/null || echo "(no log — injection did not land)"; \
	  kill $$TPID 2>/dev/null

clean:
	rm -f target injector payload.dylib minpayload.dylib FakeMinecraft.class
	rm -f tokenswapper_class.h
	rm -rf classes
	rm -f /tmp/tokenswapper_poc.log /tmp/minpayload.log /tmp/poc_*.pid /tmp/poc_*.log

.PHONY: all swaptest diag run clean
