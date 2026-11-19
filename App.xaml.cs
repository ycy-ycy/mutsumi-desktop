using System;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Mutsumi.Desktop
{
    public partial class App : Application
    {
        internal PetState State { get; private set; }
        internal PetWindow Pet { get; private set; }
        internal bool Exiting { get; private set; }
        private Forms.NotifyIcon tray;
        private Icon icon;
        private Mutex singleton;
        private EventWaitHandle wake;
        private RegisteredWaitHandle listener;
        private bool primary;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            singleton = new Mutex(true, @"Local\WakabaMutsumi.Desktop.v1", out primary);
            wake = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\WakabaMutsumi.Show.v1");
            if (!primary) { wake.Set(); Shutdown(); return; }
            State = PetState.Load();
            Pet = new PetWindow(State);
            MainWindow = Pet;
            Pet.SwitchRequested += Switch;
            Pet.MinimizeRequested += () => { Pet.Remember(); Pet.Hide(); };
            Pet.ExitRequested += Quit;
            Pet.Closing += (s, args) => { if (!Exiting) Dispatcher.BeginInvoke(new Action(Quit)); };
            Pet.Show();
            Placement.Restore(Pet, State);
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => Placement.Clamp(Pet)));
            Pet.Speak(State.X.HasValue ? Persona.For(State.Identity).Entrance : "点我互动，拖到边缘就能藏起来。", State.X.HasValue ? 2.6 : 8);
            CreateTray();
            listener = ThreadPool.RegisterWaitForSingleObject(wake, (s, timedOut) =>
            {
                if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(Reveal));
            }, null, Timeout.Infinite, false);
            SystemEvents.DisplaySettingsChanged += ScreensChanged;
        }

        private void CreateTray()
        {
            icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
            tray = new Forms.NotifyIcon { Icon = icon ?? SystemIcons.Application, Visible = true };
            tray.MouseClick += (s, e) => { if (e.Button == Forms.MouseButtons.Left) Reveal(); };
            UpdateTooltip();
        }

        private void UpdateTooltip() => tray.Text = "幕间 · " + Persona.For(State.Identity).Name + "｜单击显示桌宠";

        private void ScreensChanged(object sender, EventArgs args)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (Exiting) return;
                if (Pet.IsVisible)
                {
                    Placement.Clamp(Pet);
                    if (Pet.Mode == PetMode.Peeking) Pet.UpdateDock();
                }
            }));
        }

        private void Reveal()
        {
            if (Exiting) return;
            if (!Pet.IsVisible) Pet.Show();
            Placement.Clamp(Pet);
            if (Pet.Mode == PetMode.Peeking) Pet.UpdateDock();
            Pet.NoteInteraction();
        }

        private void Switch()
        {
            State.Identity = State.Identity == Identity.Mutsumi ? Identity.Mortis : Identity.Mutsumi;
            Pet.ChangeIdentity();
            UpdateTooltip();
            if (Pet.Mode == PetMode.Peeking) Pet.SaveSettings(); else Pet.Remember();
        }

        internal void Quit()
        {
            if (Exiting) return;
            Exiting = true;
            if (Pet.IsVisible) Pet.Remember();
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Exiting = true;
            if (primary)
            {
                SystemEvents.DisplaySettingsChanged -= ScreensChanged;
                listener?.Unregister(null);
                if (tray != null) { tray.Visible = false; tray.Dispose(); }
                icon?.Dispose();
                singleton.ReleaseMutex();
            }
            wake?.Dispose(); singleton?.Dispose();
            base.OnExit(e);
        }
    }
}
