using System;
using Microsoft.Win32;

namespace audio_mixer
{
    public static class StartupHelper
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "KilrKrowAudioMixer";
        public const string TrayArgument = "--tray";

        /// <summary>
        /// Config is the source of truth; this writes (or removes) the HKCU Run value to match it.
        /// </summary>
        public static bool ApplyStartOnWindows(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
                if (key == null) return false;

                if (enable)
                {
                    var exe = Environment.ProcessPath;
                    if (string.IsNullOrEmpty(exe)) return false;
                    key.SetValue(AppName, $"\"{exe}\" {TrayArgument}");
                }
                else if (key.GetValue(AppName) != null)
                {
                    key.DeleteValue(AppName, throwOnMissingValue: false);
                }
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to update Start with Windows: {ex.Message}");
                return false;
            }
        }
    }
}
