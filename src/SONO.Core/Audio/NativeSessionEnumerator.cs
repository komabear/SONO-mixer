using System.Runtime.InteropServices;

namespace SONO.Core.Audio;

/// <summary>
/// Fully native audio-session enumeration with DETERMINISTIC COM release — the root-cause
/// fix for the Windows Audio service (Audiosrv) memory creep. Audiosrv allocates
/// server-side state for every IAudioSessionEnumerator a client creates and frees it only
/// when the client releases it. NAudio's AudioSessionManager.RefreshSessions creates one
/// behind an RCW that only a GC finalizer frees; at SONO's 600ms poll that was ~150MB/hour
/// in Audiosrv. Here every COM pointer is a raw IntPtr released via the interface's own
/// IUnknown::Release the moment we are done with it — no RCWs, no finalizers, no growth.
///
/// Every GUID and vtable slot below was verified against the Windows 11 SDK headers
/// (mmdeviceapi.h / audiopolicy.h / Audioclient.h / endpointvolume.h, SDK 10.0.26100):
/// vtable slots 0-2 are always QueryInterface/AddRef/Release; interface methods follow in
/// header declaration order. Calls go through raw function pointers — do NOT pass these
/// IntPtrs to Marshal.GetObjectForIUnknown (that would recreate the RCW lifetime we are
/// removing). Callers run on threadpool/MTA threads and pointers are used and released
/// within one tick, so no cross-thread lifetime issues arise.
/// </summary>
public static unsafe class NativeSessionEnumerator
{
    // ---- CLSIDs / IIDs (verified in the SDK headers) ----
    private static readonly Guid CLSID_MMDeviceEnumerator   = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IMMDeviceEnumerator    = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IID_IMMDevice              = new("D666063F-1587-4E43-81F1-B948E807363F");
    private static readonly Guid IID_IAudioSessionManager2  = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private static readonly Guid IID_IAudioSessionControl2  = new("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d");
    private static readonly Guid IID_ISimpleAudioVolume     = new("87CE5498-68D6-44E5-9215-6DA47EF883D8");

