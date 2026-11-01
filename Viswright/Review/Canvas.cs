using System;
using System.IO;
using System.IO.Compression;

namespace Viswright.Review
{
    /// <summary>An RGB colour, packed for the canvas.</summary>
    public readonly record struct Rgb(byte R, byte G, byte B)
    {
        public static Rgb Lerp(Rgb a, Rgb b, double t)
        {
            t = Math.Clamp(t, 0, 1);
            return new((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
        }

        /// <summary>A ramp for "how much is visible": dark blue through teal and yellow to red.</summary>
        public static Rgb Heat(double t)
        {
            Rgb[] stops = [new(30, 40, 110), new(30, 140, 150), new(240, 220, 60), new(220, 50, 40)];
            t = Math.Clamp(t, 0, 1) * (stops.Length - 1);
            int i = Math.Min((int)t, stops.Length - 2);
            return Lerp(stops[i], stops[i + 1], t - i);
        }

        /// <summary>A diverging ramp for a change: blue where less is visible, red where more, grey for none.</summary>
        public static Rgb Change(double t)
        {
            var none = new Rgb(200, 200, 200);
            return t < 0 ? Lerp(none, new Rgb(30, 90, 200), -t) : Lerp(none, new Rgb(210, 40, 40), t);
        }
    }

    /// <summary>
    /// A plain RGB image to draw the review's pictures on, written out as PNG. Kept to what the pictures
    /// need - boxes, lines, blending, numbers - so it runs anywhere .NET does, with no imaging library.
    /// </summary>
    public sealed class Canvas
    {
        public int Width { get; }
        public int Height { get; }
        private readonly byte[] pixels;

        public Canvas(int width, int height, Rgb background)
        {
            Width = Math.Max(1, width);
            Height = Math.Max(1, height);
            pixels = new byte[Width * Height * 3];
            for (int i = 0; i < Width * Height; i++)
                (pixels[i * 3], pixels[i * 3 + 1], pixels[i * 3 + 2]) = (background.R, background.G, background.B);
        }

        public Rgb Get(int x, int y)
        {
            int i = (y * Width + x) * 3;
            return new Rgb(pixels[i], pixels[i + 1], pixels[i + 2]);
        }

        public void Set(int x, int y, Rgb c)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height) return;
            int i = (y * Width + x) * 3;
            (pixels[i], pixels[i + 1], pixels[i + 2]) = (c.R, c.G, c.B);
        }

        public void Blend(int x, int y, Rgb c, double alpha)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height) return;
            Set(x, y, Rgb.Lerp(Get(x, y), c, alpha));
        }

        public void FillRect(int x0, int y0, int x1, int y1, Rgb c, double alpha = 1)
        {
            for (int y = Math.Max(0, Math.Min(y0, y1)); y <= Math.Min(Height - 1, Math.Max(y0, y1)); y++)
                for (int x = Math.Max(0, Math.Min(x0, x1)); x <= Math.Min(Width - 1, Math.Max(x0, x1)); x++)
                    Blend(x, y, c, alpha);
        }

        public void DrawRect(int x0, int y0, int x1, int y1, Rgb c, int thickness = 2)
        {
            for (int t = 0; t < thickness; t++)
            {
                Line(x0 - t, y0 - t, x1 + t, y0 - t, c);
                Line(x0 - t, y1 + t, x1 + t, y1 + t, c);
                Line(x0 - t, y0 - t, x0 - t, y1 + t, c);
                Line(x1 + t, y0 - t, x1 + t, y1 + t, c);
            }
        }

        public void Line(int x0, int y0, int x1, int y1, Rgb c)
        {
            int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1, err = dx + dy;
            for (int guard = 0; guard < 100000; guard++)
            {
                Set(x0, y0, c);
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
        }

        public void Disc(int cx, int cy, int r, Rgb c)
        {
            for (int y = -r; y <= r; y++)
                for (int x = -r; x <= r; x++)
                    if (x * x + y * y <= r * r)
                        Set(cx + x, cy + y, c);
        }

        // 3x5 glyphs, one row per 3 bits, for item numbers and short labels.
        private static readonly string Glyphs = "0123456789-#";
        private static readonly int[][] Font =
        [
            [7, 5, 5, 5, 7], [2, 6, 2, 2, 7], [7, 1, 7, 4, 7], [7, 1, 7, 1, 7], [5, 5, 7, 1, 1],
            [7, 4, 7, 1, 7], [7, 4, 7, 5, 7], [7, 1, 1, 1, 1], [7, 5, 7, 5, 7], [7, 5, 7, 1, 7],
            [0, 0, 7, 0, 0], [5, 7, 5, 7, 5],
        ];

        /// <summary>Writes digits on a dark plate so they read on any background. Returns the width drawn.</summary>
        public int Label(int x, int y, string text, Rgb colour, int scale = 2)
        {
            int w = text.Length * 4 * scale + scale;
            FillRect(x, y, x + w - 1, y + 7 * scale - 1, new Rgb(20, 20, 20), 0.85);
            for (int k = 0; k < text.Length; k++)
            {
                int g = Glyphs.IndexOf(text[k]);
                if (g < 0) continue;
                for (int row = 0; row < 5; row++)
                    for (int col = 0; col < 3; col++)
                    {
                        if ((Font[g][row] & (4 >> col)) == 0) continue;
                        int px = x + scale + (k * 4 + col) * scale, py = y + scale + row * scale;
                        FillRect(px, py, px + scale - 1, py + scale - 1, colour);
                    }
            }
            return w;
        }

        /// <summary>A copy of part of this canvas.</summary>
        public Canvas Crop(int x0, int y0, int x1, int y1)
        {
            x0 = Math.Clamp(x0, 0, Width - 1); x1 = Math.Clamp(x1, x0, Width - 1);
            y0 = Math.Clamp(y0, 0, Height - 1); y1 = Math.Clamp(y1, y0, Height - 1);
            var crop = new Canvas(x1 - x0 + 1, y1 - y0 + 1, new Rgb(0, 0, 0));
            for (int y = y0; y <= y1; y++)
                Array.Copy(pixels, (y * Width + x0) * 3, crop.pixels, ((y - y0) * crop.Width) * 3, crop.Width * 3);
            return crop;
        }

        public void SavePng(string path)
        {
            using var file = File.Create(path);
            file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

            var header = new byte[13];
            WriteBig(header, 0, Width);
            WriteBig(header, 4, Height);
            header[8] = 8;  // bits per channel
            header[9] = 2;  // RGB
            Chunk(file, "IHDR", header);

            using var raw = new MemoryStream();
            using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            {
                for (int y = 0; y < Height; y++)
                {
                    z.WriteByte(0);  // no filter
                    z.Write(pixels, y * Width * 3, Width * 3);
                }
            }
            Chunk(file, "IDAT", raw.ToArray());
            Chunk(file, "IEND", []);
        }

        private static void Chunk(Stream s, string type, byte[] data)
        {
            var head = new byte[8];
            WriteBig(head, 0, data.Length);
            for (int i = 0; i < 4; i++) head[4 + i] = (byte)type[i];
            s.Write(head);
            s.Write(data);
            uint crc = Crc(head.AsSpan(4, 4), 0xFFFFFFFF);
            crc = Crc(data, crc) ^ 0xFFFFFFFF;
            var tail = new byte[4];
            WriteBig(tail, 0, (int)crc);
            s.Write(tail);
        }

        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        private static uint Crc(ReadOnlySpan<byte> data, uint crc)
        {
            foreach (byte b in data)
                crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        private static void WriteBig(byte[] b, int at, int v)
        {
            b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v;
        }
    }
}
