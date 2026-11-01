using System.Drawing.Drawing2D;

namespace AionClientEditor.App;

/// <summary>Left branding rail matching the A2Core Launcher.</summary>
sealed class BrandRail : Panel
{
    static readonly Image? Emblem = LoadEmblem();
    const string Game = "AION 2";
    readonly string productTitle;
    readonly string versionText;

    public BrandRail(string productTitle, string versionText)
    {
        this.productTitle = productTitle;
        this.versionText = versionText;
        Dock = DockStyle.Left;
        Width = 220;
        BackColor = A2CoreTheme.Panel;
        DoubleBuffered = true;
        Padding = new Padding(0);
    }

    static Image? LoadEmblem()
    {
        using var stream = typeof(BrandRail).Assembly.GetManifestResourceStream("emblem.png");
        return stream is null ? null : Image.FromStream(stream);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var sky = new LinearGradientBrush(ClientRectangle, Color.FromArgb(28, 34, 56), A2CoreTheme.Background, LinearGradientMode.Vertical))
            g.FillRectangle(sky, ClientRectangle);
        using (var edge = new LinearGradientBrush(new Rectangle(Width - 2, 0, 2, Height), A2CoreTheme.Accent, Color.FromArgb(0, A2CoreTheme.Accent), LinearGradientMode.Vertical))
            g.FillRectangle(edge, Width - 2, 0, 2, Height);

        var textTop = Emblem is null ? 22 : 18 + 72 + 10;
        if (Emblem is not null)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(Emblem, new Rectangle(24, 18, 72, 72));
        }

        TextRenderer.DrawText(g, Game, new Font("Segoe UI Black", 15F), new Point(22, textTop), A2CoreTheme.Accent);
        TextRenderer.DrawText(g, productTitle, new Font("Segoe UI Semibold", 11F),
            new Rectangle(24, textTop + 34, Width - 36, 44), A2CoreTheme.Text1, TextFormatFlags.WordBreak);
        TextRenderer.DrawText(g, versionText, new Font("Segoe UI", 8.5F), new Point(24, textTop + 62), A2CoreTheme.Muted);

        var subtitle = "Client table editor";
        TextRenderer.DrawText(g, subtitle, new Font("Segoe UI", 9F), new Point(24, textTop + 82), A2CoreTheme.Muted);
    }
}
