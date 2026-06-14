// TokenSwapper.java — the Java class injected into the target Minecraft JVM.
//
// This is the macOS equivalent of the `cn.zhyujun.tokenswap.TokenSwapper`
// class that the Windows TokenSwapper.dll defines inside the game JVM via
// JVMTI/JNI defineClass. Same package + class name on purpose.
//
// Responsibilities (mirrors the Windows DLL semantics):
//   * swap(accessToken): locate the live Minecraft session object and replace
//     its access token in-memory, so the running game uses the new account
//     without restarting.
//
// It is deliberately written with ZERO compile-time dependencies on the
// Minecraft jars: everything is done by reflection over classes already
// loaded in the target JVM. That keeps it buildable with a plain JDK and
// resilient across obfuscation/remap layouts. The payload.dylib calls into
// this class purely by JNI (FindClass / GetStaticMethodID / CallStatic...),
// so the method signatures here are the contract with payload.c.

package cn.zhyujun.tokenswap;

import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.lang.reflect.Modifier;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;

public final class TokenSwapper {

    // Last human-readable status, fetched by payload.c to build the HTTP
    // response message. Kept static so JNI can read it with one call.
    private static volatile String lastMessage = "";

    public static String lastMessage() {
        return lastMessage;
    }

    /**
     * Entry point invoked over JNI by payload.dylib's /token/swap handler.
     *
     * @return true if a session token was actually replaced.
     */
    public static boolean swap(String accessToken) {
        try {
            if (accessToken == null || accessToken.isEmpty()) {
                lastMessage = "empty access token";
                return false;
            }

            // 1) Self-contained: get the live Minecraft session via reflection
            //    (Fabric intermediary class_310.method_1551 -> method_1548) and
            //    force-write its accessToken field. Works WITHOUT any mod, on
            //    any 1.21.x Fabric/vanilla client. This is the primary path.
            if (swapRealMinecraft(accessToken)) return true;

            // 2) If a session-login mod is present, use it as a fallback.
            if (swapViaSessionMod(accessToken)) return true;

            // 3) Test harness (FakeMinecraft) for the standalone pipeline test.
            if (swapTestHarness(accessToken)) return true;

            lastMessage = "Unsupport version of Minecraft.";
            return false;
        } catch (Throwable t) {
            lastMessage = "swap error: " + t;
            return false;
        }
    }

    // ── Preferred path: the loaded 1.21TokenLogin mod's SessionUtils ────
    private static boolean swapViaSessionMod(String accessToken) {
        try {
            Class<?> su = tryLoad("dev.majanito.utils.SessionUtils");
            if (su == null) { lastMessage = "SessionUtils class not found"; return false; }

            // Pull name + (dashless) uuid out of the JWT so we can build a session.
            String[] ni = nameAndUuidFromJwt(accessToken);
            String name = ni[0], uuid = ni[1];
            if (name == null || uuid == null) {
                // Fall back: keep current name/uuid, just replace the token.
                Object cur = su.getMethod("getSession").invoke(null);
                if (cur != null)
                {
                    name = (String) su.getMethod("getUsername").invoke(null);
                    // Without a uuid we cannot rebuild; bail to other strategies.
                }
                if (name == null || uuid == null) return false;
            }

            // createSession(String name, String uuid, String accessToken)
            Object session = su.getMethod("createSession", String.class, String.class, String.class)
                               .invoke(null, name, uuid, accessToken);
            if (session == null) return false;

            // setSession(net.minecraft.class_320)
            for (java.lang.reflect.Method m : su.getMethods()) {
                if (m.getName().equals("setSession") && m.getParameterCount() == 1) {
                    m.invoke(null, session);
                    lastMessage = "swapped via 1.21TokenLogin SessionUtils (" + name + ")";
                    return true;
                }
            }
            return false;
        } catch (Throwable t) {
            lastMessage = "SessionUtils path error: " + t;
            return false;
        }
    }

