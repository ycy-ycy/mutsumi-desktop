using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Mutsumi.Desktop
{
    internal partial class ChatInput : Window
    {
        public bool Submitted { get; private set; }
        public string Value => Entry.Text;
        public event Action RecallRequested;

        private const double TailDepth = 9;

        public ChatInput(PetWindow owner, Identity id, string draft, string lastAnswer, bool activate, bool noticeOnly = false)
        {
            InitializeComponent();
            Owner = owner;
            Topmost = owner.Topmost;
            ShowActivated = activate;
            Left = owner.Left;
            Top = owner.Top;
            Title = Heading.Text = id == Identity.Mortis ? "和 Mortis 说话" : "和若叶睦说话";
            switch (owner.PeekingEdge)
            {
                case DockEdge.Left: Card.Margin = new Thickness(TailDepth, 0, 0, 0); break;
                case DockEdge.Right: Card.Margin = new Thickness(0, 0, TailDepth, 0); break;
                case DockEdge.Top: Card.Margin = new Thickness(0, TailDepth, 0, 0); break;
                case DockEdge.Bottom: Card.Margin = new Thickness(0, 0, 0, TailDepth); break;
            }
            if (id == Identity.Mortis)
            {
                Card.BorderBrush = new SolidColorBrush(Color.FromRgb(162, 124, 138));
                Send.Background = new SolidColorBrush(Color.FromRgb(237, 219, 226));
            }
            Entry.Text = draft ?? "";
            if (noticeOnly)
            {
                Header.Visibility = Composer.Visibility = Visibility.Collapsed;
                Width = double.NaN;
                MinWidth = 48;
                SizeToContent = SizeToContent.WidthAndHeight;
                Card.Padding = new Thickness(12, 7, 12, 7);
                Notice.Text = lastAnswer;
                Notice.Visibility = Visibility.Visible;
            }
            else SetAnswer(lastAnswer);
            bool composing = false;
            TextCompositionManager.AddPreviewTextInputStartHandler(Entry, (s, e) => composing = true);
            TextCompositionManager.AddPreviewTextInputUpdateHandler(Entry, (s, e) => composing = true);
            TextCompositionManager.AddPreviewTextInputHandler(Entry, (s, e) => composing = false);
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.ImeProcessed || composing) return;
                if (e.Key == Key.Escape) { e.Handled = true; Close(); }
                if (!Entry.IsReadOnly && e.Key == Key.Return && Entry.IsKeyboardFocusWithin && Keyboard.Modifiers == ModifierKeys.None)
                {
                    e.Handled = true;
                    if (!e.IsRepeat) Submit(s, e);
                }
            };
            Loaded += (s, e) =>
            {
                Placement.NextToPet(owner, this);
                if (!activate) return;
                Activate();
                Entry.Focus();
                Keyboard.Focus(Entry);
                Entry.CaretIndex = Entry.Text.Length;
            };
            SizeChanged += (s, e) => { if (IsLoaded) Placement.NextToPet(owner, this); };
        }

        internal void SetAnswer(string text)
        {
            Answer.Text = text ?? "";
            Answer.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
            Answer.ScrollToHome();
        }

        internal void SetPending(bool pending)
        {
            Entry.IsReadOnly = pending;
            Send.Content = pending ? "撤回修改" : "发送";
            Send.IsEnabled = pending || !string.IsNullOrWhiteSpace(Value);
        }

        internal void AimTail(DockEdge edge, Point anchor)
        {
            bool side = edge == DockEdge.Left || edge == DockEdge.Right;
            bool near = edge == DockEdge.Left || edge == DockEdge.Top;
            double cross = Math.Max(13, Math.Min((side ? ActualHeight : ActualWidth) - 13, side ? anchor.Y : anchor.X));
            double tip = near ? 1 : (side ? ActualWidth : ActualHeight) - 1;
            double seam = tip + (near ? TailDepth : -TailDepth);
            Point At(double depth, double along) => side ? new Point(depth, along) : new Point(along, depth);
            Tail.Points = new PointCollection { At(seam, cross - 6), At(tip, cross), At(seam, cross + 6) };
        }

        private void OnTextChanged(object sender, TextChangedEventArgs e)
        {
            if (Send != null) Send.IsEnabled = !string.IsNullOrWhiteSpace(Entry.Text);
        }

        private void Dismiss(object sender, RoutedEventArgs e) => Close();

        private void Submit(object sender, RoutedEventArgs e)
        {
            if (Entry.IsReadOnly) { RecallRequested?.Invoke(); return; }
            if (Submitted || string.IsNullOrWhiteSpace(Value)) return;
            Submitted = true;
            Close();
        }
    }
}
