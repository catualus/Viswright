using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Viswright.Tests
{
    /// <summary>The same tiny BSP SourceKit's tests use: three clusters in a row, one face shared by the first two.</summary>
    public static class TinyBsp
    {
        /// <summary>
        /// Writes a BSP holding only the lumps BspVisibilityData reads: three clusters in a row, each
        /// seeing its neighbours, one face shared by the first two.
        /// </summary>
        public static void Write(string path, byte[]? visibility = null, string entities = "")
        {
            var lumps = new Dictionary<int, byte[]>
            {
                [0] = System.Text.Encoding.ASCII.GetBytes(entities + "\0"),
                [4] = visibility ?? Visibility([0b011], [0b111], [0b110]),
                [6] = new byte[72],                               // one texinfo, no flags
                [7] = Concat(Face(4), Face(3), Face(6)),          // 2, 1 and 4 triangles
                [10] = Concat(Leaf(1, -1, 0, 0), Leaf(0, 0, 0, 1), Leaf(0, 1, 1, 2), Leaf(0, 2, 3, 1)),
                [16] = Concat(U16(0), U16(0), U16(1), U16(2), U16(2)),
            };

            using var stream = File.Create(path);
            using var w = new BinaryWriter(stream);
            w.Write(0x50534256);
            w.Write(20);
            int offset = 8 + 64 * 16 + 4;
            var order = new List<byte[]>();
            for (int i = 0; i < 64; i++)
            {
                var bytes = lumps.GetValueOrDefault(i, []);
                w.Write(bytes.Length == 0 ? 0 : offset);
                w.Write(bytes.Length);
                w.Write(i == 10 ? 1 : 0); // leaves in version 1, without ambient cubes
                w.Write(0);
                offset += bytes.Length;
                order.Add(bytes);
            }
            w.Write(1); // map revision
            foreach (var bytes in order)
                w.Write(bytes);
        }

        private static byte[] Visibility(params byte[][] rows)
        {
            var header = new List<byte>(BitConverter.GetBytes(rows.Length));
            int at = 4 + rows.Length * 8;
            var body = new List<byte>();
            foreach (var row in rows)
            {
                header.AddRange(BitConverter.GetBytes(at + body.Count));
                header.AddRange(BitConverter.GetBytes(0));
                body.AddRange(row);
            }
            return [.. header, .. body];
        }

        private static byte[] Face(int edges)
        {
            var face = new byte[56];
            BitConverter.GetBytes((short)edges).CopyTo(face, 8);
            BitConverter.GetBytes((short)0).CopyTo(face, 10);   // texinfo
            BitConverter.GetBytes((short)-1).CopyTo(face, 12);  // no displacement
            return face;
        }

        private static byte[] Leaf(int contents, int cluster, int firstFace, int faceCount)
        {
            var leaf = new byte[32];
            BitConverter.GetBytes(contents).CopyTo(leaf, 0);
            BitConverter.GetBytes((short)cluster).CopyTo(leaf, 4);
            BitConverter.GetBytes((ushort)1).CopyTo(leaf, 6);   // area 1
            BitConverter.GetBytes((short)(cluster * 100)).CopyTo(leaf, 8);
            BitConverter.GetBytes((short)(cluster * 100 + 64)).CopyTo(leaf, 14);
            BitConverter.GetBytes((ushort)firstFace).CopyTo(leaf, 20);
            BitConverter.GetBytes((ushort)faceCount).CopyTo(leaf, 22);
            return leaf;
        }

        private static byte[] U16(ushort value) => BitConverter.GetBytes(value);
        private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    }
}