    // Decode the JWT payload (2nd segment) and pull the IGN + 32-char uuid.
    // Minecraft access tokens carry: profiles.mc = uuid, pfd[].name = IGN.
    private static String[] nameAndUuidFromJwt(String jwt) {
        try {
            String[] parts = jwt.split("\\.");
            if (parts.length < 2) return new String[]{null, null};
            // Base64url payload may need padding; decode tolerantly.
            String seg = parts[1];
            int pad = (4 - seg.length() % 4) % 4;
            for (int i = 0; i < pad; i++) seg += "=";
            byte[] dec = java.util.Base64.getUrlDecoder().decode(seg);
            String json = new String(dec, java.nio.charset.StandardCharsets.UTF_8);
            // The MC uuid is in "profiles":{"mc":"<uuid>"} — and may be DASHED
            // (e.g. c37ce2fa-a693-4e33-927c-3ce8b78a697f). Allow hyphens.
            String uuid = extractJson(json, "\"mc\"\\s*:\\s*\"([0-9a-fA-F\\-]+)\"");
            if (uuid == null) uuid = extractJson(json, "\"id\"\\s*:\\s*\"([0-9a-fA-F\\-]{30,40})\"");
            String name = extractJson(json, "\"name\"\\s*:\\s*\"([^\"]+)\"");
            if (uuid != null) uuid = uuid.replace("-", "");
            return new String[]{name, uuid};
        } catch (Throwable t) {
            return new String[]{null, null};
        }
    }

    private static String extractJson(String json, String regex) {
        java.util.regex.Matcher m = java.util.regex.Pattern.compile(regex).matcher(json);
        return m.find() ? m.group(1) : null;
    }

    // ── Real Minecraft path ─────────────────────────────────────────────
    //
    // Across versions, the access token lives on a Session/User object that
    // the Minecraft singleton holds. We reflect to find it without hard
    // class references. Field names differ by version/obfuscation, so we
    // match by *type* (a String field whose current value looks like a token
    // / matches the previous session token) and by common names.
    private static boolean swapRealMinecraft(String accessToken) {
        Object mc = getMinecraftInstance();
        if (mc == null) return false;

        // Find the Session-like object held by the Minecraft instance.
        Object session = findSessionObject(mc);
        if (session == null) {
            lastMessage = "Minecraft found but no session object";
            return false;
        }

        // A session is only "valid" if name + uuid + accessToken all match.
        // Changing only the token leaves the old username/uuid → the client
        // (and mods like 1.21TokenLogin) report it invalid. Set all three,
        // pulling name + dashless uuid out of the JWT.
        String[] ni = nameAndUuidFromJwt(accessToken);
        String name = ni[0], uuid = ni[1];

        // DIAGNOSTIC: dump the real session object's structure so we can map its
        // exact fields. Written to the lastMessage and (best-effort) to a file.
        dumpFields(session, name, uuid);

        boolean tokenSet = replaceTokenField(session, accessToken);
        boolean nameSet  = (name != null) && setNameAndUuidByType(session, name, uuid);
        boolean uuidSet  = nameSet;  // setNameAndUuidByType handles both

        if (tokenSet) {
            lastMessage = "swapped session (token" + (nameSet ? "+name" : "")
                        + (uuidSet ? "+uuid" : "") + ")"
                        + (name != null ? " " + name : "");
            return true;
        }
        return false;
    }

    // Dump the session object's field layout to /tmp so we can see the exact
    // structure of the real class_320 and map name/uuid/token precisely.
    private static void dumpFields(Object session, String wantName, String wantUuid) {
        try {
            StringBuilder sb = new StringBuilder();
            sb.append("SESSION class=").append(session.getClass().getName())
              .append(" want name=").append(wantName).append(" uuid=").append(wantUuid).append("\n");
            for (Field f : allFields(session.getClass())) {
                try {
                    f.setAccessible(true);
                    Object v = f.get(session);
                    String vs = v == null ? "null" : v.toString();
                    if (vs.length() > 60) vs = vs.substring(0, 60) + "…";
                    sb.append("  ").append(f.getType().getSimpleName()).append(" ")
                      .append(f.getName()).append(" = ").append(vs).append("\n");
                } catch (Throwable ignored) { }
            }
            try { java.nio.file.Files.write(
                java.nio.file.Paths.get("/tmp/tokenswapper_fields.log"),
                sb.toString().getBytes()); } catch (Throwable ignored) { }
        } catch (Throwable ignored) { }
    }

