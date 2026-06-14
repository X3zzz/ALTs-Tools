using System;
using System.Collections.Generic;
using System.Numerics;
using SkiaSharp;

namespace AltsTools.Rendering
{
    /// <summary>
    /// Tiny software rasterizer: projects the player rig's textured quads with a
    /// perspective camera, z-buffers them, samples the skin texture nearest-
    /// neighbour (blocky Minecraft look) and applies flat directional lighting.
    /// Renders into a BGRA byte buffer the Avalonia control blits to a bitmap.
    /// </summary>
    public sealed class SkinRasterizer
    {
        private SKBitmap? _skin;     // 64x64 (or 64x32 expanded) skin
        private bool _legacy;
        private List<SkinModel.Part> _parts = SkinModel.BuildParts(false);
        private bool _slim;

        // Animation: 0=Auto 1=Idle 2=Walk 3=Fap, plus a time accumulator (seconds).
        public int AnimMode = 1;
        public float Time;

        public SkinRasterizer() => _skin = DefaultSkin();

        // A built-in Steve 64x64 skin so the preview is never empty. Fills each
        // texture region used by the rig's UV layout with the right colour.
        private static SKBitmap DefaultSkin()
        {
            var b = new SKBitmap(64, 64, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            using var c = new SKCanvas(b);
            c.Clear(SKColors.Transparent);
            var skin  = new SKColor(0xB1, 0x7A, 0x53);  // Steve skin tone
            var face  = new SKColor(0xC9, 0x96, 0x71);  // slightly lighter face
            var hair  = new SKColor(0x32, 0x22, 0x0F);  // dark brown hair
            var shirt = new SKColor(0x00, 0xAC, 0xB6);  // teal shirt
            var arm   = new SKColor(0xB1, 0x7A, 0x53);  // arm = skin
            var pants = new SKColor(0x3A, 0x3D, 0x9E);  // blue jeans
            void R(int x, int y, int w, int h, SKColor col) { using var p = new SKPaint { Color = col }; c.DrawRect(x, y, w, h, p); }

            // ── Head (region x0..32, y0..16) ──
            R(0, 0, 32, 16, hair);             // top/bottom/back/sides default to hair
            R(8, 8, 8, 8, face);               // FRONT face
            R(8, 8, 8, 2, hair);               // hair fringe across the top of the face
            R(16, 8, 8, 8, skin);              // LEFT side (ear area) — skin tone

            // ── Body (front 20,20,8,12; right 16,20,4,12; left 28,20,4,12; back 32,20,8,12) ──
            R(16, 16, 24, 16, shirt);          // whole torso block = shirt

            // ── Right arm (40,16 .. 56,32) & Left arm (32,48 .. 48,64) ──
            R(40, 16, 16, 16, arm);
            R(32, 48, 16, 16, arm);

            // ── Right leg (0,16 .. 16,32) & Left leg (16,48 .. 32,64) ──
            R(0, 16, 16, 16, pants);
            R(16, 48, 16, 16, pants);
            return b;
        }

        public float Yaw = 0.5f;     // radians
        public float Pitch = 0.1f;
        public float Distance = 3.0f;

        public void SetSkin(byte[]? png)
        {
            _skin?.Dispose();
            _skin = null;
            if (png == null || png.Length == 0) { _skin = DefaultSkin(); return; }
            var bmp = SKBitmap.Decode(png);
            if (bmp == null) { _skin = DefaultSkin(); return; }
            _legacy = bmp.Width == 64 && bmp.Height == 32;
            if (_legacy)
            {
                // expand 64x32 → 64x64 (top half used, rest transparent)
                var full = new SKBitmap(64, 64, SKColorType.Bgra8888, SKAlphaType.Unpremul);
                using (var c = new SKCanvas(full))
                {
                    c.Clear(SKColors.Transparent);
                    c.DrawBitmap(bmp, 0, 0);
                }
                bmp.Dispose();
                _skin = full;
            }
            else _skin = bmp;
        }

        public void SetVariant(bool slim)
        {
            if (_slim == slim && _parts.Count > 0) return;
            _slim = slim;
            _parts = SkinModel.BuildParts(slim);
        }

        // Per-part swing angle for the current animation mode + time.
        private float LimbAngle(SkinModel.PartId id)
        {
            float t = Time;
            switch (AnimMode)
            {
                case 2: // Walk — arms/legs swing opposite
                {
                    float s = MathF.Sin(t * 5f) * 0.7f;
                    return id switch
                    {
                        SkinModel.PartId.RightArm => s,
                        SkinModel.PartId.LeftArm => -s,
                        SkinModel.PartId.RightLeg => -s,
                        SkinModel.PartId.LeftLeg => s,
                        _ => 0f,
                    };
                }
                case 3: // "Fap" — both arms swing fast together, legs still
                {
                    float s = MathF.Sin(t * 16f) * 0.55f - 0.55f;
                    return (id == SkinModel.PartId.RightArm || id == SkinModel.PartId.LeftArm) ? s : 0f;
                }
                case 0: // Auto — gentle idle sway blended over time
                case 1: // Idle — subtle arm breathing
                default:
                {
                    float s = MathF.Sin(t * 1.6f) * 0.06f;
                    return id switch
                    {
                        SkinModel.PartId.RightArm => s,
                        SkinModel.PartId.LeftArm => -s,
                        _ => 0f,
                    };
                }
            }
        }

        // Render into a width*height BGRA buffer (stride = width*4).
        public void Render(byte[] buf, int width, int height, uint bg)
        {
            // clear to background
            for (int i = 0; i < width * height; i++)
            {
                int o = i * 4;
                buf[o + 0] = (byte)(bg & 0xFF);
                buf[o + 1] = (byte)((bg >> 8) & 0xFF);
                buf[o + 2] = (byte)((bg >> 16) & 0xFF);
                buf[o + 3] = (byte)((bg >> 24) & 0xFF);
            }
            if (_skin == null) return;

            var zbuf = new float[width * height];
            for (int i = 0; i < zbuf.Length; i++) zbuf[i] = float.MaxValue;

            // camera
            Vector3 eye = new(
                MathF.Cos(Pitch) * MathF.Sin(Yaw),
                MathF.Sin(Pitch),
                MathF.Cos(Pitch) * MathF.Cos(Yaw));
            eye *= Distance;
            Matrix4x4 view = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY);
            float aspect = (float)width / height;
            Matrix4x4 proj = Matrix4x4.CreatePerspectiveFieldOfView(0.9f, aspect, 0.05f, 50f);
            Matrix4x4 vp = view * proj;

            Vector3 lightDir = Vector3.Normalize(new Vector3(-0.4f, -1f, -0.55f));

            foreach (var part in _parts)
            {
                float angle = LimbAngle(part.Id);
                foreach (var raw in part.Faces)
                {
                    Face f = angle != 0f ? SkinModel.RotateX(raw, part.Pivot, angle) : raw;

                    // backface cull (normal vs view dir)
                    Vector3 faceCenter = (f.A + f.B + f.C + f.D) * 0.25f;
                    if (Vector3.Dot(f.Normal, eye - faceCenter) <= 0) continue;

                    float ndl = MathF.Max(0, Vector3.Dot(f.Normal, -lightDir));
                    float shade = 0.45f + 0.55f * ndl;

                    RasterTri(buf, zbuf, width, height, vp,
                        f.A, f.B, f.C, f.UvA, f.UvB, f.UvC, shade);
                    RasterTri(buf, zbuf, width, height, vp,
                        f.A, f.C, f.D, f.UvA, f.UvC, f.UvD, shade);
                }
            }
        }