    private static readonly PROPERTYKEY PKEY_Device_FriendlyName = new()
    {
        FormatId = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),   // functiondiscoverykeys_devpkey.h
        PropertyId = 14,
    };

    private const uint CLSCTX_ALL = 23;
    private const int ERender = 0;          // EDataFlow.eRender
    private const int EConsole = 0;         // ERole.eConsole
    private const uint DEVICE_STATE_ACTIVE = 1;
    private const uint STGM_READ = 0;
    private const short VT_LPWSTR = 31;

    // ---- raw COM plumbing ----

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, nint outer, uint clsContext, ref Guid iid, out nint obj);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pv);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    // PROPVARIANT — 8-byte packed or the union pointer misreads on x64
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct PropVariant
    {
        public short VariantType;
        private short _r1, _r2, _r3;
        public nint Pointer;
        private nint _p2;
    }

    /// <summary>vtable method pointer: obj = interface pointer, slot = vtable index.</summary>
    private static nint Vt(nint obj, int slot) => (*(nint**)obj)[slot];

    private static bool Qi(nint obj, ref Guid iid, out nint outIface)
    {
        outIface = 0;
        Guid iidLocal = iid;   // ref/out params are not fixed — copy to locals before taking &
        nint found = 0;
        var fn = (delegate* unmanaged<nint, Guid*, nint*, int>)Vt(obj, 0);
        fn(obj, &iidLocal, &found);
        outIface = found;
        return outIface != 0;
    }

    private static void ComRelease(nint obj)
    {
        if (obj != 0) _ = ((delegate* unmanaged<nint, int>)Vt(obj, 2))(obj);
    }

    // ---- IMMDeviceEnumerator (mmdeviceapi.h): 3 EnumAudioEndpoints, 4 GetDefaultAudioEndpoint ----

    private static bool EnumAudioEndpoints(nint en, out nint collection)
    {
        nint col = 0;
        var fn = (delegate* unmanaged<nint, int, uint, nint*, int>)Vt(en, 3);
        bool ok = fn(en, ERender, DEVICE_STATE_ACTIVE, &col) == 0 && col != 0;
        collection = col;
        return ok;
    }

    private static bool GetDefaultAudioEndpoint(nint en, out nint device)
    {
        nint dev = 0;
        var iid = IID_IMMDevice;
        var fn = (delegate* unmanaged<nint, int, int, Guid*, nint*, int>)Vt(en, 4);
        bool ok = fn(en, ERender, EConsole, &iid, &dev) == 0 && dev != 0;
        device = dev;
        return ok;
    }

    // ---- IMMDevice (mmdeviceapi.h): 3 Activate, 4 OpenPropertyStore, 5 GetId ----

    private static bool DeviceGetId(nint dev, out string id)
    {
        id = "";
        nint pw = 0;
        var fn = (delegate* unmanaged<nint, nint*, int>)Vt(dev, 5);
        if (fn(dev, &pw) != 0 || pw == 0) return false;
        id = Marshal.PtrToStringUni(pw) ?? "";
        Marshal.FreeCoTaskMem(pw);
        return id.Length > 0;
    }

    private static bool DeviceOpenPropertyStore(nint dev, out nint store)
    {
        nint st = 0;
        var fn = (delegate* unmanaged<nint, uint, nint*, int>)Vt(dev, 4);
        bool ok = fn(dev, STGM_READ, &st) == 0 && st != 0;
        store = st;
        return ok;
    }

    // ---- IMMDeviceCollection (mmdeviceapi.h): 3 GetCount, 4 Item ----

    private static bool CollectionCount(nint col, out int count)
    {
        int c = 0;
        var fn = (delegate* unmanaged<nint, int*, int>)Vt(col, 3);
        bool ok = fn(col, &c) == 0;
        count = c;
        return ok;
    }

    private static bool CollectionItem(nint col, int index, out nint dev)
    {
        nint d = 0;
        var fn = (delegate* unmanaged<nint, int, nint*, int>)Vt(col, 4);
        bool ok = fn(col, index, &d) == 0 && d != 0;
        dev = d;
        return ok;
    }

    // ---- IMMDevice.Activate (slot 3) ----

    private static bool DeviceActivate(nint dev, ref Guid iid, out nint iface)
    {
        Guid iidLocal = iid;
        nint it = 0;
        var fn = (delegate* unmanaged<nint, Guid*, uint, nint, nint*, int>)Vt(dev, 3);
        bool ok = fn(dev, &iidLocal, CLSCTX_ALL, 0, &it) == 0 && it != 0;
        iface = it;
        return ok;
    }

    // ---- IAudioSessionManager2 (audiopolicy.h): 5 GetSessionEnumerator ----

    private static bool GetSessionEnumerator(nint mgr, out nint se)
    {
        nint e = 0;
        var fn = (delegate* unmanaged<nint, nint*, int>)Vt(mgr, 5);
        bool ok = fn(mgr, &e) == 0 && e != 0;
        se = e;
        return ok;
    }

    // ---- IAudioSessionEnumerator (audiopolicy.h): 3 GetCount, 4 GetSession ----

    private static bool SessionCount(nint se, out int count)
    {
        int c = 0;
        var fn = (delegate* unmanaged<nint, int*, int>)Vt(se, 3);
        bool ok = fn(se, &c) == 0;
        count = c;
        return ok;
    }

    private static bool SessionAt(nint se, int index, out nint control)
    {
        nint c = 0;
        var fn = (delegate* unmanaged<nint, int, nint*, int>)Vt(se, 4);
        bool ok = fn(se, index, &c) == 0 && c != 0;
        control = c;
        return ok;
    }

    // ---- IAudioSessionControl2: base IAudioSessionControl slots 3-11, then
    //      12 GetSessionIdentifier, 13 GetSessionInstanceIdentifier, 14 GetProcessId,
    //      15 IsSystemSoundsSession, 16 SetDuckingPreference ----

    private static bool CtlGetState(nint ctl, out int state)
    {
        int s = 0;
        var fn = (delegate* unmanaged<nint, int*, int>)Vt(ctl, 3);
        bool ok = fn(ctl, &s) == 0;
        state = s;
        return ok;
    }

    private static bool CtlGetDisplayName(nint ctl, out string name)
    {
        name = "";
        nint pw = 0;
        var fn = (delegate* unmanaged<nint, nint*, int>)Vt(ctl, 4);
        if (fn(ctl, &pw) != 0 || pw == 0) return false;
        name = Marshal.PtrToStringUni(pw) ?? "";
        Marshal.FreeCoTaskMem(pw);
        return true;
    }

    private static bool CtlGetSessionIdentifier(nint ctl, out string sid)
    {
        sid = "";
        nint pw = 0;
        var fn = (delegate* unmanaged<nint, nint*, int>)Vt(ctl, 12);
        if (fn(ctl, &pw) != 0 || pw == 0) return false;
        sid = Marshal.PtrToStringUni(pw) ?? "";
        Marshal.FreeCoTaskMem(pw);
        return true;
    }

    private static bool CtlGetProcessId(nint ctl, out uint pid)
    {
        uint p = 0;
        var fn = (delegate* unmanaged<nint, uint*, int>)Vt(ctl, 14);
        bool ok = fn(ctl, &p) == 0;
        pid = p;
        return ok;
    }

    /// <summary>S_OK (true) only for the per-device system-sounds session.</summary>
    private static bool CtlIsSystemSoundsSession(nint ctl)
    {
        var fn = (delegate* unmanaged<nint, int>)Vt(ctl, 15);
        return fn(ctl) == 0;
    }

    // ---- ISimpleAudioVolume (Audioclient.h): 3 SetMasterVolume, 4 GetMasterVolume,
    //      5 SetMute, 6 GetMute. BOOL is the 4-byte Win32 BOOL — passed as int. ----

    public static bool VolumeGet(nint vol, out float level)
    {
        float l = 1f;
        var fn = (delegate* unmanaged<nint, float*, int>)Vt(vol, 4);
        bool ok = fn(vol, &l) == 0;
        level = l;
        return ok;
    }

    public static bool VolumeGetMute(nint vol, out bool mute)
    {
        int m = 0;
        var fn = (delegate* unmanaged<nint, int*, int>)Vt(vol, 6);
        bool ok = fn(vol, &m) == 0;
        mute = m != 0;
        return ok;
    }

    public static bool VolumeSet(nint vol, float level)
    {
        var fn = (delegate* unmanaged<nint, float, nint, int>)Vt(vol, 3);
        return fn(vol, level, 0) == 0;
    }

    public static bool VolumeSetMute(nint vol, bool mute)
    {
        var fn = (delegate* unmanaged<nint, int, nint, int>)Vt(vol, 5);
        return fn(vol, mute ? 1 : 0, 0) == 0;
    }

    // ---- public surface ----

    /// <summary>One live audio session. Both COM pointers are owned by the holder —
    /// release with <see cref="ReleaseSession"/> exactly once. VolumePtr may be 0 when
    /// the ISimpleAudioVolume QI failed (session listable but not enforceable).</summary>
    public readonly record struct NativeSession(
        nint Control2Ptr,       // IAudioSessionControl2*
        nint VolumePtr,         // ISimpleAudioVolume*
        uint Pid,
        int State,              // AudioSessionState: 0 Inactive, 1 Active, 2 Expired
        string Key,             // "pid:hash(sessionIdentifier)" — same format NAudio produced
        string DisplayName,     // COM display name (often empty; engine derives a prettier one)
        bool SystemSounds);

    public static void ReleaseSession(in NativeSession s)
    {
        ComRelease(s.VolumePtr);
        ComRelease(s.Control2Ptr);
    }

    /// <summary>Friendly name cache per device id — names do not change while plugged in;
    /// the IPropertyStore/PropVariant read happens only on first sight of a device.</summary>
    private static readonly Dictionary<string, string> _deviceNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Enumerate the sessions of EVERY active render endpoint in one self-contained pass.
    /// All COM objects created here are released deterministically before returning; the
    /// returned sessions belong to the caller (release each via ReleaseSession — including
    /// on exception paths). Never throws; on failure returns what was collected + error.
    /// </summary>
    public static List<NativeSession> EnumerateAllRenderSessions(out string defaultDeviceId, out string defaultDeviceName, out string? error)
    {
        var result = new List<NativeSession>();
        defaultDeviceId = "";
        defaultDeviceName = "";
        error = null;

        nint en = 0, collection = 0;
        try
        {
            Guid clsid = CLSID_MMDeviceEnumerator, iidEn = IID_IMMDeviceEnumerator;
            if (CoCreateInstance(ref clsid, 0, CLSCTX_ALL, ref iidEn, out en) != 0 || en == 0)
            {
                error = "CoCreateInstance(MMDeviceEnumerator) failed";
                return result;
            }

            // default device: id every tick, friendly name cached per id
            if (GetDefaultAudioEndpoint(en, out nint defDev))
            {
                try
                {
                    if (DeviceGetId(defDev, out defaultDeviceId) &&
                        !_deviceNames.TryGetValue(defaultDeviceId, out defaultDeviceName))
                    {
                        defaultDeviceName = ReadFriendlyName(defDev);
                        _deviceNames[defaultDeviceId] = defaultDeviceName;
                    }
                }
                finally { ComRelease(defDev); }
            }

            if (!EnumAudioEndpoints(en, out collection))
            {
                error = "EnumAudioEndpoints failed";
                return result;
            }
            if (!CollectionCount(collection, out int devCount))
            {
                error = "IMMDeviceCollection::GetCount failed";
                return result;
            }

            for (int d = 0; d < devCount; d++)
            {
                if (!CollectionItem(collection, d, out nint dev)) continue;
                try { CollectDeviceSessions(dev, result); }
                finally { ComRelease(dev); }
            }
            return result;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return result;
        }
        finally
        {
            ComRelease(collection);
            ComRelease(en);
        }
    }

    /// <summary>All active render endpoints (id + friendly name) for the output pickers
    /// (main window dropdown, settings visibility list). Deterministic COM release like
    /// everything else here; names cached per id.</summary>
    public static List<(string Id, string Name)> ActiveRenderDevices()
    {
        var list = new List<(string Id, string Name)>();
        nint en = 0, collection = 0;
        try
        {
            Guid clsid = CLSID_MMDeviceEnumerator, iidEn = IID_IMMDeviceEnumerator;
            if (CoCreateInstance(ref clsid, 0, CLSCTX_ALL, ref iidEn, out en) != 0 || en == 0) return list;
            if (!EnumAudioEndpoints(en, out collection)) return list;
            if (!CollectionCount(collection, out int count)) return list;
            for (int i = 0; i < count; i++)
            {
                if (!CollectionItem(collection, i, out nint dev)) continue;
                try
                {
                    if (DeviceGetId(dev, out string id))
                    {
                        if (!_deviceNames.TryGetValue(id, out string? name))
                        {
                            name = ReadFriendlyName(dev) ?? "";
                            _deviceNames[id] = name;
                        }
                        list.Add((id, name));
                    }
                }
                finally { ComRelease(dev); }
            }
        }
        catch { }
        finally
        {
            ComRelease(collection);
            ComRelease(en);
        }
        return list;
    }

    private static void CollectDeviceSessions(nint dev, List<NativeSession> into)
    {
        nint mgr = 0, se = 0;
        try
        {
            Guid iidMgr = IID_IAudioSessionManager2;
            if (!DeviceActivate(dev, ref iidMgr, out mgr)) return;
            if (!GetSessionEnumerator(mgr, out se)) return;
            if (!SessionCount(se, out int count)) return;

            for (int i = 0; i < count; i++)
            {
                if (!SessionAt(se, i, out nint control)) continue;
                try
                {
                    var s = BuildSession(control);
                    if (s.HasValue) into.Add(s.Value);
                }
                finally { ComRelease(control); }   // GetSession's ref; ctl2/volume are separate QI refs
            }
        }
        catch
        {
            // a device failing mid-pass must not sink the others; per-session failures
            // are isolated inside BuildSession
        }
        finally
        {
            ComRelease(se);
            ComRelease(mgr);
        }
    }

    /// <summary>QIs the session control to Control2 + ISimpleAudioVolume and reads identity.
    /// Releases everything it acquired if a read fails midway (returns null).</summary>
    private static NativeSession? BuildSession(nint control)
    {
        nint ctl2 = 0, vol = 0;
        try
        {
            var iid2 = IID_IAudioSessionControl2;
            if (!Qi(control, ref iid2, out ctl2)) return null;

            var iidVol = IID_ISimpleAudioVolume;
            Qi(control, ref iidVol, out vol);   // may fail → 0: listed but not enforceable

            CtlGetState(ctl2, out int state);
            CtlGetProcessId(ctl2, out uint pid);
            string sid = CtlGetSessionIdentifier(ctl2, out var sidRead) ? sidRead : "";
            string name = CtlGetDisplayName(ctl2, out var nameRead) ? nameRead : "";
            bool sys = CtlIsSystemSoundsSession(ctl2);

            int sidHash = sid.Length > 0 ? sid.GetHashCode() : 0;
            return new NativeSession(ctl2, vol, pid, state, $"{pid}:{sidHash}", name, sys);
        }
        catch
        {
            ComRelease(vol);
            ComRelease(ctl2);
            return null;
        }
    }

    private static string ReadFriendlyName(nint dev)
    {
        nint store = 0;
        try
        {
            if (!DeviceOpenPropertyStore(dev, out store)) return "";
            var key = PKEY_Device_FriendlyName;
            PropVariant pv = default;
            var fn = (delegate* unmanaged<nint, PROPERTYKEY*, PropVariant*, int>)Vt(store, 5);  // IPropertyStore.GetValue
            if (fn(store, &key, &pv) != 0) { PropVariantClear(ref pv); return ""; }
            string name = pv.VariantType == VT_LPWSTR ? Marshal.PtrToStringUni(pv.Pointer) ?? "" : "";
            PropVariantClear(ref pv);
            return name;
        }
        catch { return ""; }
        finally { ComRelease(store); }
    }
}
