using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace audio_mixer
{
    /// <summary>One way of calling SetDefaultEndpoint. Returns the HRESULT; throws if the COM object/interface is unavailable.</summary>
    public sealed class PolicyBackend
    {
        public string Name { get; }
        public Func<string, ERole, int> SetDefaultEndpoint { get; }

        public PolicyBackend(string name, Func<string, ERole, int> setDefaultEndpoint)
        {
            Name = name;
            SetDefaultEndpoint = setDefaultEndpoint;
        }
    }

    /// <summary>
    /// Sets the default audio endpoint through the undocumented policy-config COM API. Tries each backend in turn
    /// (remembering the one that last worked) and reports failure instead of silently doing nothing.
    /// </summary>
    public sealed class DefaultDeviceSwitcher
    {
        private readonly IReadOnlyList<PolicyBackend> _backends;
        private readonly Action<string> _log;
        private int _preferred;

        public string? LastError { get; private set; }

        public DefaultDeviceSwitcher(IReadOnlyList<PolicyBackend> backends, Action<string> log)
        {
            _backends = backends;
            _log = log;
        }

        public static PolicyBackend PolicyConfig { get; } =
            new("IPolicyConfig", (id, role) => CallCom<IPolicyConfig>(new PolicyConfigClient(), p => p.SetDefaultEndpoint(id, role)));

        public static PolicyBackend PolicyConfigVista { get; } =
            new("IPolicyConfigVista", (id, role) => CallCom<IPolicyConfigVista>(new PolicyConfigVistaClient(), p => p.SetDefaultEndpoint(id, role)));

        public static void LogToTrace(string message)
        {
            Trace.TraceError(message);
            Debug.WriteLine(message);
        }

        public static DefaultDeviceSwitcher Shared { get; } = new(new[] { PolicyConfig, PolicyConfigVista }, LogToTrace);

        private static int CallCom<T>(object comObject, Func<T, int> call) where T : class
        {
            try
            {
                // The coclass only answers its own interface; a mismatch surfaces as InvalidCastException (E_NOINTERFACE)
                return call((T)comObject);
            }
            finally
            {
                ComRelease.Release(comObject);
            }
        }

        public bool TrySet(string deviceId, ERole role)
        {
            if (string.IsNullOrEmpty(deviceId))
            {
                Fail($"SetDefaultDevice({role}): no device id given");
                return false;
            }

            var errors = new List<string>();
            int count = _backends.Count;
            int start = Volatile.Read(ref _preferred);
            for (int n = 0; n < count; n++)
            {
                int index = (start + n) % count;
                var backend = _backends[index];
                try
                {
                    int hr = backend.SetDefaultEndpoint(deviceId, role);
                    if (hr == 0)
                    {
                        Volatile.Write(ref _preferred, index);
                        LastError = null;
                        return true;
                    }
                    errors.Add($"{backend.Name}: HRESULT 0x{hr:X8}");
                }
                catch (Exception ex)
                {
                    errors.Add($"{backend.Name}: {ex.GetType().Name} {ex.Message}");
                }
            }

            Fail($"SetDefaultDevice({role}, {deviceId}) failed on every policy-config interface: {string.Join("; ", errors)}");
            return false;
        }

        private void Fail(string message)
        {
            LastError = message;
            _log(message);
        }
    }
}