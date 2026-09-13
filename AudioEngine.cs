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

        public AudioEngine()
        {
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

                                        try
                                        {
                                            processPath = process.MainModule?.FileName ?? string.Empty;
                                        }
                                        catch
                                        {
                                            // Process might be running with higher privilege or exited
                                        }
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
