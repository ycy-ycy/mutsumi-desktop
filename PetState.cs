using System;
using System.IO;
using System.Xml.Linq;

namespace Mutsumi.Desktop
{
    internal enum Identity { Mutsumi, Mortis }
    internal enum Pose
    {
        Idle, Greet, Perform, Gift, Observe, Sleep, SidePeek,
        OfferPick, ShareEarbud, StraightenCuff, MarkScore,
        Hush, PresentRose, TipBeret, SwingMoon
    }
    internal enum PetMode { Awake, Sleeping, Peeking }
    internal enum MotionPattern { Swing, Offer, Bow }

    internal sealed class PetState
    {
        public Identity Identity { get; set; }
        public int Size { get; set; } = 100;
        public bool Pinned { get; set; } = true;
        public bool Autonomous { get; set; } = true;
        public int? X { get; set; }
        public int? Y { get; set; }

        internal static readonly string Folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
        private static readonly string FilePath = Path.Combine(Folder, "settings.xml");

        public static PetState Load()
        {
            try
            {
                var root = XElement.Load(FilePath);
                return new PetState {
                    Identity = (string)root.Attribute("identity") == "Mortis" ? Identity.Mortis : Identity.Mutsumi,
                    Size = ClampSize((int?)root.Attribute("size") ?? 100),
                    Pinned = (bool?)root.Attribute("pinned") ?? true,
                    Autonomous = (bool?)root.Attribute("autonomous") ?? true,
                    X = (int?)root.Attribute("x"), Y = (int?)root.Attribute("y")
                };
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                ex is System.Xml.XmlException || ex is FormatException || ex is OverflowException)
            { return new PetState(); }
        }

        public static int ClampSize(int value) => Math.Max(60, Math.Min(160, value));

        public bool TrySave()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                var root = new XElement("pet", new XAttribute("identity", Identity),
                    new XAttribute("size", Size), new XAttribute("pinned", Pinned),
                    new XAttribute("autonomous", Autonomous));
                root.SetAttributeValue("x", X);
                root.SetAttributeValue("y", Y);
                string temp = FilePath + ".new";
                root.Save(temp);
                if (File.Exists(FilePath)) File.Replace(temp, FilePath, null);
                else File.Move(temp, FilePath);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return false; }
        }
    }

    internal sealed class PetAction
    {
        public readonly Pose Pose;
        public readonly double Duration, Period, Pause, Strength;
        public readonly MotionPattern Pattern;
        public readonly string Line;

        public PetAction(Pose pose, double duration, double period = 0, double pause = 0, string line = null,
            MotionPattern pattern = MotionPattern.Swing, double strength = 1)
        {
            Pose = pose; Duration = duration; Period = period; Pause = pause; Line = line;
            Pattern = pattern; Strength = strength;
        }
    }

    internal sealed class Persona
    {
        public string Name, Entrance;
        public PetAction[] Interactions, QuietActions;
        public static readonly PetAction QuietGuitar = new PetAction(Pose.Perform, 14, .85, 1.7, strength: .55);

        private static readonly Persona School = new Persona {
            Name = "若叶睦",
            Entrance = "……我在。",
            Interactions = new[] {
                new PetAction(Pose.Greet, 2.7, 0.94, line: "……嗯。"),
                new PetAction(Pose.Perform, 4.2, 0.54, line: "这段，再弹一遍。"),
                new PetAction(Pose.Gift, 3.5, 1.8, line: "今天摘的。给你。", pattern: MotionPattern.Offer),
                new PetAction(Pose.OfferPick, 3.6, 1.2, 1.4, "备用的。给你一片。", MotionPattern.Offer),
                new PetAction(Pose.ShareEarbud, 4.4, 1.9, 0.6, "……听。这里有两把吉他。", MotionPattern.Offer)
            },
            QuietActions = new[] {
                new PetAction(Pose.Observe, 3.3),
                new PetAction(Pose.StraightenCuff, 4.5, 0.95, 1.25),
                new PetAction(Pose.MarkScore, 6.2, 0.85, 0.45)
            }
        };
        private static readonly Persona Stage = new Persona {
            Name = "Mortis",
            Entrance = "这次，轮到我了。",
            Interactions = new[] {
                new PetAction(Pose.Greet, 2.7, 0.94, line: "你也来看我了吗？"),
                new PetAction(Pose.Perform, 4.2, 1.8, line: "请进。帷幕已经拉开了。"),
                new PetAction(Pose.Gift, 3.5, 1.8, line: "那么，下次公演见。", pattern: MotionPattern.Bow),
                new PetAction(Pose.Hush, 3.5, 1.15, 1.7, "嘘。先别告诉睦。", MotionPattern.Offer),
                new PetAction(Pose.PresentRose, 4.0, 1.6, 0.8, "这一朵，给坐在第一排的你。", MotionPattern.Offer)
            },
            QuietActions = new[] {
                new PetAction(Pose.Observe, 3.3),
                new PetAction(Pose.TipBeret, 4.4, 1.3, 1.55),
                new PetAction(Pose.SwingMoon, 6.0, 1.85)
            }
        };
        public static Persona For(Identity id) => id == Identity.Mortis ? Stage : School;
    }
}
