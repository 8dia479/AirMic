namespace AirMic.Core;

public enum PlayoutKind { Waiting, Audio, Concealed, AdaptiveHold }
public readonly record struct JitterStats(long Inserted, long Duplicates, long Late, long InvalidTimeline,
    long Played, long Lost, long Plc, long Underruns, long Resyncs, long LatencyDrops, long Holds,
    int Buffered, int Target, uint LastSequence, double JitterMs, bool Playing, bool LastWasAudio);

// Render-thread owned. No network/file IO, locks, or per-frame buffers.
public sealed class JitterBuffer
{
    public const int Capacity = 64;
    private sealed class Slot { public readonly AudioPacket Packet = new(); public bool Used; }
    private readonly Slot[] slots = Enumerable.Range(0, Capacity).Select(_ => new Slot()).ToArray();
    private readonly uint[] seenSequence = new uint[Capacity];
    private readonly bool[] seenValid = new bool[Capacity];
    private readonly short[] history = new short[PacketCodec.FrameSamples];
    private readonly int minimum, maximum;
    private uint next, anchorSequence, lastArrivalSequence, lastSequence;
    private ulong anchorTimestamp, lastArrivalUs;
    private int buffered, target, stable, expansion, shrink, losses;
    private double jitterUs;
    private float concealGain = 1;
    private short lastSample;
    private bool anchored, started, arrivalKnown, reanchoring, crossfade, lastWasAudio;
    private long inserted, duplicates, late, invalidTimeline, played, lost, plc, underruns, resyncs, latencyDrops, holds;
    public JitterBuffer(int minimumFrames = 2, int maximumFrames = 8)
    {
        if (minimumFrames < 2 || maximumFrames > 8 || minimumFrames > maximumFrames)
            throw new ArgumentOutOfRangeException(nameof(minimumFrames));
        minimum = minimumFrames; maximum = maximumFrames; target = minimum;
    }
    private void ClearSlots() { foreach (Slot slot in slots) slot.Used = false; buffered = 0; }
    public void Reset(bool countResync = false)
    {
        ClearSlots(); Array.Clear(seenValid); Array.Clear(history);
        if (countResync) resyncs++;
        anchored = started = arrivalKnown = reanchoring = crossfade = lastWasAudio = false;
        target = minimum; jitterUs = 0; stable = expansion = shrink = losses = 0;
        concealGain = 1; lastSample = 0;
    }
    private void UpdateJitter(AudioPacket packet, ulong now)
    {
        if (!arrivalKnown)
        { arrivalKnown = true; lastArrivalSequence = packet.Sequence; lastArrivalUs = now; return; }
        int delta = PacketCodec.SequenceDelta(packet.Sequence, lastArrivalSequence);
        if (delta <= 0) return;
        jitterUs += (Math.Abs((double)now - lastArrivalUs - delta * 10_000.0) - jitterUs) / 16;
        lastArrivalSequence = packet.Sequence; lastArrivalUs = now;
        int desired = Math.Clamp(1 + (int)Math.Ceiling(4 * jitterUs / 10_000), minimum, maximum);
        if (desired > target)
        { if (started) expansion += desired - target; target = desired; stable = 0; }
        else if (desired < target)
        { if (++stable >= 200) { target--; shrink++; stable = 0; } }
        else stable = 0;
    }
    public bool Insert(AudioPacket packet, ulong arrivalUs)
    {
        if (!anchored)
        { anchored = true; anchorSequence = next = packet.Sequence; anchorTimestamp = packet.Timestamp; }
        if (!PacketCodec.TimestampMatches(anchorSequence, anchorTimestamp, packet))
        { invalidTimeline++; return false; }
        int index = (int)(packet.Sequence & (Capacity - 1));
        if (seenValid[index] && seenSequence[index] == packet.Sequence) { duplicates++; return false; }
        if (reanchoring && buffered == 0) { next = packet.Sequence; reanchoring = false; }
        int delta = PacketCodec.SequenceDelta(packet.Sequence, next);
        if (delta < 0)
        { if (!started && delta > -Capacity) next = packet.Sequence; else { late++; return false; } }
        else if (delta >= Capacity)
        { ClearSlots(); next = packet.Sequence; started = false; losses = expansion = 0; crossfade = true; resyncs++; }
        if (PacketCodec.SequenceDelta(packet.Sequence, anchorSequence) > 0)
        { anchorSequence = packet.Sequence; anchorTimestamp = packet.Timestamp; }
        Slot slot = slots[index];
        if (slot.Used) { slot.Used = false; buffered--; latencyDrops++; }
        slot.Packet.CopyFrom(packet); slot.Used = true; buffered++;
        seenValid[index] = true; seenSequence[index] = packet.Sequence; inserted++;
        UpdateJitter(packet, arrivalUs); return true;
    }
    private bool DiscardNext()
    {
        Slot slot = slots[next & (Capacity - 1)];
        if (!slot.Used || slot.Packet.Sequence != next) return false;
        slot.Used = false; buffered--; next = unchecked(next + 1); latencyDrops++; crossfade = true;
        return true;
    }
    private void Conceal(Span<short> output, bool hold)
    {
        float endGain = hold ? concealGain * .98f : losses >= 5 ? 0 : concealGain * .75f;
        for (int i = 0; i < PacketCodec.FrameSamples; i++)
        {
            float t = (i + 1f) / PacketCodec.FrameSamples;
            float value = history[i] * (concealGain + t * (endGain - concealGain));
            if (i < 48) { float mix = (i + 1f) / 48; value = lastSample * (1 - mix) + value * mix; }
            output[i] = (short)Math.Clamp(value, short.MinValue, short.MaxValue);
        }
        concealGain = endGain; lastSample = output[^1]; crossfade = true;
    }
    public PlayoutKind Pop(Span<short> output)
    {
        if (output.Length != PacketCodec.FrameSamples) throw new ArgumentException("Expected 480 samples", nameof(output));
        lastWasAudio = false;
        if (!started)
        {
            if (!anchored || buffered < target) { output.Clear(); return PlayoutKind.Waiting; }
            started = true; reanchoring = false;
        }
        if (expansion != 0)
        {
            if (buffered < target && losses == 0)
            { expansion--; Conceal(output, true); holds++; return PlayoutKind.AdaptiveHold; }
            expansion = 0;
        }
        while (buffered > target + 4 && DiscardNext()) { }
        if (shrink != 0 && buffered > target + 1 && DiscardNext()) shrink--;
        Slot slot = slots[next & (Capacity - 1)];
        if (slot.Used && slot.Packet.Sequence == next)
        {
            slot.Packet.Samples.AsSpan().CopyTo(output); slot.Used = false; buffered--;
            if (crossfade)
                for (int i = 0; i < 48; i++)
                { float t = (i + 1f) / 48; output[i] = (short)(lastSample * (1 - t) + output[i] * t); }
            output.CopyTo(history); lastSample = output[^1]; concealGain = 1;
            crossfade = false; losses = 0; lastSequence = next; next = unchecked(next + 1);
            played++; lastWasAudio = true; return PlayoutKind.Audio;
        }
        lost++; plc++; losses++; next = unchecked(next + 1); Conceal(output, false);
        if (losses >= 6)
        {
            underruns++; if (target < maximum) target++;
            started = false; reanchoring = true; losses = 0;
            int nearest = int.MaxValue;
            foreach (Slot future in slots)
            {
                if (!future.Used) continue;
                int delta = PacketCodec.SequenceDelta(future.Packet.Sequence, next);
                if (delta < 0) { future.Used = false; buffered--; }
                else nearest = Math.Min(nearest, delta);
            }
            if (nearest != int.MaxValue) { next = unchecked(next + (uint)nearest); reanchoring = false; }
        }
        return PlayoutKind.Concealed;
    }
    public JitterStats Stats => new(inserted, duplicates, late, invalidTimeline, played, lost, plc,
        underruns, resyncs, latencyDrops, holds, buffered, target, lastSequence, jitterUs / 1000, started, lastWasAudio);
}
