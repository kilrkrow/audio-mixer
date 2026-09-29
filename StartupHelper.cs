using System;
using System.IO;
using Microsoft.Win32;

namespace audio_mixer
{
    public static class StartupHelper
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "KilrKrowAudioMixer";
        public const string TrayArgument = "--tray";
        public const string StartMenuShortcutFileName = "KilrKrow Mixer.lnk";

        /// <summary>
        /// Typical user Start Menu shortcut path:
        /// %AppData%\Microsoft\Windows\Start Menu\Programs\KilrKrow Mixer.lnk
        /// </summary>
        public static string StartMenuShortcutPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                StartMenuShortcutFileName);

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

        public static bool IsStartMenuShortcutPresent()
        {
            try
            {
                return File.Exists(StartMenuShortcutPath);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Creates or removes the Start Menu Programs shortcut to the running exe.
        /// </summary>
        public static bool ApplyStartMenuShortcut(bool enable)
        {
            try
            {
                var path = StartMenuShortcutPath;
                if (enable)
                {
                    var exe = Environment.ProcessPath;
                    if (string.IsNullOrEmpty(exe)) return false;

                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);

                    CreateShortcut(path, exe, Path.GetDirectoryName(exe) ?? string.Empty, "KilrKrow Mixer");
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                }
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to update Start Menu shortcut: {ex.Message}");
                return false;
            }
        }

        private static void CreateShortcut(string shortcutPath, string targetPath, string workingDirectory, string description)
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell")
                ?? throw new InvalidOperationException("WScript.Shell is unavailable.");
            dynamic shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("Could not create WScript.Shell.");
            var shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = targetPath;
            shortcut.WorkingDirectory = workingDirectory;
            shortcut.Description = description;
            shortcut.IconLocation = targetPath + ",0";
            shortcut.Save();
        }
    }
}
