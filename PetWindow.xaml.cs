using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Mutsumi.Desktop
{
    internal partial class PetWindow : Window
    {
        private readonly PetState state;
        private readonly DispatcherTimer idle = new DispatcherTimer();
        private readonly DispatcherTimer blink = new DispatcherTimer();
        private readonly DispatcherTimer finish = new DispatcherTimer();
        private readonly DispatcherTimer gesture = new DispatcherTimer();
        private readonly DispatcherTimer hold = new DispatcherTimer();
        private const int MinimizeHoldMs = 800;
        private const int ExitHoldMs = 1200;
        private readonly DispatcherTimer speechTimeout = new DispatcherTimer();
        private readonly DispatcherTimer frameTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0 / 30) };
        private readonly Random random = new Random();
        private readonly Stopwatch motionTime = new Stopwatch();
        private CutoutMotion motion;
        private Point down;
        private Point grip;
        private DockEdge dockEdge;
        private DockEdge? pulledFrom;
        private ActionShuffle clickActions, quietActions;
        private int quietMomentsBeforeGuitar;
        private MouseButton? pressedButton, pendingClick;
        private int pressTimestamp;
        private bool dragged, doubleClick;
        private bool suspended = true;
        private bool settingsWarningShown;
        private DateTime lastInteraction = DateTime.UtcNow;
        private DateTime nextAction = DateTime.UtcNow.AddSeconds(43);
        private DateTime nextScratch;
        public PetMode Mode { get; private set; }
        internal DockEdge? PeekingEdge => Mode == PetMode.Peeking ? dockEdge : (DockEdge?)null;
        public event Action SwitchRequested, MinimizeRequested, ExitRequested;

        public PetWindow(PetState state)
        {
            this.state = state;
            ResetActions();
            InitializeComponent();
            ResizePet();
            Topmost = state.Pinned;
            Doll.Source = SpriteArt.Get(state.Identity, Pose.Idle);
            Doll.MouseDown += Press;
            Doll.MouseMove += Drag;
            Doll.MouseUp += Release;
            Doll.LostMouseCapture += (s, e) => { if (pressedButton.HasValue) CancelPress(); };
            gesture.Interval = TimeSpan.FromMilliseconds(Forms.SystemInformation.DoubleClickTime);
            gesture.Tick += (s, e) => FinishSingleClick();
            hold.Tick += (s, e) =>
            {
                hold.Stop();
                if (!pressedButton.HasValue || dragged || MovedBeyondThreshold(Forms.Cursor.Position)) return;
                var button = pressedButton.Value;
                var stateNow = button == MouseButton.Left ? Mouse.LeftButton : Mouse.RightButton;
                if (stateNow == MouseButtonState.Pressed) CompleteHold(button);
            };
            finish.Tick += (s, e) => { finish.Stop(); Rest(); };
            speechTimeout.Tick += (s, e) => { speechTimeout.Stop(); Bubble.Visibility = Visibility.Collapsed; CloseEdgeNotice(); };
            idle.Tick += (s, e) => IdleMoment();
            blink.Tick += (s, e) =>
            {
                blink.Stop();
                if (suspended || Mode != PetMode.Awake || finish.IsEnabled) return;
                if (!pressedButton.HasValue && !pendingClick.HasValue)
                    Doll.BeginAnimation(Image.SourceProperty, IdleClips.Blink(state.Identity));
                ScheduleBlink();
            };
            frameTimer.Tick += StepFrame;
            IsVisibleChanged += (s, e) => Suspend(!IsVisible);
            LocationChanged += (s, e) =>
            {
                if (chatInput?.IsLoaded == true) Placement.NextToPet(this, chatInput);
                if (edgeNotice?.IsLoaded == true) Placement.NextToPet(this, edgeNotice);
            };
            SourceInitialized += (s, e) =>
            {
                var source = (HwndSource)PresentationSource.FromVisual(this);
                source.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
                {
                    if (msg == 0x21) { handled = true; return new IntPtr(3); } // MA_NOACTIVATE
                    return IntPtr.Zero;
                });
            };
        }

        public void ResizePet()
        {
            Width = 224 * state.Size / 100.0;
            Height = 288 * state.Size / 100.0;
        }

        private void Press(object sender, MouseButtonEventArgs e)
        {
            var button = e.ChangedButton;
            if (button != MouseButton.Left && button != MouseButton.Right) return;
            e.Handled = true;
            if (pressedButton.HasValue || (e.LeftButton == MouseButtonState.Pressed && e.RightButton == MouseButtonState.Pressed))
            {
                CancelPress();
                return;
            }
            NoteInteraction();
            Point screen = PointToScreen(e.GetPosition(this));
            doubleClick = pendingClick == button && gesture.IsEnabled &&
                unchecked((uint)(e.Timestamp - pressTimestamp)) <= Forms.SystemInformation.DoubleClickTime &&
                Math.Abs(screen.X - down.X) <= Forms.SystemInformation.DoubleClickSize.Width / 2.0 &&
                Math.Abs(screen.Y - down.Y) <= Forms.SystemInformation.DoubleClickSize.Height / 2.0;
            if (doubleClick) ClearPendingClick();
            else FinishSingleClick();
            down = screen;
            pressTimestamp = e.Timestamp;
            Point local = e.GetPosition(ArtLayer);
            grip = new Point(local.X / ArtLayer.ActualWidth, local.Y / ArtLayer.ActualHeight);
            pulledFrom = Mode == PetMode.Peeking ? dockEdge : (DockEdge?)null;
            dragged = false;
            if (!Doll.CaptureMouse()) return;
            pressedButton = button;
            hold.Interval = TimeSpan.FromMilliseconds(button == MouseButton.Left ? MinimizeHoldMs : ExitHoldMs);
            hold.Start();
        }

        private void ClearPendingClick()
        {
            gesture.Stop();
            pendingClick = null;
        }

        private void FinishSingleClick()
        {
            var button = pendingClick;
            ClearPendingClick();
            if (button == MouseButton.Left) Play(clickActions.Draw(random));
            else if (button == MouseButton.Right) OpenChat();
        }

        private void EndPress()
        {
            hold.Stop();
            pressedButton = null;
            if (Doll.IsMouseCaptured) Doll.ReleaseMouseCapture();
        }

        private void CancelPress()
        {
            if (pressedButton == MouseButton.Left && dragged) { Placement.Clamp(this); Remember(); }
            EndPress();
            ClearPendingClick();
        }

        private void CompleteHold(MouseButton button)
        {
            EndPress();
            ClearPendingClick();
            if (button == MouseButton.Left) MinimizeRequested?.Invoke();
            else ExitRequested?.Invoke();
        }

        private bool MovedBeyondThreshold(System.Drawing.Point pointer)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            return Math.Abs(pointer.X - down.X) >= SystemParameters.MinimumHorizontalDragDistance * dpi.DpiScaleX ||
                Math.Abs(pointer.Y - down.Y) >= SystemParameters.MinimumVerticalDragDistance * dpi.DpiScaleY;
        }

        private void Drag(object sender, MouseEventArgs e)
        {
            if (!pressedButton.HasValue) return;
            var pointer = Forms.Cursor.Position;
            if (!dragged)
            {
                if (!MovedBeyondThreshold(pointer)) return;
                dragged = true;
                hold.Stop();
                if (pressedButton != MouseButton.Left) return;
                // A click on the exposed character will return to the last free
                // position, even if she was dragged from one edge to another.
                Remember();
                if (Mode != PetMode.Awake) SetMode(PetMode.Awake, restorePosition: false);
                else Rest();
                if (!IsChatActive) { speechTimeout.Stop(); Bubble.Visibility = Visibility.Collapsed; }
            }
            if (pressedButton == MouseButton.Left) Placement.FollowPointer(this, pointer, grip);
            e.Handled = true;
        }

        private void Release(object sender, MouseButtonEventArgs e)
        {
            var button = e.ChangedButton;
            if (pressedButton != button) return;
            e.Handled = true;
            bool moved = MovedBeyondThreshold(Forms.Cursor.Position);
            if (!dragged && !moved && unchecked((uint)(e.Timestamp - pressTimestamp)) >= hold.Interval.TotalMilliseconds)
            {
                CompleteHold(button);
                return;
            }
            EndPress();
            if (dragged || moved)
            {
                if (dragged && button == MouseButton.Left) FinishDrop();
                return;
            }
            if (doubleClick)
            {
                if (button == MouseButton.Left) SwitchRequested?.Invoke();
                else SetMode(PetMode.Sleeping);
                return;
            }
            pendingClick = button;
            gesture.Start();
        }

        private void FinishDrop()
        {
            var pointer = Forms.Cursor.Position;
            var monitor = Forms.Screen.FromPoint(pointer);
            Placement.FollowPointer(this, pointer, grip);
            var screenInk = Placement.ScreenInk(Doll, SpriteArt.Get(state.Identity, Pose.Idle));
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            var edge = Placement.DropEdge(monitor.WorkingArea, screenInk, pointer, 12 * scale);
            if (edge.HasValue && edge == pulledFrom &&
                monitor.Equals(Forms.Screen.FromPoint(new System.Drawing.Point((int)down.X, (int)down.Y))))
            {
                double dx = pointer.X - down.X, dy = pointer.Y - down.Y;
                double inward = edge == DockEdge.Left ? dx : edge == DockEdge.Right ? -dx : edge == DockEdge.Top ? dy : -dy;
                if (inward > 20 * scale) edge = null;
            }
            NoteInteraction();
            if (edge.HasValue) SetMode(PetMode.Peeking, edge: edge, monitor: monitor);
            else { Placement.Clamp(this); Remember(); }
        }

        private void ResetActions()
        {
            var persona = Persona.For(state.Identity);
            clickActions = new ActionShuffle(persona.Interactions);
            quietActions = new ActionShuffle(persona.QuietActions);
            quietMomentsBeforeGuitar = random.Next(2);
            ScheduleScratch();
        }

        private void ScheduleScratch() => nextScratch = DateTime.UtcNow.AddSeconds(random.Next(38, 71));

        private void ScheduleBlink()
        {
            blink.Interval = TimeSpan.FromMilliseconds(random.Next(4600, 8401));
            blink.Start();
        }

        public void Remember()
        {
            if (Mode == PetMode.Peeking) return;
            var r = Placement.Bounds(this);
            state.X = r.X; state.Y = r.Y;
            SaveSettings();
        }

        internal void SaveSettings()
        {
            if (state.TrySave() || settingsWarningShown) return;
            settingsWarningShown = true;
            MessageBox.Show("无法保存设置。请确认桌宠所在文件夹可写，并且磁盘空间充足。",
                "幕间 · 睦桌宠", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        public void Speak(string text, double seconds)
        {
            speechTimeout.Stop();
            Speech.Text = text;
            Bubble.Visibility = Visibility.Visible;
            speechTimeout.Interval = TimeSpan.FromSeconds(seconds);
            speechTimeout.Start();
        }

        public void ScheduleIdle()
        {
            idle.Stop();
            if (suspended || Mode != PetMode.Awake || !state.Autonomous || finish.IsEnabled) return;
            idle.Interval = TimeSpan.FromSeconds(random.Next(9, 17));
            idle.Start();
        }

        public void NoteInteraction()
        {
            lastInteraction = DateTime.UtcNow;
            nextAction = lastInteraction.AddSeconds(random.Next(32, 64));
        }

        private void IdleMoment()
        {
            idle.Stop();
            if (pressedButton.HasValue || pendingClick.HasValue || IsChatActive) { ScheduleIdle(); return; }
            if (DateTime.UtcNow - lastInteraction >= TimeSpan.FromMinutes(5))
            {
                SetMode(PetMode.Sleeping);
                return;
            }
            if (DateTime.UtcNow >= nextScratch)
            {
                ScheduleScratch();
                finish.Stop(); StopMotion();
                speechTimeout.Stop(); Bubble.Visibility = Visibility.Collapsed;
                StartMotion(new ScratchMotion(state.Identity));
                finish.Interval = TimeSpan.FromMilliseconds(ScratchMotion.Milliseconds);
                finish.Start();
                return;
            }
            if (DateTime.UtcNow >= nextAction)
            {
                nextAction = DateTime.UtcNow.AddSeconds(random.Next(32, 64));
                var actions = Persona.For(state.Identity).Interactions;
                Play(actions[random.Next(actions.Length)], false);
                return;
            }
            Observe();
        }

        public void Observe()
        {
            if (suspended || Mode != PetMode.Awake) return;
            idle.Stop(); finish.Stop(); StopMotion();
            speechTimeout.Stop(); Bubble.Visibility = Visibility.Collapsed;
            bool guitar = state.Identity == Identity.Mutsumi && quietMomentsBeforeGuitar-- == 0;
            double duration;
            if (guitar)
            {
                quietMomentsBeforeGuitar = random.Next(1, 3);
                StartPose(Persona.QuietGuitar);
                duration = random.Next(8, 15);
            }
            else
            {
                var action = quietActions.Draw(random);
                StartPose(action);
                duration = action.Duration;
            }
            if (motion == null)
                Lean.BeginAnimation(RotateTransform.AngleProperty, Loop(0, state.Identity == Identity.Mortis ? -2 : .8, 1.4));
            finish.Interval = TimeSpan.FromSeconds(duration);
            finish.Start();
        }

        public void SetMode(PetMode next, bool restorePosition = true, DockEdge? edge = null, Forms.Screen monitor = null)
        {
            NoteInteraction();
            if (Mode == next) return;
            bool wasPeeking = Mode == PetMode.Peeking;
            HideChatWindows();
            if (next == PetMode.Peeking)
            {
                monitor = monitor ?? Forms.Screen.FromRectangle(Placement.Bounds(this));
                if (!edge.HasValue) Remember();
                dockEdge = edge ?? Placement.TaskbarEdge(monitor);
            }
            Mode = next;
            ClearPendingClick();
            speechTimeout.Stop();
            Bubble.Visibility = Visibility.Collapsed;
            Bubble.Margin = new Thickness(0, next == PetMode.Sleeping ? 75 : 4, 0, 0);
            if (wasPeeking && restorePosition) Placement.Restore(this, state);
            UpdateDock(restorePosition, monitor);
            Rest();
            RefreshChat();
        }

        public void UpdateDock(bool animate = false, Forms.Screen monitor = null)
        {
            bool peeking = Mode == PetMode.Peeking;
            Vector shift = peeking ? Placement.Dock(this, dockEdge, monitor ?? Forms.Screen.FromRectangle(Placement.Bounds(this))) : new Vector();
            ShelfMirror.ScaleX = peeking && dockEdge == DockEdge.Right ? -1 : 1;
            ShelfTurn.Angle = peeking && dockEdge == DockEdge.Top ? 180 : 0;
            // Edge portraits have a cut edge. Reveal them from behind the edge
            // so that cut never floats out over the desktop.
            double? entry = peeking && shift.X != 0 ? shift.X + Math.Sign(shift.X) * 28 : (double?)null;
            Slide(TranslateTransform.XProperty, shift.X, animate, entry);
            Slide(TranslateTransform.YProperty, shift.Y, animate);
        }

        private void Slide(DependencyProperty property, double value, bool animate, double? from = null)
        {
            double current = from ?? (double)ShelfSlide.GetValue(property);
            ShelfSlide.BeginAnimation(property, null);
            ShelfSlide.SetValue(property, value);
            if (animate && Math.Abs(current - value) > 0.1)
                ShelfSlide.BeginAnimation(property, new DoubleAnimation(current, value, TimeSpan.FromMilliseconds(420)) {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop
                });
        }

        private void Suspend(bool value)
        {
            suspended = value;
            if (!value)
            {
                NoteInteraction(); ScheduleScratch(); Rest(); RefreshChat();
                ShowRequestedSwitchLine(conversation.LastOrDefault());
                return;
            }
            if (DateTime.UtcNow < readUntil) readUntil = DateTime.MaxValue;
            HideChatWindows();
            CancelPress();
            finish.Stop(); idle.Stop(); speechTimeout.Stop();
            Bubble.Visibility = Visibility.Collapsed;
            StopMotion();
            Doll.Source = null;
        }

        private void StopMotion()
        {
            blink.Stop();
            frameTimer.Stop();
            motionTime.Reset();
            motion = null;
            Doll.BeginAnimation(Image.SourceProperty, null);
            Lean.BeginAnimation(RotateTransform.AngleProperty, null);
            Lift.BeginAnimation(TranslateTransform.YProperty, null);
            Doll.BeginAnimation(OpacityProperty, null);
            Effects.Children.Clear();
        }

        private static DoubleAnimation Loop(double from, double to, double seconds, int fps = 18)
        {
            var a = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds)) {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Timeline.SetDesiredFrameRate(a, fps);
            return a;
        }

        private void Rest()
        {
            finish.Stop(); StopMotion();
            bool asleep = Mode == PetMode.Sleeping;
            bool side = Mode == PetMode.Peeking && (dockEdge == DockEdge.Left || dockEdge == DockEdge.Right);
            Doll.Source = SpriteArt.Get(state.Identity, asleep ? Pose.Sleep : side ? Pose.SidePeek : Pose.Idle);
            Lift.BeginAnimation(TranslateTransform.YProperty,
                Loop(0, asleep ? -1.3 : -.7, asleep ? 2.8 : 2.1, 10));
            if (asleep) SleepBreath();
            if (!suspended && Mode == PetMode.Awake) ScheduleBlink();
            ScheduleIdle();
        }

        private void SleepBreath()
        {
            var z = new TextBlock { Text = "z", FontFamily = new FontFamily("Georgia"), FontStyle = FontStyles.Italic,
                FontSize = 18, Foreground = Brushes.DarkSeaGreen, Opacity = 0, RenderTransform = new TranslateTransform() };
            Canvas.SetLeft(z, 157); Canvas.SetTop(z, 158); Effects.Children.Add(z);
            var fade = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(5.6), RepeatBehavior = RepeatBehavior.Forever };
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, TimeSpan.Zero));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.85, TimeSpan.FromSeconds(1.0)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, TimeSpan.FromSeconds(2.8)));
            Timeline.SetDesiredFrameRate(fade, 10);
            z.BeginAnimation(OpacityProperty, fade);
            var drift = new DoubleAnimation(5, -24, TimeSpan.FromSeconds(5.6)) { RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(drift, 10);
            ((TranslateTransform)z.RenderTransform).BeginAnimation(TranslateTransform.YProperty, drift);
        }

        public void ChangeIdentity()
        {
            bool reopenComposer = chatInput != null;
            bool focusComposer = chatInput?.IsActive == true;
            HideChatWindows();
            NoteInteraction();
            ResetActions();
            ClearPendingClick();
            Rest();
            Doll.BeginAnimation(OpacityProperty, new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(310)));
            if (reopenComposer) ShowComposer(focusComposer);
            else if (IsChatActive) RefreshChat();
            else { speechTimeout.Stop(); Bubble.Visibility = Visibility.Collapsed; }
            if (chatRequest != null || conversation.Count > 0)
            {
                switchLineRequested = true;
                if (chatRequest == null) PrepareSwitchLines(conversation.Last());
                return;
            }
            if (IsChatActive) return;
            if (Mode != PetMode.Awake) return;
            Speak(Persona.For(state.Identity).Entrance, 2.8);
            Accent("✦", 55, 104, 0); Accent("✧", 163, 131, 0.18);
        }

        private void StartPose(PetAction action)
        {
            if (action.Period == 0)
            {
                Doll.Source = SpriteArt.Get(state.Identity, action.Pose);
                return;
            }
            StartMotion(new PoseMotion(state.Identity, action));
        }

        private void StartMotion(CutoutMotion clip)
        {
            motion = clip;
            clip.Sample(0);
            Doll.Source = clip.Image;
            motionTime.Restart(); frameTimer.Start();
        }

        private void Play(PetAction action, bool speak = true)
        {
            if (suspended) return;
            if (speak) NoteInteraction();
            if (Mode != PetMode.Awake) SetMode(PetMode.Awake);
            idle.Stop(); finish.Stop(); StopMotion();
            StartPose(action);
            Pose pose = action.Pose;
            bool stage = state.Identity == Identity.Mortis;
            if (!IsChatActive)
            {
                if (speak) Speak(action.Line, action.Duration - 0.25);
                else { speechTimeout.Stop(); Bubble.Visibility = Visibility.Collapsed; }
            }
            if (pose == Pose.Perform)
            {
                string[] marks = stage ? new[] { "✧", "✦", "✧" } : new[] { "♪", "♫", "♪" };
                for (int i = 0; i < 3; i++) Accent(marks[i], i % 2 == 0 ? 171 : 40, 134 - i * 6, 0.18 + 0.48 * i);
            }
            finish.Interval = TimeSpan.FromSeconds(action.Duration);
            finish.Start();
        }

        private void StepFrame(object sender, EventArgs e)
        {
            motion?.Sample(motionTime.Elapsed.TotalSeconds);
        }

        private void Accent(string glyph, int x, int y, double delay)
        {
            var mark = new TextBlock { Text = glyph, FontSize = 21, FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI Symbol"), Foreground = state.Identity == Identity.Mortis ?
                    Brushes.PaleVioletRed : Brushes.DarkSeaGreen, Opacity = 0, RenderTransform = new TranslateTransform() };
            Canvas.SetLeft(mark, x); Canvas.SetTop(mark, y);
            Effects.Children.Add(mark);
            var opacity = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromSeconds(delay) };
            opacity.KeyFrames.Add(new LinearDoubleKeyFrame(0, TimeSpan.Zero));
            opacity.KeyFrames.Add(new LinearDoubleKeyFrame(1, TimeSpan.FromSeconds(0.22)));
            opacity.KeyFrames.Add(new LinearDoubleKeyFrame(0, TimeSpan.FromSeconds(1.3)));
            mark.BeginAnimation(OpacityProperty, opacity);
            ((TranslateTransform)mark.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(0, -27, TimeSpan.FromSeconds(1.3)) { BeginTime = TimeSpan.FromSeconds(delay) });
        }
    }
}
