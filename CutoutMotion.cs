using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace Mutsumi.Desktop
{
    // The drawing stays on screen. Only transforms on its moving parts change on a tick.
    internal abstract class CutoutMotion
    {
        protected readonly DrawingGroup Stage = new DrawingGroup();
        public DrawingImage Image { get; }

        protected CutoutMotion(BitmapSource body)
        {
            var bounds = new Rect(0, 0, body.PixelWidth, body.PixelHeight);
            Stage.ClipGeometry = new RectangleGeometry(bounds);
            Stage.Children.Add(new ImageDrawing(body, bounds));
            Image = new DrawingImage(Stage);
        }

        public abstract void Sample(double seconds);

        protected static ImageDrawing Part(XElement element)
        {
            var image = SpriteArt.Load("Layers/" + (string)element.Attribute("name"));
            return new ImageDrawing(image, new Rect((int)element.Attribute("x"), (int)element.Attribute("y"),
                image.PixelWidth, image.PixelHeight));
        }

        protected static Dictionary<string, XElement> Library(string file)
        {
            var uri = new Uri("pack://application:,,,/Mutsumi;component/Assets/Layers/" + file);
            using (var stream = Application.GetResourceStream(uri).Stream)
                return XDocument.Load(stream).Root.Elements("rig").ToDictionary(e => (string)e.Attribute("name"));
        }
    }

    internal sealed class PoseMotion : CutoutMotion
    {
        private static readonly Dictionary<string, XElement> Poses = Library("poses.xml");
        private readonly List<Joint> parts = new List<Joint>();
        private readonly PetAction action;
        private double previousPhase = double.NaN;

        private sealed class Joint
        {
            public readonly MatrixTransform Transform = new MatrixTransform();
            private readonly double angle, dx, dy, pivotX, pivotY;

            public Joint(XElement part)
            {
                angle = (double)part.Attribute("angle");
                dx = (double)part.Attribute("dx");
                dy = (double)part.Attribute("dy");
                pivotX = (double)part.Attribute("pivotX");
                pivotY = (double)part.Attribute("pivotY");
            }

            public void Set(double phase)
            {
                var matrix = Matrix.Identity;
                matrix.RotateAt(phase * angle, pivotX, pivotY);
                matrix.Translate(phase * dx, phase * dy);
                Transform.Matrix = matrix;
            }
        }

        public PoseMotion(Identity identity, PetAction action)
            : base(SpriteArt.Load("Layers/" + SpriteArt.Name(identity, action.Pose)))
        {
            this.action = action;
            foreach (var part in Poses[SpriteArt.Name(identity, action.Pose)].Elements("part"))
            {
                var joint = new Joint(part);
                var group = new DrawingGroup { Transform = joint.Transform };
                group.Children.Add(Part(part));
                Stage.Children.Add(group);
                parts.Add(joint);
            }
        }

        public override void Sample(double seconds)
        {
            double beat = Math.Min(1, seconds % (action.Period + action.Pause) / action.Period);
            double phase;
            if (action.Pattern == MotionPattern.Bow)
            {
                // One dip, hold and rise, with her shoes planted throughout.
                double progress = seconds < .85 ? seconds / .85 : 1 - Math.Max(0, (seconds - 1.95) / 1.35);
                phase = Ease(progress);
            }
            else if (action.Pattern == MotionPattern.Offer)
                phase = Ease(Math.Min(beat / .30, (1 - beat) / .50));
            else
                phase = beat < 1 ? Math.Sin(2 * Math.PI * beat) : 0;
            phase *= action.Strength;
            if (phase == previousPhase) return;
            previousPhase = phase;
            foreach (var part in parts) part.Set(phase);
        }

        private static double Ease(double progress) =>
            (1 - Math.Cos(Math.PI * Math.Max(0, Math.Min(1, progress)))) / 2;
    }

    internal sealed class ScratchMotion : CutoutMotion
    {
        public const int Milliseconds = 1690;
        private static readonly Dictionary<string, XElement> Arms = Library("scratch.xml");
        private static readonly int[] Times = { 0, 50, 95, 145, 195, 250, 305, 355, 410, 475, 535, 635, 700,
            810, 875, 950, 1010, 1120, 1190, 1250, 1310, 1375, 1440, 1510, 1590 };
        private static readonly int[] Order = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 9, 8, 9, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0 };

        private readonly struct Key
        {
            public readonly int Slot;
            public readonly double Shoulder, Elbow;
            public Key(int slot, double shoulder, double elbow)
            { Slot = slot; Shoulder = shoulder; Elbow = elbow; }
        }

        // Alternate hand drawings only when the direction of the palm changes.
        // Angles for the shoulder and elbow interpolate between those drawings.
        private static readonly Key[] School = {
            default, new Key(1,0,0), new Key(1,0,-53), new Key(2,0,20), new Key(2,0,0), new Key(2,0,-25),
            new Key(3,-39,17), new Key(3,-19,19), new Key(3,-8,5), new Key(3,0,0), new Key(3,0,4.5) };
        private static readonly Key[] StagePersona = {
            default, new Key(2,0,24), new Key(2,0,12), new Key(2,0,0), new Key(2,0,-14), new Key(2,0,-29),
            new Key(3,-39,15), new Key(3,-19,17), new Key(3,-8,3), new Key(3,0,-2), new Key(3,0,2) };

        private sealed class Arm
        {
            public readonly RotateTransform Shoulder, Elbow;
            public readonly DrawingGroup Drawing;
            public Arm(XElement arm)
            {
                Shoulder = new RotateTransform(0, (double)arm.Attribute("shoulderX"), (double)arm.Attribute("shoulderY"));
                Elbow = new RotateTransform(0, (double)arm.Attribute("elbowX"), (double)arm.Attribute("elbowY"));
                Drawing = new DrawingGroup { Transform = Shoulder, Opacity = 0 };
                Drawing.Children.Add(Part(arm.Element("upper")));
                var forearm = new DrawingGroup { Transform = Elbow };
                forearm.Children.Add(Part(arm.Element("fore")));
                Drawing.Children.Add(forearm);
            }
        }

        private readonly DrawingGroup overlay = new DrawingGroup { Opacity = 0 };
        private readonly Arm[] arms = new Arm[4];
        private readonly Key[] keys;
        private int visible;

        public ScratchMotion(Identity identity) : base(SpriteArt.Get(identity, Pose.Idle))
        {
            keys = identity == Identity.Mutsumi ? School : StagePersona;
            var rig = Arms[identity.ToString().ToLowerInvariant()];
            overlay.Children.Add(Part(rig.Element("back")));
            foreach (var data in rig.Elements("arm"))
            {
                var arm = new Arm(data);
                arms[(int)data.Attribute("slot")] = arm;
                overlay.Children.Add(arm.Drawing);
            }
            Stage.Children.Add(overlay);
        }

        public override void Sample(double seconds)
        {
            double ms = seconds * 1000;
            int next = 1;
            while (next < Times.Length - 1 && ms >= Times[next]) next++;
            Key from = keys[Order[next - 1]], to = keys[Order[next]];
            double t = Math.Max(0, Math.Min(1, (ms - Times[next - 1]) / (Times[next] - Times[next - 1])));
            t = t * t * (3 - 2 * t);
            if (from.Slot != to.Slot)
            {
                if (t < .5) to = from;
                else from = to;
            }
            if (visible != from.Slot)
            {
                if (visible != 0) arms[visible].Drawing.Opacity = 0;
                visible = from.Slot;
                overlay.Opacity = visible == 0 ? 0 : 1;
                if (visible != 0) arms[visible].Drawing.Opacity = 1;
            }
            if (visible == 0) return;
            arms[visible].Shoulder.Angle = from.Shoulder + (to.Shoulder - from.Shoulder) * t;
            arms[visible].Elbow.Angle = from.Elbow + (to.Elbow - from.Elbow) * t;
        }
    }
}
