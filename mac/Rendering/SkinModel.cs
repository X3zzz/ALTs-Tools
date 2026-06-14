using System;
using System.Collections.Generic;
using System.Numerics;

namespace AltsTools.Rendering
{
    // A single textured quad face of a cuboid (4 corners + per-vertex UVs in
    // 0..1 texture space + a face normal for flat lighting).
    public struct Face
    {
        public Vector3 A, B, C, D;     // CCW corners
        public Vector2 UvA, UvB, UvC, UvD;
        public Vector3 Normal;
    }

    // UV rect in Minecraft's 64x64 skin pixel space (x,y top-left, w,h).
    public readonly record struct UvRect(float X, float Y, float W, float H);

    // The six face UVs of a cuboid, in order: Right(+X) Left(-X) Top(+Y)
    // Bottom(-Y) Front(+Z) Back(-Z) — matching the Windows PlayerMeshFactory.
    public readonly record struct CubeUv(UvRect Right, UvRect Left, UvRect Top,
                                         UvRect Bottom, UvRect Front, UvRect Back);

    /// <summary>
    /// Builds the Minecraft player rig as a list of textured faces, reusing the
    /// exact pixel sizes / UV layout from the Windows PlayerMeshFactory. Units:
    /// 1 block-pixel = 1/16. Texture is the standard 64x64 skin.
    /// </summary>
    public static class SkinModel
    {
        public const float Px = 1f / 16f;
        private const float Tex = 64f;

        public enum PartId { Head, Body, RightArm, LeftArm, RightLeg, LeftLeg }

        // A body part = its faces + the pivot it rotates about (shoulder/hip).
        public sealed class Part
        {
            public PartId Id;
            public List<Face> Faces = new();
            public Vector3 Pivot;
        }

        /// <summary>Build the rig as separable parts so limbs can be animated
        /// about their pivots. Each limb is centred 6px below its pivot.</summary>
        public static List<Part> BuildParts(bool slim)
        {
            float armW = slim ? 3f * Px : 4f * Px;
            float armX = 4 * Px + armW * 0.5f;

            var parts = new List<Part>();

            // Head + body: pivots at their own centre (head can nod, body fixed).
            parts.Add(MakePart(PartId.Head, new Vector3(0, 12 * Px, 0), new Vector3(0, 12 * Px, 0),
                (c => { AddCuboid(c, new Vector3(0, 12 * Px, 0), new(8 * Px, 8 * Px, 8 * Px), HeadBase());
                        AddCuboid(c, new Vector3(0, 12 * Px, 0), new(8 * Px, 8 * Px, 8 * Px), HeadLayer(), 0.5f * Px); })));
            parts.Add(MakePart(PartId.Body, new Vector3(0, 2 * Px, 0), new Vector3(0, 8 * Px, 0),
                (c => { AddCuboid(c, new Vector3(0, 2 * Px, 0), new(8 * Px, 12 * Px, 4 * Px), BodyBase());
                        AddCuboid(c, new Vector3(0, 2 * Px, 0), new(8 * Px, 12 * Px, 4 * Px), BodyLayer(), 0.45f * Px); })));

            // Arms: pivot at the shoulder (top of the limb, y = +8), limb centre y = +2.
            parts.Add(MakePart(PartId.RightArm, new Vector3(-armX, 2 * Px, 0), new Vector3(-armX, 8 * Px, 0),
                c => AddCuboid(c, new Vector3(-armX, 2 * Px, 0), new(armW, 12 * Px, 4 * Px), RightArmBase(slim))));
            parts.Add(MakePart(PartId.LeftArm, new Vector3(+armX, 2 * Px, 0), new Vector3(+armX, 8 * Px, 0),
                c => AddCuboid(c, new Vector3(+armX, 2 * Px, 0), new(armW, 12 * Px, 4 * Px), LeftArmBase(slim))));

            // Legs: pivot at the hip (top of leg, y = -4), limb centre y = -10.
            parts.Add(MakePart(PartId.RightLeg, new Vector3(-2 * Px, -10 * Px, 0), new Vector3(-2 * Px, -4 * Px, 0),
                c => AddCuboid(c, new Vector3(-2 * Px, -10 * Px, 0), new(4 * Px, 12 * Px, 4 * Px), RightLegBase())));
            parts.Add(MakePart(PartId.LeftLeg, new Vector3(+2 * Px, -10 * Px, 0), new Vector3(+2 * Px, -4 * Px, 0),
                c => AddCuboid(c, new Vector3(+2 * Px, -10 * Px, 0), new(4 * Px, 12 * Px, 4 * Px), LeftLegBase())));
            return parts;
        }

        private static Part MakePart(PartId id, Vector3 _, Vector3 pivot, Action<List<Face>> build)
        {
            var p = new Part { Id = id, Pivot = pivot };
            build(p.Faces);
            return p;
        }

        // Flatten all parts to faces (no animation) — kept for compatibility.
        public static List<Face> BuildRig(bool slim)
        {
            var faces = new List<Face>(12 * 6);
            foreach (var part in BuildParts(slim)) faces.AddRange(part.Faces);
            return faces;
        }

