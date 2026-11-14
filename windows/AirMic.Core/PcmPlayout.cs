namespace AirMic.Core;

public sealed class PcmPlayout(AudioQueue ingress, int minimumFrames = 2, int maximumFrames = 8)
{
    private readonly JitterBuffer jitter = new(minimumFrames, maximumFrames);
    private readonly IngressFrame scratch = new();
    private readonly short[] block = new short[PacketCodec.FrameSamples];
    private int offset = PacketCodec.FrameSamples;
    private ulong generation;
    private float current, next, volume = 1;
    private double fraction, rate = 1;
    private bool primed;
    public float Volume { set => Volatile.Write(ref volume, float.IsFinite(value) ? Math.Clamp(value, 0, 2) : 1); }
    private float TakeSample()
    {
        if (offset == PacketCodec.FrameSamples) { jitter.Pop(block); offset = 0; }
        return block[offset++];
    }
    public void Render(Span<short> destination)
    {
        for (int i = 0; i < AudioQueue.Capacity && ingress.TryDequeue(scratch); i++)
        {
            if (scratch.Generation != generation)
            {
                jitter.Reset(generation != 0); generation = scratch.Generation;
                offset = PacketCodec.FrameSamples; primed = false; fraction = 0; rate = 1;
            }
            jitter.Insert(scratch.Packet, scratch.ArrivalUs);
        }
        JitterStats state = jitter.Stats;
        double desired = 1 + Math.Clamp((state.Buffered - state.Target) * .0003, -.0003, .0003);
        rate += .02 * (desired - rate);
        if (!primed) { current = TakeSample(); next = TakeSample(); primed = true; }
        float gain = Volatile.Read(ref volume);
        for (int i = 0; i < destination.Length; i++)
        {
            double sample = (current + (next - current) * fraction) * gain;
            destination[i] = (short)Math.Clamp(Math.Round(sample, MidpointRounding.AwayFromZero), short.MinValue, short.MaxValue);
            fraction += rate;
            while (fraction >= 1) { fraction--; current = next; next = TakeSample(); }
        }
    }
    // Read only by the render owner; publish a copied snapshot to the UI.
    public JitterStats Stats => jitter.Stats;
    public double CorrectionPpm => (rate - 1) * 1e6;
}
