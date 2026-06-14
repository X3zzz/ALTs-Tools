using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace AltsTools
{
    /// <summary>
    /// macOS counterpart of the Windows Helper. Replaces WMI / javaw enumeration
    /// and the embedded-DLL extraction with macOS equivalents.
    /// </summary>
    public class Helper
    {
        // Path to the extracted payload dylib (set by ExtractInjectionDll).
        public static string tmpFileName = "";

        /// <summary>
        /// A running java process the user can inject into.
        /// </summary>
        public sealed class JavaProc
        {
            public int Pid { get; init; }
            public string CommandLine { get; init; } = "";

            // True if this looks like the actual Minecraft GAME JVM (not a
            // launcher like HMCL/PCL). The game runs the client main class.
            public bool IsGame =>
                CommandLine.Contains("net.fabricmc.loader") ||
                CommandLine.Contains("KnotClient") ||
                CommandLine.Contains("net.minecraft.client.main.Main") ||
                CommandLine.Contains("--gameDir") ||
                CommandLine.Contains("minecraft/versions");

            // True for known launchers — these should NOT be injected.
            public bool IsLauncher =>
                CommandLine.Contains("HMCL") || CommandLine.Contains("PCL") ||
                CommandLine.Contains("bakaxl", StringComparison.OrdinalIgnoreCase) ||
                CommandLine.Contains("launcher.jar", StringComparison.OrdinalIgnoreCase);

            public string Display => (IsGame ? "🎮 游戏  " : IsLauncher ? "🚫 启动器  " : "")
                                     + $"[{Pid}]  {ShortName}";
            public string ShortName
            {
                get
                {
                    var parts = CommandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 0; i < parts.Length - 1; i++)
                        if (parts[i] == "-jar")
                            return Path.GetFileName(parts[i + 1]);
                    // game has no -jar; show the version name if present
                    int vi = CommandLine.IndexOf("minecraft/versions/", StringComparison.Ordinal);
                    if (vi >= 0)
                    {
                        string rest = CommandLine.Substring(vi + 19);
                        int slash = rest.IndexOf('/');
                        if (slash > 0) return "Minecraft " + rest.Substring(0, slash);
                    }
                    return parts.Length > 0 ? Path.GetFileName(parts[0]) : "java";
                }
            }
        }

        /// <summary>
        /// Extract the embedded payload.dylib to a temp file, mirroring the
        /// Windows ExtractInjectionDll. The dylib is shipped as an embedded
        /// resource named "AltsTools.payload.dylib".
        /// </summary>
        public static void ExtractInjectionDll()
        {
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "altstools");
                Directory.CreateDirectory(dir);
                tmpFileName = Path.Combine(dir, "payload.dylib");

                using var resource = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("AltsTools.payload.dylib");
                if (resource == null)
                {
                    // Fallback: use the side-by-side dylib from the build output.
                    string local = Path.Combine(AppContext.BaseDirectory, "payload.dylib");
                    if (File.Exists(local)) tmpFileName = local;
                    return;
                }
                using var file = new FileStream(tmpFileName, FileMode.Create, FileAccess.Write);
                resource.CopyTo(file);
            }
            catch { /* best-effort, like the Windows version */ }
        }

        /// <summary>
        /// Enumerate running Java processes via `pgrep`/`ps` (replaces the
        /// Windows Process.GetProcessesByName("javaw") + WMI command-line query).
        /// </summary>
        public static List<JavaProc> GetJavaProcesses()
        {
            var result = new List<JavaProc>();
            try
            {
                // `ps -axww -o pid=,command=` gives pid + full command line.
                var psi = new ProcessStartInfo
                {
                    FileName = "/bin/ps",
                    Arguments = "-axww -o pid=,command=",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi)!;
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();

                foreach (var line in output.Split('\n'))
                {
                    string trimmed = line.TrimStart();
                    if (trimmed.Length == 0) continue;
                    int sp = trimmed.IndexOf(' ');
                    if (sp <= 0) continue;
                    if (!int.TryParse(trimmed[..sp], out int pid)) continue;
                    string cmd = trimmed[(sp + 1)..];

                    // Only Java processes that look like a game/launcher.
                    bool isJava = cmd.Contains("/java ") || cmd.Contains("/java\t")
                                  || cmd.EndsWith("/java") || cmd.Contains("java -")
                                  || cmd.Contains(".jar");
                    bool looksMinecraft = cmd.Contains("minecraft", StringComparison.OrdinalIgnoreCase)
                                          || cmd.Contains(".jar");
                    if (isJava && looksMinecraft)
                        result.Add(new JavaProc { Pid = pid, CommandLine = cmd });
                }
            }
            catch { /* swallow, like the Windows enum */ }
            return result;
        }
    }
}
