using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace AltsTools.Services
{
    /// <summary>
    /// macOS token-injection service. Same shape and HTTP protocol as the
    /// Windows TokenInjectionService, but DLL injection is replaced by invoking
    /// the validated native `injector` binary (see mac-inject-poc): it hijacks a
    /// thread in the target JVM and dlopen()s payload.dylib, which then reports
    /// back to our listener on :38964 and serves /handshake/init + /token/swap.
    ///
    /// The HTTP listener + swap/handshake logic is identical to Windows (pure
    /// HttpListener/HttpClient, fully cross-platform).
    /// </summary>
    public static class TokenInjectionService
    {
        /// <summary>pid → port the injected payload is listening on.</summary>
        public static readonly ConcurrentDictionary<int, int> PidPortMap = new();

        private static readonly HttpClient _http = new();

        // UI notification hook (set by the app) so this service stays UI-agnostic.
        public static Action<string, bool>? Notify;   // (message, isError)
        private static void Toast(string msg, bool err = false) => Notify?.Invoke(msg, err);

        // Path to the compiled native injector and the payload dylib.
        public static string InjectorPath { get; set; } =
            Path.Combine(AppContext.BaseDirectory, "injector");

        // Last stdout/stderr from the injector — surfaced for diagnostics.
        public static string LastInjectorOutput { get; private set; } = "";

        // Diagnostic log so we can see what the :38964 listener actually received.
        private static void DiagLog(string msg)
        {
            try { File.AppendAllText("/tmp/altstools_host.log",
                $"{DateTime.Now:HH:mm:ss.fff} {msg}\n"); } catch { }
        }

        // ── Resident-payload persistence (inject once, swap many) ──────────
        // Persist pid→port so a payload injected earlier (even before a GUI
        // restart) can be reused without re-injecting / restarting Minecraft.
        private static readonly string MapFile =
            Path.Combine(Path.GetTempPath(), "altstools_pidport.txt");

        private static void PersistMap()
        {
            try
            {
                var lines = new System.Text.StringBuilder();
                foreach (var kv in PidPortMap) lines.AppendLine($"{kv.Key}={kv.Value}");
                File.WriteAllText(MapFile, lines.ToString());
            }
            catch { }
        }

        /// <summary>
        /// If a payload from an earlier injection is still resident in this pid,
        /// recover its port (from the persisted map) and verify it answers, so we
        /// can reuse it instead of injecting again.
        /// </summary>
        public static async Task<bool> TryRecoverResidentAsync(int pid)
        {
            int port = 0;
            try
            {
                if (File.Exists(MapFile))
                    foreach (var line in File.ReadAllLines(MapFile))
                    {
                        var p = line.Split('=');
                        if (p.Length == 2 && int.TryParse(p[0], out int k)
                            && int.TryParse(p[1], out int v) && k == pid) port = v;
                    }
            }
            catch { }
            if (port <= 0) return false;

            // Ping the payload to confirm it's still alive in that pid.
            try
            {
                var content = new StringContent("{}", Encoding.UTF8, "application/json");
                using var cts = new System.Threading.CancellationTokenSource(1500);
                var resp = await _http.PostAsync($"http://127.0.0.1:{port}/handshake/init", content, cts.Token);
                if (resp.IsSuccessStatusCode)
                {
                    PidPortMap[pid] = port;
                    DiagLog($"recovered resident payload pid={pid} port={port}");
                    return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Inject payload.dylib into <paramref name="pid"/> by invoking the
        /// native injector with sudo (task_for_pid requires root). Returns true
        /// if the injector exited 0.
        /// </summary>
        public static bool InjectDll(int pid, string dylibPath)
        {
            if (!File.Exists(dylibPath) || !File.Exists(InjectorPath))
            {
                Toast($"injector or payload missing\ninjector={InjectorPath}\npayload={dylibPath}", true);
                return false;
            }
            try
            {
                // `osascript` prompts for admin via the GUI so we don't need a TTY.
                // Run the injector and capture its output (2>&1) so we can see
                // exactly what it reported (task_for_pid errors, dlopen, etc.).
                string script =
                    $"do shell script \"{Shell(InjectorPath)} {pid} {Shell(dylibPath)} 2>&1\" with administrator privileges";
                var psi = new ProcessStartInfo
                {
                    FileName = "/usr/bin/osascript",
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("-e");
                psi.ArgumentList.Add(script);

                using var p = Process.Start(psi)!;
                string outp = p.StandardOutput.ReadToEnd();
                string err  = p.StandardError.ReadToEnd();
                p.WaitForExit(20000);

                LastInjectorOutput = string.IsNullOrWhiteSpace(outp) ? err : outp;
                if (p.ExitCode != 0)
                {
                    // osascript reports a non-zero / cancelled / errored run here.
                    Toast($"注入器出错：{(string.IsNullOrWhiteSpace(err) ? outp : err)}".Trim(), true);
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Toast($"inject failed: {ex.Message}", true);
                return false;
            }
        }

        private static string Shell(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        /// <summary>Send a token-swap request to the injected payload on its port.</summary>
        public static async Task SendSwapTokenAsync(int port, string accessToken)
        {
            try
            {
                var content = new StringContent(
                    JsonSerializer.Serialize(new { access_token = accessToken }),
                    Encoding.UTF8, "application/json");
                var response = await _http.PostAsync($"http://localhost:{port}/token/swap", content);
                string body = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                bool success = root.TryGetProperty("success", out var sp) && sp.GetBoolean();
                string message = root.TryGetProperty("message", out var mp) ? mp.GetString() ?? "" : "";
                Toast(success ? $"Token injected: {message}" : $"Injection returned failure: {message}", !success);
            }
            catch (Exception ex)
            {
                Toast($"send swap failed: {ex.Message}", true);
            }
        }

        /// <summary>Handle one inbound /client/online callback from a payload.</summary>
        public static async Task HandleRequestAsync(HttpListenerContext context)
        {
            if (context.Request.HttpMethod != "POST"
                || context.Request.Url?.AbsolutePath != "/client/online")
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }
            try
            {
                using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                string json = await reader.ReadToEndAsync();
                DiagLog($"/client/online body: {json}");
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("pid", out var pidEl) || !pidEl.TryGetInt32(out int pid))
                {
                    await Write(context, 400, "Missing or invalid 'pid'");
                    return;
                }
                if (root.TryGetProperty("error", out var errEl)
                    && errEl.ValueKind == JsonValueKind.String
                    && !string.IsNullOrEmpty(errEl.GetString()))
                {
                    Toast(errEl.GetString()!, true);
                }
                if (root.TryGetProperty("port", out var portEl)
                    && portEl.TryGetInt32(out int port) && port > 0)
                {
                    PidPortMap[pid] = port;
                    PersistMap();
                    DiagLog($"registered pid={pid} port={port}");
                    // DO NOT handshake here: the payload's server thread is
                    // blocked reading OUR response to this very request, and
                    // won't reach accept() until we reply. Handshaking now would
                    // deadlock. Reply 200 first, then handshake on a new task.
                    _ = Task.Run(async () => { await Task.Delay(150); await TryInitHandshakeAsync(port); });
                }
                await Write(context, 200, "OK");
            }
            catch (Exception ex)
            {
                await Write(context, 500, $"Error: {ex.Message}");
            }
        }

        private static async Task TryInitHandshakeAsync(int port)
        {
            try
            {
                var content = new StringContent("{}", Encoding.UTF8, "application/json");
                var response = await _http.PostAsync($"http://localhost:{port}/handshake/init", content);
                string body = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(body);
                bool success = doc.RootElement.TryGetProperty("success", out var sp) && sp.GetBoolean();
                Toast(success ? "Found injected Minecraft process" : $"Handshake failed on :{port}", !success);
            }
            catch (Exception ex)
            {
                Toast($"Handshake error on :{port}: {ex.Message}", true);
            }
        }

        private static async Task Write(HttpListenerContext ctx, int status, string body)
        {
            ctx.Response.StatusCode = status;
            byte[] buf = Encoding.UTF8.GetBytes(body);
            await ctx.Response.OutputStream.WriteAsync(buf);
            ctx.Response.Close();
        }
    }
}
