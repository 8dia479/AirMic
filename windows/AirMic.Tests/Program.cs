using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using AirMic.Core;

static class Tests
{
    static int checks;
    static void Check(bool condition, string message = "check")
    { checks++; if (!condition) throw new InvalidOperationException(message); }
    static AudioPacket Packet(uint seq, short value = 1000, ulong origin = 100_000)
    {
        var p = new AudioPacket { Sequence = seq, Timestamp = origin + seq * 480UL };
        Array.Fill(p.Samples, value); return p;
    }
    static void Protocol()
    {
        var packet = new AudioPacket { Sequence = 0, Timestamp = 0x0102030405060708, Flags = PacketCodec.Start };
        packet.Samples[0] = short.MinValue; packet.Samples[1] = -1; packet.Samples[2] = 1; packet.Samples[3] = short.MaxValue;
        byte[] wire = new byte[PacketCodec.PacketBytes]; var decoded = new AudioPacket();
        Check(PacketCodec.Encode(packet, wire));
        Check(Convert.ToHexString(wire.AsSpan(0, 24)) == "00000000010203040506070801E000018000FFFF00017FFF", "BE golden vector");
        Check(PacketCodec.Decode(wire, decoded) == DecodeError.None);
        Check(decoded.Timestamp == packet.Timestamp && decoded.Samples.SequenceEqual(packet.Samples));
        for (int n = 0; n < 1200; n++) Check(PacketCodec.Decode(new byte[n], decoded) != DecodeError.None);
        wire[14] = 1; Check(PacketCodec.Decode(wire, decoded) == DecodeError.UnknownFlags); wire[14] = 0;
        wire[3] = 1; Check(PacketCodec.Decode(wire, decoded) == DecodeError.InvalidStart); wire[3] = 0;
        var random = new Random(9237);
        for (int i = 0; i < 5000; i++)
        {
            random.NextBytes(wire); wire[12] = 1; wire[13] = 0xe0; wire[14] = 0; wire[15] = 0;
            Check(PacketCodec.Decode(wire, decoded) == DecodeError.None);
            byte[] copy = new byte[wire.Length]; Check(PacketCodec.Encode(decoded, copy)); Check(wire.SequenceEqual(copy));
        }
        Check(PacketCodec.SequenceDelta(0, uint.MaxValue) == 1);
        Check(PacketCodec.SequenceDelta(uint.MaxValue, 0) == -1);
        var wrap = new AudioPacket { Sequence = 0, Timestamp = 1480 };
        Check(PacketCodec.TimestampMatches(uint.MaxValue, 1000, wrap));
        wrap.Timestamp = 0; Check(!PacketCodec.TimestampMatches(uint.MaxValue, ulong.MaxValue, wrap));
        byte[] ack = new byte[24]; var expected = new ReceiverAck(uint.MaxValue, ulong.MaxValue, 77);
        Check(PacketCodec.EncodeAck(expected, ack)); Check(PacketCodec.DecodeAck(ack, out var result) && expected == result);
        ack[23] = 1; Check(!PacketCodec.DecodeAck(ack, out _));
    }
    static void Sessions()
    {
        var gate = new SessionGate(); var a = new Peer(0xc0a80001, 6000); var b = new Peer(0xc0a80002, 6000);
        Check(gate.Admit(Packet(0), a, 1).NewStream);
        Check(!gate.Admit(Packet(1), b, 2).Accepted);
        var bad = Packet(1); bad.Timestamp++; Check(gate.Admit(bad, a, 3).Reason == RejectReason.Timeline);
        Check(gate.Admit(Packet(1), a, 4).Accepted);
        Check(gate.Admit(Packet(1000), b, 2_000_005).NewStream);
        var newer = Packet(0, origin: 1_000_000); newer.Flags = PacketCodec.Start;
        Check(gate.Admit(newer, b with { Port = 7000 }, 2_000_006).NewStream);
        var older = Packet(0); older.Flags = PacketCodec.Start;
        Check(!gate.Admit(older, b, 2_000_007).Accepted);
        Check(!new SessionGate(a.IPv4).Admit(Packet(0), b, 0).Accepted);
    }
    static void Queue()
    {
        var queue = new AudioQueue(); var p = Packet(1); var outFrame = new IngressFrame();
        for (int i = 0; i < AudioQueue.Capacity; i++) { p.Sequence = (uint)i; Check(queue.TryEnqueue(p, 1, 2)); }
        Check(!queue.TryEnqueue(p, 1, 2));
        for (int i = 0; i < AudioQueue.Capacity; i++) { Check(queue.TryDequeue(outFrame)); Check(outFrame.Packet.Sequence == i); }
        Check(!queue.TryDequeue(outFrame));
        const int count = 100_000;
        var producer = Task.Run(() =>
        {
            var source = new AudioPacket();
            for (uint i = 0; i < count; i++)
            { source.Sequence = i; source.Samples[0] = unchecked((short)i); while (!queue.TryEnqueue(source, i, 1)) Thread.Yield(); }
        });
        for (uint i = 0; i < count; i++)
        {
            while (!queue.TryDequeue(outFrame)) Thread.Yield();
            Check(outFrame.Packet.Sequence == i && outFrame.Packet.Samples[0] == unchecked((short)i), "SPSC order/data");
        }
        Check(producer.Wait(10000));
    }
    static void Jitter()
    {
        var jitter = new JitterBuffer(); short[] output = new short[480];
        Check(jitter.Pop(output) == PlayoutKind.Waiting);
        Check(jitter.Insert(Packet(1, 1001), 10000)); Check(jitter.Insert(Packet(0), 0));
        Check(!jitter.Insert(Packet(1), 11000));
        Check(jitter.Pop(output) == PlayoutKind.Audio && output[^1] == 1000);
        Check(jitter.Pop(output) == PlayoutKind.Audio && output[^1] == 1001);
        Check(jitter.Pop(output) == PlayoutKind.Concealed && Math.Abs((int)output[^1]) < 1001);
        for (int i = 0; i < 5; i++) jitter.Pop(output);
        Check(jitter.Stats.Underruns == 1 && jitter.Stats.Target == 3);
        jitter.Reset(); Check(jitter.Stats.Buffered == 0);
        for (uint i = 0; i < 30; i++) jitter.Insert(Packet(i), i * 10_000UL);
        jitter.Pop(output); Check(jitter.Stats.LatencyDrops > 0 && jitter.Stats.Buffered <= 6);
        jitter.Reset();
        var p = Packet(uint.MaxValue); p.Timestamp = 5000; jitter.Insert(p, 0);
        p.Sequence = 0; p.Timestamp = 5480; jitter.Insert(p, 10000);
        Check(jitter.Pop(output) == PlayoutKind.Audio); Check(jitter.Pop(output) == PlayoutKind.Audio);
    }
    static void PlayoutAllocation()
    {
        var queue = new AudioQueue(); var play = new PcmPlayout(queue); var packet = Packet(0); short[] output = new short[480];
        for (int i = 0; i < 1000; i++)
        { packet.Sequence = (uint)i; packet.Timestamp = 100_000 + (ulong)i * 480; queue.TryEnqueue(packet, (ulong)i * 10_000, 1); play.Render(output); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 1000; i < 3000; i++)
        { packet.Sequence = (uint)i; packet.Timestamp = 100_000 + (ulong)i * 480; queue.TryEnqueue(packet, (ulong)i * 10_000, 1); play.Render(output); }
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(bytes == 0, $"Managed core steady-state allocated {bytes} bytes");
        Check(play.Stats.Played > 2900); Check(play.Stats.Underruns == 0);
        play.Volume = 0; play.Render(output); Check(output.All(s => s == 0));
        Console.WriteLine($"Managed core allocation guard: {bytes} bytes / 2000 cycles (not WASAPI/CLR-wide)");
    }
    static void Udp()
    {
        using var receiver = new UdpReceiver(new AudioQueue(), IPAddress.Loopback, 0, enqueueAudio: false);
        using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ReceiveTimeout = 3000 };
        sender.Connect(IPAddress.Loopback, receiver.LocalPort);
        byte[] bytes = new byte[976], ack = new byte[64]; var p = Packet(0); p.Flags = PacketCodec.Start;
        PacketCodec.Encode(p, bytes); sender.Send(bytes);
        int count = sender.Receive(ack);
        Check(PacketCodec.DecodeAck(ack.AsSpan(0, count), out var receipt));
        Check(receipt.Sequence == 0 && receipt.Timestamp == p.Timestamp && receipt.AcceptedPackets == 1);
        sender.Send(new byte[977]);
        Check(SpinWait.SpinUntil(() => receiver.Stats.Invalid == 1, 3000));
        Check(receiver.Stats.Accepted == 1 && receiver.Stats.QueueDrops == 0);
    }
    public static int Main()
    {
        try { Protocol(); Sessions(); Queue(); Jitter(); PlayoutAllocation(); Udp(); Console.WriteLine($"PASS: {checks:N0} managed checks"); return 0; }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
