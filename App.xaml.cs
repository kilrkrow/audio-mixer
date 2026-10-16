using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace audio_mixer
{
    public partial class App : System.Windows.Application
    {
        private static Mutex? _mutex;
        private WinForms.NotifyIcon? _trayIcon;
        private MainWindow? _mainWindow;
        private AudioEngine? _audioEngine;
        private AppConfig? _config;
        private ModeService? _modeService;
        private HotkeyManager? _hotkeyManager;
        private WinForms.ContextMenuStrip? _contextMenu;
        private DispatcherTimer? _sessionPoller;
        private IntPtr _trayIconHandle = IntPtr.Zero;
        private AudioSnapshot? _startupSnapshot;
        private bool _pollInFlight;
        private static Font? _trayHeaderFont;

        // Audio COM calls can block inside the Windows audio service or a driver. If graceful shutdown does not
        // finish quickly, force the process down instead of leaving an un-closable tray app behind.
        private readonly ShutdownWatchdog _watchdog = new(
            grace: TimeSpan.FromSeconds(5),
            exitWait: TimeSpan.FromSeconds(3),
            exit: code => Environment.Exit(code),
            kill: () => Process.GetCurrentProcess().Kill());
        private static readonly TimeSpan AudioCallTimeout = TimeSpan.FromSeconds(2);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        public AudioEngine AudioEngine => _audioEngine ??= new AudioEngine();
        public AppConfig Config => _config ??= ConfigManager.Load();
        public ModeService Modes => _modeService ??= new ModeService(AudioEngine, Config, SaveConfig);
        public bool IsExiting { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            // 1. Single Instance Check
            _mutex = new Mutex(true, "KilrKrowAudioMixerMutex", out bool createdNew);
            if (!createdNew)
            {
                // Already running (e.g. taskbar pin clicked): ask that instance to open the builder
                try
                {
                    using var signal = EventWaitHandle.OpenExisting(ShowBuilderEventName);
                    signal.Set();
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    System.Windows.MessageBox.Show(
                        "KilrKrow Audio Mixer is already running in the system tray.",
                        "Audio Mixer",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information
                    );
                }
                Shutdown();
                return;
            }

            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Log off / shutdown / restart: the builder window normally cancels Close (hide-to-tray), which would
            // otherwise make this app block Windows from ending the session.
            SessionEnding += (s, args) =>
            {
                IsExiting = true;
                _watchdog.Arm();
            };

            // 2. Initialize Engine, Config & Modes
            // Snapshot Windows audio BEFORE any mode apply so Exit can restore it.
            _audioEngine = new AudioEngine();
            _startupSnapshot = _audioEngine.CaptureSnapshot();
            _config = ConfigManager.Load();
            _modeService = new ModeService(_audioEngine, _config, SaveConfig);
            StartupHelper.ApplyStartOnWindows(_config.StartWithWindows);

            // 3. Create the builder window (hidden until asked for)
            _mainWindow = new MainWindow();
            _modeService.ActiveModeChanged += () => _mainWindow.OnActiveModeChanged();

            // 4. Initialize Hotkeys on the builder's HWND
            var helper = new System.Windows.Interop.WindowInteropHelper(_mainWindow);
            helper.EnsureHandle();
            _hotkeyManager = new HotkeyManager(helper.Handle);

            var source = System.Windows.Interop.HwndSource.FromHwnd(helper.Handle);
            source?.AddHook(HwndMessageHook);

            RegisterGlobalHotkeys();

            // 5. Create Tray Icon
            InitializeTrayIcon();

            // 6. Follow new apps: seed what's already playing (no blast-apply at login), then poll
            _modeService.SeedKnownPids();
            _sessionPoller = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _sessionPoller.Tick += (s, args) => PollSessions();
            _sessionPoller.Start();

            ListenForShowBuilderRequests();

            // Launched by hand or from a pin: show the builder. Start with Windows passes --tray to stay hidden.
            if (!Array.Exists(e.Args, a => string.Equals(a, StartupHelper.TrayArgument, StringComparison.OrdinalIgnoreCase)))
            {
                ShowBuilder();
            }
        }

        /// <summary>
        /// Reads sessions on a worker thread (the Windows audio service can stall) and applies the result on the
        /// UI thread. At most one read is in flight, so a stalled audio service cannot stack up threads or freeze the UI.
        /// </summary>
        private void PollSessions()
        {
            if (_pollInFlight || IsExiting || _audioEngine == null || _modeService == null) return;
            _pollInFlight = true;
            var engine = _audioEngine;
            Task.Run(() => engine.GetSessions()).ContinueWith(t =>
            {
                _pollInFlight = false;
                if (IsExiting || !t.IsCompletedSuccessfully) return;
                _modeService?.Poll(t.Result);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private const string ShowBuilderEventName = "KilrKrowAudioMixerShowBuilder";

        private void ListenForShowBuilderRequests()
        {
            var signal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowBuilderEventName);
            var thread = new Thread(() =>
            {
                while (signal.WaitOne())
                {
                    Dispatcher.BeginInvoke(ShowBuilder);
                }
            })
            { IsBackground = true, Name = "ShowBuilderListener" };
            thread.Start();
        }

        private void InitializeTrayIcon()
        {
            _trayIcon = new WinForms.NotifyIcon();
            UpdateTrayIconImage();
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

            _contextMenu = new WinForms.ContextMenuStrip
            {
                Renderer = new DarkMenuRenderer(),
                ShowImageMargin = false,
                ShowCheckMargin = true
            };
            _contextMenu.Opening += (s, e) => BuildTrayMenu();
            BuildTrayMenu();
            _trayIcon.ContextMenuStrip = _contextMenu;
            UpdateTrayTooltip();
            _trayIcon.Visible = true;

            // Single click = quick mode switcher; double click = open the builder
            _trayIcon.MouseClick += (s, e) =>
            {
                if (e.Button == WinForms.MouseButtons.Left)
                {
                    typeof(WinForms.NotifyIcon)
                        .GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)
                        ?.Invoke(_trayIcon, null);
                }
            };
            _trayIcon.MouseDoubleClick += (s, e) =>
            {
                if (e.Button == WinForms.MouseButtons.Left) ShowBuilder();
            };
        }

        private void OnUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
        {
            // Taskbar light/dark switches arrive as General
            if (e.Category == Microsoft.Win32.UserPreferenceCategory.General)
                Dispatcher.BeginInvoke(UpdateTrayIconImage);
        }

        private static bool TaskbarUsesLightTheme()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("SystemUsesLightTheme") is int v && v != 0;
            }
            catch
            {
                return false;
            }
        }

        private void UpdateTrayIconImage()
        {
            if (_trayIcon == null) return;
            try
            {
                var size = WinForms.SystemInformation.SmallIconSize.Width;
                using var bitmap = DrawTrayIcon(size, TaskbarUsesLightTheme());
                var newHandle = bitmap.GetHicon();
                _trayIcon.Icon = Icon.FromHandle(newHandle);

                if (_trayIconHandle != IntPtr.Zero) DestroyIcon(_trayIconHandle);
                _trayIconHandle = newHandle;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to create custom tray icon: {ex.Message}");
                _trayIcon.Icon = SystemIcons.Application;
            }
        }

        /// <summary>
        /// Three mixer faders with blue / magenta / green caps, matching assets/icon.png.
        /// Tracks are light on a dark taskbar and dark on a light one.
        /// </summary>
        private static Bitmap DrawTrayIcon(int size, bool lightTaskbar)
        {
            var bitmap = new Bitmap(size, size);
            using var g = Graphics.FromImage(bitmap);
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            float u = size / 16f;
            var track = lightTaskbar ? Color.FromArgb(40, 44, 58) : Color.FromArgb(235, 236, 245);
            var caps = new[]
            {
                (X: 3f, Y: 9.5f, Color: Color.FromArgb(0, 140, 255)),  // blue
                (X: 8f, Y: 5.5f, Color: Color.FromArgb(255, 20, 147)), // magenta
                (X: 13f, Y: 8f, Color: Color.FromArgb(120, 230, 0))    // green
            };

            using (var pen = new Pen(track, 1.5f * u) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                foreach (var c in caps)
                    g.DrawLine(pen, c.X * u, 1.5f * u, c.X * u, 14.5f * u);
            }

            float w = 4.5f * u, h = 3.2f * u, r = 1.2f * u;
            foreach (var c in caps)
            {
                var rect = new RectangleF(c.X * u - w / 2, c.Y * u - h / 2, w, h);
                using var path = new GraphicsPath();
                path.AddArc(rect.Left, rect.Top, r * 2, r * 2, 180, 90);
                path.AddArc(rect.Right - r * 2, rect.Top, r * 2, r * 2, 270, 90);
                path.AddArc(rect.Right - r * 2, rect.Bottom - r * 2, r * 2, r * 2, 0, 90);
                path.AddArc(rect.Left, rect.Bottom - r * 2, r * 2, r * 2, 90, 90);
                path.CloseFigure();
                using var brush = new SolidBrush(c.Color);
                g.FillPath(brush, path);
            }

            return bitmap;
        }

        /// <summary>
        /// Rebuilt on every open so the active check, hotkeys and devices are always live.
        /// </summary>
        private void BuildTrayMenu()
        {
            if (_contextMenu == null) return;
            var menu = _contextMenu;

            // Clear() only detaches items; dispose them (and the old Output device dropdown) so they don't wait on finalizers
            var oldItems = new List<WinForms.ToolStripItem>();
            foreach (WinForms.ToolStripItem old in menu.Items) oldItems.Add(old);
            menu.Items.Clear();
            foreach (var old in oldItems) old.Dispose();

            _trayHeaderFont ??= new Font("Segoe UI Semibold", 8f);
            var header = new WinForms.ToolStripLabel("MODES") { ForeColor = Color.FromArgb(0, 240, 255), Font = _trayHeaderFont };
            menu.Items.Add(header);

            if (Config.Modes.Count == 0)
            {
                menu.Items.Add(new WinForms.ToolStripMenuItem("No modes yet — open the builder") { Enabled = false });
            }

            foreach (var mode in Config.Modes)
            {
                var captured = mode;
                var item = new WinForms.ToolStripMenuItem(mode.Name)
                {
                    Checked = mode.Id == Config.ActiveModeId,
                    ShortcutKeyDisplayString = mode.Hotkey?.ToString() ?? string.Empty
                };
                item.Click += (s, e) => Modes.Apply(captured);
                menu.Items.Add(item);
            }

            menu.Items.Add(new WinForms.ToolStripSeparator());

            var reapply = new WinForms.ToolStripMenuItem(
                Config.ActiveMode != null ? $"Re-apply \"{Config.ActiveMode.Name}\"" : "Re-apply mode")
            {
                Enabled = Config.ActiveMode != null,
                ShortcutKeyDisplayString = Config.ResetLevelsHotkey.ToString()
            };
            reapply.Click += (s, e) => Modes.ReapplyActive();
            menu.Items.Add(reapply);

            // Audio reads are time-limited so a stalled audio service can't freeze the tray menu
            var devices = TimeLimitedRunner.Shared.TryRun(() => AudioEngine.GetDevices(playbackOnly: true), AudioCallTimeout, out var found) && found != null
                ? found
                : new List<AudioDevice>();
            var output = new WinForms.ToolStripMenuItem("Output device");
            foreach (var device in devices)
            {
                var id = device.Id;
                var name = device.Name;
                var dItem = new WinForms.ToolStripMenuItem(name) { Checked = device.IsDefault };
                dItem.Click += (s, e) =>
                {
                    if (!AudioEngine.SetDefaultDeviceAllRoles(id))
                    {
                        HudWindow.ShowHud($"Couldn't switch output to {name}");
                        return;
                    }
                    HudWindow.ShowHud($"🎧 Output: {name}");
                };
                output.DropDownItems.Add(dItem);
            }
            if (output.DropDownItems.Count > 0 && output.DropDown is WinForms.ToolStripDropDownMenu outputDrop)
            {
                outputDrop.Renderer = menu.Renderer;
                outputDrop.ShowImageMargin = false;
                outputDrop.ShowCheckMargin = true;
            }
            output.Enabled = output.DropDownItems.Count > 0;
            menu.Items.Add(output);

            bool? micMuted = TimeLimitedRunner.Shared.TryRun(() => AudioEngine.GetMicMute(), AudioCallTimeout, out bool? micRead) ? micRead : null;
            var mic = new WinForms.ToolStripMenuItem("Mic muted") { Checked = micMuted == true, Enabled = micMuted != null };
            mic.Click += (s, e) =>
            {
                bool mute = AudioEngine.GetMicMute() != true;
                AudioEngine.SetMicMute(mute);
                HudWindow.ShowHud(mute ? "🎙️ Mic muted" : "🎙️ Mic live");
            };
            menu.Items.Add(mic);

            menu.Items.Add(new WinForms.ToolStripSeparator());

            var edit = new WinForms.ToolStripMenuItem("Edit modes && sources…")
            {
                ShortcutKeyDisplayString = Config.ToggleMixerHotkey.ToString()
            };
            edit.Click += (s, e) => ShowBuilder();
            menu.Items.Add(edit);

            var startup = new WinForms.ToolStripMenuItem("Start with Windows") { Checked = Config.StartWithWindows };
            startup.Click += (s, e) => SetStartWithWindows(!Config.StartWithWindows);
            menu.Items.Add(startup);

            var startMenu = new WinForms.ToolStripMenuItem("Add to Start Menu")
            {
                Checked = StartupHelper.IsStartMenuShortcutPresent()
            };
            startMenu.Click += (s, e) => SetStartMenuShortcut(!StartupHelper.IsStartMenuShortcutPresent());
            menu.Items.Add(startMenu);

            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => ExitApp());
        }

        public void UpdateTrayTooltip()
        {
            if (_trayIcon == null) return;
            var active = Config.ActiveMode;
            var text = active != null ? $"KilrKrow Mixer — {active.Name}" : "KilrKrow Mixer";
            _trayIcon.Text = text.Length > 63 ? text[..63] : text;
        }

        public void SetStartWithWindows(bool enable)
        {
            if (!StartupHelper.ApplyStartOnWindows(enable))
            {
                HudWindow.ShowHud("⚠️ Couldn't update Start with Windows.");
                return;
            }
            Config.StartWithWindows = enable;
            SaveConfig();
            _mainWindow?.RefreshSettings();
        }

        public void SetStartMenuShortcut(bool enable)
        {
            if (!StartupHelper.ApplyStartMenuShortcut(enable))
            {
                HudWindow.ShowHud("Couldn't update Start Menu shortcut.");
                return;
            }
            HudWindow.ShowHud(enable ? "Added to Start Menu." : "Removed from Start Menu.");
        }

        public void RegisterGlobalHotkeys()
        {
            if (_hotkeyManager == null) return;

            _hotkeyManager.UnregisterAll();

            var used = new List<(HotkeyConfig Chord, string Owner)>();
            var failed = new List<string>();

            void Register(HotkeyConfig? chord, string owner, Action callback)
            {
                if (chord == null || chord.IsEmpty) return;
                var clash = used.Find(u => u.Chord.SameChord(chord));
                if (clash.Chord != null)
                {
                    failed.Add($"{chord} ({owner} — already used by {clash.Owner})");
                    return;
                }
                if (_hotkeyManager.Register(chord.Modifiers, chord.Key, callback))
                    used.Add((chord, owner));
                else
                    failed.Add($"{chord} ({owner} — taken by another app)");
            }

            Register(Config.ToggleMixerHotkey, "Open builder", ToggleBuilder);
            Register(Config.ResetLevelsHotkey, "Re-apply mode", () => Modes.ReapplyActive());
            Register(Config.FavoriteOutputHotkey, "Favorite output", SwitchToFavoriteOutput);
            Register(Config.FavoriteInputHotkey, "Favorite mic", SwitchToFavoriteInput);

            foreach (var mode in Config.Modes)
            {
                var captured = mode;
                Register(mode.Hotkey, mode.Name, () => Modes.Apply(captured));
            }

            if (failed.Count > 0)
            {
                HudWindow.ShowHud("⚠️ Hotkey not registered:\n" + string.Join("\n", failed));
            }
        }

        private void ToggleBuilder()
        {
            if (_mainWindow == null) return;
            if (_mainWindow.IsVisible && _mainWindow.IsActive)
                _mainWindow.Hide();
            else
                ShowBuilder();
        }

        private void ShowBuilder()
        {
            _mainWindow?.ShowBuilder();
        }

        private void SwitchToFavoriteOutput()
        {
            string favoriteId = Config.FavoriteOutputDeviceId;
            if (string.IsNullOrEmpty(favoriteId))
            {
                HudWindow.ShowHud("⚠️ No Favorite Output Device configured.");
                return;
            }

            var devices = AudioEngine.GetDevices(playbackOnly: true);
            var favDevice = devices.Find(d => d.Id == favoriteId);

            if (favDevice != null)
            {
                if (!AudioEngine.SetDefaultDeviceAllRoles(favoriteId))
                {
                    HudWindow.ShowHud($"Couldn't switch output to favorite:\n{favDevice.Name}");
                    return;
                }

                HudWindow.ShowHud($"🎧 Output set to favorite:\n{favDevice.Name}");
                _mainWindow?.RefreshUI();
            }
            else
            {
                HudWindow.ShowHud("⚠️ Favorite Output Device not found/active.");
            }
        }

        private void SwitchToFavoriteInput()
        {
            string favoriteId = Config.FavoriteInputDeviceId;
            if (string.IsNullOrEmpty(favoriteId))
            {
                HudWindow.ShowHud("⚠️ No Favorite Mic configured.");
                return;
            }

            var devices = AudioEngine.GetDevices(playbackOnly: false);
            var favDevice = devices.Find(d => d.Id == favoriteId && !d.IsPlayback);

            if (favDevice != null)
            {
                if (!AudioEngine.SetDefaultDeviceAllRoles(favoriteId))
                {
                    HudWindow.ShowHud($"Couldn't switch mic to favorite:\n{favDevice.Name}");
                    return;
                }

                HudWindow.ShowHud($"🎙️ Mic set to favorite:\n{favDevice.Name}");
                _mainWindow?.RefreshUI();
            }
            else
            {
                HudWindow.ShowHud("⚠️ Favorite Mic not found/active.");
            }
        }

        public void SaveConfig()
        {
            if (_config != null)
            {
                ConfigManager.Save(_config);
            }
            UpdateTrayTooltip();
        }

        private IntPtr HwndMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            _hotkeyManager?.ProcessMessage(msg, wParam, ref handled);
            return IntPtr.Zero;
        }

        private void ExitApp()
        {
            IsExiting = true;
            _watchdog.Arm();
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            IsExiting = true;
            _watchdog.Arm();
            _sessionPoller?.Stop();
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

            // Restore Windows audio to the startup snapshot (true Exit only; hide-to-tray skips this).
            // Runs on a worker with a time limit: it is synchronous audio COM + SetDefaultDevice and must not hang Exit.
            try
            {
                var engine = _audioEngine;
                var snapshot = _startupSnapshot;
                if (engine != null && !TimeLimitedRunner.Shared.TryRun(() => engine.RestoreSnapshot(snapshot), TimeSpan.FromSeconds(3)))
                    Debug.WriteLine("Audio restore on exit timed out or failed; exiting anyway.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to restore audio on exit: {ex.Message}");
            }

            SaveConfig();
            _hotkeyManager?.UnregisterAll();

            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }

            if (_trayIconHandle != IntPtr.Zero)
            {
                DestroyIcon(_trayIconHandle);
            }

            _mainWindow?.Close();

            base.OnExit(e);
        }

        /// <summary>
        /// Obsidian tray menu: cyan dot for the active mode, light text, muted disabled items.
        /// </summary>
        private sealed class DarkMenuRenderer : WinForms.ToolStripProfessionalRenderer
        {
            private static readonly Color Text = Color.FromArgb(228, 230, 235);
            private static readonly Color Muted = Color.FromArgb(101, 103, 107);
            private static readonly Color Accent = Color.FromArgb(0, 240, 255);

            public DarkMenuRenderer() : base(new DarkMenuColors())
            {
                RoundedEdges = false;
            }

            protected override void OnRenderItemCheck(WinForms.ToolStripItemImageRenderEventArgs e)
            {
                var r = e.ImageRectangle;
                int d = 8;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var brush = new SolidBrush(Accent);
                e.Graphics.FillEllipse(brush, r.Left + (r.Width - d) / 2f, r.Top + (r.Height - d) / 2f, d, d);
            }

            protected override void OnRenderItemText(WinForms.ToolStripItemTextRenderEventArgs e)
            {
                if (e.Item is not WinForms.ToolStripLabel)
                    e.TextColor = e.Item.Enabled ? Text : Muted;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderArrow(WinForms.ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor = Text;
                base.OnRenderArrow(e);
            }
        }

        private sealed class DarkMenuColors : WinForms.ProfessionalColorTable
        {
            private static readonly Color Bg = Color.FromArgb(18, 21, 31);
            private static readonly Color Hover = Color.FromArgb(38, 42, 58);
            private static readonly Color Line = Color.FromArgb(40, 44, 58);
            private static readonly Color Accent = Color.FromArgb(0, 240, 255);

            public override Color ToolStripDropDownBackground => Bg;
            public override Color ImageMarginGradientBegin => Bg;
            public override Color ImageMarginGradientMiddle => Bg;
            public override Color ImageMarginGradientEnd => Bg;
            public override Color MenuBorder => Line;
            public override Color MenuItemBorder => Hover;
            public override Color MenuItemSelected => Hover;
            public override Color MenuItemSelectedGradientBegin => Hover;
            public override Color MenuItemSelectedGradientEnd => Hover;
            public override Color MenuItemPressedGradientBegin => Hover;
            public override Color MenuItemPressedGradientEnd => Hover;
            public override Color SeparatorDark => Line;
            public override Color SeparatorLight => Bg;
            public override Color CheckBackground => Color.FromArgb(0, 70, 80);
            public override Color CheckSelectedBackground => Color.FromArgb(0, 90, 100);
            public override Color CheckPressedBackground => Color.FromArgb(0, 90, 100);
            public override Color ButtonSelectedBorder => Accent;
        }
    }
}