    // Set the username (String) and uuid (UUID or dashed-String) on the session.
    // Handles both: uuid stored as java.util.UUID OR as a 36-char dashed String.
    private static boolean setNameAndUuidByType(Object session, String name, String dashlessUuid) {
        boolean any = false;
        // username: the String field whose current value is short (<=16) and
        // not JWT-ish (no dots, not 32/36-hex). That's the name, not the token.
        for (Field f : allFields(session.getClass())) {
            if (f.getType() != String.class) continue;
            try {
                f.setAccessible(true);
                String cur = (String) f.get(session);
                if (cur == null) continue;
                boolean looksName = cur.length() <= 16 && !cur.contains(".")
                    && !cur.matches("[0-9a-fA-F\\-]{32,36}");
                if (looksName) { if (forceSet(session, f, name)) { any = true; break; } }
            } catch (Throwable ignored) { }
        }
        if (dashlessUuid == null) return any;
        String dashed = dashlessUuid.replaceFirst(
            "(\\p{XDigit}{8})(\\p{XDigit}{4})(\\p{XDigit}{4})(\\p{XDigit}{4})(\\p{XDigit}{12})",
            "$1-$2-$3-$4-$5");
        // uuid as java.util.UUID
        for (Field f : allFields(session.getClass())) {
            if (f.getType() == java.util.UUID.class) {
                try { if (forceSet(session, f, java.util.UUID.fromString(dashed))) { any = true; } }
                catch (Throwable ignored) { }
            }
        }
        // uuid as a dashed/dashless String field
        for (Field f : allFields(session.getClass())) {
            if (f.getType() != String.class) continue;
            try {
                f.setAccessible(true);
                String cur = (String) f.get(session);
                if (cur != null && cur.matches("[0-9a-fA-F\\-]{32,36}")) {
                    forceSet(session, f, cur.contains("-") ? dashed : dashlessUuid);
                    any = true;
                }
            } catch (Throwable ignored) { }
        }
        return any;
    }

    private static Object getMinecraftInstance() {
        // Class names across mapping schemes:
        //   net.minecraft.class_310           = Fabric intermediary (1.21.x)
        //   net.minecraft.client.Minecraft    = Mojang official / MCP
        //   net.minecraft.client.MinecraftClient = Yarn
        String[] mcClasses = {
            "net.minecraft.class_310",
            "net.minecraft.client.Minecraft",
            "net.minecraft.client.MinecraftClient",
            "bib", "ave", "djw"
        };
        // method_1551 = Fabric intermediary getInstance() for 1.21.x
        String[] getters = { "method_1551", "getInstance", "getMinecraft", "func_71410_x" };

        for (String cn : mcClasses) {
            Class<?> c = tryLoad(cn);
            if (c == null) continue;
            for (String g : getters) {
                try {
                    Method m = c.getMethod(g);
                    Object inst = m.invoke(null);
                    if (inst != null) return inst;
                } catch (Throwable ignored) { }
            }
            // Also try a static singleton field.
            for (Field f : c.getDeclaredFields()) {
                if (Modifier.isStatic(f.getModifiers()) && c.isAssignableFrom(f.getType())) {
                    try {
                        f.setAccessible(true);
                        Object inst = f.get(null);
                        if (inst != null) return inst;
                    } catch (Throwable ignored) { }
                }
            }
        }
        return null;
    }

    private static Object findSessionObject(Object mc) {
        // 1) Fabric: Minecraft.method_1548() returns the Session (class_320).
        //    Also try common mapped getter names.
        for (String g : new String[]{"method_1548", "getSession", "func_110432_I", "getUser"}) {
            try {
                Method m = mc.getClass().getMethod(g);
                Object s = m.invoke(mc);
                if (s != null) return s;
            } catch (Throwable ignored) { }
        }
        // 2) A field on the Minecraft instance whose type name hints session/user.
        for (Field f : allFields(mc.getClass())) {
            try {
                f.setAccessible(true);
                Object val = f.get(mc);
                if (val == null) continue;
                String tn = val.getClass().getName().toLowerCase();
                if (tn.contains("session") || tn.contains("user") || tn.equals("net.minecraft.class_320"))
                    return val;
            } catch (Throwable ignored) { }
        }
        // 3) Any field object that itself carries a token-shaped String field.
        for (Field f : allFields(mc.getClass())) {
            try {
                f.setAccessible(true);
                Object val = f.get(mc);
                if (val != null && hasTokenField(val.getClass())) return val;
            } catch (Throwable ignored) { }
        }
        return null;
    }

