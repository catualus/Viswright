using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.Versioning;
using ComputeSharp;
using SourceKit.Geometry;

namespace Viswright
{
    /// <summary>
    /// Casts rays on the GPU through Direct3D 12: the same tree and the same triangle test as
    /// <see cref="TriangleBvh.Trace"/>, run as a compute shader, about ten times faster than every core
    /// of the CPU. The tree is walked without a stack - each node says where to go when its box is missed
    /// - since a shader has no per-thread array to keep one in. Results can differ from the CPU's on a
    /// ray that grazes a triangle's edge, so they are used for measuring, never for deciding a change.
    /// </summary>
    [SupportedOSPlatform("windows6.2")]
    public sealed class GpuRays : IDisposable
    {
        private readonly GraphicsDevice device;
        private readonly ReadOnlyBuffer<float> bounds, triangles, directions;
        private readonly ReadOnlyBuffer<int> miss, first, count;
        private readonly int[] original;
        private readonly int rays;

        public string DeviceName => device.Name;

        private GpuRays(GraphicsDevice device, TriangleBvh bvh, Vec3[] directionList)
        {
            this.device = device;
            var (b, m, f, c, t, o) = bvh.Threaded();
            original = o;
            rays = directionList.Length;
            bounds = Upload(device, b);
            miss = Upload(device, m);
            first = Upload(device, f);
            count = Upload(device, c);
            triangles = Upload(device, t);
            directions = Upload(device, directionList.SelectMany(d => new[] { (float)d.X, (float)d.Y, (float)d.Z }).ToArray());
        }

        /// <summary>The GPU with the most memory of its own, or null when there is none or Direct3D 12 will not start.</summary>
        public static GpuRays? TryOpen(TriangleBvh bvh, Vec3[] directions)
        {
            try
            {
                var device = GraphicsDevice.EnumerateDevices().Where(d => d.IsHardwareAccelerated)
                    .OrderByDescending(d => d.DedicatedMemorySize).FirstOrDefault();
                return device == null ? null : new GpuRays(device, bvh, directions);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return null;
            }
        }

