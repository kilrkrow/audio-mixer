using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Input;

namespace audio_mixer
{
    public partial class App : System.Windows.Application
    {
        private static Mutex? _mutex;
        private System.Windows.Forms.NotifyIcon? _trayIcon;
        private MainWindow? _mainWindow;
        private AudioEngine? _audioEngine;
        private AppConfig? _config;
        private HotkeyManager? _hotkeyManager;
        private System.Windows.Forms.ContextMenuStrip? _contextMenu;
        private IntPtr _trayIconHandle = IntPtr.Zero;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        public AudioEngine AudioEngine => _audioEngine ??= new AudioEngine();
        public AppConfig Config => _config ??= ConfigManager.Load();

        protected override void OnStartup(StartupEventArgs e)
        {
            // 1. Single Instance Check
            _mutex = new Mutex(true, "KilrKrowAudioMixerMutex", out bool createdNew);
            if (!createdNew)
            {
                System.Windows.MessageBox.Show(
                    "KilrKrow Audio Mixer is already running in the system tray.",
                    "Audio Mixer",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );
                Shutdown();
                return;
            }

            base.OnStartup(e);

            // 2. Initialize Engine & Config
            _audioEngine = new AudioEngine();
            _config = ConfigManager.Load();

            // 3. Create Windows
            _mainWindow = new MainWindow();
            
            // 4. Initialize Hotkeys
            var helper = new System.Windows.Interop.WindowInteropHelper(_mainWindow);
            // Ensure window handle is created so we can bind hotkeys to it
            helper.EnsureHandle();
            _hotkeyManager = new HotkeyManager(helper.Handle);
            
            // Set up HWND Hook on MainWindow to receive hotkeys
            var source = System.Windows.Interop.HwndSource.FromHwnd(helper.Handle);
            source?.AddHook(HwndMessageHook);

            RegisterGlobalHotkeys();

            // 5. Create Tray Icon
            InitializeTrayIcon();
        }

        private void InitializeTrayIcon()
        {
            _trayIcon = new System.Windows.Forms.NotifyIcon();
            
            // Programmatically draw tray icon
            try
            {
                using (var bitmap = new Bitmap(16, 16))
                using (var g = Graphics.FromImage(bitmap))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;

                    // Draw Speaker body
                    using (var path = new GraphicsPath())
                    {
                        path.AddLine(1, 5, 4, 5);
                        path.AddLine(4, 5, 7, 2);
                        path.AddLine(7, 2, 7, 14);
                        path.AddLine(7, 14, 4, 11);
                        path.AddLine(4, 11, 1, 11);
                        path.CloseFigure();
                        
                        using (var brush = new SolidBrush(Color.FromArgb(235, 235, 245)))
                        {
                            g.FillPath(brush, path);
                        }
                    }

                    // Draw Soundwaves
                    using (var pen = new Pen(Color.FromArgb(235, 235, 245), 1.5f))
                    {
                        g.DrawArc(pen, 8, 5, 6, 6, -60, 120);
                        g.DrawArc(pen, 6, 2, 10, 12, -60, 120);
                    }

                    _trayIconHandle = bitmap.GetHicon();
                    _trayIcon.Icon = Icon.FromHandle(_trayIconHandle);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to create custom tray icon: {ex.Message}");
                _trayIcon.Icon = SystemIcons.Application;
            }

            _trayIcon.Text = "KilrKrow Audio Mixer";
            _trayIcon.Visible = true;

            // Context Menu
            _contextMenu = new System.Windows.Forms.ContextMenuStrip();
            _contextMenu.Items.Add("Open Mixer", null, (s, e) => ShowMixerWindow());
            _contextMenu.Items.Add("Reset App Volume Levels", null, (s, e) => ResetVolumeLevels());
            _contextMenu.Items.Add("-");
            _contextMenu.Items.Add("Exit", null, (s, e) => ExitApp());
            _trayIcon.ContextMenuStrip = _contextMenu;

            // Click listener
            _trayIcon.MouseClick += (s, e) =>
            {
                if (e.Button == System.Windows.Forms.MouseButtons.Left)
                {
                    ToggleMixerWindow();
                }
            };
        }

