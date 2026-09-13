using System;
using System.Runtime.InteropServices;

namespace audio_mixer
{
    public enum ERole
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    internal class PolicyConfigClient
    {
    }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-875f3408a28f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(string pszDeviceName, out IntPtr ppFormat);
        [PreserveSig] int GetDeviceFormat(string pszDeviceName, bool bDefault, out IntPtr ppFormat);
        [PreserveSig] int ResetDeviceFormat(string pszDeviceName);
        [PreserveSig] int SetDeviceFormat(string pszDeviceName, IntPtr pEndpointFormat);
        [PreserveSig] int GetProcessingPeriod(string pszDeviceName, bool bDefault, out long pmftDefault, out long pmftMinimum);
        [PreserveSig] int SetProcessingPeriod(string pszDeviceName, long pmftPeriod);
        [PreserveSig] int GetShareMode(string pszDeviceName, out int pMode);
        [PreserveSig] int SetShareMode(string pszDeviceName, int mode);
        [PreserveSig] int GetPropertyValue(string pszDeviceName, bool bDefault, IntPtr pKey, out IntPtr pv);
        [PreserveSig] int SetPropertyValue(string pszDeviceName, bool bDefault, IntPtr pKey, IntPtr pv);
        [PreserveSig] int SetDefaultEndpoint(string pszDeviceName, ERole role);
        [PreserveSig] int SetEndpointVisibility(string pszDeviceName, bool bVisible);
    }
}
