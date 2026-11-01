using QRCoder;

namespace Aion.Launcher.BuildTool;

static class TerminalQr
{
    public static void WriteChallenge(string url, string? pngPath)
    {
        Console.WriteLine();
        Console.WriteLine(url);
        Console.WriteLine();
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        Console.WriteLine(RenderAscii(data));
        if (pngPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pngPath))!);
            File.WriteAllBytes(pngPath, new PngByteQRCode(data).GetGraphic(8));
            Console.WriteLine($"QR PNG written to {pngPath}");
        }
    }

    static string RenderAscii(QRCodeData data)
    {
        var modules = data.ModuleMatrix;
        var lines = new List<string>();
        for (var y = 0; y < modules.Count; y++)
        {
            var row = modules[y];
            var line = new char[row.Count * 2];
            for (var x = 0; x < row.Count; x++)
                (line[x * 2], line[x * 2 + 1]) = row[x] ? ('█', '█') : (' ', ' ');
            lines.Add(new string(line));
        }
        return string.Join(Environment.NewLine, lines);
    }
}