        // Rotate a face about a pivot on the X axis (arm/leg swing) by `angle`.
        public static Face RotateX(Face f, Vector3 pivot, float angle)
        {
            float cos = MathF.Cos(angle), sin = MathF.Sin(angle);
            Vector3 R(Vector3 v)
            {
                Vector3 d = v - pivot;
                return new Vector3(d.X, d.Y * cos - d.Z * sin, d.Y * sin + d.Z * cos) + pivot;
            }
            Vector3 Rn(Vector3 n) => new(n.X, n.Y * cos - n.Z * sin, n.Y * sin + n.Z * cos);
            return new Face
            {
                A = R(f.A), B = R(f.B), C = R(f.C), D = R(f.D),
                UvA = f.UvA, UvB = f.UvB, UvC = f.UvC, UvD = f.UvD,
                Normal = Rn(f.Normal),
            };
        }

        private static void AddCuboid(List<Face> outF, Vector3 center, Vector3 size, CubeUv uv, float inflate = 0f)
        {
            Vector3 h = size * 0.5f + new Vector3(inflate);
            // 8 corners
            Vector3 c000 = center + new Vector3(-h.X, -h.Y, -h.Z);
            Vector3 c100 = center + new Vector3(+h.X, -h.Y, -h.Z);
            Vector3 c010 = center + new Vector3(-h.X, +h.Y, -h.Z);
            Vector3 c110 = center + new Vector3(+h.X, +h.Y, -h.Z);
            Vector3 c001 = center + new Vector3(-h.X, -h.Y, +h.Z);
            Vector3 c101 = center + new Vector3(+h.X, -h.Y, +h.Z);
            Vector3 c011 = center + new Vector3(-h.X, +h.Y, +h.Z);
            Vector3 c111 = center + new Vector3(+h.X, +h.Y, +h.Z);

            // Right (+X)
            outF.Add(Quad(c101, c100, c110, c111, uv.Right, new Vector3(1, 0, 0)));
            // Left (-X)
            outF.Add(Quad(c000, c001, c011, c010, uv.Left, new Vector3(-1, 0, 0)));
            // Top (+Y)
            outF.Add(Quad(c011, c111, c110, c010, uv.Top, new Vector3(0, 1, 0)));
            // Bottom (-Y)
            outF.Add(Quad(c000, c100, c101, c001, uv.Bottom, new Vector3(0, -1, 0)));
            // Front (+Z)
            outF.Add(Quad(c001, c101, c111, c011, uv.Front, new Vector3(0, 0, 1)));
            // Back (-Z)
            outF.Add(Quad(c100, c000, c010, c110, uv.Back, new Vector3(0, 0, -1)));
        }

        private static Face Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, UvRect r, Vector3 n)
        {
            // UVs: A=top-left, B=top-right, C=bottom-right, D=bottom-left in tex space.
            return new Face
            {
                A = a, B = b, C = c, D = d, Normal = n,
                UvA = new Vector2(r.X / Tex, r.Y / Tex),
                UvB = new Vector2((r.X + r.W) / Tex, r.Y / Tex),
                UvC = new Vector2((r.X + r.W) / Tex, (r.Y + r.H) / Tex),
                UvD = new Vector2(r.X / Tex, (r.Y + r.H) / Tex),
            };
        }

        // ── UV layout (verbatim from Windows PlayerMeshFactory) ──
        private static CubeUv HeadBase() => new(
            new(0, 8, 8, 8), new(16, 8, 8, 8), new(8, 0, 8, 8), new(16, 0, 8, 8), new(8, 8, 8, 8), new(24, 8, 8, 8));
        private static CubeUv HeadLayer() => new(
            new(32, 8, 8, 8), new(48, 8, 8, 8), new(40, 0, 8, 8), new(48, 0, 8, 8), new(40, 8, 8, 8), new(56, 8, 8, 8));
        private static CubeUv BodyBase() => new(
            new(16, 20, 4, 12), new(28, 20, 4, 12), new(20, 16, 8, 4), new(28, 16, 8, 4), new(20, 20, 8, 12), new(32, 20, 8, 12));
        private static CubeUv BodyLayer() => new(
            new(16, 36, 4, 12), new(28, 36, 4, 12), new(20, 32, 8, 4), new(28, 32, 8, 4), new(20, 36, 8, 12), new(32, 36, 8, 12));
        private static CubeUv RightLegBase() => new(
            new(0, 20, 4, 12), new(8, 20, 4, 12), new(4, 16, 4, 4), new(8, 16, 4, 4), new(4, 20, 4, 12), new(12, 20, 4, 12));
        private static CubeUv LeftLegBase() => new(
            new(16, 52, 4, 12), new(24, 52, 4, 12), new(20, 48, 4, 4), new(24, 48, 4, 4), new(20, 52, 4, 12), new(28, 52, 4, 12));
        private static CubeUv RightArmBase(bool slim) => slim
            ? new(new(40, 20, 4, 12), new(47, 20, 4, 12), new(44, 16, 3, 4), new(47, 16, 3, 4), new(44, 20, 3, 12), new(51, 20, 3, 12))
            : new(new(40, 20, 4, 12), new(48, 20, 4, 12), new(44, 16, 4, 4), new(48, 16, 4, 4), new(44, 20, 4, 12), new(52, 20, 4, 12));
        private static CubeUv LeftArmBase(bool slim) => slim
            ? new(new(32, 52, 4, 12), new(39, 52, 4, 12), new(36, 48, 3, 4), new(39, 48, 3, 4), new(36, 52, 3, 12), new(43, 52, 3, 12))
            : new(new(32, 52, 4, 12), new(40, 52, 4, 12), new(36, 48, 4, 4), new(40, 48, 4, 4), new(36, 52, 4, 12), new(44, 52, 4, 12));
    }
}
