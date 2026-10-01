using audio_mixer;
using Xunit;

namespace audio_mixer.Tests;

public class ResolveApplyLevelTests
{
    private static AppConfig TypicalConfig() => new()
    {
        SchemaVersion = AppConfig.CurrentSchemaVersion,
        Sources =
        {
            new AudioSourceGroup
            {
                Id = "voice",
                Name = "Voice",
                ProcessNames = { "discord", "discordptb", "discordcanary" }
            },
            new AudioSourceGroup
            {
                Id = "audio",
                Name = "Audio",
                ProcessNames = { "spotify" }
            },
            new AudioSourceGroup
            {
                Id = "browser",
                Name = "Browser",
                ProcessNames = { "firefox", "chrome" }
            },
            new AudioSourceGroup
            {
                Id = "games",
                Name = "Games",
                PathContains = { AppConfig.SteamGamesFolder }
            },
            // Live configs often use a GUID id for user-created sources (not "work")
            new AudioSourceGroup
            {
                Id = "97db36f6cc2c4c29a682d828e1a959f5",
                Name = "Work Apps",
                ProcessNames = { "jarvistray", "grok bot" }
            }
        }
    };

    /// <summary>
    /// OLD buggy Apply path: LevelFor(membership) only — never falls through to Other.
    /// Kept here so the regression is explicit if someone reverts ApplyToSessions.
    /// </summary>
    private static SourceLevel? OldBuggyApplyLevel(AppConfig config, MixMode mode, string processName, string? processPath = null) =>
        mode.LevelFor(config.ResolveSourceId(processName, processPath));

    [Fact]
    public void QuietMode_OnlyOtherOn_At62_NamedOffRowsFallThrough_NotStickyOffSlider()
    {
        var config = TypicalConfig();
        // Matches Guy's live Quiet Mode: only Everything else ON @ 62%
        var quiet = new MixMode
        {
            Name = "Quiet Mode",
            Levels = { new SourceLevel { SourceId = BuiltInSources.Other, Volume = 0.62f } }
        };

        // Membership maps Discord → voice (OFF / absent from Levels)
        Assert.Equal("voice", config.ResolveSourceId("Discord", null));
        Assert.Null(quiet.LevelFor("voice")); // voice Enabled=false

        // OLD path: leave untouched (null) — sticky Voice slider would remain in Windows mixer
        Assert.Null(OldBuggyApplyLevel(config, quiet, "Discord", null));

        // NEW path: fall through to Everything else @ 62%
        var discord = config.ResolveApplyLevel(quiet, "Discord", null);
        Assert.NotNull(discord);
        Assert.Equal(BuiltInSources.Other, discord!.SourceId);
        Assert.Equal(0.62f, discord.Volume);

        // Firefox → browser OFF → Other 62%
        Assert.Equal("browser", config.ResolveSourceId("firefox", null));
        Assert.Null(OldBuggyApplyLevel(config, quiet, "firefox", null));
        Assert.Equal(0.62f, config.ResolveApplyLevel(quiet, "firefox", null)!.Volume);

        // JarvisTray → Work Apps (GUID id) OFF → Other 62%
        Assert.Equal("97db36f6cc2c4c29a682d828e1a959f5", config.ResolveSourceId("JarvisTray", null));
        Assert.Null(quiet.LevelFor("97db36f6cc2c4c29a682d828e1a959f5"));
        Assert.Null(OldBuggyApplyLevel(config, quiet, "JarvisTray", null));
        Assert.Equal(0.62f, config.ResolveApplyLevel(quiet, "JarvisTray", null)!.Volume);

        // System sounds OFF → Other 62%
        Assert.Equal(BuiltInSources.System, config.ResolveSourceId("System Sounds", null));
        Assert.Null(OldBuggyApplyLevel(config, quiet, "System Sounds", null));
        Assert.Equal(0.62f, config.ResolveApplyLevel(quiet, "System Sounds", null)!.Volume);

        // Telegram / Steam resolve directly to Other — both old and new apply Other
        Assert.Equal(BuiltInSources.Other, config.ResolveSourceId("Telegram",
            @"C:\Users\x\AppData\Roaming\Telegram Desktop\Telegram.exe"));
        Assert.Equal(0.62f, config.ResolveApplyLevel(quiet, "Telegram",
            @"C:\Users\x\AppData\Roaming\Telegram Desktop\Telegram.exe")!.Volume);
        Assert.Equal(0.62f, OldBuggyApplyLevel(config, quiet, "Telegram",
            @"C:\Users\x\AppData\Roaming\Telegram Desktop\Telegram.exe")!.Volume);

        Assert.Equal(0.62f, config.ResolveApplyLevel(quiet, "steam",
            @"C:\Program Files (x86)\Steam\steam.exe")!.Volume);
    }

