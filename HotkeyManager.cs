using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace audio_mixer
{
    public class HotkeyManager
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int WM_HOTKEY = 0x0312;

        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;
        public const uint MOD_NOREPEAT = 0x4000;

        private readonly IntPtr _hWnd;
        private readonly Dictionary<int, Action> _callbacks = new();
        private int _currentId = 1;

        public HotkeyManager(IntPtr hWnd)
        {
            _hWnd = hWnd;
        }

        public bool Register(ModifierKeys modifiers, Key key, Action callback)
        {
            uint fsModifiers = 0;
            if (modifiers.HasFlag(ModifierKeys.Alt)) fsModifiers |= MOD_ALT;
            if (modifiers.HasFlag(ModifierKeys.Control)) fsModifiers |= MOD_CONTROL;
            if (modifiers.HasFlag(ModifierKeys.Shift)) fsModifiers |= MOD_SHIFT;
            if (modifiers.HasFlag(ModifierKeys.Windows)) fsModifiers |= MOD_WIN;
            
            // Add no repeat for cleaner hotkey triggering
            fsModifiers |= MOD_NOREPEAT;

            // Convert WPF key to Win32 Virtual Key
            int virtualKey = KeyInterop.VirtualKeyFromKey(key);

            int id = _currentId++;
            bool success = RegisterHotKey(_hWnd, id, fsModifiers, (uint)virtualKey);
            
            if (success)
            {
                _callbacks[id] = callback;
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"Failed to register hotkey for modifiers {modifiers} and key {key}.");
            }

            return success;
        }

        public void UnregisterAll()
        {
            foreach (int id in _callbacks.Keys)
            {
                UnregisterHotKey(_hWnd, id);
            }
            _callbacks.Clear();
            _currentId = 1;
        }

        public void ProcessMessage(int msg, IntPtr wParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (_callbacks.TryGetValue(id, out var callback))
                {
                    callback.Invoke();
                    handled = true;
                }
            }
        }
    }
}
