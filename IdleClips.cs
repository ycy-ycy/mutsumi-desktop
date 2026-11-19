using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace Mutsumi.Desktop
{
    internal static class IdleClips
    {
        private static readonly Dictionary<Identity, ObjectAnimationUsingKeyFrames> Blinks =
            new Dictionary<Identity, ObjectAnimationUsingKeyFrames>();
        private static readonly Int32Rect Eyes = new Int32Rect(138, 99, 116, 64);

        public static ObjectAnimationUsingKeyFrames Blink(Identity id)
        {
            if (Blinks.TryGetValue(id, out var clip)) return clip;
            var idle = SpriteArt.Get(id, Pose.Idle);
            var strip = SpriteArt.Load(id.ToString().ToLowerInvariant() + "-blink");
            var views = new ImageSource[] { idle, null, null };
            for (int i = 0; i < 2; i++)
            {
                var crop = new CroppedBitmap(strip, new Int32Rect(i * Eyes.Width, 0, Eyes.Width, Eyes.Height));
                var drawing = new DrawingGroup();
                using (var dc = drawing.Open())
                {
                    dc.DrawImage(idle, new Rect(0, 0, idle.PixelWidth, idle.PixelHeight));
                    dc.DrawImage(crop, new Rect(Eyes.X, Eyes.Y, Eyes.Width, Eyes.Height));
                }
                views[i + 1] = new DrawingImage(drawing);
            }
            clip = new ObjectAnimationUsingKeyFrames {
                Duration = TimeSpan.FromMilliseconds(210), FillBehavior = FillBehavior.Stop
            };
            int[] order = { 0, 1, 2, 1, 0 }, times = { 0, 32, 68, 142, 195 };
            for (int i = 0; i < order.Length; i++)
                clip.KeyFrames.Add(new DiscreteObjectKeyFrame(views[order[i]], TimeSpan.FromMilliseconds(times[i])));
            clip.Freeze();
            return Blinks[id] = clip;
        }
    }
}
