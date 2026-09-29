using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace audio_mixer
{
    public class AudioDevice
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public bool IsPlayback { get; set; }
        public bool IsDefault { get; set; }
        public bool IsFavorite { get; set; }
    }

    public class AudioSession
    {
        public string SessionId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string ProcessName { get; set; } = string.Empty;
        public string ProcessPath { get; set; } = string.Empty;
        public string IconBase64 { get; set; } = string.Empty;
        public float Volume { get; set; }
        public bool IsMuted { get; set; }
        public uint ProcessId { get; set; }
    }

    public class SessionSnapshot
    {
        public uint ProcessId { get; set; }
        public string ProcessName { get; set; } = string.Empty;
        public string ProcessPath { get; set; } = string.Empty;
        public float Volume { get; set; }
        public bool IsMuted { get; set; }
    }

    /// <summary>
    /// Point-in-time Windows audio state captured at process start (before any mode apply).
    /// Restored on true quit so tray-close does not roll anything back.
    /// </summary>
    public class AudioSnapshot
    {
        public string? DefaultRenderDeviceId { get; set; }
        public string? DefaultCaptureDeviceId { get; set; }
        public float? MasterVolume { get; set; }
        public bool? MicMuted { get; set; }
        public List<SessionSnapshot> Sessions { get; set; } = new();
    }

    public class AudioEngine
    {
        private readonly Dictionary<string, string> _iconCache = new();

        // Win32 Shell API for Icon extraction
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        private const uint SHGFI_ICON = 0x000000100;
        private const uint SHGFI_SMALLICON = 0x000000001;

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint flags, System.Text.StringBuilder exeName, ref uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        public AudioEngine()
        {
        }

        /// <summary>
        /// Exe path for folder rules. MainModule fails for elevated processes (common with anti-cheat games);
        /// QueryFullProcessImageName only needs limited-query rights, so try it second.
        /// </summary>
        private static string GetProcessPath(Process process)
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (!string.IsNullOrEmpty(path)) return path;
            }
            catch
            {
                // Higher privilege or exited; fall through
            }

            IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, process.Id);
            if (handle == IntPtr.Zero) return string.Empty;
            try
            {
                var buffer = new System.Text.StringBuilder(1024);
                uint size = (uint)buffer.Capacity;
                return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : string.Empty;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        public List<AudioDevice> GetDevices(bool playbackOnly = false)
        {
            var list = new List<AudioDevice>();
            
            try
            {
                using (var enumerator = new MMDeviceEnumerator())
                {
                    // Playback Devices
                    var renderDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                    string defaultRenderId = string.Empty;
                    try
                    {
                        using (var defaultPlay = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                        {
                            defaultRenderId = defaultPlay.ID;
                        }
                    }
                    catch { }

                    foreach (var d in renderDevices)
                    {
                        list.Add(new AudioDevice
                        {
                            Id = d.ID,
                            Name = d.FriendlyName,
                            IsPlayback = true,
                            IsDefault = (d.ID == defaultRenderId)
                        });
                        d.Dispose();
                    }

                    if (!playbackOnly)
                    {
                        // Capture Devices
                        var captureDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                        string defaultCaptureId = string.Empty;
                        try
                        {
                            using (var defaultRec = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia))
                            {
                                defaultCaptureId = defaultRec.ID;
                            }
                        }
                        catch { }

                        foreach (var d in captureDevices)
                        {
                            list.Add(new AudioDevice
                            {
                                Id = d.ID,
                                Name = d.FriendlyName,
                                IsPlayback = false,
                                IsDefault = (d.ID == defaultCaptureId)
                            });
                            d.Dispose();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error enumerating devices: {ex.Message}");
            }

            return list;
        }

        public void SetDefaultDevice(string deviceId, ERole role = ERole.Multimedia)
        {
            try
            {
                var policyConfig = new PolicyConfigClient() as IPolicyConfig;
                if (policyConfig != null)
                {
                    int hr = policyConfig.SetDefaultEndpoint(deviceId, role);
                    if (hr != 0)
                    {
                        Marshal.ThrowExceptionForHR(hr);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error setting default device: {ex.Message}");
            }
        }

        public List<AudioSession> GetSessions()
        {
            var sessions = new List<AudioSession>();
            try
            {
                using (var enumerator = new MMDeviceEnumerator())
                using (var defaultRender = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                {
                    var sessionManager = defaultRender.AudioSessionManager;
                    var sessionList = sessionManager.Sessions;

                    for (int i = 0; i < sessionList.Count; i++)
                    {
                        using (var session = sessionList[i])
                        {
                            if ((int)session.State == 2) // 2 represents AudioSessionStateExpired
                                continue;

                            uint processId = session.GetProcessID;
                            string processName = string.Empty;
                            string displayName = string.Empty;
                            string processPath = string.Empty;

                            if (processId != 0)
                            {
                                try
                                {
                                    using (var process = Process.GetProcessById((int)processId))
                                    {
                                        processName = process.ProcessName;
                                        displayName = process.MainWindowTitle;
                                        if (string.IsNullOrEmpty(displayName))
                                        {
                                            displayName = processName;
                                        }

                                        processPath = GetProcessPath(process);
                                    }
                                }
                                catch
                                {
                                    displayName = !string.IsNullOrEmpty(session.DisplayName) ? session.DisplayName : "System Sound";
                                    processName = "System Sound";
                                }
                            }
                            else
                            {
                                displayName = !string.IsNullOrEmpty(session.DisplayName) ? session.DisplayName : "System Sounds";
                                processName = "System Sounds";
                            }

                            // Format name nicely
                            if (string.IsNullOrEmpty(displayName) || displayName == "System Sounds" || displayName == "System Sound")
                            {
                                displayName = "System Sounds";
                                processName = "System Sounds";
                            }

                            // Hide inactive / non-app sound controls
                            if (processName == "Idle")
                                continue;

                            // Fetch or extract application icon
                            string iconBase64 = string.Empty;
                            if (!string.IsNullOrEmpty(processPath))
                            {
                                iconBase64 = GetIconFromCacheOrExtract(processPath);
                            }

                            sessions.Add(new AudioSession
                            {
                                SessionId = processId.ToString(),
                                DisplayName = displayName,
                                ProcessName = processName,
                                ProcessPath = processPath,
                                IconBase64 = iconBase64,
                                Volume = session.SimpleAudioVolume.Volume,
                                IsMuted = session.SimpleAudioVolume.Mute,
                                ProcessId = processId
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error fetching audio sessions: {ex.Message}");
            }

            return sessions;
        }

        public void SetSessionVolume(uint processId, float volume)
        {
            try
            {
                using (var enumerator = new MMDeviceEnumerator())
                using (var defaultRender = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                {
                    var sessionManager = defaultRender.AudioSessionManager;
                    var sessionList = sessionManager.Sessions;

                    for (int i = 0; i < sessionList.Count; i++)
                    {
                        using (var session = sessionList[i])
                        {
                            if (session.GetProcessID == processId)
                            {
                                session.SimpleAudioVolume.Volume = Math.Clamp(volume, 0.0f, 1.0f);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error setting session volume: {ex.Message}");
            }
        }

        public void SetSessionMute(uint processId, bool mute)
        {
            try
            {
                using (var enumerator = new MMDeviceEnumerator())
                using (var defaultRender = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                {
                    var sessionManager = defaultRender.AudioSessionManager;
                    var sessionList = sessionManager.Sessions;

                    for (int i = 0; i < sessionList.Count; i++)
                    {
                        using (var session = sessionList[i])
                        {
                            if (session.GetProcessID == processId)
                            {
                                session.SimpleAudioVolume.Mute = mute;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error setting session mute: {ex.Message}");
            }
        }

        /// <summary>
        /// Sets volume/mute for many PIDs in a single pass over the default render device's sessions.
        /// </summary>
        public void ApplySessionLevels(IReadOnlyDictionary<uint, SourceLevel> levels)
        {
            if (levels.Count == 0) return;
            try
            {
                using (var enumerator = new MMDeviceEnumerator())
                using (var defaultRender = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                {
                    var sessionList = defaultRender.AudioSessionManager.Sessions;
                    for (int i = 0; i < sessionList.Count; i++)
                    {
                        using (var session = sessionList[i])
                        {
                            if (levels.TryGetValue(session.GetProcessID, out var level))
                            {
                                session.SimpleAudioVolume.Volume = Math.Clamp(level.Volume, 0.0f, 1.0f);
                                session.SimpleAudioVolume.Mute = level.Mute;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error applying session levels: {ex.Message}");
            }
        }

        public float? GetMasterVolume()
        {
            try
            {
                using (var enumerator = new MMDeviceEnumerator())
                using (var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                {
                    return device.AudioEndpointVolume.MasterVolumeLevelScalar;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error reading master volume: {ex.Message}");
                return null;
            }
        }

        public void SetMasterVolume(float volume)
        {
            try
            {
                using (var enumerator = new MMDeviceEnumerator())
                using (var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                {
                    device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(volume, 0.0f, 1.0f);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error setting master volume: {ex.Message}");
            }
        }

        public bool? GetMicMute()
        {
            try
            {
                using (var enumerator = new MMDeviceEnumerator())
                using (var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications))
                {
                    return device.AudioEndpointVolume.Mute;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error reading mic mute: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Mutes the default mic. Covers both the communications and multimedia defaults in case they differ.
        /// </summary>
        public void SetMicMute(bool mute)
        {
            try
            {
                using (var enumerator = new MMDeviceEnumerator())
                {
                    foreach (var role in new[] { Role.Communications, Role.Multimedia })
                    {
                        try
                        {
                            using (var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, role))
                            {
                                device.AudioEndpointVolume.Mute = mute;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error setting mic mute: {ex.Message}");
            }
        }

        public AudioSnapshot CaptureSnapshot()
        {
            var snap = new AudioSnapshot
            {
                MasterVolume = GetMasterVolume(),
                MicMuted = GetMicMute()
            };

            try
            {
                using (var enumerator = new MMDeviceEnumerator())
                {
                    try
                    {
                        using var render = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                        snap.DefaultRenderDeviceId = render.ID;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Snapshot render default: {ex.Message}");
                    }

                    try
                    {
                        using var capture = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                        snap.DefaultCaptureDeviceId = capture.ID;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Snapshot capture default: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error capturing device defaults: {ex.Message}");
            }

            foreach (var s in GetSessions())
            {
                snap.Sessions.Add(new SessionSnapshot
                {
                    ProcessId = s.ProcessId,
                    ProcessName = s.ProcessName,
                    ProcessPath = s.ProcessPath,
                    Volume = s.Volume,
                    IsMuted = s.IsMuted
                });
            }

            return snap;
        }

        /// <summary>
        /// Restores devices, master volume, mic mute, and any still-running sessions from a startup snapshot.
        /// </summary>
        public void RestoreSnapshot(AudioSnapshot? snap)
        {
            if (snap == null) return;

            try
            {
                if (!string.IsNullOrEmpty(snap.DefaultRenderDeviceId))
                {
                    SetDefaultDevice(snap.DefaultRenderDeviceId, ERole.Console);
                    SetDefaultDevice(snap.DefaultRenderDeviceId, ERole.Multimedia);
                    SetDefaultDevice(snap.DefaultRenderDeviceId, ERole.Communications);
                }

                if (!string.IsNullOrEmpty(snap.DefaultCaptureDeviceId))
                {
                    SetDefaultDevice(snap.DefaultCaptureDeviceId, ERole.Console);
                    SetDefaultDevice(snap.DefaultCaptureDeviceId, ERole.Multimedia);
                    SetDefaultDevice(snap.DefaultCaptureDeviceId, ERole.Communications);
                }

                if (snap.MasterVolume is float master)
                    SetMasterVolume(master);

                if (snap.MicMuted is bool micMuted)
                    SetMicMute(micMuted);

                if (snap.Sessions.Count == 0) return;

                var current = GetSessions();
                var levels = new Dictionary<uint, SourceLevel>();

                foreach (var saved in snap.Sessions)
                {
                    AudioSession? match = null;
                    foreach (var c in current)
                    {
                        if (c.ProcessId != 0 && c.ProcessId == saved.ProcessId)
                        {
                            match = c;
                            break;
                        }
                    }

                    if (match == null)
                    {
                        foreach (var c in current)
                        {
                            if (!string.Equals(c.ProcessName, saved.ProcessName, StringComparison.OrdinalIgnoreCase))
                                continue;
                            if (!string.IsNullOrEmpty(saved.ProcessPath)
                                && !string.Equals(c.ProcessPath, saved.ProcessPath, StringComparison.OrdinalIgnoreCase))
                                continue;
                            match = c;
                            break;
                        }
                    }

                    if (match == null) continue;
                    if (levels.ContainsKey(match.ProcessId)) continue;

                    levels[match.ProcessId] = new SourceLevel
                    {
                        Volume = saved.Volume,
                        Mute = saved.IsMuted
                    };
                }

                ApplySessionLevels(levels);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error restoring audio snapshot: {ex.Message}");
            }
        }

        private string GetIconFromCacheOrExtract(string path)
        {
            if (_iconCache.TryGetValue(path, out var cached))
            {
                return cached;
            }

            string base64 = ExtractIconAsBase64(path);
            if (!string.IsNullOrEmpty(base64))
            {
                _iconCache[path] = base64;
            }
            return base64;
        }

        private string ExtractIconAsBase64(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return string.Empty;

            var shinfo = new SHFILEINFO();
            IntPtr hImg = SHGetFileInfo(filePath, 0, ref shinfo, (uint)Marshal.SizeOf(shinfo), SHGFI_ICON | SHGFI_SMALLICON);

            if (shinfo.hIcon == IntPtr.Zero)
                return string.Empty;

            try
            {
                using (var icon = Icon.FromHandle(shinfo.hIcon))
                using (var bitmap = icon.ToBitmap())
                using (var ms = new MemoryStream())
                {
                    bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    byte[] bytes = ms.ToArray();
                    return "data:image/png;base64," + Convert.ToBase64String(bytes);
                }
            }
            catch
            {
                return string.Empty;
            }
            finally
            {
                DestroyIcon(shinfo.hIcon);
            }
        }
    }
}
