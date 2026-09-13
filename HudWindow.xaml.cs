using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace audio_mixer
{
    public partial class HudWindow : Window
    {
        private static HudWindow? _activeHud;
        private Storyboard? _storyboard;

        public HudWindow(string message)
        {
            InitializeComponent();
            TxtMessage.Text = message;
            
            // Position in center of screen
            PositionWindow();
            
            Loaded += HudWindow_Loaded;
        }

        private void HudWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _storyboard = (Storyboard)Resources["FadeInOut"];
            _storyboard.Completed += Storyboard_Completed;
            _storyboard.Begin(this);
        }

        private void PositionWindow()
        {
            var screen = System.Windows.Forms.Screen.PrimaryScreen;
            if (screen != null)
            {
                var workingArea = screen.WorkingArea;
                // Place it in the lower middle quadrant, slightly above taskbar
                Left = workingArea.Left + (workingArea.Width - Width) / 2;
                Top = workingArea.Top + (workingArea.Height * 0.7) - (Height / 2);
            }
        }

        public static void ShowHud(string message)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                if (_activeHud != null)
                {
                    _activeHud.UpdateText(message);
                }
                else
                {
                    _activeHud = new HudWindow(message);
                    _activeHud.Show();
                }
            });
        }

        private void UpdateText(string message)
        {
            TxtMessage.Text = message;
            
            // Restart the animation sequence to keep HUD visible
            if (_storyboard != null)
            {
                _storyboard.Stop(this);
                _storyboard.Begin(this);
            }
        }

        private void Storyboard_Completed(object? sender, EventArgs e)
        {
            _activeHud = null;
            Close();
        }
    }
}
