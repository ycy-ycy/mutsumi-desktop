using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using WpfPoint = System.Windows.Point;
using Screen = System.Windows.Forms.Screen;

namespace Mutsumi.Desktop
{
    internal enum DockEdge { Left, Top, Right, Bottom }

    internal static class Placement
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        public static Rectangle Bounds(Window window)
        {
            GetWindowRect(new WindowInteropHelper(window).Handle, out var r);
            return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        }

        private static void Move(Window window, Rectangle area, int x, int y)
        {
            var r = Bounds(window);
            x = Math.Max(area.Left, Math.Min(area.Right - r.Width, x));
            y = Math.Max(area.Top, Math.Min(area.Bottom - r.Height, y));
            MoveTo(window, x, y);
        }

        private static void MoveTo(Window window, int x, int y) =>
            SetWindowPos(new WindowInteropHelper(window).Handle, IntPtr.Zero, x, y, 0, 0, 0x0015);

        public static void FollowPointer(Window pet, System.Drawing.Point pointer, System.Windows.Point grip)
        {
            var r = Bounds(pet);
            MoveTo(pet, pointer.X - (int)Math.Round(grip.X * r.Width),
                pointer.Y - (int)Math.Round(grip.Y * r.Height));
        }

        // Use the monitor under the pointer at release, including when the
        // sprite straddles two monitors. Compare visible ink, not window padding.
        public static DockEdge? DropEdge(Rectangle area, Rect ink, System.Drawing.Point pointer, double tolerance)
        {
            DockEdge? edge = null;
            double closest = double.PositiveInfinity;
            void Consider(DockEdge side, double inkGap, double pointerGap)
            {
                if (Math.Min(inkGap, pointerGap) > tolerance || pointerGap >= closest) return;
                edge = side;
                closest = pointerGap;
            }
            Consider(DockEdge.Left, ink.Left - area.Left, pointer.X - area.Left);
            Consider(DockEdge.Right, area.Right - ink.Right, area.Right - pointer.X);
            Consider(DockEdge.Top, ink.Top - area.Top, pointer.Y - area.Top);
            Consider(DockEdge.Bottom, area.Bottom - ink.Bottom, area.Bottom - pointer.Y);
            return edge;
        }

        public static void Restore(Window pet, PetState state)
        {
            var area = state.X.HasValue && state.Y.HasValue
                ? Screen.FromPoint(new System.Drawing.Point(state.X.Value, state.Y.Value)).WorkingArea
                : Screen.PrimaryScreen.WorkingArea;
            var size = Bounds(pet);
            Move(pet, area, state.X ?? area.Right - size.Width - 42, state.Y ?? area.Bottom - size.Height - 20);
        }

        public static void Clamp(Window window)
        {
            var r = Bounds(window);
            Move(window, Screen.FromRectangle(r).WorkingArea, r.X, r.Y);
        }

        public static Rect ScreenInk(System.Windows.Controls.Image doll, BitmapSource portrait)
        {
            var ink = SpriteArt.InkBounds(portrait);
            return new Rect(
                doll.PointToScreen(new WpfPoint(ink.Left * doll.ActualWidth, ink.Top * doll.ActualHeight)),
                doll.PointToScreen(new WpfPoint(ink.Right * doll.ActualWidth, ink.Bottom * doll.ActualHeight)));
        }

        public static void NextToPet(PetWindow pet, ChatInput input)
        {
            var character = Bounds(pet);
            var box = Bounds(input);
            var area = Screen.FromRectangle(character).WorkingArea;
            int x = character.Left + (character.Width - box.Width) / 2;
            int y = character.Top - box.Height - 4;
            if (pet.PeekingEdge is DockEdge edge && pet.Doll.Source is BitmapSource portrait)
            {
                var clip = new Rect(character.X, character.Y, character.Width, character.Height);
                var visible = Rect.Intersect(ScreenInk(pet.Doll, portrait), clip);
                if (visible.IsEmpty) visible = clip;
                var anchor = new WpfPoint(visible.Left + visible.Width / 2, visible.Top + visible.Height / 2);
                x = (int)Math.Round(anchor.X) - box.Width / 2;
                y = (int)Math.Round(anchor.Y) - box.Height / 2;
                switch (edge)
                {
                    case DockEdge.Left: anchor.X = visible.Right; x = (int)Math.Ceiling(anchor.X); break;
                    case DockEdge.Right: anchor.X = visible.Left; x = (int)Math.Floor(anchor.X) - box.Width; break;
                    case DockEdge.Top: anchor.Y = visible.Bottom; y = (int)Math.Ceiling(anchor.Y); break;
                    case DockEdge.Bottom: anchor.Y = visible.Top; y = (int)Math.Floor(anchor.Y) - box.Height; break;
                }
                Move(input, area, x, y);
                input.AimTail(edge, input.PointFromScreen(anchor));
                return;
            }
            if (y < area.Top)
            {
                x = character.Left - box.Width >= area.Left ? character.Left - box.Width : character.Right;
                y = character.Top + character.Height / 4;
            }
            Move(input, area, x, y);
        }

        public static DockEdge TaskbarEdge(Screen screen)
        {
            var area = screen.WorkingArea;
            var full = screen.Bounds;
            int left = area.Left - full.Left, right = full.Right - area.Right;
            int top = area.Top - full.Top, bottom = full.Bottom - area.Bottom;
            int reserved = Math.Max(Math.Max(left, right), Math.Max(top, bottom));
            if (reserved == bottom) return DockEdge.Bottom;
            if (reserved == left) return DockEdge.Left;
            if (reserved == right) return DockEdge.Right;
            return DockEdge.Top;
        }

        // Move in physical pixels and shift the artwork in its own design units.
        public static Vector Dock(Window pet, DockEdge edge, Screen screen)
        {
            var r = Bounds(pet);
            var area = screen.WorkingArea;
            switch (edge)
            {
                case DockEdge.Left:
                    Move(pet, area, area.Left, r.Top); return new Vector(-101, 0);
                case DockEdge.Right:
                    Move(pet, area, area.Right - r.Width, r.Top); return new Vector(101, 0);
                case DockEdge.Top:
                    Move(pet, area, r.Left, area.Top); return new Vector(0, -123);
                default:
                    Move(pet, area, r.Left, area.Bottom - r.Height); return new Vector(0, 123);
            }
        }

    }
}
