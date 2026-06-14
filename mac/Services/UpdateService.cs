using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace AltsTools.Services
{
    /// <summary>
    /// Update checker. The original Windows project referenced this type but did
    /// not ship its definition; this is a faithful, minimal implementation:
    /// the auto-check preference is persisted (RegistryService) and a manual
    /// check opens the GitHub releases page. Kept intentionally simple so the
    /// Settings page behaves like the original.
    /// </summary>
    public static class UpdateService
    {
        private const string Key = "AutoCheckUpdates";
        private const string ReleasesUrl = "https://github.com/NoobCock/RefreshToAccess2/releases";

        public static bool AutoCheckEnabled
        {
            get => RegistryService.Read(Key) != "0";   // default on
            set => RegistryService.Write(Key, value ? "1" : "0");
        }

        public static Task ShowUpdateFlowAsync(bool manual)
        {
            if (!manual && !AutoCheckEnabled) return Task.CompletedTask;
            try
            {
                Process.Start(new ProcessStartInfo { FileName = ReleasesUrl, UseShellExecute = true });
            }
            catch { /* best-effort */ }
            return Task.CompletedTask;
        }
    }
}
