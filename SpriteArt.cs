using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mutsumi.Desktop
{
    internal static class SpriteArt
    {
        private static readonly Dictionary<string, BitmapSource> Textures = new Dictionary<string, BitmapSource>();
        private static readonly Dictionary<BitmapSource, Rect> Ink = new Dictionary<BitmapSource, Rect>();

        public static string Name(Identity id, Pose pose) =>
            id.ToString().ToLowerInvariant() + "-" + pose.ToString().ToLowerInvariant();

        public static BitmapSource Get(Identity id, Pose pose) => Load(Name(id, pose));

        public static BitmapSource Load(string name)
        {
            if (Textures.TryGetValue(name, out var art)) return art;
            var uri = new Uri("pack://application:,,,/Mutsumi;component/Assets/" + name + ".png");
            var source = new BitmapImage(uri);
            art = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
            art.Freeze();
            return Textures[name] = art;
        }


        // Docking and chat bubbles share the same visible outline of the static portraits.
        public static Rect InkBounds(BitmapSource frame)
        {
            if (Ink.TryGetValue(frame, out var bounds)) return bounds;
            int width = frame.PixelWidth, height = frame.PixelHeight, stride = width * 4;
            var pixels = new byte[height * stride];
            frame.CopyPixels(pixels, stride, 0);
            int left = width, top = height, right = -1, bottom = -1;
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                if (pixels[y * stride + x * 4 + 3] < 80) continue;
                left = Math.Min(left, x); right = Math.Max(right, x);
                top = Math.Min(top, y); bottom = Math.Max(bottom, y);
            }
            return Ink[frame] = new Rect((double)left / width, (double)top / height,
                (double)(right + 1 - left) / width, (double)(bottom + 1 - top) / height);
        }

    }
}
