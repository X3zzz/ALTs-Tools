using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using SkiaSharp;
using AltsTools.Models;

namespace AltsTools.Services
{
    /// <summary>
    /// macOS port of the head-skin cache. Same behaviour as the WPF version
    /// (fetch from Mojang, crop the 8×8 face + hat overlay, scale to 64×64,
    /// store as base64 PNG in the ProfileDataBlock) but image work uses
    /// SkiaSharp and the result is an Avalonia Bitmap instead of a WPF
    /// BitmapImage.
    /// </summary>
    public static class HeadSkinCacheService
    {
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
        private static readonly SemaphoreSlim _gate = new(3);

        static HeadSkinCacheService()
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("TokenTools/2.2");
        }

        public static async Task<Bitmap?> GetHeadAsync(
            ProfileDataBlock block, bool force = false, CancellationToken ct = default)
        {
            if (!force && !string.IsNullOrEmpty(block.headSkinBase64))
            {
                var cached = await Task.Run(() => DecodeBase64(block.headSkinBase64!), ct);
                if (cached != null) return cached;
            }

            string? uuid = block.profileData?.UUID;
            if (string.IsNullOrWhiteSpace(uuid)) return null;
            string clean = uuid.Replace("-", "");

            await _gate.WaitAsync(ct);
            try
            {
                if (!force && !string.IsNullOrEmpty(block.headSkinBase64))
                {
                    var cached = await Task.Run(() => DecodeBase64(block.headSkinBase64!), ct);
                    if (cached != null) return cached;
                }

                string skinUrl = await ResolveSkinUrlAsync(clean, ct);
                if (string.IsNullOrEmpty(skinUrl)) return null;

                byte[] skinBytes = await _http.GetByteArrayAsync(skinUrl, ct);
                byte[]? headPng = await Task.Run(() => CropHead(skinBytes), ct);
                if (headPng == null) return null;

                block.headSkinBase64 = Convert.ToBase64String(headPng);
                return await Task.Run(() => DecodeBase64(block.headSkinBase64), ct);
            }
            catch { return null; }
            finally { _gate.Release(); }
        }

        public static void Invalidate(ProfileDataBlock block) => block.headSkinBase64 = null;

        private static Bitmap? DecodeBase64(string b64)
        {
            try
            {
                byte[] data = Convert.FromBase64String(b64);
                using var ms = new MemoryStream(data);
                return new Bitmap(ms);
            }
            catch { return null; }
        }

        private static async Task<string> ResolveSkinUrlAsync(string uuid, CancellationToken ct)
        {
            var resp = await _http.GetAsync(
                $"https://sessionserver.mojang.com/session/minecraft/profile/{uuid}", ct);
            if (!resp.IsSuccessStatusCode) return "";

            string json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("properties", out var props)) return "";

            foreach (var p in props.EnumerateArray())
            {
                if (p.TryGetProperty("name", out var n) && n.GetString() == "textures"
                    && p.TryGetProperty("value", out var v))
                {
                    string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(v.GetString()!));
                    using var td = JsonDocument.Parse(decoded);
                    if (td.RootElement.TryGetProperty("textures", out var tex)
                        && tex.TryGetProperty("SKIN", out var skin)
                        && skin.TryGetProperty("url", out var url)
                        && url.ValueKind == JsonValueKind.String)
                        return url.GetString() ?? "";
                }
            }
            return "";
        }

        // 8×8 face + hat overlay → 64×64 PNG bytes, using SkiaSharp.
        private static byte[]? CropHead(byte[] skinPng)
        {
            try
            {
                using var src = SKBitmap.Decode(skinPng);
                if (src == null) return null;
                int w = src.Width, h = src.Height;

                const int S = 8, O = 64;
                using var outBmp = new SKBitmap(O, O, SKColorType.Bgra8888, SKAlphaType.Premul);

                // sample the 8×8 face, blend hat, then nearest-neighbour to 64×64
                var face = new SKColor[S * S];
                for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                    face[y * S + x] = src.GetPixel(x + 8, y + 8);

                if (w >= 48 && h >= 16)
                {
                    for (int y = 0; y < S; y++)
                    for (int x = 0; x < S; x++)
                    {
                        var hat = src.GetPixel(x + 40, y + 8);
                        byte a = hat.Alpha;
                        if (a == 0) continue;
                        if (a == 255) { face[y * S + x] = hat; continue; }
                        var b = face[y * S + x];
                        float f = a / 255f, inv = 1f - f;
                        face[y * S + x] = new SKColor(
                            (byte)(hat.Red * f + b.Red * inv),
                            (byte)(hat.Green * f + b.Green * inv),
                            (byte)(hat.Blue * f + b.Blue * inv),
                            255);
                    }
                }

                for (int y = 0; y < O; y++)
                {
                    int sy = y * S / O;
                    for (int x = 0; x < O; x++)
                    {
                        int sx = x * S / O;
                        outBmp.SetPixel(x, y, face[sy * S + sx]);
                    }
                }

                using var img = SKImage.FromBitmap(outBmp);
                using var enc = img.Encode(SKEncodedImageFormat.Png, 100);
                return enc.ToArray();
            }
            catch { return null; }
        }
    }
}
