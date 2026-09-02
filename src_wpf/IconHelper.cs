using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;

namespace AntigravityQuota
{
    public static class IconHelper
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        public static Icon CreateQuotaIcon(int size = 32)
        {
            using var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);

                // 翡翠绿渐变圆形背景
                using var bgBrush = new LinearGradientBrush(
                    new Rectangle(0, 0, size, size),
                    Color.FromArgb(16, 185, 129),
                    Color.FromArgb(5, 150, 105),
                    45f
                );
                g.FillEllipse(bgBrush, 1, 1, size - 2, size - 2);

                // 白色闪电 ⚡ 图标
                float s = size;
                var pts = new PointF[]
                {
                    new PointF(s * 0.54f, s * 0.16f),
                    new PointF(s * 0.28f, s * 0.52f),
                    new PointF(s * 0.48f, s * 0.52f),
                    new PointF(s * 0.42f, s * 0.84f),
                    new PointF(s * 0.74f, s * 0.44f),
                    new PointF(s * 0.54f, s * 0.44f)
                };
                g.FillPolygon(Brushes.White, pts);
            }

            IntPtr hIcon = bmp.GetHicon();
            Icon icon = (Icon)Icon.FromHandle(hIcon).Clone();
            DestroyIcon(hIcon);
            return icon;
        }

        public static void SaveAppIcoFile(string path)
        {
            try
            {
                using var icon = CreateQuotaIcon(64);
                using var stream = new FileStream(path, FileMode.Create);
                icon.Save(stream);
            }
            catch { }
        }
    }
}
