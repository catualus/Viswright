using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SourceKit;
using SourceKit.Geometry;

namespace Viswright
{
    /// <summary>
    /// The yardstick visibility is counted in: each drawn face's triangles as its shape has them, from its
    /// corners.
    ///
    /// A compiled face lists every vertex on its edge, and fixing T-junctions puts a vertex on a face's edge
    /// wherever a neighbouring face has a corner, so the same wall can count 2 triangles in one compile and
    /// 12 in another. Counted that way, maps compiled with and without <c>-notjunc</c> differ by a third,
    /// and a hint that splits faces raises what its neighbours count. A vertex where the edge runs straight
    /// on is not a corner; a face with n corners is n - 2 triangles whatever its edge carries.
    /// Displacements keep their grid's count, and faces that are never drawn count nothing.
    /// </summary>
    public static class FaceCorners
    {
        private const int LumpVertexes = 3, LumpFaces = 7, LumpEdges = 12, LumpSurfedges = 13;

        /// <summary>How far from straight a vertex's two edges must turn for it to count as a corner (the sine of the angle).</summary>
        public const double Turn = 1e-3;

        /// <summary>
        /// The triangles each face of the BSP at <paramref name="path"/> costs, by face index, as
        /// <paramref name="vis"/> (the same BSP) has them but counted from corners.
        /// </summary>
        public static int[] Load(string path, BspVisibilityData vis)
        {
            using var stream = File.OpenRead(path);
            using var r = new BinaryReader(stream, Encoding.ASCII);
            r.ReadInt32();
            r.ReadInt32();
            var offsets = new int[64];
            var lengths = new int[64];
            for (int i = 0; i < 64; i++)
            {
                offsets[i] = r.ReadInt32();
                lengths[i] = r.ReadInt32();
                r.ReadInt32();
                r.ReadInt32();
            }
            byte[] Lump(int index) => lengths[index] <= 0 ? [] : LzmaLump.Read(r, offsets[index], lengths[index]);

            byte[] faces = Lump(LumpFaces), vertexes = Lump(LumpVertexes), edges = Lump(LumpEdges), surfedges = Lump(LumpSurfedges);
            var triangles = new int[vis.Faces.Count];
            for (int f = 0; f < triangles.Length; f++)
            {
                var face = vis.Faces[f];
                triangles[f] = face.Triangles;
                if (face.Triangles == 0 || face.IsDisplacement || (f + 1) * 56 > faces.Length)
                    continue;
                int first = BitConverter.ToInt32(faces, f * 56 + 4);
                int count = BitConverter.ToInt16(faces, f * 56 + 8);
                var points = new Vec3[count];
                bool whole = true;
                for (int i = 0; i < count && whole; i++)
                {
                    int at = (first + i) * 4;
                    if (at + 4 > surfedges.Length) { whole = false; break; }
                    int edge = BitConverter.ToInt32(surfedges, at);
                    int e = Math.Abs(edge) * 4 + (edge >= 0 ? 0 : 2);
                    if (e + 2 > edges.Length) { whole = false; break; }
                    int v = BitConverter.ToUInt16(edges, e) * 12;
                    if (v + 12 > vertexes.Length) { whole = false; break; }
                    points[i] = new Vec3(BitConverter.ToSingle(vertexes, v), BitConverter.ToSingle(vertexes, v + 4), BitConverter.ToSingle(vertexes, v + 8));
                }
                if (whole)
                    triangles[f] = Math.Max(0, Corners(points) - 2);
            }
            return triangles;
        }

        /// <summary>How many of a polygon's vertices, in order round its edge, are corners rather than points on a straight edge.</summary>
        public static int Corners(ReadOnlySpan<Vec3> points)
        {
            // A vertex listed twice in a row is one vertex.
            var polygon = new List<Vec3>(points.Length);
            foreach (var p in points)
                if (polygon.Count == 0 || (p - polygon[^1]).Length > 1e-3)
                    polygon.Add(p);
            while (polygon.Count > 1 && (polygon[0] - polygon[^1]).Length <= 1e-3)
                polygon.RemoveAt(polygon.Count - 1);
            int corners = 0;
            for (int i = 0; i < polygon.Count; i++)
            {
                var a = polygon[i] - polygon[(i + polygon.Count - 1) % polygon.Count];
                var c = polygon[(i + 1) % polygon.Count] - polygon[i];
                double lengths = a.Length * c.Length;
                if (lengths > 1e-9 && a.Cross(c).Length / lengths > Turn)
                    corners++;
            }
            return corners;
        }
    }
}
