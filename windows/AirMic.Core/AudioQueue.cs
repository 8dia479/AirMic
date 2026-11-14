namespace AirMic.Core;

public sealed class IngressFrame
{
    public readonly AudioPacket Packet = new();
    public ulong ArrivalUs, Generation;
    public void CopyFrom(IngressFrame other)
    {
        Packet.CopyFrom(other.Packet); ArrivalUs = other.ArrivalUs; Generation = other.Generation;
    }
}

// Exactly one producer and one consumer. All sample buffers are allocated at construction.
public sealed class AudioQueue
{
    public const int Capacity = 64;
    private readonly IngressFrame[] frames = Enumerable.Range(0, Capacity).Select(_ => new IngressFrame()).ToArray();
    private uint write, read;
    public bool TryEnqueue(AudioPacket packet, ulong arrivalUs, ulong generation)
    {
        uint w = write;
        if (unchecked(w - Volatile.Read(ref read)) >= Capacity) return false;
        IngressFrame frame = frames[w & (Capacity - 1)];
        frame.Packet.CopyFrom(packet); frame.ArrivalUs = arrivalUs; frame.Generation = generation;
        Volatile.Write(ref write, unchecked(w + 1));
        return true;
    }
    public bool TryDequeue(IngressFrame destination)
    {
        uint r = read;
        if (r == Volatile.Read(ref write)) return false;
        destination.CopyFrom(frames[r & (Capacity - 1)]);
        Volatile.Write(ref read, unchecked(r + 1));
        return true;
    }
    public int Count => (int)Math.Min(unchecked(Volatile.Read(ref write) - Volatile.Read(ref read)), Capacity);
}