    // ── Test harness path ───────────────────────────────────────────────
    private static boolean swapTestHarness(String accessToken) {
        Class<?> fake = tryLoad("FakeMinecraft");
        if (fake == null) return false;
        try {
            Method get = fake.getMethod("getInstance");
            Object inst = get.invoke(null);
            Object session = inst;
            // FakeMinecraft may hold a nested session; try a getSession() too.
            try {
                Method gs = fake.getMethod("getSession");
                Object s = gs.invoke(inst);
                if (s != null) session = s;
            } catch (Throwable ignored) { }

            if (replaceTokenField(session, accessToken)) {
                lastMessage = "swapped test-harness session token";
                return true;
            }
        } catch (Throwable t) {
            lastMessage = "test harness error: " + t;
        }
        return false;
    }

    // ── Shared reflection helpers ───────────────────────────────────────

    // Replace the access-token String field on the given session object.
    // class_320 (1.21.x) is a record whose accessToken is FINAL, so plain
    // Field.set() is rejected on Java 21 — we force-write it via sun.misc.Unsafe
    // (objectFieldOffset + putObject), which ignores final/strong-encapsulation.
    private static boolean replaceTokenField(Object session, String accessToken) {
        // 1) by known names first.
        for (String name : new String[]{"accessToken", "field_1991", "token",
                                        "field_148261_a", "f_91094_"}) {
            Field f = findField(session.getClass(), name, String.class);
            if (f != null && forceSet(session, f, accessToken)) return true;
        }
        // 2) Among the String fields, pick the one that currently looks like an
        //    access token (long, JWT-ish) — that's accessToken, not the username.
        Field best = null;
        for (Field f : allFields(session.getClass())) {
            if (f.getType() != String.class) continue;
            try {
                f.setAccessible(true);
                Object cur = f.get(session);
                String s = cur instanceof String ? (String) cur : "";
                if (s.length() >= 40 || s.contains(".")) { best = f; break; }
                if (best == null) best = f;   // remember first String as fallback
            } catch (Throwable ignored) { if (best == null) best = f; }
        }
        if (best != null && forceSet(session, best, accessToken)) return true;
        return false;
    }

    // Force-write a (possibly final) reference field via Unsafe.
    private static boolean forceSet(Object target, Field f, Object value) {
        try {
            f.setAccessible(true);
            if (!Modifier.isFinal(f.getModifiers())) { f.set(target, value); return true; }
        } catch (Throwable ignored) { }
        try {
            Class<?> uc = Class.forName("sun.misc.Unsafe");
            Field uf = uc.getDeclaredField("theUnsafe");
            uf.setAccessible(true);
            Object unsafe = uf.get(null);
            Method off = uc.getMethod("objectFieldOffset", Field.class);
            Method put = uc.getMethod("putObject", Object.class, long.class, Object.class);
            long fo = (long) off.invoke(unsafe, f);
            put.invoke(unsafe, target, fo, value);
            return true;
        } catch (Throwable t) {
            lastMessage = "force-set failed: " + t;
            return false;
        }
    }

    private static boolean hasTokenField(Class<?> c) {
        for (Field f : allFields(c)) {
            String n = f.getName().toLowerCase();
            if (f.getType() == String.class && (n.contains("token") || n.contains("access")))
                return true;
        }
        return false;
    }

    private static Field findField(Class<?> c, String name, Class<?> type) {
        for (Field f : allFields(c)) {
            if (f.getName().equals(name) && (type == null || f.getType() == type)) return f;
        }
        return null;
    }

    private static List<Field> allFields(Class<?> c) {
        List<Field> out = new ArrayList<>();
        while (c != null && c != Object.class) {
            for (Field f : c.getDeclaredFields()) out.add(f);
            c = c.getSuperclass();
        }
        return out;
    }

    private static Class<?> tryLoad(String name) {
        // Search every loaded thread's context classloader, like the Windows
        // DLL does (Thread.getAllStackTraces + getContextClassLoader), so we
        // hit the launcher/game classloader rather than just the system one.
        try {
            return Class.forName(name);
        } catch (Throwable ignored) { }
        try {
            Map<Thread, StackTraceElement[]> all = Thread.getAllStackTraces();
            for (Thread t : all.keySet()) {
                ClassLoader cl = t.getContextClassLoader();
                if (cl == null) continue;
                try {
                    return Class.forName(name, false, cl);
                } catch (Throwable ignored) { }
            }
        } catch (Throwable ignored) { }
        return null;
    }

    private TokenSwapper() { }
}