        [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The buffers hold float and int, which have no fields to keep.")]
        private static ReadOnlyBuffer<T> Upload<T>(GraphicsDevice device, T[] data) where T : unmanaged =>
            device.AllocateReadOnlyBuffer(data.Length == 0 ? new T[1] : data);

        /// <summary>
        /// For each of <paramref name="origins"/>, the triangle (by its index in the corners the tree was
        /// built from) each direction's ray meets first, or -1; <paramref name="hits"/> is filled origin by
        /// origin, one entry per direction.
        /// </summary>
        [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The buffers hold float and int, which have no fields to keep.")]
        public void Trace(ReadOnlySpan<Vec3> origins, Span<int> hits)
        {
            // A dispatch is a grid of directions by origins; keep each to a 64 MB result.
            int batch = Math.Max(1, Math.Min(65535, (1 << 24) / rays));
            using var points = device.AllocateReadOnlyBuffer<float>(Math.Max(1, origins.Length) * 3);
            var flat = new float[origins.Length * 3];
            for (int i = 0; i < origins.Length; i++)
                (flat[i * 3], flat[i * 3 + 1], flat[i * 3 + 2]) = ((float)origins[i].X, (float)origins[i].Y, (float)origins[i].Z);
            if (flat.Length > 0)
                points.CopyFrom(flat);
            using var result = device.AllocateReadWriteBuffer<int>(batch * rays);
            var chunk = new int[batch * rays];
            for (int p0 = 0; p0 < origins.Length; p0 += batch)
            {
                SourceKitConcurrency.ThrowIfCancelled();
                int n = Math.Min(batch, origins.Length - p0);
                device.For(rays, n, new TraceShader(bounds, miss, first, count, triangles, points, directions, result, rays, p0));
                result.CopyTo(chunk, 0, 0, n * rays);
                for (int i = 0; i < n * rays; i++)
                    hits[p0 * rays + i] = chunk[i] < 0 ? -1 : original[chunk[i]];
            }
        }

        public void Dispose()
        {
            bounds.Dispose(); triangles.Dispose(); directions.Dispose();
            miss.Dispose(); first.Dispose(); count.Dispose();
        }
    }

    /// <summary>One thread per ray: X is the direction, Y the origin within the batch.</summary>
    [SupportedOSPlatform("windows6.2")]
    [ThreadGroupSize(64, 1, 1)]
    [GeneratedComputeShaderDescriptor]
    internal readonly partial struct TraceShader : IComputeShader
    {
        private readonly ReadOnlyBuffer<float> bounds;
        private readonly ReadOnlyBuffer<int> miss;
        private readonly ReadOnlyBuffer<int> first;
        private readonly ReadOnlyBuffer<int> count;
        private readonly ReadOnlyBuffer<float> tris;
        private readonly ReadOnlyBuffer<float> origins;
        private readonly ReadOnlyBuffer<float> dirs;
        private readonly ReadWriteBuffer<int> hits;
        private readonly int rays;
        private readonly int pointOffset;

        public TraceShader(ReadOnlyBuffer<float> bounds, ReadOnlyBuffer<int> miss, ReadOnlyBuffer<int> first, ReadOnlyBuffer<int> count,
            ReadOnlyBuffer<float> tris, ReadOnlyBuffer<float> origins, ReadOnlyBuffer<float> dirs, ReadWriteBuffer<int> hits, int rays, int pointOffset)
        {
            this.bounds = bounds;
            this.miss = miss;
            this.first = first;
            this.count = count;
            this.tris = tris;
            this.origins = origins;
            this.dirs = dirs;
            this.hits = hits;
            this.rays = rays;
            this.pointOffset = pointOffset;
        }

        public void Execute()
        {
            int r = ThreadIds.X;
            int p = pointOffset + ThreadIds.Y;
            float ox = origins[p * 3], oy = origins[p * 3 + 1], oz = origins[p * 3 + 2];
            float dx = dirs[r * 3], dy = dirs[r * 3 + 1], dz = dirs[r * 3 + 2];
            float ix = 1f / dx, iy = 1f / dy, iz = 1f / dz;
            float best = 3.0e38f;
            int hit = -1;
            int node = 0;
            while (node >= 0)
            {
                int b = node * 6;
                float t1 = (bounds[b] - ox) * ix, t2 = (bounds[b + 3] - ox) * ix;
                float tmin = Hlsl.Min(t1, t2), tmax = Hlsl.Max(t1, t2);
                t1 = (bounds[b + 1] - oy) * iy; t2 = (bounds[b + 4] - oy) * iy;
                tmin = Hlsl.Max(tmin, Hlsl.Min(t1, t2)); tmax = Hlsl.Min(tmax, Hlsl.Max(t1, t2));
                t1 = (bounds[b + 2] - oz) * iz; t2 = (bounds[b + 5] - oz) * iz;
                tmin = Hlsl.Max(tmin, Hlsl.Min(t1, t2)); tmax = Hlsl.Min(tmax, Hlsl.Max(t1, t2));
                if (tmax < Hlsl.Max(tmin, 0) || tmin > best)
                {
                    node = miss[node];
                    continue;
                }
                int c = count[node];
                if (c == 0)
                {
                    node = node + 1;
                    continue;
                }
                int f = first[node];
                for (int i = f; i < f + c; i++)
                {
                    int k = i * 9;
                    float e1x = tris[k + 3], e1y = tris[k + 4], e1z = tris[k + 5], e2x = tris[k + 6], e2y = tris[k + 7], e2z = tris[k + 8];
                    float px = dy * e2z - dz * e2y, py = dz * e2x - dx * e2z, pz = dx * e2y - dy * e2x;
                    float det = e1x * px + e1y * py + e1z * pz;
                    if (Hlsl.Abs(det) < 1e-9f) continue;
                    float inv = 1f / det;
                    float sx = ox - tris[k], sy = oy - tris[k + 1], sz = oz - tris[k + 2];
                    float u = (sx * px + sy * py + sz * pz) * inv;
                    if (u < 0 || u > 1) continue;
                    float qx = sy * e1z - sz * e1y, qy = sz * e1x - sx * e1z, qz = sx * e1y - sy * e1x;
                    float v = (dx * qx + dy * qy + dz * qz) * inv;
                    if (v < 0 || u + v > 1) continue;
                    float t = (e2x * qx + e2y * qy + e2z * qz) * inv;
                    if (t > 0.01f && t < best)
                    {
                        best = t;
                        hit = i;
                    }
                }
                node = miss[node];
            }
            hits[ThreadIds.Y * rays + r] = hit;
        }
    }
}
