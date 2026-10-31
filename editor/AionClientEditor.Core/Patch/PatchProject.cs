using System.Text.Json;
using System.Text.Json.Serialization;

namespace AionClientEditor.Patch;

/// <summary>Editable client overrides stored outside the base paks (export/import as JSON).</summary>
public sealed class PatchProject
{
    public int SchemaVersion { get; set; } = 1;
    public string BuildFingerprint { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<PatchEntry> Entries { get; set; } = [];

    public static PatchProject Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<PatchProject>(json, JsonOptions) ?? new PatchProject();
    }

    public void Save(string path)
    {
        UpdatedAt = DateTimeOffset.UtcNow;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    public PatchEntry? Find(string virtualPath) =>
        Entries.FirstOrDefault(e => e.VirtualPath.Equals(virtualPath, StringComparison.OrdinalIgnoreCase));

    public void Upsert(string virtualPath, byte[] onDiskBytes, byte[]? baselineSha256 = null)
    {
        var hex = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(onDiskBytes));
        var existing = Find(virtualPath);
        if (existing is null)
        {
            Entries.Add(new PatchEntry
            {
                VirtualPath = virtualPath.Replace('\\', '/'),
                Sha256 = hex,
                BaselineSha256 = baselineSha256 is null ? null : Convert.ToHexStringLower(baselineSha256),
                PayloadFile = PayloadFileName(virtualPath),
            });
        }
        else
        {
            existing.Sha256 = hex;
            existing.PayloadFile ??= PayloadFileName(virtualPath);
        }
    }

    public static string PayloadFileName(string virtualPath)
    {
        var safe = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(virtualPath.ToLowerInvariant())));
        return safe + ".bin";
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed class PatchEntry
{
    public string VirtualPath { get; set; } = "";
    public string PayloadFile { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string? BaselineSha256 { get; set; }
    public string? Note { get; set; }
}
