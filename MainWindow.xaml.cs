using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace audio_mixer
{
    /// <summary>
    /// The mode builder: a normal window for creating modes and sources.
    /// Day-to-day switching happens from the tray, not here.
    /// </summary>
    public partial class MainWindow : Window
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int attrSize);
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_CAPTION_COLOR = 35;

        private readonly AudioEngine _engine;
        private readonly AppConfig _config;
        private readonly ModeService _modes;
        private readonly DispatcherTimer _saveTimer;
        private readonly DispatcherTimer _liveApplyTimer;
        private readonly DispatcherTimer _playingTimer;

        private bool _loading;
        private string? _selectedModeId;
        private string _playingKey = string.Empty;

        private static App AppRef => (App)Application.Current;
        private MixMode? SelectedMode => _config.Modes.Find(m => m.Id == _selectedModeId);

        public MainWindow()
        {
            InitializeComponent();

            _engine = AppRef.AudioEngine;
            _config = AppRef.Config;
            _modes = AppRef.Modes;

            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _saveTimer.Tick += (s, e) => FlushSave();

            // While editing the active mode, re-apply shortly after the user stops dragging
            _liveApplyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _liveApplyTimer.Tick += (s, e) =>
            {
                _liveApplyTimer.Stop();
                var mode = SelectedMode;
                if (mode != null && mode.Id == _config.ActiveModeId)
                    _modes.Apply(mode, showHud: false);
            };

            _playingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            _playingTimer.Tick += (s, e) => RefreshPlaying();

            SourceInitialized += (s, e) => ApplyDarkTitleBar();
            IsVisibleChanged += (s, e) => UpdatePlayingTimer();
        }

        private void ApplyDarkTitleBar()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int dark = 1;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
                int caption = 0x00170F0C; // COLORREF (BGR) of #0c0f17
                DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to set dark title bar: {ex.Message}");
            }
        }

        public void ShowBuilder()
        {
            RefreshAll();
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            FlushSave();
            if (!AppRef.IsExiting)
            {
                // Closing the builder keeps the app alive in the tray
                e.Cancel = true;
                Hide();
            }
            base.OnClosing(e);
        }

        public void RefreshUI()
        {
            Dispatcher.Invoke(() =>
            {
                if (IsVisible) RefreshAll();
            });
        }

        public void OnActiveModeChanged()
        {
            RefreshActiveBadge();
            RefreshModeList();
            UpdateLiveBanner();
        }

        private void RefreshAll()
        {
            RefreshActiveBadge();
            RefreshModeList();
            RefreshModeEditor();
            RefreshSources();
            RefreshPlaying(force: true);
            RefreshSettings();
        }

        private void RefreshActiveBadge()
        {
            var active = _config.ActiveMode;
            TxtActiveMode.Text = active != null ? $"Active: {active.Name}" : "No mode active";
            TxtActiveMode.Foreground = Brush(active != null ? "BrushTextPrimary" : "BrushTextSecondary");
            ActiveDot.Fill = Brush(active != null ? "BrushNeonCyan" : "BrushTextMuted");
        }

        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (PanelModes == null || PanelSources == null || PanelSettings == null) return; // during InitializeComponent

            PanelModes.Visibility = TabModes.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PanelSources.Visibility = TabSources.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PanelSettings.Visibility = TabSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

            if (TabModes.IsChecked == true) RefreshModeEditor(); // sources may have been renamed/added
            if (TabSources.IsChecked == true)
            {
                RefreshSources();
                RefreshPlaying(force: true);
            }
            if (TabSettings.IsChecked == true) RefreshSettings();

            UpdatePlayingTimer();
        }

        private void UpdatePlayingTimer()
        {
            if (IsVisible && TabSources.IsChecked == true) _playingTimer.Start();
            else _playingTimer.Stop();
        }

        #region Saving

        private void ScheduleSave()
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void FlushSave()
        {
            if (!_saveTimer.IsEnabled) return;
            _saveTimer.Stop();
            AppRef.SaveConfig();
        }

        /// <summary>
        /// Something in the selected mode changed: persist it, and if it's the active mode, let the user hear it.
        /// </summary>
        private void ModeEdited()
        {
            ScheduleSave();
            var mode = SelectedMode;
            if (mode != null && mode.Id == _config.ActiveModeId)
            {
                _liveApplyTimer.Stop();
                _liveApplyTimer.Start();
            }
        }

        #endregion

        #region Modes tab

        private void RefreshModeList()
        {
            _loading = true;
            try
            {
                if (SelectedMode == null)
                    _selectedModeId = _config.ActiveMode?.Id ?? _config.Modes.FirstOrDefault()?.Id;

                ModeList.Items.Clear();
                foreach (var mode in _config.Modes)
                {
                    bool isActive = mode.Id == _config.ActiveModeId;
                    var grid = new Grid();
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var dot = new System.Windows.Shapes.Ellipse
                    {
                        Width = 7,
                        Height = 7,
                        Margin = new Thickness(0, 0, 8, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Fill = isActive ? Brush("BrushNeonCyan") : Brushes.Transparent,
                        ToolTip = isActive ? "Active mode" : null
                    };
                    grid.Children.Add(dot);

                    var name = new TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(mode.Name) ? "(unnamed)" : mode.Name,
                        FontSize = 13,
                        FontFamily = new FontFamily("Segoe UI Semibold, Inter"),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(name, 1);
                    grid.Children.Add(name);

                    if (mode.Hotkey is { IsEmpty: false })
                    {
                        var hk = new TextBlock
                        {
                            Text = mode.Hotkey.ToString(),
                            FontSize = 10,
                            Foreground = Brush("BrushTextMuted"),
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(8, 0, 0, 0)
                        };
                        Grid.SetColumn(hk, 2);
                        grid.Children.Add(hk);
                    }

                    var item = new ListBoxItem { Content = grid, Tag = mode.Id };
                    ModeList.Items.Add(item);
                    if (mode.Id == _selectedModeId) item.IsSelected = true;
                }
            }
            finally
            {
                _loading = false;
            }
        }

        private void ModeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            if (ModeList.SelectedItem is ListBoxItem { Tag: string id })
            {
                FlushSave();
                _selectedModeId = id;
                RefreshModeEditor();
            }
        }

        private void UpdateLiveBanner()
        {
            var mode = SelectedMode;
            bool live = mode != null && mode.Id == _config.ActiveModeId;
            LiveBanner.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
            BtnApplyMode.Content = live ? "Re-apply" : "Apply now";
        }

        private void RefreshModeEditor()
        {
            _loading = true;
            try
            {
                var mode = SelectedMode;
                bool has = mode != null;
                TxtNoMode.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
                ModeEditor.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
                BtnDuplicateMode.IsEnabled = has;
                BtnDeleteMode.IsEnabled = has;
                if (mode == null) return;

                TxtModeName.Text = mode.Name;
                TxtModeHotkey.Text = mode.Hotkey?.ToString() ?? string.Empty;
                UpdateLiveBanner();

                LevelRows.Children.Clear();
                foreach (var group in _config.Sources)
                {
                    var parts = group.PathContains.Select(f => $"anything in {f}").Concat(group.ProcessNames).ToList();
                    string subtitle = parts.Count == 0
                        ? "No apps yet — add some on the Sources tab"
                        : string.Join(", ", parts.Take(4)) + (parts.Count > 4 ? $" +{parts.Count - 4}" : string.Empty);
                    LevelRows.Children.Add(BuildSourceLevelRow(mode, group.Id, group.Name, subtitle));
                }
                LevelRows.Children.Add(BuildSourceLevelRow(mode, BuiltInSources.System, BuiltInSources.NameOf(BuiltInSources.System), "Windows notification sounds"));
                LevelRows.Children.Add(BuildSourceLevelRow(mode, BuiltInSources.Other, BuiltInSources.NameOf(BuiltInSources.Other), "Any app not in a source"));

                SystemRows.Children.Clear();
                SystemRows.Children.Add(BuildMasterRow(mode));
                SystemRows.Children.Add(BuildMicRow(mode));
                SystemRows.Children.Add(BuildOutputRow(mode));
            }
            finally
            {
                _loading = false;
            }
        }

        private FrameworkElement BuildSourceLevelRow(MixMode mode, string sourceId, string title, string subtitle)
        {
            var level = mode.LevelFor(sourceId);
            return BuildLevelRow(title, subtitle,
                enabled: level != null,
                volume: level?.Volume ?? 1.0f,
                muted: level?.Mute ?? false,
                onEnabled: (on, vol, mute) =>
                {
                    mode.Levels.RemoveAll(l => l.SourceId == sourceId);
                    if (on) mode.Levels.Add(new SourceLevel { SourceId = sourceId, Volume = vol, Mute = mute });
                    ModeEdited();
                },
                onVolume: vol =>
                {
                    if (mode.LevelFor(sourceId) is { } l) { l.Volume = vol; ModeEdited(); }
                },
                onMute: mute =>
                {
                    if (mode.LevelFor(sourceId) is { } l) { l.Mute = mute; ModeEdited(); }
                });
        }

        private FrameworkElement BuildMasterRow(MixMode mode)
        {
            return BuildLevelRow("Master volume", "Overall level of the output device",
                enabled: mode.MasterVolume != null,
                volume: mode.MasterVolume ?? _engine.GetMasterVolume() ?? 1.0f,
                muted: null,
                onEnabled: (on, vol, _) => { mode.MasterVolume = on ? vol : null; ModeEdited(); },
                onVolume: vol => { if (mode.MasterVolume != null) { mode.MasterVolume = vol; ModeEdited(); } },
                onMute: null);
        }

        private FrameworkElement BuildMicRow(MixMode mode)
        {
            var combo = new ComboBox { Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
            combo.Items.Add("Muted");
            combo.Items.Add("Live");
            combo.SelectedIndex = mode.MicMuted == false ? 1 : 0;
            combo.SelectionChanged += (s, e) =>
            {
                if (_loading || mode.MicMuted == null) return;
                mode.MicMuted = combo.SelectedIndex == 0;
                ModeEdited();
            };

            return BuildChoiceRow("Microphone", "Mute or unmute the default mic", mode.MicMuted != null, combo,
                on => { mode.MicMuted = on ? combo.SelectedIndex == 0 : null; ModeEdited(); });
        }

        private FrameworkElement BuildOutputRow(MixMode mode)
        {
            var devices = _engine.GetDevices(playbackOnly: true);
            if (!string.IsNullOrEmpty(mode.OutputDeviceId) && devices.All(d => d.Id != mode.OutputDeviceId))
                devices.Add(new AudioDevice { Id = mode.OutputDeviceId, Name = "(disconnected device)", IsPlayback = true });

            var combo = new ComboBox
            {
                ItemsSource = devices,
                DisplayMemberPath = "Name",
                SelectedValuePath = "Id",
                MinWidth = 220,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            combo.SelectedValue = mode.OutputDeviceId ?? devices.Find(d => d.IsDefault)?.Id;
            combo.SelectionChanged += (s, e) =>
            {
                if (_loading || mode.OutputDeviceId == null) return;
                mode.OutputDeviceId = combo.SelectedValue as string;
                ModeEdited();
            };

            return BuildChoiceRow("Output device", "Switch the default playback device", mode.OutputDeviceId != null, combo,
                on => { mode.OutputDeviceId = on ? combo.SelectedValue as string : null; ModeEdited(); });
        }

        /// <summary>
        /// [toggle] Title/subtitle [slider] [%] [mute]. Controls dim when the toggle is off.
        /// </summary>
        private FrameworkElement BuildLevelRow(string title, string subtitle, bool enabled, float volume, bool? muted,
            Action<bool, float, bool> onEnabled, Action<float> onVolume, Action<bool>? onMute)
        {
            var grid = NewRowGrid();

            var toggle = new CheckBox { Style = (Style)FindResource("GlassToggle"), IsChecked = enabled };
            grid.Children.Add(toggle);

            var label = BuildRowLabel(title, subtitle);
            Grid.SetColumn(label, 1);
            grid.Children.Add(label);

            var slider = new Slider
            {
                Style = (Style)FindResource("GlassSlider"),
                Minimum = 0,
                Maximum = 100,
                Value = Math.Round(volume * 100),
                IsSnapToTickEnabled = true,
                TickFrequency = 1,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0)
            };
            Grid.SetColumn(slider, 2);
            grid.Children.Add(slider);

            var pct = new TextBlock
            {
                Text = $"{(int)slider.Value}%",
                FontSize = 12,
                Foreground = Brush("BrushTextSecondary"),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetColumn(pct, 3);
            grid.Children.Add(pct);

            bool isMuted = muted ?? false;
            Button? muteBtn = null;
            System.Windows.Shapes.Path? mutePath = null;
            if (onMute != null)
            {
                mutePath = new System.Windows.Shapes.Path { Width = 14, Height = 14, Stretch = Stretch.Uniform };
                muteBtn = new Button
                {
                    Style = (Style)FindResource("GlassIconButton"),
                    Content = mutePath,
                    Margin = new Thickness(8, 0, 0, 0)
                };
                Grid.SetColumn(muteBtn, 4);
                grid.Children.Add(muteBtn);
            }

            void Render()
            {
                bool on = toggle.IsChecked == true;
                slider.IsEnabled = on && !isMuted;
                slider.Opacity = on ? (isMuted ? 0.4 : 1) : 0.3;
                pct.Opacity = on ? 1 : 0.35;
                pct.Text = on && isMuted ? "muted" : $"{(int)slider.Value}%";
                label.Opacity = on ? 1 : 0.55;
                if (muteBtn != null && mutePath != null)
                {
                    muteBtn.IsEnabled = on;
                    muteBtn.ToolTip = isMuted ? "Unmute in this mode" : "Mute in this mode";
                    mutePath.Data = (Geometry)FindResource(isMuted ? "IconMute" : "IconVolume");
                    mutePath.Fill = Brush(isMuted ? "BrushNeonMagenta" : "BrushTextSecondary");
                }
            }
            Render();

            toggle.Click += (s, e) =>
            {
                Render();
                onEnabled(toggle.IsChecked == true, (float)(slider.Value / 100.0), isMuted);
            };
            slider.ValueChanged += (s, e) =>
            {
                Render();
                if (!_loading) onVolume((float)(slider.Value / 100.0));
            };
            if (muteBtn != null)
            {
                muteBtn.Click += (s, e) =>
                {
                    isMuted = !isMuted;
                    Render();
                    onMute!(isMuted);
                };
            }

            return grid;
        }

        /// <summary>
        /// [toggle] Title/subtitle [control spanning the rest].
        /// </summary>
        private FrameworkElement BuildChoiceRow(string title, string subtitle, bool enabled, Control control, Action<bool> onEnabled)
        {
            var grid = NewRowGrid();

            var toggle = new CheckBox { Style = (Style)FindResource("GlassToggle"), IsChecked = enabled };
            grid.Children.Add(toggle);

            var label = BuildRowLabel(title, subtitle);
            Grid.SetColumn(label, 1);
            grid.Children.Add(label);

            control.VerticalAlignment = VerticalAlignment.Center;
            control.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(control, 2);
            Grid.SetColumnSpan(control, 3);
            grid.Children.Add(control);

            void Render()
            {
                bool on = toggle.IsChecked == true;
                control.IsEnabled = on;
                control.Opacity = on ? 1 : 0.4;
                label.Opacity = on ? 1 : 0.55;
            }
            Render();

            toggle.Click += (s, e) =>
            {
                Render();
                onEnabled(toggle.IsChecked == true);
            };

            return grid;
        }

        private static Grid NewRowGrid()
        {
            var grid = new Grid { Margin = new Thickness(0, 5, 0, 5), MinHeight = 36 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });  // toggle
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) }); // label
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // slider
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });  // percent
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });     // mute
            return grid;
        }

        private StackPanel BuildRowLabel(string title, string subtitle)
        {
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 13,
                FontFamily = new FontFamily("Segoe UI Semibold, Inter"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            stack.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 10.5,
                Foreground = Brush("BrushTextMuted"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = subtitle
            });
            return stack;
        }

        private void TxtModeName_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading || SelectedMode is not { } mode) return;
            mode.Name = TxtModeName.Text;
            ScheduleSave();

            // Update the list label in place so typing doesn't lose focus
            foreach (ListBoxItem item in ModeList.Items)
            {
                if (item.Tag as string == mode.Id && item.Content is Grid g)
                {
                    var tb = g.Children.OfType<TextBlock>().FirstOrDefault(t => Grid.GetColumn(t) == 1);
                    if (tb != null) tb.Text = string.IsNullOrWhiteSpace(mode.Name) ? "(unnamed)" : mode.Name;
                }
            }
            if (mode.Id == _config.ActiveModeId) RefreshActiveBadge();
        }

        private void BtnApplyMode_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedMode is not { } mode) return;
            FlushSave();
            _modes.Apply(mode);
            UpdateLiveBanner();
        }

        private void BtnCaptureLevels_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedMode is not { } mode) return;
            int count = _modes.CaptureCurrent(mode);
            if (count == 0)
            {
                HudWindow.ShowHud("Nothing is playing right now.");
                return;
            }
            RefreshModeEditor();
            ScheduleSave();
            HudWindow.ShowHud($"Copied levels for {count} source{(count == 1 ? "" : "s")}.");
        }

        private void BtnNewMode_Click(object sender, RoutedEventArgs e)
        {
            var mode = new MixMode { Name = UniqueModeName("New mode") };
            _config.Modes.Add(mode);
            _selectedModeId = mode.Id;
            AppRef.SaveConfig();
            RefreshModeList();
            RefreshModeEditor();
            TxtModeName.Focus();
            TxtModeName.SelectAll();
        }

        private void BtnDuplicateMode_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedMode is not { } source) return;
            var copy = JsonSerializer.Deserialize<MixMode>(JsonSerializer.Serialize(source))!;
            copy.Id = Guid.NewGuid().ToString("N");
            copy.Hotkey = null; // a copied chord would collide with the original
            copy.Name = UniqueModeName($"{source.Name} copy");
            _config.Modes.Insert(_config.Modes.IndexOf(source) + 1, copy);
            _selectedModeId = copy.Id;
            AppRef.SaveConfig();
            RefreshModeList();
            RefreshModeEditor();
        }

        private void BtnDeleteMode_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedMode is not { } mode) return;
            var answer = MessageBox.Show(this, $"Delete the mode \"{mode.Name}\"?", "Delete mode",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;

            int index = _config.Modes.IndexOf(mode);
            _config.Modes.Remove(mode);
            if (_config.ActiveModeId == mode.Id) _config.ActiveModeId = null;

            _selectedModeId = _config.Modes.Count == 0 ? null : _config.Modes[Math.Min(index, _config.Modes.Count - 1)].Id;
            AppRef.SaveConfig();
            AppRef.RegisterGlobalHotkeys();
            RefreshActiveBadge();
            RefreshModeList();
            RefreshModeEditor();
        }

        private string UniqueModeName(string baseName)
        {
            string name = baseName;
            for (int i = 2; _config.Modes.Any(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)); i++)
                name = $"{baseName} {i}";
            return name;
        }

        #endregion

        #region Sources tab

        private void RefreshSources()
        {
            SourceCards.Children.Clear();
            foreach (var group in _config.Sources)
                SourceCards.Children.Add(BuildSourceCard(group));
        }

        private FrameworkElement BuildSourceCard(AudioSourceGroup group)
        {
            var card = new Border
            {
                Background = Brush("BrushCardBackground"),
                BorderBrush = Brush("BrushBorderGlass"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 10, 10, 12),
                Margin = new Thickness(0, 0, 0, 12)
            };
            var stack = new StackPanel();
            card.Child = stack;

            // Header: editable name · usage · delete
            var header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nameBox = new TextBox
            {
                Text = group.Name,
                FontSize = 14,
                FontFamily = new FontFamily("Segoe UI Semibold, Inter"),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                Padding = new Thickness(4, 3, 4, 3),
                ToolTip = "Rename"
            };
            nameBox.TextChanged += (s, e) =>
            {
                group.Name = nameBox.Text;
                ScheduleSave();
            };
            header.Children.Add(nameBox);

            int usedBy = _config.Modes.Count(m => m.LevelFor(group.Id) != null);
            var usage = new TextBlock
            {
                Text = usedBy == 0 ? "Not used by any mode" : $"Used by {usedBy} mode{(usedBy == 1 ? "" : "s")}",
                FontSize = 11,
                Foreground = Brush("BrushTextMuted"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            };
            Grid.SetColumn(usage, 1);
            header.Children.Add(usage);

            var deleteBtn = new Button
            {
                Style = (Style)FindResource("GlassIconButton"),
                ToolTip = "Delete source",
                Content = new System.Windows.Shapes.Path
                {
                    Data = (Geometry)FindResource("IconDelete"),
                    Fill = Brush("BrushTextSecondary"),
                    Width = 12,
                    Height = 12,
                    Stretch = Stretch.Uniform
                }
            };
            deleteBtn.Click += (s, e) => DeleteSource(group);
            Grid.SetColumn(deleteBtn, 2);
            header.Children.Add(deleteBtn);
            stack.Children.Add(header);

            // App chips
            var chips = new WrapPanel { Margin = new Thickness(4, 0, 0, 8) };
            if (group.ProcessNames.Count == 0 && group.PathContains.Count == 0)
            {
                chips.Children.Add(new TextBlock
                {
                    Text = "No apps yet. Type an app name or folder below, or assign one from Playing now.",
                    FontSize = 11,
                    Foreground = Brush("BrushTextMuted"),
                    TextWrapping = TextWrapping.Wrap
                });
            }
            foreach (var folder in group.PathContains.ToList())
            {
                chips.Children.Add(BuildChip($"📁 anything in {folder}", $"Remove folder rule {folder}", () =>
                    group.PathContains.RemoveAll(f => string.Equals(AudioSourceGroup.NormalizeFolder(f), AudioSourceGroup.NormalizeFolder(folder), StringComparison.OrdinalIgnoreCase))));
            }
            foreach (var process in group.ProcessNames.ToList())
            {
                chips.Children.Add(BuildChip(process, $"Remove {process}", () =>
                    group.ProcessNames.RemoveAll(p => AppConfig.NormalizeProcessName(p) == AppConfig.NormalizeProcessName(process))));
            }
            stack.Children.Add(chips);

            // Add-app row
            var addRow = new Grid { Margin = new Thickness(4, 0, 0, 0) };
            addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var inputHost = new Grid();
            var input = new TextBox { FontSize = 12, Padding = new Thickness(8, 5, 8, 5) };
            var placeholder = new TextBlock
            {
                Text = @"App name (discord) or folder (\steamapps\common\)",
                FontSize = 12,
                Foreground = Brush("BrushTextMuted"),
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            input.TextChanged += (s, e) => placeholder.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            inputHost.Children.Add(input);
            inputHost.Children.Add(placeholder);
            addRow.Children.Add(inputHost);

            var addBtn = new Button { Content = "Add", Style = (Style)FindResource("GlassButton"), Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(8, 0, 0, 0) };
            Grid.SetColumn(addBtn, 1);
            addRow.Children.Add(addBtn);

            void Add()
            {
                var text = input.Text.Trim();
                if (text.Contains('\\') || text.Contains('/'))
                {
                    // Folder rule: every app whose exe path contains this
                    var folder = AudioSourceGroup.NormalizeFolder(text);
                    if (!group.PathContains.Any(f => string.Equals(AudioSourceGroup.NormalizeFolder(f), folder, StringComparison.OrdinalIgnoreCase)))
                        group.PathContains.Add(folder);
                    AppRef.SaveConfig();
                    RefreshSources();
                    RefreshPlaying(force: true);
                    return;
                }

                var name = AppConfig.NormalizeProcessName(text);
                if (name.Length == 0) return;
                AssignProcess(name, group.Id);
            }
            addBtn.Click += (s, e) => Add();
            input.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { Add(); e.Handled = true; }
            };
            stack.Children.Add(addRow);

            return card;
        }

        private FrameworkElement BuildChip(string text, string removeTip, Action remove)
        {
            var chip = new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Color.FromArgb(0x14, 0xff, 0xff, 0xff)),
                BorderBrush = Brush("BrushBorderGlass"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 2, 4, 2),
                Margin = new Thickness(0, 0, 6, 6)
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });

            var removeBtn = new Button
            {
                Style = (Style)FindResource("GlassIconButton"),
                Width = 20,
                Height = 20,
                Padding = new Thickness(5),
                BorderThickness = new Thickness(0),
                Margin = new Thickness(4, 0, 0, 0),
                ToolTip = removeTip,
                Content = new System.Windows.Shapes.Path
                {
                    Data = (Geometry)FindResource("IconClose"),
                    Fill = Brush("BrushTextSecondary"),
                    Stretch = Stretch.Uniform
                }
            };
            removeBtn.Click += (s, e) =>
            {
                remove();
                AppRef.SaveConfig();
                RefreshSources();
                RefreshPlaying(force: true);
            };
            row.Children.Add(removeBtn);
            chip.Child = row;
            return chip;
        }

        /// <summary>
        /// Moves an app into a source (or out of all of them for Everything else). An app lives in at most one source.
        /// </summary>
        private void AssignProcess(string processName, string sourceId)
        {
            var n = AppConfig.NormalizeProcessName(processName);
            foreach (var g in _config.Sources)
                g.ProcessNames.RemoveAll(p => AppConfig.NormalizeProcessName(p) == n);
            _config.UnassignedProcessNames.RemoveAll(p => AppConfig.NormalizeProcessName(p) == n);

            if (sourceId == BuiltInSources.Other)
                _config.UnassignedProcessNames.Add(n); // opt out of folder rules too
            else
                _config.SourceById(sourceId)?.ProcessNames.Add(n);
            AppRef.SaveConfig();
            RefreshSources();
            RefreshPlaying(force: true);
        }

        private void BtnNewSource_Click(object sender, RoutedEventArgs e)
        {
            string name = "New source";
            for (int i = 2; _config.Sources.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)); i++)
                name = $"New source {i}";

            _config.Sources.Add(new AudioSourceGroup { Name = name });
            AppRef.SaveConfig();
            RefreshSources();
            RefreshPlaying(force: true);

            if (SourceCards.Children[^1] is FrameworkElement card)
            {
                card.Dispatcher.BeginInvoke(() =>
                {
                    card.BringIntoView();
                    var box = FindVisualChild<TextBox>(card);
                    box?.Focus();
                    box?.SelectAll();
                }, DispatcherPriority.Loaded);
            }
        }

        private void DeleteSource(AudioSourceGroup group)
        {
            int usedBy = _config.Modes.Count(m => m.LevelFor(group.Id) != null);
            string msg = usedBy == 0
                ? $"Delete the source \"{group.Name}\"?"
                : $"Delete the source \"{group.Name}\"? It's used by {usedBy} mode{(usedBy == 1 ? "" : "s")}; its level will be removed from them.";
            if (MessageBox.Show(this, msg, "Delete source", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;

            _config.Sources.Remove(group);
            foreach (var mode in _config.Modes)
                mode.Levels.RemoveAll(l => l.SourceId == group.Id);
            AppRef.SaveConfig();
            RefreshSources();
            RefreshPlaying(force: true);
        }

        private void RefreshPlaying(bool force = false)
        {
            var sessions = _engine.GetSessions()
                .GroupBy(s => s.ProcessName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(s => s.ProcessName == "System Sounds" ? 1 : 0)
                .ThenBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Only rebuild when the set of apps changes, so an open dropdown isn't yanked away
            string key = string.Join("|", sessions.Select(s => s.ProcessName));
            if (!force && key == _playingKey) return;
            _playingKey = key;

            PlayingList.Children.Clear();
            if (sessions.Count == 0)
            {
                PlayingList.Children.Add(new TextBlock
                {
                    Text = "Nothing is playing. Start the app you want to add and it will show up here.",
                    FontSize = 11,
                    Foreground = Brush("BrushTextMuted"),
                    TextWrapping = TextWrapping.Wrap
                });
                return;
            }

            foreach (var s in sessions)
                PlayingList.Children.Add(BuildPlayingRow(s));
        }

        private FrameworkElement BuildPlayingRow(AudioSession s)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });

            FrameworkElement icon;
            var bmp = BitmapImageFromBase64(s.IconBase64);
            if (bmp != null)
                icon = new Image { Source = bmp, Width = 18, Height = 18 };
            else
                icon = new System.Windows.Shapes.Path { Data = (Geometry)FindResource("IconVolume"), Fill = Brush("BrushTextMuted"), Width = 14, Height = 14, Stretch = Stretch.Uniform };
            icon.VerticalAlignment = VerticalAlignment.Center;
            icon.HorizontalAlignment = HorizontalAlignment.Left;
            grid.Children.Add(icon);

            bool isSystem = s.ProcessName == "System Sounds";
            string title = isSystem ? "System sounds" : s.DisplayName;
            string subtitle = isSystem ? "Built-in" : AppConfig.NormalizeProcessName(s.ProcessName);
            var folderSource = _config.FolderSourceFor(s.ProcessPath);
            if (!isSystem && folderSource != null && _config.ResolveSourceId(s) == folderSource.Id)
                subtitle += " · by folder";
            var label = BuildRowLabel(title, subtitle);
            label.Margin = new Thickness(0, 0, 8, 0);
            ((TextBlock)label.Children[0]).FontSize = 12;
            Grid.SetColumn(label, 1);
            grid.Children.Add(label);

            if (isSystem)
            {
                var fixedText = new TextBlock { Text = "System sounds", FontSize = 11, Foreground = Brush("BrushTextSecondary"), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(fixedText, 2);
                grid.Children.Add(fixedText);
                return grid;
            }

            var options = _config.Sources.Select(g => new KeyValuePair<string, string>(g.Id, string.IsNullOrWhiteSpace(g.Name) ? "(unnamed)" : g.Name)).ToList();
            options.Add(new KeyValuePair<string, string>(BuiltInSources.Other, BuiltInSources.NameOf(BuiltInSources.Other)));

            var combo = new ComboBox
            {
                ItemsSource = options,
                DisplayMemberPath = "Value",
                SelectedValuePath = "Key",
                SelectedValue = _config.ResolveSourceId(s),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11
            };
            string process = s.ProcessName;
            string path = s.ProcessPath;
            combo.SelectionChanged += (sender, e) =>
            {
                if (combo.SelectedValue is string target && target != _config.ResolveSourceId(process, path))
                    AssignProcess(process, target);
            };
            Grid.SetColumn(combo, 2);
            grid.Children.Add(combo);
            return grid;
        }

        #endregion

        #region Settings tab

        public void RefreshSettings()
        {
            _loading = true;
            try
            {
                CheckStartWithWindows.IsChecked = _config.StartWithWindows;

                var none = new AudioDevice { Id = string.Empty, Name = "(none)" };
                var devices = _engine.GetDevices();
                var outputs = new List<AudioDevice> { none };
                outputs.AddRange(devices.Where(d => d.IsPlayback));
                var inputs = new List<AudioDevice> { none };
                inputs.AddRange(devices.Where(d => !d.IsPlayback));

                ComboFavOutput.ItemsSource = outputs;
                ComboFavOutput.SelectedValue = _config.FavoriteOutputDeviceId;
                ComboFavInput.ItemsSource = inputs;
                ComboFavInput.SelectedValue = _config.FavoriteInputDeviceId;

                TxtHotkeyToggleMixer.Text = _config.ToggleMixerHotkey.ToString();
                TxtHotkeyFavOutput.Text = _config.FavoriteOutputHotkey.ToString();
                TxtHotkeyFavInput.Text = _config.FavoriteInputHotkey.ToString();
                TxtHotkeyResetLevels.Text = _config.ResetLevelsHotkey.ToString();
            }
            finally
            {
                _loading = false;
            }
        }

        private void CheckStartWithWindows_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            AppRef.SetStartWithWindows(CheckStartWithWindows.IsChecked == true);
        }

        private void ComboFavOutput_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            _config.FavoriteOutputDeviceId = ComboFavOutput.SelectedValue as string ?? string.Empty;
            AppRef.SaveConfig();
        }

        private void ComboFavInput_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            _config.FavoriteInputDeviceId = ComboFavInput.SelectedValue as string ?? string.Empty;
            AppRef.SaveConfig();
        }

        private void HotkeyTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true;
            if (sender is not TextBox textBox) return;

            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Tab) { e.Handled = false; return; }

            HotkeyConfig newHotkey;
            if (key is Key.Back or Key.Delete)
            {
                newHotkey = new HotkeyConfig { Key = Key.None };
            }
            else
            {
                // Ignore bare modifier presses (Ctrl, Alt, Shift, Win alone)
                if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
                    Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
                    return;

                // Must press at least one modifier
                var modifiers = Keyboard.Modifiers;
                if (modifiers == ModifierKeys.None) return;

                newHotkey = new HotkeyConfig { Modifiers = modifiers, Key = key };
            }

            textBox.Text = newHotkey.ToString();

            if (textBox == TxtModeHotkey)
            {
                if (SelectedMode is not { } mode) return;
                mode.Hotkey = newHotkey.IsEmpty ? null : newHotkey;
                RefreshModeList();
            }
            else if (textBox == TxtHotkeyToggleMixer) _config.ToggleMixerHotkey = newHotkey;
            else if (textBox == TxtHotkeyFavOutput) _config.FavoriteOutputHotkey = newHotkey;
            else if (textBox == TxtHotkeyFavInput) _config.FavoriteInputHotkey = newHotkey;
            else if (textBox == TxtHotkeyResetLevels) _config.ResetLevelsHotkey = newHotkey;

            AppRef.SaveConfig();
            AppRef.RegisterGlobalHotkeys();
        }

        #endregion

        #region Helpers

        private SolidColorBrush Brush(string key) => (SolidColorBrush)FindResource(key);

        private static BitmapImage? BitmapImageFromBase64(string base64)
        {
            if (string.IsNullOrEmpty(base64)) return null;
            try
            {
                int index = base64.IndexOf("base64,", StringComparison.Ordinal);
                if (index >= 0) base64 = base64[(index + 7)..];

                var bi = new BitmapImage();
                bi.BeginInit();
                bi.StreamSource = new MemoryStream(Convert.FromBase64String(base64));
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

        private static T? FindVisualChild<T>(DependencyObject obj) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
            {
                var child = VisualTreeHelper.GetChild(obj, i);
                if (child is T t) return t;
                var nested = FindVisualChild<T>(child);
                if (nested != null) return nested;
            }
            return null;
        }

        #endregion
    }
}
