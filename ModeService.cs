using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;

namespace audio_mixer
{
    /// <summary>
    /// Applies modes to the live mix. UI-free apart from the HUD.
    /// </summary>
    public sealed class ModeService
    {
        private readonly AudioEngine _engine;
        private readonly AppConfig _config;
        private readonly Action _save;
        private HashSet<uint> _knownPids = new();

        public event Action? ActiveModeChanged;

        public ModeService(AudioEngine engine, AppConfig config, Action save)
        {
            _engine = engine;
            _config = config;
            _save = save;
        }

        public MixMode? Active => _config.ActiveMode;

        /// <summary>
        /// Applies a mode once to everything currently playing and makes it the active mode.
        /// </summary>
        public void Apply(MixMode mode, bool showHud = true)
        {
            bool deviceSwitched = false;
            if (!string.IsNullOrEmpty(mode.OutputDeviceId))
            {
                var device = _engine.GetDevices(playbackOnly: true).Find(d => d.Id == mode.OutputDeviceId);
                if (device != null && !device.IsDefault)
                {
                    _engine.SetDefaultDevice(device.Id, ERole.Console);
                    _engine.SetDefaultDevice(device.Id, ERole.Multimedia);
                    _engine.SetDefaultDevice(device.Id, ERole.Communications);
                    deviceSwitched = true;
                }
            }

            if (mode.MasterVolume is float master)
                _engine.SetMasterVolume(master);

            if (mode.MicMuted is bool micMuted)
                _engine.SetMicMute(micMuted);

            int count = ApplyToSessions(mode, _engine.GetSessions());

            bool changed = _config.ActiveModeId != mode.Id;
            _config.ActiveModeId = mode.Id;
            _save();
            SeedKnownPids();

            // Sessions take a moment to move to a newly-default device; catch them on a second pass.
            if (deviceSwitched)
            {
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    if (_config.ActiveModeId == mode.Id)
                    {
                        ApplyToSessions(mode, _engine.GetSessions());
                        SeedKnownPids();
                    }
                };
                timer.Start();
            }

            if (showHud)
            {
                var extras = new List<string>();
                if (count > 0) extras.Add($"{count} app{(count == 1 ? "" : "s")} adjusted");
                if (mode.MicMuted == true) extras.Add("mic muted");
                else if (mode.MicMuted == false) extras.Add("mic live");
                HudWindow.ShowHud($"🎚️ {mode.Name}" + (extras.Count > 0 ? "\n" + string.Join(" · ", extras) : string.Empty));
            }

            if (changed) ActiveModeChanged?.Invoke();
        }

        public void ReapplyActive()
        {
            var active = Active;
            if (active == null)
            {
                HudWindow.ShowHud("⚠️ No active mode yet.\nPick one from the tray.");
                return;
            }
            Apply(active);
        }

        /// <summary>
        /// Remember what's already playing so the poller only reacts to apps launched afterwards.
        /// </summary>
        public void SeedKnownPids()
        {
            _knownPids = _engine.GetSessions().Select(s => s.ProcessId).ToHashSet();
        }

        /// <summary>
        /// Poll tick: give newly appeared sessions the active mode's level (e.g. Discord launched mid-game).
        /// </summary>
        public void Poll()
        {
            var sessions = _engine.GetSessions();
            var active = Active;
            if (active != null)
            {
                var appeared = sessions.Where(s => !_knownPids.Contains(s.ProcessId)).ToList();
                if (appeared.Count > 0)
                    ApplyToSessions(active, appeared);
            }
            _knownPids = sessions.Select(s => s.ProcessId).ToHashSet();
        }

        /// <summary>
        /// Overwrites the mode's levels with what's playing right now. Only touches sources with a running app.
        /// </summary>
        public int CaptureCurrent(MixMode mode)
        {
            int captured = 0;
            var seen = new HashSet<string>();
            foreach (var s in _engine.GetSessions())
            {
                var sourceId = _config.ResolveSourceId(s);
                if (!seen.Add(sourceId)) continue;

                var level = mode.LevelFor(sourceId);
                if (level == null)
                {
                    level = new SourceLevel { SourceId = sourceId };
                    mode.Levels.Add(level);
                }
                level.Volume = s.Volume;
                level.Mute = s.IsMuted;
                captured++;
            }

            if (mode.MasterVolume != null && _engine.GetMasterVolume() is float master)
                mode.MasterVolume = master;
            if (mode.MicMuted != null && _engine.GetMicMute() is bool mic)
                mode.MicMuted = mic;

            return captured;
        }

        private int ApplyToSessions(MixMode mode, IEnumerable<AudioSession> sessions)
        {
            var levels = new Dictionary<uint, SourceLevel>();
            foreach (var s in sessions)
            {
                // Off rows never write their own level; uncovered sessions take Everything else.
                var level = _config.ResolveApplyLevel(mode, s);
                if (level != null)
                    levels[s.ProcessId] = level;
            }
            _engine.ApplySessionLevels(levels);
            return levels.Count;
        }
    }
}
