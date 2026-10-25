# probe-umik.ps1 - read the Windows capture endpoint for the UMIK-1: name, mute, level.
# The meter sits at -54 dBFS and does not move when the capsule is tapped, so the question
# is whether Windows is attenuating it before ffmpeg ever sees it.
param(
    [string]$Match = 'UMIK',
    [int]$Flow = 1        # 0 = render (speakers), 1 = capture (microphones)
)

Add-Type -Language CSharp @'
using System;
using System.Runtime.InteropServices;

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
class MMDeviceEnumeratorComObject { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    int GetDevice(string id, out IMMDevice device);
    int RegisterEndpointNotificationCallback(IntPtr client);
    int UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceCollection
{
    int GetCount(out int count);
    int Item(int index, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    int OpenPropertyStore(int access, out IPropertyStore properties);
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    int GetState(out int state);
}

[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPropertyStore
{
    int GetCount(out int count);
    int GetAt(int index, out PROPERTYKEY key);
    int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
    int Commit();
}

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioEndpointVolume
{
    int RegisterControlChangeNotify(IntPtr notify);
    int UnregisterControlChangeNotify(IntPtr notify);
    int GetChannelCount(out int count);
    int SetMasterVolumeLevel(float levelDb, Guid context);
    int SetMasterVolumeLevelScalar(float level, Guid context);
    int GetMasterVolumeLevel(out float levelDb);
    int GetMasterVolumeLevelScalar(out float level);
    int SetChannelVolumeLevel(uint channel, float levelDb, Guid context);
    int SetChannelVolumeLevelScalar(uint channel, float level, Guid context);
    int GetChannelVolumeLevel(uint channel, out float levelDb);
    int GetChannelVolumeLevelScalar(uint channel, out float level);
    int SetMute(int mute, Guid context);
    int GetMute(out int mute);
}

[StructLayout(LayoutKind.Sequential)]
public struct PROPERTYKEY { public Guid fmtid; public int pid; }

[StructLayout(LayoutKind.Explicit)]
public struct PROPVARIANT
{
    [FieldOffset(0)] public short vt;
    [FieldOffset(8)] public IntPtr p;
}

public class CapEndpoint
{
    public string Name = "";
    public string Id = "";
    public float Scalar = -1f;
    public float Db = 0f;
    public bool Muted = false;
    public int Channels = 0;
    public string Error = "";

    static string ReadString(IPropertyStore store, PROPERTYKEY key)
    {
        PROPVARIANT v;
        if (store.GetValue(ref key, out v) != 0) { return ""; }
        if (v.vt != 31) { return ""; }          // VT_LPWSTR
        return Marshal.PtrToStringUni(v.p);
    }

    public static CapEndpoint[] List(int flow)
    {
        var result = new System.Collections.Generic.List<CapEndpoint>();
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        IMMDeviceCollection col;
        en.EnumAudioEndpoints(flow, 1, out col);    // flow: 0 = render, 1 = capture; DEVICE_STATE_ACTIVE
        int n;
        col.GetCount(out n);
        for (int i = 0; i < n; i++)
        {
            IMMDevice dev;
            col.Item(i, out dev);
            var e = new CapEndpoint();
            try
            {
                IPropertyStore ps;
                dev.OpenPropertyStore(0, out ps);
                var key = new PROPERTYKEY();
                key.fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"); // PKEY_Device_FriendlyName
                key.pid = 14;
                e.Name = ReadString(ps, key);
                dev.GetId(out e.Id);

                Guid iid = typeof(IAudioEndpointVolume).GUID;
                object o;
                dev.Activate(ref iid, 1, IntPtr.Zero, out o);   // CLSCTX_INPROC_SERVER
                var vol = (IAudioEndpointVolume)o;
                vol.GetMasterVolumeLevelScalar(out e.Scalar);
                vol.GetMasterVolumeLevel(out e.Db);
                int m;
                vol.GetMute(out m);
                e.Muted = m != 0;
                vol.GetChannelCount(out e.Channels);
            }
            catch (Exception ex) { e.Error = ex.Message; }
            result.Add(e);
        }
        return result.ToArray();
    }
}
'@

$eps = [CapEndpoint]::List($Flow)
foreach ($e in $eps) {
    $mark = if ($e.Name -match $Match) { '>>' } else { '  ' }
    $pct  = if ($e.Scalar -ge 0) { "{0,6:N1}%" -f ($e.Scalar * 100) } else { "  ?    " }
    $db   = if ($e.Scalar -ge 0) { "{0,7:N1} dB" -f $e.Db } else { "         " }
    Write-Output ("{0} {1,-38} vol {2}  {3}  mute {4}  ch {5}" -f $mark, $e.Name, $pct, $db, $e.Muted, $e.Channels)
    if ($e.Error) { Write-Output ("     error: " + $e.Error) }
}

