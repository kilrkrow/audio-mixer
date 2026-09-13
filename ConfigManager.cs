using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows.Input;

namespace audio_mixer
{
    public class HotkeyConfig
    {
        public ModifierKeys Modifiers { get; set; }
        public Key Key { get; set; }

        public override string ToString()
        {
            var parts = new List<string>();
            if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
            parts.Add(Key.ToString());
            return string.Join(" + ", parts);
        }
    }

    public class AppConfig
    {
        public string FavoriteOutputDeviceId { get; set; } = string.Empty;
        public string FavoriteInputDeviceId { get; set; } = string.Empty;

        public HotkeyConfig ToggleMixerHotkey { get; set; } = new() { Modifiers = ModifierKeys.Control | ModifierKeys.Alt, Key = Key.M };
        public HotkeyConfig FavoriteOutputHotkey { get; set; } = new() { Modifiers = ModifierKeys.Control | ModifierKeys.Alt, Key = Key.O };
        public HotkeyConfig FavoriteInputHotkey { get; set; } = new() { Modifiers = ModifierKeys.Control | ModifierKeys.Alt, Key = Key.I };
        public HotkeyConfig ResetLevelsHotkey { get; set; } = new() { Modifiers = ModifierKeys.Control | ModifierKeys.Alt, Key = Key.L };

        // Process name (lowercase, e.g. "discord") -> Volume (0.0 to 1.0)
        public Dictionary<string, float> AppVolumePresets { get; set; } = new(StringComparer.OrdinalIgnoreCase)
        {
            { "discord", 1.0f },
            { "spotify", 0.5f },
            { "chrome", 0.6f }
        };
    }

    public static class ConfigManager
    {
        private static readonly string FolderPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "KilrKrowAudioMixer"
        );
        private static readonly string FilePath = Path.Combine(FolderPath, "config.json");

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public static AppConfig Load()
        {
            try
            {
                if (!Directory.Exists(FolderPath))
                {
                    Directory.CreateDirectory(FolderPath);
                }

                if (!File.Exists(FilePath))
                {
                    var defaultConfig = new AppConfig();
                    Save(defaultConfig);
                    return defaultConfig;
                }

                string json = File.ReadAllText(FilePath);
                var config = JsonSerializer.Deserialize<AppConfig>(json, Options);
                
                // Keep the string comparer ordinal case insensitive for app names
                if (config != null)
                {
                    var presetsCopy = new Dictionary<string, float>(config.AppVolumePresets, StringComparer.OrdinalIgnoreCase);
                    config.AppVolumePresets = presetsCopy;
                    return config;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load config: {ex.Message}");
            }

            return new AppConfig();
        }

        public static void Save(AppConfig config)
        {
            try
            {
                if (!Directory.Exists(FolderPath))
                {
                    Directory.CreateDirectory(FolderPath);
                }

                string json = JsonSerializer.Serialize(config, Options);
                File.WriteAllText(FilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save config: {ex.Message}");
            }
        }
    }
}
