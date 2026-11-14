using System.Buffers.Binary;
using System.Diagnostics;

namespace AirMic.Core;

public sealed class AudioPacket
{
    public uint Sequence;
    public ulong Timestamp;
    public ushort Flags;
    public readonly short[] Samples = new short[PacketCodec.FrameSamples];
    public void CopyFrom(AudioPacket other)
    {
        Sequence = other.Sequence; Timestamp = other.Timestamp; Flags = other.Flags;
        other.Samples.AsSpan().CopyTo(Samples);
    }
}

public readonly record struct ReceiverAck(uint Sequence, ulong Timestamp, uint AcceptedPackets);
public enum DecodeError { None, WrongSize, SampleCount, UnknownFlags, InvalidStart }

public static class PacketCodec
{
    public const int SampleRate = 48_000, FrameSamples = 480, HeaderBytes = 16;
    public const int PacketBytes = HeaderBytes + FrameSamples * 2, AckBytes = 24, DefaultPort = 49_000;
    public const ushort Start = 1, Discontinuity = 2;
    public static int SequenceDelta(uint a, uint b) => unchecked((int)(a - b));
    public static DecodeError Decode(ReadOnlySpan<byte> bytes, AudioPacket packet)
    {
        if (bytes.Length != PacketBytes) return DecodeError.WrongSize;
        if (BinaryPrimitives.ReadUInt16BigEndian(bytes[12..]) != FrameSamples) return DecodeError.SampleCount;
        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(bytes[14..]);
        if ((flags & ~(Start | Discontinuity)) != 0) return DecodeError.UnknownFlags;
        uint seq = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        if ((flags & Start) != 0 && seq != 0) return DecodeError.InvalidStart;
        packet.Sequence = seq; packet.Flags = flags;
        packet.Timestamp = BinaryPrimitives.ReadUInt64BigEndian(bytes[4..]);
        for (int i = 0; i < FrameSamples; i++)
            packet.Samples[i] = BinaryPrimitives.ReadInt16BigEndian(bytes[(HeaderBytes + 2 * i)..]);
        return DecodeError.None;
    }
    public static bool Encode(AudioPacket packet, Span<byte> bytes)
    {
        if (bytes.Length != PacketBytes || (packet.Flags & ~(Start | Discontinuity)) != 0
            || ((packet.Flags & Start) != 0 && packet.Sequence != 0)) return false;
        BinaryPrimitives.WriteUInt32BigEndian(bytes, packet.Sequence);
        BinaryPrimitives.WriteUInt64BigEndian(bytes[4..], packet.Timestamp);
        BinaryPrimitives.WriteUInt16BigEndian(bytes[12..], FrameSamples);
        BinaryPrimitives.WriteUInt16BigEndian(bytes[14..], packet.Flags);
        for (int i = 0; i < FrameSamples; i++)
            BinaryPrimitives.WriteInt16BigEndian(bytes[(HeaderBytes + 2 * i)..], packet.Samples[i]);
        return true;
    }
    public static bool TimestampMatches(uint sequence, ulong timestamp, AudioPacket packet)
    {
        long ticks = (long)SequenceDelta(packet.Sequence, sequence) * FrameSamples;
        if (ticks >= 0)
            return timestamp <= ulong.MaxValue - (ulong)ticks && packet.Timestamp == timestamp + (ulong)ticks;
        ulong delta = (ulong)-ticks;
        return timestamp >= delta && packet.Timestamp == timestamp - delta;
    }
    public static bool EncodeAck(ReceiverAck ack, Span<byte> bytes)
    {
        if (bytes.Length != AckBytes) return false;
        "AMAK"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32BigEndian(bytes[4..], ack.Sequence);
        BinaryPrimitives.WriteUInt64BigEndian(bytes[8..], ack.Timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(bytes[16..], ack.AcceptedPackets);
        BinaryPrimitives.WriteUInt16BigEndian(bytes[20..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(bytes[22..], 0);
        return true;
    }
    public static bool DecodeAck(ReadOnlySpan<byte> bytes, out ReceiverAck ack)
    {
        ack = default;
        if (bytes.Length != AckBytes || !bytes[..4].SequenceEqual("AMAK"u8)
            || BinaryPrimitives.ReadUInt16BigEndian(bytes[20..]) != 1
            || BinaryPrimitives.ReadUInt16BigEndian(bytes[22..]) != 0) return false;
        ack = new(BinaryPrimitives.ReadUInt32BigEndian(bytes[4..]),
            BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]), BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]));
        return true;
    }
}

public static class MonoClock
{
    private static readonly double MicrosecondsPerTick = 1_000_000.0 / Stopwatch.Frequency;
    public static ulong NowUs => (ulong)(Stopwatch.GetTimestamp() * MicrosecondsPerTick);
}

public readonly record struct Peer(uint IPv4, ushort Port);
public enum RejectReason { None, Source, Timeline }
public readonly record struct Admission(bool Accepted, bool NewStream, ulong Generation, RejectReason Reason);

// Network-thread owned. Source pinning is not authentication.
public sealed class SessionGate(uint allowedIp = 0)
{
    public const ulong IdleUs = 2_000_000;
    private bool active;
    private Peer peer;
    private ulong lastArrival, highestTimestamp, generation, anchorTimestamp;
    private uint anchorSequence;
    public Admission Admit(AudioPacket packet, Peer from, ulong nowUs)
    {
        if (allowedIp != 0 && allowedIp != from.IPv4) return Reject(RejectReason.Source);
        bool idle = !active || nowUs - lastArrival >= IdleUs;
        bool fresh = active && from.IPv4 == peer.IPv4 && (packet.Flags & PacketCodec.Start) != 0
            && packet.Timestamp > highestTimestamp;
        if (idle || fresh)
        {
            peer = from; active = true; generation++;
            anchorSequence = packet.Sequence; anchorTimestamp = highestTimestamp = packet.Timestamp;
            lastArrival = nowUs;
            return new(true, true, generation, RejectReason.None);
        }
        if (from != peer) return Reject(RejectReason.Source);
        if (!PacketCodec.TimestampMatches(anchorSequence, anchorTimestamp, packet)) return Reject(RejectReason.Timeline);
        if (packet.Timestamp > highestTimestamp)
        {
            highestTimestamp = anchorTimestamp = packet.Timestamp; anchorSequence = packet.Sequence;
        }
        lastArrival = nowUs;
        return new(true, false, generation, RejectReason.None);
    }
    private Admission Reject(RejectReason reason) => new(false, false, generation, reason);
}
