using System.Net;
using System.Net.Sockets;

namespace AirMic.Core;

public readonly record struct NetworkStats(long Accepted, long Invalid, long SourceRejected, long TimelineRejected,
    long QueueDrops, long SocketErrors, long Acks, long LastArrivalUs, string Error);

public sealed class UdpReceiver : IDisposable
{
    private readonly AudioQueue ingress;
    private readonly SessionGate gate;
    private readonly bool enqueueAudio;
    private readonly Socket socket;
    private readonly Thread thread;
    private volatile bool stopping;
    private long accepted, invalid, sourceRejected, timelineRejected, drops, errors, acks, lastArrival;
    private string error = "";
    public int LocalPort => ((IPEndPoint)socket.LocalEndPoint!).Port;
    public UdpReceiver(AudioQueue queue, IPAddress bindAddress, int port, uint allowedIp = 0, bool enqueueAudio = true)
    {
        if (bindAddress.AddressFamily != AddressFamily.InterNetwork || port is < 0 or > 65535)
            throw new ArgumentException("IPv4 / valid UDP port required");
        ingress = queue; gate = new(allowedIp); this.enqueueAudio = enqueueAudio;
        socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.ReceiveBufferSize = 256 * 1024;
            if (OperatingSystem.IsWindows())
            {
                socket.ExclusiveAddressUse = true;
                socket.IOControl(unchecked((int)0x9800000C), new byte[4], null); // SIO_UDP_CONNRESET off
            }
            socket.Bind(new IPEndPoint(bindAddress, port)); socket.Blocking = false;
            thread = new Thread(ReceiveLoop) { IsBackground = true, Name = "AirMic UDP" };
            thread.Start();
        }
        catch { socket.Dispose(); throw; }
    }
    private void ReceiveLoop()
    {
        byte[] bytes = new byte[2048], ackBytes = new byte[PacketCodec.AckBytes];
        AudioPacket packet = new();
        SocketAddress address = new(AddressFamily.InterNetwork, SocketAddress.GetMaximumAddressSize(AddressFamily.InterNetwork));
        ulong lastAck = 0;
        try
        {
            while (!stopping)
            {
                if (!socket.Poll(10_000, SelectMode.SelectRead)) continue;
                for (int i = 0; i < 32 && !stopping; i++)
                {
                    int count;
                    try { count = socket.ReceiveFrom(bytes.AsSpan(), SocketFlags.None, address); }
                    catch (SocketException e) when (e.SocketErrorCode == SocketError.WouldBlock) { break; }
                    catch (SocketException e) when (e.SocketErrorCode is SocketError.MessageSize or SocketError.ConnectionReset)
                    { Interlocked.Increment(ref invalid); continue; }
                    if (PacketCodec.Decode(bytes.AsSpan(0, count), packet) != DecodeError.None)
                    { Interlocked.Increment(ref invalid); continue; }
                    uint ip = ((uint)address[4] << 24) | ((uint)address[5] << 16) | ((uint)address[6] << 8) | address[7];
                    ushort port = (ushort)((address[2] << 8) | address[3]);
                    ulong now = MonoClock.NowUs;
                    Admission admission = gate.Admit(packet, new(ip, port), now);
                    if (!admission.Accepted)
                    {
                        if (admission.Reason == RejectReason.Source) Interlocked.Increment(ref sourceRejected);
                        else Interlocked.Increment(ref timelineRejected);
                        continue;
                    }
                    long total = Interlocked.Increment(ref accepted);
                    Interlocked.Exchange(ref lastArrival, (long)now);
                    if (enqueueAudio && !ingress.TryEnqueue(packet, now, admission.Generation)) Interlocked.Increment(ref drops);
                    if (admission.NewStream || now - lastAck >= 250_000)
                    {
                        PacketCodec.EncodeAck(new(packet.Sequence, packet.Timestamp, unchecked((uint)total)), ackBytes);
                        try
                        {
                            if (socket.SendTo(ackBytes, SocketFlags.None, address) == ackBytes.Length)
                            { Interlocked.Increment(ref acks); lastAck = now; }
                        }
                        catch (SocketException) { Interlocked.Increment(ref errors); }
                    }
                }
            }
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            if (!stopping) { Volatile.Write(ref error, e.Message); Interlocked.Increment(ref errors); }
        }
    }
    public NetworkStats Stats => new(Interlocked.Read(ref accepted), Interlocked.Read(ref invalid),
        Interlocked.Read(ref sourceRejected), Interlocked.Read(ref timelineRejected), Interlocked.Read(ref drops),
        Interlocked.Read(ref errors), Interlocked.Read(ref acks), Interlocked.Read(ref lastArrival), Volatile.Read(ref error));
    public void Dispose()
    {
        stopping = true; socket.Dispose();
        if (Thread.CurrentThread != thread && !thread.Join(3000)) throw new TimeoutException("UDP thread did not stop");
    }
}
