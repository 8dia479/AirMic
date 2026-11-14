using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AirMic.Core;
using AirMic.Setup;

static class MigrationTests
{
    static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    static MemoryStream Fixture(string? extraName = null, bool corrupt = false, bool missingHash = false, bool duplicate = false,
        int schema = 1, string hardwareId = DriverPackage.HardwareId, bool bom = false)
    {
        // Intentionally synthetic bytes: validates ZIP/manifest logic ONLY. Never passed to Windows PnP.
        var files = new Dictionary<string, byte[]> {
            [DriverPackage.Inf] = Encoding.UTF8.GetBytes("[Model]\nRoot\\AirMic\n"),
            [DriverPackage.Catalog] = [1, 2, 3], [DriverPackage.Binary] = [4, 5, 6]
        };
        var hashes = files.ToDictionary(p => p.Key, p => Convert.ToHexString(SHA256.HashData(p.Value)));
        if (missingHash) hashes.Remove(DriverPackage.Binary);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = schema, hardwareId, inf = DriverPackage.Inf, catalog = DriverPackage.Catalog, files = hashes });
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            foreach ((string name, byte[] data) in files)
            { using Stream file = zip.CreateEntry(name).Open(); file.Write(corrupt && name == DriverPackage.Binary ? new byte[] { 0, 0, 0 } : data); }
            using (Stream file = zip.CreateEntry("manifest.json").Open())
            { if (bom) file.Write(new byte[] { 0xef, 0xbb, 0xbf }); file.Write(json); }
            if (extraName is not null) { using Stream file = zip.CreateEntry(extraName).Open(); file.WriteByte(0); }
            if (duplicate) { using Stream file = zip.CreateEntry("airmic.SYS").Open(); file.WriteByte(0); }
        }
        stream.Position = 0; return stream;
    }
    static void Reject(MemoryStream input)
    {
        using (input)
        {
            try { DriverPackage.Read(input); }
            catch (InvalidDataException) { return; }
            throw new InvalidOperationException("Unsafe driver fixture accepted");
        }
    }
    static void Packages()
    {
        using (var input = Fixture()) Check(DriverPackage.Read(input).Files.Count == 3, "Valid manifest rejected");
        using (var input = Fixture(bom: true)) Check(DriverPackage.Read(input).Files.Count == 3, "PowerShell UTF8 BOM manifest rejected");
        Reject(Fixture(corrupt: true)); Reject(Fixture(missingHash: true)); Reject(Fixture(duplicate: true));
        Reject(Fixture(schema: 2)); Reject(Fixture(hardwareId: @"Root\Other"));
        foreach (string name in new[] { "../escape", @"..\escape", "/absolute", "C:evil", "CON", "aux.txt", "lpt1.cat", "trailing.", "file/sub", "unhashed.txt", "manifest.JSON" })
            Reject(Fixture(extraName: name));
        foreach (string name in new[] { ".", "..", "", "foo:bar", "with space", "sub/dir", "COM9.sys", "PRN" })
            Check(!DriverPackage.SafeName(name), "Unsafe Windows filename");
        Check(DriverPackage.SafeName("AirMic.sys") && DriverPackage.SafeName("SYSVAD-LICENSE.txt"), "Safe name rejected");
        Console.WriteLine("PASS: driver package validation (synthetic ZIP fixtures only; NOT driver signing or installation)");
    }
    static void VirtualClock()
    {
        foreach (int drift in new[] { -200, 0, 200 })
        {
            var queue = new AudioQueue(); var playout = new PcmPlayout(queue);
            var packet = new AudioPacket(); Array.Fill(packet.Samples, (short)1000);
            short[] output = new short[240];
            double due = 0, interval = 10000 / (1 + drift / 1_000_000.0);
            uint sequence = 0; int peakBuffered = 0;
            // 600 seconds virtual time with 5ms WASAPI-sized chunks; NOT a real-time/device soak.
            for (long tick = 0; tick < 120_000; tick++)
            {
                long now = tick * 5000;
                while (due <= now)
                {
                    packet.Sequence = sequence; packet.Timestamp = 100_000 + sequence * 480UL;
                    Check(queue.TryEnqueue(packet, (ulong)due, 1), "Virtual clock ingress overflow");
                    sequence++; due += interval;
                }
                playout.Render(output);
                peakBuffered = Math.Max(peakBuffered, playout.Stats.Buffered);
                Check(double.IsFinite(playout.CorrectionPpm) && Math.Abs(playout.CorrectionPpm) <= 300.00001, "ASRC correction bound");
                foreach (short x in output) Check(x is >= 0 and <= 1000, "ASRC amplitude bound");
            }
            Check(playout.Stats.Underruns == 0 && playout.Stats.Plc == 0, "Steady drift caused playout underrun");
            Check(playout.Stats.Played > 59_900 && peakBuffered <= 8, "Unbounded virtual clock latency");
            Console.WriteLine($"PASS: managed 600s VIRTUAL timeline, drift {drift}ppm; played={playout.Stats.Played}, peak buffered={peakBuffered}, correction={playout.CorrectionPpm:F2}ppm (not measured E2E)");
        }
    }
    public static void Run() { Packages(); VirtualClock(); }
}
