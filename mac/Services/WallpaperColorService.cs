using System;
using System.Diagnostics;
using System.IO;
using Avalonia.Media;
using SkiaSharp;

namespace AltsTools.Services
{
    /// <summary>
    /// macOS port: extracts a dominant "seed" color from the desktop wallpaper
    /// for Material You-style dynamic theming. Reads the wallpaper path via
    /// AppleScript and decodes/samples it with SkiaSharp (replacing the Windows
    /// registry + System.Drawing path). Returns an Avalonia Color.
    /// </summary>
    public static class WallpaperColorService
    {
        public static Color? TryGetSeedColor()
        {
            string? path = GetWallpaperPath();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            try { return ExtractSeed(path); } catch { return null; }
        }

        private static string? GetWallpaperPath()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "/usr/bin/osascript",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("-e");
                psi.ArgumentList.Add("tell application \"System Events\" to get picture of current desktop");
                using var p = Process.Start(psi)!;
                string outp = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(3000);
                return string.IsNullOrEmpty(outp) ? null : outp;
            }
            catch { return null; }
        }

        private static Color ExtractSeed(string path)
        {
            using var input = File.OpenRead(path);
            using var bmp = SKBitmap.Decode(input);
            if (bmp == null) return Color.Parse("#6750A4");

            const int sample = 48;
            using var small = bmp.Resize(new SKImageInfo(sample, sample), SKFilterQuality.Medium)
                              ?? bmp.Copy();

            double bestScore = -1;
            Color best = Color.FromRgb(0x67, 0x50, 0xA4);
            long ar = 0, ag = 0, ab = 0, n = 0;

            for (int y = 0; y < small.Height; y++)
            for (int x = 0; x < small.Width; x++)
            {
                var px = small.GetPixel(x, y);
                if (px.Alpha < 16) continue;
                ar += px.Red; ag += px.Green; ab += px.Blue; n++;
                RgbToHsv(px.Red, px.Green, px.Blue, out _, out double s, out double v);
                double score = s * (1 - Math.Abs(v - 0.62));
                if (score > bestScore) { bestScore = score; best = Color.FromRgb(px.Red, px.Green, px.Blue); }
            }
            if (bestScore < 0.08 && n > 0)
                best = Color.FromRgb((byte)(ar / n), (byte)(ag / n), (byte)(ab / n));
            return Vivify(best);
        }

        private static Color Vivify(Color c)
        {
            RgbToHsv(c.R, c.G, c.B, out double h, out double s, out double v);
            s = Math.Clamp(s < 0.35 ? 0.45 : s, 0.35, 0.95);
            v = Math.Clamp(v, 0.45, 0.85);
            HsvToRgb(h, s, v, out byte r, out byte g, out byte b);
            return Color.FromRgb(r, g, b);
        }

        private static void RgbToHsv(byte r, byte g, byte b, out double h, out double s, out double v)
        {
            double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
            double max = Math.Max(rd, Math.Max(gd, bd)), min = Math.Min(rd, Math.Min(gd, bd));
            double d = max - min; v = max; s = max <= 0 ? 0 : d / max;
            if (d <= 0) { h = 0; return; }
            if (max == rd) h = 60 * (((gd - bd) / d) % 6);
            else if (max == gd) h = 60 * (((bd - rd) / d) + 2);
            else h = 60 * (((rd - gd) / d) + 4);
            if (h < 0) h += 360;
        }

        private static void HsvToRgb(double h, double s, double v, out byte r, out byte g, out byte b)
        {
            double c = v * s, x = c * (1 - Math.Abs((h / 60 % 2) - 1)), m = v - c, rd, gd, bd;
            if (h < 60) { rd = c; gd = x; bd = 0; }
            else if (h < 120) { rd = x; gd = c; bd = 0; }
            else if (h < 180) { rd = 0; gd = c; bd = x; }
            else if (h < 240) { rd = 0; gd = x; bd = c; }
            else if (h < 300) { rd = x; gd = 0; bd = c; }
            else { rd = c; gd = 0; bd = x; }
            r = (byte)Math.Round((rd + m) * 255);
            g = (byte)Math.Round((gd + m) * 255);
            b = (byte)Math.Round((bd + m) * 255);
        }
    }
}
