using System.Collections.Frozen;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AirMic.Setup;

// Portable PRE-INSTALL validation, not a driver/signature verifier. Windows PnP
// still validates the actual signed catalog and its coverage before loading a driver.
public sealed class DriverPackage
{
    public const string HardwareId = @"Root\AirMic";
    public const string Inf = "AirMic.inf", Catalog = "AirMic.cat", Binary = "AirMic.sys";
    public const long MaximumBytes = 64 * 1024 * 1024;
    public IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Files { get; }
    private DriverPackage(Dictionary<string, ReadOnlyMemory<byte>> files) => Files = files.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    private sealed record Manifest(int SchemaVersion, string HardwareId, string Inf, string Catalog, Dictionary<string, string> Files);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true, MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    public static bool SafeName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length is < 1 or > 128 || name[0] == '.' || name[^1] == '.') return false;
        foreach (char c in name)
            if (c is not (>= 'a' and <= 'z') and not (>= 'A' and <= 'Z') and not (>= '0' and <= '9') and not ('_' or '-' or '.')) return false;
        string stem = name.Split('.')[0];
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)) return false;
        if (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && stem[3] is >= '1' and <= '9') return false;
        return true;
    }
    public static DriverPackage Read(Stream input)
    {
        using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        if (zip.Entries.Count is < 4 or > 64) throw new InvalidDataException("Driver package file count outside 4..64");
        long total = 0;
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            if (!SafeName(entry.FullName) || !entries.TryAdd(entry.FullName, entry)) throw new InvalidDataException("Unsafe/duplicate driver filename");
            if (entry.Length < 0 || entry.Length > MaximumBytes - total) throw new InvalidDataException("Driver package exceeds 64 MiB");
            total += entry.Length;
        }
        if (!entries.Remove("manifest.json", out ZipArchiveEntry? manifestEntry) || manifestEntry.Length > 65536)
            throw new InvalidDataException("Driver manifest missing or too large");
        Manifest manifest;
        try
        {
            ReadOnlySpan<byte> json = ReadEntry(manifestEntry);
            // Windows PowerShell 5's -Encoding utf8 emits a BOM; support that producer too.
            if (json.Length >= 3 && json[0] == 0xef && json[1] == 0xbb && json[2] == 0xbf) json = json[3..];
            manifest = JsonSerializer.Deserialize<Manifest>(json, JsonOptions)
                ?? throw new InvalidDataException("Empty driver manifest");
        }
        catch (JsonException e) { throw new InvalidDataException("Invalid driver manifest JSON", e); }
        if (manifest.SchemaVersion != 1 || manifest.HardwareId != HardwareId || manifest.Inf != Inf || manifest.Catalog != Catalog
            || manifest.Files is null || manifest.Files.Count != entries.Count)
            throw new InvalidDataException("Wrong driver manifest schema/device/file set");
        var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, string digest) in manifest.Files)
        {
            if (!SafeName(name) || !entries.Remove(name, out ZipArchiveEntry? entry) || digest is null || digest.Length != 64)
                throw new InvalidDataException("Driver manifest/file mismatch");
            byte[] bytes = ReadEntry(entry);
            if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(digest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Driver SHA-256 mismatch: " + name);
            if (!files.TryAdd(name, bytes)) throw new InvalidDataException("Duplicate manifest filename");
        }
        if (entries.Count != 0 || !files.ContainsKey(Inf) || !files.ContainsKey(Catalog) || !files.ContainsKey(Binary))
            throw new InvalidDataException("Required AirMic driver files missing");
        if (files[Inf].Length > 1024 * 1024) throw new InvalidDataException("Unexpected driver INF size");
        using var text = new StreamReader(new MemoryStream(files[Inf].ToArray()), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        if (!text.ReadToEnd().Contains(HardwareId, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Wrong INF hardware ID");
        return new(files);
    }
    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        byte[] bytes = new byte[checked((int)entry.Length)];
        using Stream source = entry.Open();
        source.ReadExactly(bytes);
        if (source.ReadByte() != -1) throw new InvalidDataException("Unexpected ZIP entry length");
        return bytes;
    }
}
