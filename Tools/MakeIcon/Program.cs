using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace MakeIcon
{
    /// <summary>
    /// One-off build-time utility. Converts Images\IntraDeployLogo.png into a multi-size
    /// Windows ICO (16,24,32,48,64,128,256). The source PNG is only read, never modified.
    /// Images 64px and up are stored PNG-compressed (lossless alpha); smaller sizes use a
    /// classic 32bpp BGRA DIB with an empty AND mask (alpha carries transparency).
    /// </summary>
    internal static class Program
    {
        private static void Main(string[] args)
        {
            string source = args.Length > 0 ? args[0] : @"Images\IntraDeployLogo.png";
            string output = args.Length > 1 ? args[1] : @"Images\IntraDeploy.ico";

            using (var src = new Bitmap(source))
            {
                int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
                var entries = new List<byte[]>();

                foreach (int size in sizes)
                {
                    using (var bmp = Render(src, size))
                    {
                        entries.Add(size >= 64 ? EncodePng(bmp) : EncodeDib(bmp));
                    }
                }

                WriteIco(output, sizes, entries);
            }

            Console.WriteLine("Wrote " + output + " (" + new FileInfo(output).Length + " bytes)");
        }

        private static Bitmap Render(Image source, int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            bmp.SetResolution(96f, 96f);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.Clear(Color.Transparent);
                g.DrawImage(source, new Rectangle(0, 0, size, size));
            }
            return bmp;
        }

        private static byte[] EncodePng(Bitmap bmp)
        {
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }

        /// <summary>BITMAPINFOHEADER + 32bpp bottom-up BGRA pixels + (empty) AND mask.</summary>
        private static byte[] EncodeDib(Bitmap bmp)
        {
            int width = bmp.Width;
            int height = bmp.Height;

            Rectangle rect = new Rectangle(0, 0, width, height);
            BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int stride = data.Stride;
            int pixelBytes = stride * height;
            byte[] pixels = new byte[pixelBytes];
            Marshal.Copy(data.Scan0, pixels, 0, pixelBytes);
            bmp.UnlockBits(data);

            int maskStride = ((width + 31) / 32) * 4; // DWORD-aligned bits-per-row
            int total = 40 + pixelBytes + (maskStride * height);
            var dib = new byte[total];

            // BITMAPINFOHEADER: biHeight counts pixels + mask (bottom-up).
            WriteInt32(dib, 0, 40);
            WriteInt32(dib, 4, width);
            WriteInt32(dib, 8, height * 2);
            WriteInt16(dib, 12, 1);   // planes
            WriteInt16(dib, 14, 32);  // bpp
            WriteInt32(dib, 16, 0);   // BI_RGB

            for (int y = 0; y < height; y++)
            {
                int srcRow = y * stride;
                int dstRow = (height - 1 - y) * stride; // bottom-up
                Buffer.BlockCopy(pixels, srcRow, dib, 40 + dstRow, stride);
            }
            // AND mask bytes remain zero: alpha channel carries transparency.

            return dib;
        }

        private static void WriteIco(string path, int[] sizes, List<byte[]> entries)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write((ushort)0);               // reserved
                writer.Write((ushort)1);               // type: icon
                writer.Write((ushort)entries.Count);

                int offset = 6 + (16 * entries.Count);
                for (int i = 0; i < entries.Count; i++)
                {
                    int size = sizes[i];
                    int dimension = size == 256 ? 0 : size;
                    writer.Write((byte)dimension);     // width
                    writer.Write((byte)dimension);     // height
                    writer.Write((byte)0);             // color count
                    writer.Write((byte)0);             // reserved
                    writer.Write((ushort)1);           // planes
                    writer.Write((ushort)32);          // bpp
                    writer.Write((uint)entries[i].Length);
                    writer.Write((uint)offset);
                    offset += entries[i].Length;
                }

                foreach (byte[] entry in entries)
                {
                    writer.Write(entry);
                }

                writer.Flush();
                File.WriteAllBytes(path, ms.ToArray());
            }
        }

        private static void WriteInt32(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        private static void WriteInt16(byte[] buffer, int offset, short value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
        }
    }
}
