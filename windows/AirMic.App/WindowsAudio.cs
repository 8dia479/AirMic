using System.Runtime.InteropServices;
using AirMic.Core;

namespace AirMic.App;

internal sealed record AudioEndpoint(string Id, string Name, bool Capture, bool IsAirMic)
{
    // VB-CABLE playback endpoint ("CABLE Input") routes audio to its paired
    // recording endpoint ("CABLE Output"), which conferencing apps can select.
    public bool IsVirtualCable => Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase)
        || Name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase)
        || Name.Contains("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase);
    public override string ToString() => Name;
}

internal static class WindowsAudio
{
    public static List<AudioEndpoint> Enumerate(bool capture)
    {
        var result = new List<AudioEndpoint>();
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorObject();
        IMMDeviceCollection? collection = null;
        try
        {
            Check(enumerator.EnumAudioEndpoints(capture ? 1 : 0, 1, out collection));
            Check(collection.GetCount(out uint count));
            for (uint i = 0; i < count; i++)
            {
                Check(collection.Item(i, out IMMDevice device));
                IPropertyStore? properties = null;
                try
                {
                    Check(device.GetId(out string id)); Check(device.OpenPropertyStore(0, out properties));
                    var key = new PropertyKey { FormatId = new("a45c254e-df1c-4efd-8020-67d146a850e0"), PropertyId = 14 };
                    Check(properties.GetValue(ref key, out PropVariant name));
                    try { result.Add(new(id, (name.Type == 31 ? Marshal.PtrToStringUni(name.Pointer) : null) ?? id, capture, AudioDeviceIdentity.IsAirMic(properties))); }
                    finally { Clear(ref name); }
                }
                finally { Release(properties); Release(device); }
            }
            return result;
        }
        finally { Release(collection); Release(enumerator); }
    }
    public static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
    public static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
    public static void Clear(ref PropVariant value) => PropVariantClear(ref value);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
}

internal readonly record struct RenderStats(long Played, long Lost, long Plc, long Duplicates, long Late,
    long Underruns, long QueueBuffered, long Target, long BufferFrames, long PeakMilli, long Resyncs,
    long LatencyDrops, long Holds, double EndpointLatencyMs, string Error);

