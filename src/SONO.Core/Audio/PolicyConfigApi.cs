using System.Runtime.InteropServices;

namespace SONO.Core.Audio;

/// <summary>
/// Undocumented-but-stable PolicyConfig COM API (used by the Sound settings panel itself)
/// to change the DEFAULT render endpoint. Guids are the well-known ones shipped since Vista.
/// </summary>
public static class PolicyConfigApi
{
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient { }

    [Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(nint a, nint b);
        [PreserveSig] int GetDeviceFormat(nint a, int b, nint c);
        [PreserveSig] int ResetDeviceFormat(nint a);
        [PreserveSig] int SetDeviceFormat(nint a, nint b, nint c);
        [PreserveSig] int GetProcessingPeriod(nint a, int b, nint c, nint d);
        [PreserveSig] int SetProcessingPeriod(nint a, nint b);
        [PreserveSig] int GetShareMode(nint a, nint b);
        [PreserveSig] int SetShareMode(nint a, nint b);
        [PreserveSig] int GetPropertyValue(nint a, ref PropertyKey b, ref PropVariant c);
        [PreserveSig] int SetPropertyValue(nint a, ref PropertyKey b, ref PropVariant c);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        [PreserveSig] int SetEndpointVisibility(nint a, int b);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public int PropertyId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public short VariantType;
        public int Reserved1, Reserved2, Reserved3;
        public nint Pointer;
        public int Reserved4;
    }

    // role: 0 = Console, 1 = Multimedia, 2 = Communications
    public static void SetDefaultDevice(string deviceId, int role = 0)
    {
        var client = (IPolicyConfig)new PolicyConfigClient();
        int hr = client.SetDefaultEndpoint(deviceId, role);
        if (hr != 0) throw new InvalidOperationException($"SetDefaultEndpoint failed: 0x{hr:X8}");
    }
}
