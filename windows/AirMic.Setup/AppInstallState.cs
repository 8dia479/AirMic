using System.Text.Json;
using System.Text.Json.Serialization;

namespace AirMic.Setup;

public sealed record InstalledVersion(string Folder, string[] Files);

// Durable application-side upgrade journal. Windows driver/PnP changes cannot be
// atomically rolled back with files/registry. Keep the previous app active and a
// fully recorded repair/uninstall path until those external changes finish.
public sealed record AppInstallState(int SchemaVersion, string Current, List<InstalledVersion> Versions, string Pending = "")
{
    public static AppInstallState Empty => new(1, "", []);
    private static readonly JsonSerializerOptions Options = new()
    {
        MaxDepth = 8, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    public static bool IsVersionName(string? name) => name is not null && name.Length == 64
        && name.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static AppInstallState Read(ReadOnlySpan<byte> data)
    {
        if (data.Length > 65536) throw new InvalidDataException("Installation journal is too large");
        try
        {
            var state = JsonSerializer.Deserialize<AppInstallState>(data, Options)
                ?? throw new InvalidDataException("Empty installation journal");
            state.Validate(); return state;
        }
        catch (JsonException e) { throw new InvalidDataException("Invalid installation journal", e); }
    }
    public byte[] Serialize()
    {
        Validate();
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(this, Options);
        if (data.Length > 65536) throw new InvalidDataException("Installation journal is too large");
        return data;
    }
    public void Validate()
    {
        if (SchemaVersion != 1 || Versions is null || Versions.Count is < 1 or > 64
            || !IsVersionName(Current) || Pending is null || (Pending != "" && !IsVersionName(Pending)))
            throw new InvalidDataException("Unsafe installation journal");
        var folders = new HashSet<string>(StringComparer.Ordinal);
        foreach (InstalledVersion version in Versions)
        {
            if (version is null || !IsVersionName(version.Folder) || !folders.Add(version.Folder)
                || version.Files is null || version.Files.Length is < 1 or > 64
                || version.Files.Any(f => !DriverPackage.SafeName(f))
                || version.Files.Distinct(StringComparer.OrdinalIgnoreCase).Count() != version.Files.Length
                || !version.Files.Contains("AirMic.exe", StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("Unsafe version/files in installation journal");
        }
        if (!folders.Contains(Current) || (Pending != "" && !folders.Contains(Pending)))
            throw new InvalidDataException("Unrecorded current/pending app version");
    }
    public AppInstallState Prepare(string version, IEnumerable<string> files)
    {
        if (SchemaVersion != 1 || Current != "" || Pending != "" || Versions is not { Count: 0 }) Validate();
        var candidate = new InstalledVersion(version, files.ToArray());
        var planned = new AppInstallState(1, Current == "" ? version : Current,
            Versions.Where(v => v.Folder != version).Append(candidate).ToList(), version);
        planned.Validate(); return planned;
    }
    public AppInstallState Complete(string version)
    {
        Validate();
        if (Pending != version) throw new InvalidOperationException("No matching prepared installation");
        return this with { Current = version, Pending = "" };
    }
}