// Dedicated MTA render owner. WPF and network never execute on this thread.
internal sealed class WasapiRenderer : IDisposable
{
    private readonly string endpointId;
    private readonly PcmPlayout playout;
    private readonly bool requireAirMic;
    private readonly ManualResetEvent stop = new(false);
    private readonly AutoResetEvent audioEvent = new(false);
    private readonly ManualResetEventSlim ready = new(false);
    private readonly Thread thread;
    private string error = "";
    private long played, lost, plc, duplicates, late, underruns, buffered, target, bufferFrames, peak, resyncs, trimmed, holds, latencyUs;
    private int disposed;
    public WasapiRenderer(string endpointId, AudioQueue queue, int minimum, int maximum, float volume, bool requireAirMic)
    {
        this.endpointId = endpointId; this.requireAirMic = requireAirMic;
        playout = new(queue, minimum, maximum) { Volume = volume };
        thread = new Thread(RenderLoop) { IsBackground = true, Name = "AirMic WASAPI", Priority = ThreadPriority.Highest };
        thread.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(10))) { Dispose(); throw new TimeoutException("WASAPI 초기화 시간 초과"); }
        if (Volatile.Read(ref error).Length != 0) { string message = error; Dispose(); throw new InvalidOperationException(message); }
    }
    public float Volume { set => playout.Volume = value; }
    private unsafe void RenderLoop()
    {
        object? enumeratorObject = null, deviceObject = null, clientObject = null, renderObject = null;
        IAudioClient? client = null;
        IntPtr mmcss = IntPtr.Zero;
        bool comInitialized = false;
        try
        {
            WindowsAudio.Check(CoInitializeEx(IntPtr.Zero, 0)); comInitialized = true;
            enumeratorObject = new MMDeviceEnumeratorObject();
            var enumerator = (IMMDeviceEnumerator)enumeratorObject;
            WindowsAudio.Check(enumerator.GetDevice(endpointId, out IMMDevice device)); deviceObject = device;
            if (requireAirMic)
            {
                WindowsAudio.Check(device.OpenPropertyStore(0, out IPropertyStore properties));
                try
                {
                    if (!AudioDeviceIdentity.IsAirMic(properties))
                        throw new InvalidOperationException("Root\\AirMic 장치 신원을 확인하지 못했습니다. 장치를 새로고침하세요.");
                }
                finally { WindowsAudio.Release(properties); }
            }
            Guid clientId = typeof(IAudioClient).GUID;
            WindowsAudio.Check(device.Activate(ref clientId, 23, IntPtr.Zero, out clientObject));
            client = (IAudioClient)clientObject;
            var format = new WaveFormat { Tag = 1, Channels = 1, SampleRate = PacketCodec.SampleRate,
                AverageBytes = PacketCodec.SampleRate * 2, BlockAlign = 2, Bits = 16, ExtraSize = 0 };
            // Shared, event-driven, mono PCM16 client. Windows performs endpoint format conversion.
            WindowsAudio.Check(client.Initialize(0, 0x80000000u | 0x08000000u | 0x00040000u,
                200_000, 0, (IntPtr)(&format), IntPtr.Zero));
            WindowsAudio.Check(client.SetEventHandle(audioEvent.SafeWaitHandle.DangerousGetHandle()));
            WindowsAudio.Check(client.GetBufferSize(out uint capacity));
            Interlocked.Exchange(ref bufferFrames, capacity);
            WindowsAudio.Check(client.GetStreamLatency(out long reportedLatency));
            Interlocked.Exchange(ref latencyUs, reportedLatency / 10);
            Guid renderId = typeof(IAudioRenderClient).GUID;
            WindowsAudio.Check(client.GetService(ref renderId, out renderObject));
            var render = (IAudioRenderClient)renderObject;
            WindowsAudio.Check(render.GetBuffer(capacity, out _));
            WindowsAudio.Check(render.ReleaseBuffer(capacity, 2)); // AUDCLNT_BUFFERFLAGS_SILENT
            mmcss = AvSetMmThreadCharacteristics("Pro Audio", out _);
            WaitHandle[] events = [stop, audioEvent]; // Allocate once, before steady-state.
            WindowsAudio.Check(client.Start()); ready.Set();
            while (true)
            {
                int signaled = WaitHandle.WaitAny(events, 1000);
                if (signaled == 0) break;
                if (signaled == WaitHandle.WaitTimeout) throw new TimeoutException("오디오 이벤트가 1초 이상 중지됐습니다. 장치를 다시 선택하세요.");
                WindowsAudio.Check(client.GetCurrentPadding(out uint padding));
                if (padding > capacity) throw new InvalidOperationException("WASAPI padding 범위 오류");
                uint available = capacity - padding;
                if (available == 0) continue;
                WindowsAudio.Check(render.GetBuffer(available, out IntPtr data));
                try
                {
                    var samples = new Span<short>((void*)data, checked((int)available));
                    playout.Render(samples);
                    int maximum = 0;
                    foreach (short sample in samples) maximum = Math.Max(maximum, Math.Abs((int)sample));
                    Interlocked.Exchange(ref peak, maximum * 1000L / 32768);
                }
                finally { WindowsAudio.Check(render.ReleaseBuffer(available, 0)); }
                JitterStats s = playout.Stats;
                Interlocked.Exchange(ref played, s.Played); Interlocked.Exchange(ref lost, s.Lost);
                Interlocked.Exchange(ref plc, s.Plc); Interlocked.Exchange(ref duplicates, s.Duplicates);
                Interlocked.Exchange(ref late, s.Late); Interlocked.Exchange(ref underruns, s.Underruns);
                Interlocked.Exchange(ref buffered, s.Buffered); Interlocked.Exchange(ref target, s.Target);
                Interlocked.Exchange(ref resyncs, s.Resyncs); Interlocked.Exchange(ref trimmed, s.LatencyDrops);
                Interlocked.Exchange(ref holds, s.Holds);
            }
        }
        catch (Exception e) { Volatile.Write(ref error, $"WASAPI: {e.Message} (0x{e.HResult:X8})"); }
        finally
        {
            ready.Set();
            if (client is not null) { try { client.Stop(); } catch (COMException) { } }
            if (mmcss != IntPtr.Zero) AvRevertMmThreadCharacteristics(mmcss);
            WindowsAudio.Release(renderObject); WindowsAudio.Release(clientObject);
            WindowsAudio.Release(deviceObject); WindowsAudio.Release(enumeratorObject);
            if (comInitialized) CoUninitialize();
        }
    }
    public RenderStats Stats => new(Interlocked.Read(ref played), Interlocked.Read(ref lost), Interlocked.Read(ref plc),
        Interlocked.Read(ref duplicates), Interlocked.Read(ref late), Interlocked.Read(ref underruns),
        Interlocked.Read(ref buffered), Interlocked.Read(ref target), Interlocked.Read(ref bufferFrames),
        Interlocked.Read(ref peak), Interlocked.Read(ref resyncs), Interlocked.Read(ref trimmed), Interlocked.Read(ref holds),
        Interlocked.Read(ref latencyUs) / 1000.0, Volatile.Read(ref error));
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Set();
        if (!thread.Join(3000)) throw new TimeoutException("WASAPI thread가 종료되지 않았습니다. 앱을 다시 시작하세요.");
        stop.Dispose(); audioEvent.Dispose(); ready.Dispose();
    }
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("avrt.dll", CharSet = CharSet.Unicode, EntryPoint = "AvSetMmThreadCharacteristicsW")]
    private static extern IntPtr AvSetMmThreadCharacteristics(string name, out uint index);
    [DllImport("avrt.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WaveFormat
{
    public ushort Tag, Channels;
    public uint SampleRate, AverageBytes;
    public ushort BlockAlign, Bits, ExtraSize;
}
[StructLayout(LayoutKind.Sequential)] internal struct PropertyKey { public Guid FormatId; public uint PropertyId; }
[StructLayout(LayoutKind.Explicit, Size = 24)] internal struct PropVariant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public IntPtr Pointer; }
[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), ClassInterface(ClassInterfaceType.None)] internal class MMDeviceEnumeratorObject { }
[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int flow, uint state, out IMMDeviceCollection devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
}
[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-C0A29A650D0F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int Item(uint index, out IMMDevice device);
}
[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr activation, [MarshalAs(UnmanagedType.IUnknown)] out object value);
    [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore properties);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out uint state);
}
[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetAt(uint index, out PropertyKey key);
    [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
    [PreserveSig] int Commit();
}
[ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, uint flags, long duration, long periodicity, IntPtr format, IntPtr session);
    [PreserveSig] int GetBufferSize(out uint frames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint padding);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
    [PreserveSig] int GetMixFormat(out IntPtr format);
    [PreserveSig] int GetDevicePeriod(out long normal, out long minimum);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr handle);
    [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}
[ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioRenderClient
{
    [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
    [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
}