        private void RasterTri(byte[] buf, float[] zbuf, int width, int height, Matrix4x4 vp,
            Vector3 p0, Vector3 p1, Vector3 p2, Vector2 t0, Vector2 t1, Vector2 t2, float shade)
        {
            // project to clip → ndc → screen, keep w for perspective-correct UV
            if (!Project(p0, vp, width, height, out var s0, out float w0)) return;
            if (!Project(p1, vp, width, height, out var s1, out float w1)) return;
            if (!Project(p2, vp, width, height, out var s2, out float w2)) return;

            int minX = (int)MathF.Max(0, MathF.Floor(Math.Min(s0.X, Math.Min(s1.X, s2.X))));
            int maxX = (int)MathF.Min(width - 1, MathF.Ceiling(Math.Max(s0.X, Math.Max(s1.X, s2.X))));
            int minY = (int)MathF.Max(0, MathF.Floor(Math.Min(s0.Y, Math.Min(s1.Y, s2.Y))));
            int maxY = (int)MathF.Min(height - 1, MathF.Ceiling(Math.Max(s0.Y, Math.Max(s1.Y, s2.Y))));

            float area = Edge(s0, s1, s2);
            if (MathF.Abs(area) < 1e-6f) return;
            float inv = 1f / area;

            // perspective-correct attrs
            float iw0 = 1f / w0, iw1 = 1f / w1, iw2 = 1f / w2;
            Vector2 u0 = t0 * iw0, u1 = t1 * iw1, u2 = t2 * iw2;

            for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                float b0 = Edge(s1, s2, p) * inv;
                float b1 = Edge(s2, s0, p) * inv;
                float b2 = Edge(s0, s1, p) * inv;
                if (b0 < 0 || b1 < 0 || b2 < 0) continue;

                float z = b0 * s0.Z + b1 * s1.Z + b2 * s2.Z;
                int idx = y * width + x;
                if (z >= zbuf[idx]) continue;

                float iw = b0 * iw0 + b1 * iw1 + b2 * iw2;
                Vector2 uv = (b0 * u0 + b1 * u1 + b2 * u2) / iw;

                int tx = Math.Clamp((int)(uv.X * 64f), 0, 63);
                int ty = Math.Clamp((int)(uv.Y * 64f), 0, 63);
                var col = _skin!.GetPixel(tx, ty);
                if (col.Alpha < 8) continue;  // transparent skin pixel

                zbuf[idx] = z;
                int o = idx * 4;
                buf[o + 0] = (byte)(col.Blue * shade);
                buf[o + 1] = (byte)(col.Green * shade);
                buf[o + 2] = (byte)(col.Red * shade);
                buf[o + 3] = 255;
            }
        }

        private static bool Project(Vector3 p, Matrix4x4 vp, int w, int h, out Vector3 screen, out float clipW)
        {
            Vector4 c = Vector4.Transform(new Vector4(p, 1f), vp);
            clipW = c.W;
            if (c.W <= 0.0001f) { screen = default; return false; }
            float ndcX = c.X / c.W, ndcY = c.Y / c.W, ndcZ = c.Z / c.W;
            screen = new Vector3((ndcX * 0.5f + 0.5f) * w, (1f - (ndcY * 0.5f + 0.5f)) * h, ndcZ);
            return true;
        }

        private static float Edge(Vector3 a, Vector3 b, Vector2 c)
            => (c.X - a.X) * (b.Y - a.Y) - (c.Y - a.Y) * (b.X - a.X);
        private static float Edge(Vector3 a, Vector3 b, Vector3 c)
            => (c.X - a.X) * (b.Y - a.Y) - (c.Y - a.Y) * (b.X - a.X);
    }
}
