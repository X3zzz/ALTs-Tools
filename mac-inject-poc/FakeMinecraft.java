// FakeMinecraft.java — a stand-in for the Minecraft client JVM.
//
// It holds a Session object with an accessToken, exactly the shape that
// TokenSwapper.swap() hunts for, and prints the token once a second so we
// can watch it change the instant the injected swap lands. This lets us
// validate the whole inject → defineClass → swap pipeline against a real
// JVM without touching the actual game.

public final class FakeMinecraft {

    public static final class Session {
        // non-final String token field — the swap target
        private String accessToken = "ORIGINAL_TOKEN";
        private String username = "Steve";
        public String getAccessToken() { return accessToken; }
    }

    private static final FakeMinecraft INSTANCE = new FakeMinecraft();
    private final Session session = new Session();

    public static FakeMinecraft getInstance() { return INSTANCE; }
    public Session getSession() { return session; }

    public static void main(String[] args) throws Exception {
        System.out.println("FakeMinecraft running, pid=" + ProcessHandle.current().pid());
        System.out.flush();
        String last = null;
        while (true) {
            String t = INSTANCE.session.getAccessToken();
            if (!t.equals(last)) {
                System.out.println("[FakeMinecraft] session.accessToken = " + t);
                System.out.flush();
                last = t;
            }
            Thread.sleep(1000);
        }
    }
}
