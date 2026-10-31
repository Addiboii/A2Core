using System.Text.Json;

namespace AionClientEditor.App;

sealed class EditorSettings
{
    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "A2CorePrivate",
        "client-editor-settings.json");

    public string? LastPaksFolder { get; set; }
    public string? LastKeyJsonPath { get; set; }
    public string? LastOodleDllPath { get; set; }
    public string? LastUsmapPath { get; set; }

    public static EditorSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<EditorSettings>(File.ReadAllText(FilePath)) ?? new EditorSettings()
                : new EditorSettings();
        }
        catch
        {
            return new EditorSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