    [Fact]
    public void QuietMode_OnlyOtherOn_FallsThroughDisabledNamedSources_AndMatchesTelegram()
    {
        var config = TypicalConfig();
        var quiet = new MixMode
        {
            Name = "Quiet Mode",
            Levels = { new SourceLevel { SourceId = BuiltInSources.Other, Volume = 0.16f } }
        };

        var discord = config.ResolveApplyLevel(quiet, "Discord", null);
        Assert.NotNull(discord);
        Assert.Equal(BuiltInSources.Other, discord!.SourceId);
        Assert.Equal(0.16f, discord.Volume);
        Assert.Null(quiet.LevelFor("voice"));

        Assert.Equal(BuiltInSources.Other, config.ResolveSourceId("Telegram",
            @"C:\Users\x\AppData\Roaming\Telegram Desktop\Telegram.exe"));
        var telegram = config.ResolveApplyLevel(quiet, "Telegram",
            @"C:\Users\x\AppData\Roaming\Telegram Desktop\Telegram.exe");
        Assert.NotNull(telegram);
        Assert.Equal(BuiltInSources.Other, telegram!.SourceId);
        Assert.Equal(0.16f, telegram.Volume);

        var steam = config.ResolveApplyLevel(quiet, "steam", @"C:\Program Files (x86)\Steam\steam.exe");
        Assert.NotNull(steam);
        Assert.Equal(BuiltInSources.Other, steam!.SourceId);
        Assert.Equal(0.16f, steam.Volume);

        Assert.Equal(BuiltInSources.Other, config.ResolveApplyLevel(quiet, "firefox", null)!.SourceId);
        Assert.Equal(BuiltInSources.Other, config.ResolveApplyLevel(quiet, "JarvisTray", null)!.SourceId);
        Assert.Equal(BuiltInSources.Other, config.ResolveApplyLevel(quiet, "System Sounds", null)!.SourceId);
    }

    [Fact]
    public void OffRows_NeverContributeTheirOwnSourceLevel()
    {
        var config = TypicalConfig();
        var quiet = new MixMode
        {
            Levels =
            {
                new SourceLevel { SourceId = BuiltInSources.Other, Volume = 0.16f }
            }
        };

        var level = config.ResolveApplyLevel(quiet, "discord", null);
        Assert.NotNull(level);
        Assert.Equal(BuiltInSources.Other, level!.SourceId);
        Assert.Equal(0.16f, level.Volume);
        Assert.Null(quiet.LevelFor("voice"));
        Assert.Null(quiet.LevelFor("browser"));
        Assert.Null(quiet.LevelFor("97db36f6cc2c4c29a682d828e1a959f5"));
        Assert.Null(quiet.LevelFor(BuiltInSources.System));
    }

    [Fact]
    public void FocusWork_EnabledSourcesApply_DisabledWithoutOtherLeftUntouched()
    {
        var config = TypicalConfig();
        var focus = new MixMode
        {
            Name = "Focus Work",
            Levels =
            {
                new SourceLevel { SourceId = BuiltInSources.System, Volume = 0.15f },
                new SourceLevel { SourceId = "audio", Volume = 1.0f },
                new SourceLevel { SourceId = "97db36f6cc2c4c29a682d828e1a959f5", Volume = 1.0f }
            }
        };

        Assert.Equal(0.15f, config.ResolveApplyLevel(focus, "System Sounds", null)!.Volume);
        Assert.Equal("audio", config.ResolveApplyLevel(focus, "spotify", null)!.SourceId);
        Assert.Equal("97db36f6cc2c4c29a682d828e1a959f5",
            config.ResolveApplyLevel(focus, "JarvisTray", null)!.SourceId);

        // Voice off + no Everything else → Discord untouched
        Assert.Null(config.ResolveApplyLevel(focus, "discord", null));
        Assert.Null(config.ResolveApplyLevel(focus, "firefox", null));
        Assert.Null(config.ResolveApplyLevel(focus, "Telegram", null));
    }

    [Fact]
    public void Gaming_EnabledVoiceWinsOverEverythingElse()
    {
        var config = TypicalConfig();
        var gaming = new MixMode
        {
            Name = "Gaming",
            Levels =
            {
                new SourceLevel { SourceId = "voice", Volume = 1.0f },
                new SourceLevel { SourceId = "games", Volume = 0.5f },
                new SourceLevel { SourceId = BuiltInSources.System, Volume = 0.10f },
                new SourceLevel { SourceId = BuiltInSources.Other, Volume = 0.16f }
            }
        };

        var discord = config.ResolveApplyLevel(gaming, "discord", null);
        Assert.NotNull(discord);
        Assert.Equal("voice", discord!.SourceId);
        Assert.Equal(1.0f, discord.Volume);

        var game = config.ResolveApplyLevel(gaming, "game",
            @"D:\SteamLibrary\steamapps\common\SomeGame\game.exe");
        Assert.NotNull(game);
        Assert.Equal("games", game!.SourceId);
        Assert.Equal(0.5f, game.Volume);

        // Unmatched still takes Other when Voice/Games don't claim them
        Assert.Equal(0.16f, config.ResolveApplyLevel(gaming, "Telegram", null)!.Volume);
    }

    [Fact]
    public void QuietMode_MustNotWriteDisabledRowSlider_EvenIfCallerStillHasStaleLevelFor()
    {
        // Guard: if Apply ever goes back to LevelFor(membership) only, Discord stays null
        // while Other is on — Windows mixer keeps sticky Voice OFF slider (e.g. 21%).
        var config = TypicalConfig();
        var quiet = new MixMode
        {
            Levels = { new SourceLevel { SourceId = BuiltInSources.Other, Volume = 0.62f } }
        };

        Assert.Equal("voice", config.ResolveSourceId("discord", null));
        Assert.True(
            quiet.LevelFor("voice") == null && quiet.LevelFor(BuiltInSources.Other) != null,
            "precondition: voice off, other on");

        var applied = config.ResolveApplyLevel(quiet, "discord", null);
        Assert.NotNull(applied);
        Assert.Equal(0.62f, applied!.Volume);
        Assert.NotEqual("voice", applied.SourceId);
    }
}
