using System;
using System.Collections.Concurrent;
using System.IO;

namespace AltsTools.Services
{
    /// <summary>
    /// macOS replacement for the Windows registry-backed key/value store.
    /// Keeps the SAME static API (EnsureCreated / Read / Write) the rest of the
    /// app expects, so ProfileService and LocalizationManager port unchanged.
    ///
    /// Windows: HKEY_CURRENT_USER\Software\RefreshToAccess\{key}
    /// macOS:   ~/Library/Application Support/RefreshToAccess/{key}.dat
    ///
    /// Values are the already-encrypted base64 strings produced by MessagePacker
    /// — the crypto is identical to the Windows build, only the storage medium
    /// differs.
    /// </summary>
    public static class RegistryService
    {
        private static readonly string BaseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), // ~/Library/Application Support
            "RefreshToAccess");

        // Small in-process cache so repeated reads don't hit disk.
        private static readonly ConcurrentDictionary<string, string> _cache = new();

        public static void EnsureCreated()
        {
            try { Directory.CreateDirectory(BaseDir); }
            catch { /* best-effort, matches Windows EnsureCreated swallow */ }
        }

        public static string Read(string key)
        {
            if (_cache.TryGetValue(key, out var cached))
                return cached;
            try
            {
                string path = PathFor(key);
                if (File.Exists(path))
                {
                    string v = File.ReadAllText(path);
                    _cache[key] = v;
                    return v;
                }
            }
            catch { /* fall through to empty, like the Windows version */ }
            return string.Empty;
        }

        public static void Write(string key, string data)
        {
            try
            {
                EnsureCreated();
                File.WriteAllText(PathFor(key), data);
                _cache[key] = data;
            }
            catch { /* best-effort */ }
        }

        private static string PathFor(string key)
        {
            // Sanitise the key so it is a safe single filename.
            foreach (char c in Path.GetInvalidFileNameChars())
                key = key.Replace(c, '_');
            return Path.Combine(BaseDir, key + ".dat");
        }
    }
}