        public void RegisterGlobalHotkeys()
        {
            if (_hotkeyManager == null) return;

            _hotkeyManager.UnregisterAll();

            // 1. Toggle Mixer visibility
            _hotkeyManager.Register(
                _config!.ToggleMixerHotkey.Modifiers,
                _config.ToggleMixerHotkey.Key,
                ToggleMixerWindow
            );

            // 2. Switch Default Output to Favorite
            _hotkeyManager.Register(
                _config.FavoriteOutputHotkey.Modifiers,
                _config.FavoriteOutputHotkey.Key,
                SwitchToFavoriteOutput
            );

            // 3. Switch Default Input to Favorite
            _hotkeyManager.Register(
                _config.FavoriteInputHotkey.Modifiers,
                _config.FavoriteInputHotkey.Key,
                SwitchToFavoriteInput
            );

            // 4. Reset App volumes to Presets
            _hotkeyManager.Register(
                _config.ResetLevelsHotkey.Modifiers,
                _config.ResetLevelsHotkey.Key,
                ResetVolumeLevels
            );
        }

        private void ToggleMixerWindow()
        {
            if (_mainWindow == null) return;
            if (_mainWindow.IsVisible)
            {
                _mainWindow.HideMixer();
            }
            else
            {
                _mainWindow.ShowMixer();
            }
        }

        private void ShowMixerWindow()
        {
            _mainWindow?.ShowMixer();
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
                AudioEngine.SetDefaultDevice(favoriteId, ERole.Multimedia);
                AudioEngine.SetDefaultDevice(favoriteId, ERole.Console);
                AudioEngine.SetDefaultDevice(favoriteId, ERole.Communications);

                // Show notification HUD
                HudWindow.ShowHud($"🎧 Output set to favorite:\n{favDevice.Name}");
                
                // Refresh list if window is open
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
                AudioEngine.SetDefaultDevice(favoriteId, ERole.Multimedia);
                AudioEngine.SetDefaultDevice(favoriteId, ERole.Console);
                AudioEngine.SetDefaultDevice(favoriteId, ERole.Communications);

                // Show notification HUD
                HudWindow.ShowHud($"🎙️ Mic set to favorite:\n{favDevice.Name}");
                
                // Refresh list if window is open
                _mainWindow?.RefreshUI();
            }
            else
            {
                HudWindow.ShowHud("⚠️ Favorite Mic not found/active.");
            }
        }

        private void ResetVolumeLevels()
        {
            var presets = Config.AppVolumePresets;
            if (presets == null || presets.Count == 0)
            {
                HudWindow.ShowHud("⚠️ No Application Level presets configured.");
                return;
            }

            var sessions = AudioEngine.GetSessions();
            int count = 0;

            foreach (var s in sessions)
            {
                if (presets.TryGetValue(s.ProcessName, out float presetVolume))
                {
                    AudioEngine.SetSessionVolume(s.ProcessId, presetVolume);
                    count++;
                }
            }

            HudWindow.ShowHud($"🎛️ Audio Levels Reset\nApplied presets to {count} active apps.");
            _mainWindow?.RefreshUI();
        }

        public void SaveConfig()
        {
            if (_config != null)
            {
                ConfigManager.Save(_config);
            }
        }

        private IntPtr HwndMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            _hotkeyManager?.ProcessMessage(msg, wParam, ref handled);
            return IntPtr.Zero;
        }

        private void ExitApp()
        {
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Save config
            SaveConfig();

            // Unregister Hotkeys
            _hotkeyManager?.UnregisterAll();

            // Dispose Tray Icon
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }

            if (_trayIconHandle != IntPtr.Zero)
            {
                DestroyIcon(_trayIconHandle);
            }

            // Dispose Windows
            _mainWindow?.Close();

            base.OnExit(e);
        }
    }
}
