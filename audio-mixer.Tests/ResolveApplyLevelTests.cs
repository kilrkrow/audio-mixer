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
            new AudioSourceGroup
            {
                Id = "work",
                Name = "Work Apps",
                ProcessNames = { "jarvistray" }
            }
        }
    };

    [Fact]
    public void QuietMode_OnlyOtherOn_FallsThroughDisabledNamedSources_AndMatchesTelegram()
    {
        var config = TypicalConfig();
        var quiet = new MixMode
        {
            Name = "Quiet Mode",
            Levels = { new SourceLevel { SourceId = BuiltInSources.Other, Volume = 0.16f } }
        };

        // Discord is in Voice (off) → Everything else 16%, not Voice's slider
        var discord = config.ResolveApplyLevel(quiet, "Discord", null);
        Assert.NotNull(discord);
        Assert.Equal(BuiltInSources.Other, discord!.SourceId);
        Assert.Equal(0.16f, discord.Volume);
        Assert.Null(quiet.LevelFor("voice"));

        // Telegram Desktop is not in any named source → Everything else
        Assert.Equal(BuiltInSources.Other, config.ResolveSourceId("Telegram",
            @"C:\Users\x\AppData\Roaming\Telegram Desktop\Telegram.exe"));
        var telegram = config.ResolveApplyLevel(quiet, "Telegram",
            @"C:\Users\x\AppData\Roaming\Telegram Desktop\Telegram.exe");
        Assert.NotNull(telegram);
        Assert.Equal(BuiltInSources.Other, telegram!.SourceId);
        Assert.Equal(0.16f, telegram.Volume);

        // Steam.exe is outside steamapps\common → Other (Steam-class apps still get Everything else)
        var steam = config.ResolveApplyLevel(quiet, "steam", @"C:\Program Files (x86)\Steam\steam.exe");
        Assert.NotNull(steam);
        Assert.Equal(BuiltInSources.Other, steam!.SourceId);
        Assert.Equal(0.16f, steam.Volume);

        // Firefox / JarvisTray / System with their rows off → fall through to Other
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
        // Voice row is absent — apply must not invent a Voice SourceLevel at 48% etc.
        Assert.Null(quiet.LevelFor("voice"));
        Assert.Null(quiet.LevelFor("browser"));
        Assert.Null(quiet.LevelFor("work"));
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
                new SourceLevel { SourceId = "work", Volume = 1.0f }
            }
        };

        Assert.Equal(0.15f, config.ResolveApplyLevel(focus, "System Sounds", null)!.Volume);
        Assert.Equal("audio", config.ResolveApplyLevel(focus, "spotify", null)!.SourceId);
        Assert.Equal("work", config.ResolveApplyLevel(focus, "JarvisTray", null)!.SourceId);

        // Voice off + no Everything else → Discord untouched
        Assert.Null(config.ResolveApplyLevel(focus, "discord", null));
        // Browser off + no Other → Firefox untouched
        Assert.Null(config.ResolveApplyLevel(focus, "firefox", null));
        // Telegram would be Other membership, Other off → untouched
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
    }
}
