using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace UsbShareMonitor
{
    /// <summary>Draws the up/down-arrow icon in code, so there is no image file to ship.</summary>
    internal static class AppIcon
    {
        public static Icon Create(bool online, params int[] sizes)
        {
            using var ms = new MemoryStream(BuildIco(online, sizes));
            return new Icon(ms);
        }

        public static Bitmap Render(int size, bool online)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            float s = size;
            var bg = new RectangleF(s * 0.03f, s * 0.03f, s * 0.94f, s * 0.94f);
            using (var path = RoundedRect(bg, s * 0.24f))
            using (var brush = new LinearGradientBrush(bg,
                       online ? Color.FromArgb(0x36, 0x9A, 0xFF) : Color.FromArgb(0xA4, 0xAA, 0xB0),
                       online ? Color.FromArgb(0x00, 0x5C, 0xD6) : Color.FromArgb(0x70, 0x76, 0x7D), 90f))
            {
                g.FillPath(brush, path);
            }

            using var white = new SolidBrush(Color.White);
            FillArrow(g, white, s, 0.35f, down: true);
            FillArrow(g, white, s, 0.65f, down: false);
            return bmp;
        }

        /// <summary>Multi-size .ico: 32-bit DIB entries, PNG for 256 px.</summary>
        public static byte[] BuildIco(bool online, params int[] sizes)
        {
            var images = new List<byte[]>();
            foreach (int size in sizes)
                using (var bmp = Render(size, online))
                    images.Add(size >= 256 ? Png(bmp) : Dib(bmp));

            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write((short)0);
            w.Write((short)1);
            w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                byte dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                w.Write(dim);
                w.Write(dim);
                w.Write((byte)0);
                w.Write((byte)0);
                w.Write((short)1);
                w.Write((short)32);
                w.Write(images[i].Length);
                w.Write(offset);
                offset += images[i].Length;
            }
            foreach (var img in images) w.Write(img);
            w.Flush();
            return ms.ToArray();
        }

        private static void FillArrow(Graphics g, Brush brush, float s, float cx, bool down)
        {
            const float shaft = 0.12f, headW = 0.36f, headH = 0.25f, tip = 0.19f, tail = 0.81f;
            float l = cx - shaft / 2, r = cx + shaft / 2, hl = cx - headW / 2, hr = cx + headW / 2;
            PointF[] pts = down
                ? new[] { P(l, tip), P(r, tip), P(r, tail - headH), P(hr, tail - headH), P(cx, tail), P(hl, tail - headH), P(l, tail - headH) }
                : new[] { P(l, tail), P(r, tail), P(r, tip + headH), P(hr, tip + headH), P(cx, tip), P(hl, tip + headH), P(l, tip + headH) };
            g.FillPolygon(brush, pts);

            PointF P(float x, float y) => new PointF(x * s, y * s);
        }

        private static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            float d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static byte[] Png(Bitmap bmp)
        {
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }

        private static byte[] Dib(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            int maskStride = (w + 31) / 32 * 4;
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write(40);            // BITMAPINFOHEADER size
            bw.Write(w);
            bw.Write(h * 2);         // colour + mask
            bw.Write((short)1);
            bw.Write((short)32);
            bw.Write(0);             // BI_RGB
            bw.Write(w * h * 4 + maskStride * h);
            bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);

            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[w * 4];
                for (int y = h - 1; y >= 0; y--) // bottom-up
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                    bw.Write(row);
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }
            bw.Write(new byte[maskStride * h]); // empty AND mask; alpha does the work
            bw.Flush();
            return ms.ToArray();
        }
    }
}
