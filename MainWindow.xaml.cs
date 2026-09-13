using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace audio_mixer
{
    public partial class MainWindow : Window
    {
        // DWM Windows 11 Acrylic API P/Invoke
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int attrSize);
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

        private readonly AudioEngine _engine;
        private readonly AppConfig _config;
        private readonly DispatcherTimer _refreshTimer;
        private bool _isNavigatingToSettings = false;
        private bool _isClosing = false;
        private double _targetTop = 0;

        public MainWindow()
        {
            InitializeComponent();
            
            var app = (App)Application.Current;
            _engine = app.AudioEngine;
            _config = app.Config;

            // Polling timer to refresh sessions smoothly
            _refreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1200)
            };
            _refreshTimer.Tick += RefreshTimer_Tick;

            Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyAcrylicBlur();
            RefreshDevices();
            RefreshPresetsUI();
        }

        private void ApplyAcrylicBlur()
        {
            try
            {
                var helper = new System.Windows.Interop.WindowInteropHelper(this);
                IntPtr hwnd = helper.Handle;

                // Value 3 = Acrylic blur on Windows 11
                int backdropType = 3;
                DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to set Acrylic blur: {ex.Message}");
                // Fallback is handled in XAML (solid dark brush with slight opacity)
            }
        }

        public void ShowMixer()
        {
            _isClosing = false;
            
            // Refresh content before displaying
            RefreshDevices();
            RefreshAppSessionsList();
            RefreshPresetsUI();

            // Position next to tray
            PositionWindowNearTray();

            // Slide Up & Fade In
            var slideUp = (Storyboard)Resources["SlideUp"];
            var doubleAnim = (DoubleAnimation)slideUp.Children[1];
            doubleAnim.From = _targetTop + 35;
            doubleAnim.To = _targetTop;

            this.Opacity = 0;
            this.Top = _targetTop + 35;
            this.Show();
            this.Activate();

            slideUp.Begin(this);

            // Start polling
            _refreshTimer.Start();
        }

        public void HideMixer()
        {
            if (_isClosing) return;
            _isClosing = true;

            _refreshTimer.Stop();

            // Slide Down & Fade Out
            var slideDown = (Storyboard)Resources["SlideDown"];
            var doubleAnim = (DoubleAnimation)slideDown.Children[1];
            doubleAnim.From = this.Top;
            doubleAnim.To = this.Top + 35;

            slideDown.Completed += (s, e) =>
            {
                if (_isClosing) // Ensure another show request hasn't canceled this
                {
                    this.Hide();
                    _isClosing = false;
                }
            };

            slideDown.Begin(this);
        }

        private void PositionWindowNearTray()
        {
            var screen = System.Windows.Forms.Screen.PrimaryScreen;
            if (screen != null)
            {
                var workingArea = screen.WorkingArea;
                // Width = 380, Height = 540 (including Grid margins)
                Left = workingArea.Right - Width - 6;
                _targetTop = workingArea.Bottom - Height - 6;
            }
        }

        private void Window_Deactivated(object sender, EventArgs e)
        {
            // Auto hide mixer when user clicks outside, matching system tray behavior
            HideMixer();
        }

        private void RefreshTimer_Tick(object? sender, EventArgs e)
        {
            RefreshAppSessionsList();
        }

        public void RefreshUI()
        {
            Dispatcher.Invoke(() =>
            {
                RefreshDevices();
                RefreshAppSessionsList();
                RefreshPresetsUI();
            });
        }

        #region Mixer Tab Logic

        private bool _updatingDeviceCombos = false;

        private void RefreshDevices()
        {
            _updatingDeviceCombos = true;

            try
            {
                var devices = _engine.GetDevices();
                var playDevices = new List<AudioDevice>();
                var recDevices = new List<AudioDevice>();

                foreach (var d in devices)
                {
                    if (d.IsPlayback) playDevices.Add(d);
                    else recDevices.Add(d);
                }

                // Playback
                ComboPlayback.ItemsSource = playDevices;
                ComboPlayback.DisplayMemberPath = "Name";
                ComboPlayback.SelectedValuePath = "Id";

                var activePlayDefault = playDevices.Find(d => d.IsDefault);
                if (activePlayDefault != null)
                {
                    ComboPlayback.SelectedValue = activePlayDefault.Id;
                    CheckFavPlayback.IsChecked = (activePlayDefault.Id == _config.FavoriteOutputDeviceId);
                }

                // Recording
                ComboRecording.ItemsSource = recDevices;
                ComboRecording.DisplayMemberPath = "Name";
                ComboRecording.SelectedValuePath = "Id";

                var activeRecDefault = recDevices.Find(d => d.IsDefault);
                if (activeRecDefault != null)
                {
                    ComboRecording.SelectedValue = activeRecDefault.Id;
                    CheckFavRecording.IsChecked = (activeRecDefault.Id == _config.FavoriteInputDeviceId);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to refresh devices: {ex.Message}");
            }
            finally
            {
                _updatingDeviceCombos = false;
            }
        }

        private void ComboPlayback_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingDeviceCombos) return;
            if (ComboPlayback.SelectedValue is string devId)
            {
                _engine.SetDefaultDevice(devId, ERole.Multimedia);
                _engine.SetDefaultDevice(devId, ERole.Console);
                CheckFavPlayback.IsChecked = (devId == _config.FavoriteOutputDeviceId);
            }
        }

        private void ComboRecording_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingDeviceCombos) return;
            if (ComboRecording.SelectedValue is string devId)
            {
                _engine.SetDefaultDevice(devId, ERole.Multimedia);
                _engine.SetDefaultDevice(devId, ERole.Communications);
                CheckFavRecording.IsChecked = (devId == _config.FavoriteInputDeviceId);
            }
        }

        private void CheckFavPlayback_Changed(object sender, RoutedEventArgs e)
        {
            if (_updatingDeviceCombos) return;
            if (ComboPlayback.SelectedValue is string devId)
            {
                if (CheckFavPlayback.IsChecked == true)
                {
                    _config.FavoriteOutputDeviceId = devId;
                }
                else if (_config.FavoriteOutputDeviceId == devId)
                {
                    _config.FavoriteOutputDeviceId = string.Empty;
                }
                ((App)Application.Current).SaveConfig();
                RefreshPresetsUI();
            }
        }

        private void CheckFavRecording_Changed(object sender, RoutedEventArgs e)
        {
            if (_updatingDeviceCombos) return;
            if (ComboRecording.SelectedValue is string devId)
            {
                if (CheckFavRecording.IsChecked == true)
                {
                    _config.FavoriteInputDeviceId = devId;
                }
                else if (_config.FavoriteInputDeviceId == devId)
                {
                    _config.FavoriteInputDeviceId = string.Empty;
                }
                ((App)Application.Current).SaveConfig();
                RefreshPresetsUI();
            }
        }

        private async void RefreshAppSessionsList()
        {
            try
            {
                // Enumerate sessions on MTA background thread to prevent STA omissions
                var sessions = await System.Threading.Tasks.Task.Run(() => _engine.GetSessions());

                Dispatcher.Invoke(() =>
                {
                    var activeIds = new HashSet<uint>();

                    foreach (var s in sessions)
                    {
                        activeIds.Add(s.ProcessId);
                        UpdateOrCreateSessionRow(s);
                    }

                    // Remove rows for sessions that ended
                    for (int i = ListAppSessions.Children.Count - 1; i >= 0; i--)
                    {
                        var child = ListAppSessions.Children[i] as FrameworkElement;
                        if (child != null && child.Tag is uint pid && !activeIds.Contains(pid))
                        {
                            ListAppSessions.Children.RemoveAt(i);
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to refresh app sessions: {ex.Message}");
            }
        }

        private void UpdateOrCreateSessionRow(AudioSession s)
        {
            // Find existing
            FrameworkElement? row = null;
            foreach (FrameworkElement child in ListAppSessions.Children)
            {
                if (child.Tag is uint pid && pid == s.ProcessId)
                {
                    row = child;
                    break;
                }
            }

            if (row != null)
            {
                // Update volume and mute slider states if NOT currently captured by user drag
                var slider = FindVisualChild<Slider>(row);
                if (slider != null && !slider.IsMouseCaptureWithin)
                {
                    slider.Value = s.Volume * 100;
                }

                var muteBtn = FindVisualChild<Button>(row);
                if (muteBtn != null)
                {
                    var path = FindVisualChild<System.Windows.Shapes.Path>(muteBtn);
                    if (path != null)
                    {
                        path.Data = s.IsMuted 
                            ? (Geometry)this.TryFindResource("IconMute") 
                            : (Geometry)this.TryFindResource("IconVolume");
                        path.Fill = s.IsMuted 
                            ? (SolidColorBrush)this.TryFindResource("BrushNeonMagenta") 
                            : (SolidColorBrush)this.TryFindResource("BrushTextSecondary");
                    }
                }
                
                var percentText = FindVisualChildByName<TextBlock>(row, "TxtPercent");
                if (percentText != null)
                {
                    percentText.Text = $"{(int)(s.Volume * 100)}%";
                }
            }
            else
            {
                // Create custom row
                var border = new Border
                {
                    Background = (SolidColorBrush)this.TryFindResource("BrushCardBackground"),
                    BorderBrush = (SolidColorBrush)this.TryFindResource("BrushBorderGlass"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 0, 0, 8),
                    Tag = s.ProcessId
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) }); // Icon
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Name + Slider
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(35) }); // Percent
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) }); // Mute Btn

                // App Icon
                var img = new Image
                {
                    Width = 20,
                    Height = 20,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center
                };
                
                var iconSource = BitmapImageFromBase64(s.IconBase64);
                if (iconSource != null)
                {
                    img.Source = iconSource;
                }
                else
                {
                    // Fallback default volume wave path
                    var fallbackPath = new System.Windows.Shapes.Path
                    {
                        Data = (Geometry)this.TryFindResource("IconVolume"),
                        Fill = (SolidColorBrush)this.TryFindResource("BrushTextMuted"),
                        Width = 14,
                        Height = 14,
                        Stretch = Stretch.Uniform
                    };
                    var fallbackGrid = new Grid();
                    fallbackGrid.Children.Add(fallbackPath);
                    Grid.SetColumn(fallbackGrid, 0);
                    grid.Children.Add(fallbackGrid);
                }

                if (img.Source != null)
                {
                    Grid.SetColumn(img, 0);
                    grid.Children.Add(img);
                }

                // Text Name + Slider Panel
                var stack = new StackPanel { Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = System.Windows.VerticalAlignment.Center };
                
                var nameText = new TextBlock
                {
                    Text = s.DisplayName,
                    Foreground = (SolidColorBrush)this.TryFindResource("BrushTextPrimary"),
                    FontSize = 11,
                    FontFamily = new System.Windows.Media.FontFamily("Segoe UI Semibold, Inter"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 0, 0, 2)
                };
                
                var slider = new Slider
                {
                    Style = (Style)this.TryFindResource("GlassSlider"),
                    Minimum = 0,
                    Maximum = 100,
                    Value = s.Volume * 100,
                    Tag = s.ProcessId
                };
                
                // Volume Percentage Text
                var percentText = new TextBlock
                {
                    Name = "TxtPercent",
                    Text = $"{(int)(s.Volume * 100)}%",
                    Foreground = (SolidColorBrush)this.TryFindResource("BrushTextSecondary"),
                    FontSize = 10,
                    FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Inter"),
                    VerticalAlignment = System.Windows.VerticalAlignment.Center,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Right
                };

                slider.ValueChanged += (sender, args) =>
                {
                    _engine.SetSessionVolume(s.ProcessId, (float)(slider.Value / 100.0));
                    percentText.Text = $"{(int)slider.Value}%";
                };

                stack.Children.Add(nameText);
                stack.Children.Add(slider);
                Grid.SetColumn(stack, 1);
                grid.Children.Add(stack);

                Grid.SetColumn(percentText, 2);
                grid.Children.Add(percentText);

                // Mute Button
                var mutePath = new System.Windows.Shapes.Path
                {
                    Data = s.IsMuted 
                        ? (Geometry)this.TryFindResource("IconMute") 
                        : (Geometry)this.TryFindResource("IconVolume"),
                    Fill = s.IsMuted 
                        ? (SolidColorBrush)this.TryFindResource("BrushNeonMagenta") 
                        : (SolidColorBrush)this.TryFindResource("BrushTextSecondary"),
                    Width = 14,
                    Height = 14,
                    Stretch = Stretch.Uniform
                };

                var muteBtn = new Button
                {
                    Style = (Style)this.TryFindResource("GlassIconButton"),
                    Content = mutePath,
                    Tag = s
                };

                muteBtn.Click += (sender, args) =>
                {
                    bool newMute = !s.IsMuted;
                    s.IsMuted = newMute;
                    _engine.SetSessionMute(s.ProcessId, newMute);
                    mutePath.Data = newMute 
                        ? (Geometry)Resources["IconMute"] 
                        : (Geometry)Resources["IconVolume"];
                    mutePath.Fill = newMute 
                        ? (SolidColorBrush)Resources["BrushNeonMagenta"] 
                        : (SolidColorBrush)Resources["BrushTextSecondary"];
                };

                Grid.SetColumn(muteBtn, 3);
                grid.Children.Add(muteBtn);

                border.Child = grid;
                ListAppSessions.Children.Add(border);
            }
        }

        #endregion

        #region Settings Tab Logic

        private void RefreshPresetsUI()
        {
            // Favorite device display
            var devices = _engine.GetDevices();
            var favOut = devices.Find(d => d.Id == _config.FavoriteOutputDeviceId);
            var favIn = devices.Find(d => d.Id == _config.FavoriteInputDeviceId && !d.IsPlayback);

            TxtFavPlaybackName.Text = favOut != null ? favOut.Name : "None Selected";
            TxtFavRecordingName.Text = favIn != null ? favIn.Name : "None Selected";

            // Bind hotkey configurations
            TxtHotkeyToggleMixer.Text = _config.ToggleMixerHotkey.ToString();
            TxtHotkeyFavOutput.Text = _config.FavoriteOutputHotkey.ToString();
            TxtHotkeyFavInput.Text = _config.FavoriteInputHotkey.ToString();
            TxtHotkeyResetLevels.Text = _config.ResetLevelsHotkey.ToString();

            // Active sessions dropdown to add presets
            var activeSessions = _engine.GetSessions();
            var comboItems = new List<string>();
            foreach (var s in activeSessions)
            {
                if (s.ProcessName != "System Sounds" && 
                    !string.IsNullOrEmpty(s.ProcessName) && 
                    !_config.AppVolumePresets.ContainsKey(s.ProcessName) && 
                    !comboItems.Contains(s.ProcessName))
                {
                    comboItems.Add(s.ProcessName);
                }
            }
            ComboAddPreset.ItemsSource = comboItems;
            if (comboItems.Count > 0)
                ComboAddPreset.SelectedIndex = 0;

            // App Presets container list
            ListPresetsContainer.Children.Clear();
            foreach (var preset in _config.AppVolumePresets)
            {
                var border = new Border
                {
                    Background = (SolidColorBrush)this.TryFindResource("BrushCardBackground"),
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(8),
                    Margin = new Thickness(0, 0, 0, 6)
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) }); // App Name
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Preset Volume
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) }); // Delete

                var appText = new TextBlock
                {
                    Text = preset.Key,
                    Foreground = (SolidColorBrush)this.TryFindResource("BrushTextPrimary"),
                    FontSize = 11,
                    FontFamily = new System.Windows.Media.FontFamily("Segoe UI Semibold, Inter"),
                    VerticalAlignment = System.Windows.VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };

                var slider = new Slider
                {
                    Style = (Style)this.TryFindResource("GlassSlider"),
                    Minimum = 0,
                    Maximum = 100,
                    Value = preset.Value * 100,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 6, 0)
                };

                slider.ValueChanged += (s, e) =>
                {
                    _config.AppVolumePresets[preset.Key] = (float)(slider.Value / 100.0);
                    ((App)Application.Current).SaveConfig();
                };

                var deleteBtn = new Button
                {
                    Style = (Style)this.TryFindResource("GlassIconButton"),
                    Content = new System.Windows.Shapes.Path
                    {
                        Data = (Geometry)this.TryFindResource("IconClose"),
                        Fill = (SolidColorBrush)this.TryFindResource("BrushTextSecondary"),
                        Width = 10,
                        Height = 10,
                        Stretch = Stretch.Uniform
                    }
                };

                deleteBtn.Click += (s, e) =>
                {
                    _config.AppVolumePresets.Remove(preset.Key);
                    ((App)Application.Current).SaveConfig();
                    RefreshPresetsUI();
                };

                Grid.SetColumn(appText, 0);
                grid.Children.Add(appText);
                Grid.SetColumn(slider, 1);
                grid.Children.Add(slider);
                Grid.SetColumn(deleteBtn, 2);
                grid.Children.Add(deleteBtn);

                border.Child = grid;
                ListPresetsContainer.Children.Add(border);
            }
        }

        private void BtnAddPreset_Click(object sender, RoutedEventArgs e)
        {
            if (ComboAddPreset.SelectedItem is string processName)
            {
                _config.AppVolumePresets[processName] = 0.8f; // Default preset 80%
                ((App)Application.Current).SaveConfig();
                RefreshPresetsUI();
            }
        }

        private void HotkeyTextBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // Intercept standard key events
            e.Handled = true;

            var textBox = sender as TextBox;
            if (textBox == null) return;

            // Ignore bare modifier presses (Ctrl, Alt, Shift, Win alone)
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.LeftCtrl || key == Key.RightCtrl ||
                key == Key.LeftAlt || key == Key.RightAlt ||
                key == Key.LeftShift || key == Key.RightShift ||
                key == Key.LWin || key == Key.RWin)
            {
                return;
            }

            // Must press at least one modifier
            var modifiers = Keyboard.Modifiers;
            if (modifiers == ModifierKeys.None)
            {
                return;
            }

            var newHotkey = new HotkeyConfig
            {
                Modifiers = modifiers,
                Key = key
            };

            textBox.Text = newHotkey.ToString();

            // Save to correct config field
            if (textBox == TxtHotkeyToggleMixer) _config.ToggleMixerHotkey = newHotkey;
            else if (textBox == TxtHotkeyFavOutput) _config.FavoriteOutputHotkey = newHotkey;
            else if (textBox == TxtHotkeyFavInput) _config.FavoriteInputHotkey = newHotkey;
            else if (textBox == TxtHotkeyResetLevels) _config.ResetLevelsHotkey = newHotkey;

            var app = (App)Application.Current;
            app.SaveConfig();
            app.RegisterGlobalHotkeys();
        }

        #endregion

        #region Navigation & Helpers

        private void BtnNav_Click(object sender, RoutedEventArgs e)
        {
            _isNavigatingToSettings = !_isNavigatingToSettings;

            if (_isNavigatingToSettings)
            {
                // Switch to settings
                PanelMixer.Visibility = Visibility.Collapsed;
                PanelSettings.Visibility = Visibility.Visible;
                PathNav.Data = (Geometry)this.TryFindResource("IconFavorite"); // Switch gear icon to favorite star to act as "back" icon
                RefreshPresetsUI();
            }
            else
            {
                // Switch back to mixer
                PanelMixer.Visibility = Visibility.Visible;
                PanelSettings.Visibility = Visibility.Collapsed;
                PathNav.Data = (Geometry)this.TryFindResource("IconSettings");
                RefreshDevices();
                RefreshAppSessionsList();
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            HideMixer();
        }

        // Helper to convert base64 to BitmapImage
        private static BitmapImage? BitmapImageFromBase64(string base64)
        {
            if (string.IsNullOrEmpty(base64)) return null;
            try
            {
                if (base64.StartsWith("data:image"))
                {
                    int index = base64.IndexOf("base64,");
                    if (index >= 0)
                    {
                        base64 = base64.Substring(index + 7);
                    }
                }
                byte[] binaryData = Convert.FromBase64String(base64);
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.StreamSource = new MemoryStream(binaryData);
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            catch
            {
                return null;
            }
        }

        // Visual helper to find nested typed children (like slider inside Row Grid)
        private static T? FindVisualChild<T>(DependencyObject obj) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
            {
                var child = VisualTreeHelper.GetChild(obj, i);
                if (child is T t) return t;
                
                var childOfChild = FindVisualChild<T>(child);
                if (childOfChild != null) return childOfChild;
            }
            return null;
        }

        private static T? FindVisualChildByName<T>(DependencyObject obj, string name) where T : FrameworkElement
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
            {
                var child = VisualTreeHelper.GetChild(obj, i);
                if (child is T t && t.Name == name) return t;
                
                var childOfChild = FindVisualChildByName<T>(child, name);
                if (childOfChild != null) return childOfChild;
            }
            return null;
        }

        #endregion
    }
}