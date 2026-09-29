using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace audio_mixer
{
    public class HotkeyConfig
    {
        public ModifierKeys Modifiers { get; set; }
        public Key Key { get; set; }

        [JsonIgnore]
        public bool IsEmpty => Key == Key.None;

        public bool SameChord(HotkeyConfig? other) =>
            other != null && !other.IsEmpty && other.Modifiers == Modifiers && other.Key == Key;

        public override string ToString()
        {
            if (IsEmpty) return string.Empty;
            var parts = new List<string>();
            if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
            parts.Add(Key.ToString());
            return string.Join(" + ", parts);
        }
    }

    /// <summary>
    /// A named bucket of apps ("Voice", "Games", ...). Defined once, referenced by every mode.
    /// </summary>
    public class AudioSourceGroup
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = string.Empty;
        public List<string> ProcessNames { get; set; } = new();
    }

    /// <summary>
    /// A mode's level for one source. Absent from <see cref="MixMode.Levels"/> = leave that source alone.
    /// </summary>
    public class SourceLevel
    {
        public string SourceId { get; set; } = string.Empty;
        public float Volume { get; set; } = 1.0f; // 0..1
        public bool Mute { get; set; }
    }

    /// <summary>
    /// A switchable template: "Focus Work", "Gaming", ...
    /// Every field is optional; anything not set is left untouched when the mode is applied.
    /// </summary>
    public class MixMode
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = string.Empty;
        public HotkeyConfig? Hotkey { get; set; }
        public List<SourceLevel> Levels { get; set; } = new();
        public float? MasterVolume { get; set; }
        public bool? MicMuted { get; set; }
        public string? OutputDeviceId { get; set; }

        public SourceLevel? LevelFor(string sourceId) => Levels.Find(l => l.SourceId == sourceId);
    }

    /// <summary>
    /// Pseudo-sources that exist in every config and can't be deleted.
    /// </summary>
    public static class BuiltInSources
    {
        public const string System = "system";
        public const string Other = "other";

        public static string NameOf(string id) => id switch
        {
            System => "System sounds",
            Other => "Everything else",
            _ => id
        };
    }

    public class AppConfig
    {
        public const int CurrentSchemaVersion = 2;

        // Configs written before modes existed deserialize as 1 and get seeded.
        public int SchemaVersion { get; set; } = 1;

        public string FavoriteOutputDeviceId { get; set; } = string.Empty;
        public string FavoriteInputDeviceId { get; set; } = string.Empty;

        // Property names kept from v1 so existing config.json hotkeys survive.
        public HotkeyConfig ToggleMixerHotkey { get; set; } = new() { Modifiers = ModifierKeys.Control | ModifierKeys.Alt, Key = Key.M };
        public HotkeyConfig FavoriteOutputHotkey { get; set; } = new() { Modifiers = ModifierKeys.Control | ModifierKeys.Alt, Key = Key.O };
        public HotkeyConfig FavoriteInputHotkey { get; set; } = new() { Modifiers = ModifierKeys.Control | ModifierKeys.Alt, Key = Key.I };
        public HotkeyConfig ResetLevelsHotkey { get; set; } = new() { Modifiers = ModifierKeys.Control | ModifierKeys.Alt, Key = Key.L };

        public List<AudioSourceGroup> Sources { get; set; } = new();
        public List<MixMode> Modes { get; set; } = new();
        public string? ActiveModeId { get; set; }
        public bool StartWithWindows { get; set; }

        [JsonIgnore]
        public MixMode? ActiveMode => Modes.Find(m => m.Id == ActiveModeId);

        public AudioSourceGroup? SourceById(string id) => Sources.Find(s => s.Id == id);

        public static string NormalizeProcessName(string? name)
        {
            var n = (name ?? string.Empty).Trim();
            if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                n = n[..^4];
            return n.ToLowerInvariant();
        }

        /// <summary>
        /// Which source a running process belongs to: System sounds, the first group listing it, or Everything else.
        /// </summary>
        public string ResolveSourceId(string processName)
        {
            if (processName is "System Sounds" or "System Sound")
                return BuiltInSources.System;

            var n = NormalizeProcessName(processName);
            foreach (var g in Sources)
            {
                if (g.ProcessNames.Any(p => NormalizeProcessName(p) == n))
                    return g.Id;
            }
            return BuiltInSources.Other;
        }

        public string SourceName(string id) =>
            SourceById(id)?.Name ?? BuiltInSources.NameOf(id);

        /// <summary>
        /// First run (or first run after upgrading from the flat preset map): seed starter sources + example modes.
        /// </summary>
        public void EnsureSeeded()
        {
            if (SchemaVersion >= CurrentSchemaVersion) return;

            if (Sources.Count == 0)
            {
                Sources.Add(new AudioSourceGroup { Id = "voice", Name = "Voice", ProcessNames = { "discord", "discordptb", "discordcanary", "teams", "ms-teams", "slack", "zoom", "skype", "mumble", "ts3client_win64" } });
                Sources.Add(new AudioSourceGroup { Id = "audio", Name = "Audio", ProcessNames = { "spotify", "musicbee", "vlc", "foobar2000", "itunes", "applemusic", "wmplayer", "tidal" } });
                Sources.Add(new AudioSourceGroup { Id = "browser", Name = "Browser", ProcessNames = { "chrome", "firefox", "msedge", "brave", "opera", "vivaldi" } });
                Sources.Add(new AudioSourceGroup { Id = "games", Name = "Games" });
            }

            if (Modes.Count == 0)
            {
                Modes.Add(new MixMode
                {
                    Name = "Focus Work",
                    MicMuted = true,
                    Levels =
                    {
                        new SourceLevel { SourceId = BuiltInSources.System, Volume = 0.15f },
                        new SourceLevel { SourceId = "audio", Volume = 1.0f }
                    }
                });
                Modes.Add(new MixMode
                {
                    Name = "Gaming",
                    Levels =
                    {
                        new SourceLevel { SourceId = "voice", Volume = 1.0f },
                        new SourceLevel { SourceId = "games", Volume = 0.5f },
                        new SourceLevel { SourceId = BuiltInSources.System, Volume = 0.10f }
                    }
                });
            }

            SchemaVersion = CurrentSchemaVersion;
        }
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
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public static AppConfig Load()
        {
            AppConfig? config = null;
            try
            {
                if (File.Exists(FilePath))
                {
                    string json = File.ReadAllText(FilePath);
                    config = JsonSerializer.Deserialize<AppConfig>(json, Options);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load config: {ex.Message}");
            }

            config ??= new AppConfig();
            bool needsSave = config.SchemaVersion < AppConfig.CurrentSchemaVersion;
            config.EnsureSeeded();
            if (needsSave) Save(config);
            return config;
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
