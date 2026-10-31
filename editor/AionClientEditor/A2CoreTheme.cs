namespace AionClientEditor.App;

/// <summary>A2Core launcher palette (see launcher/AionLauncher/MainForm.cs).</summary>
static class A2CoreTheme
{
    public static readonly Color Background = Color.FromArgb(11, 15, 26);
    public static readonly Color Panel = Color.FromArgb(20, 27, 43);
    public static readonly Color Line = Color.FromArgb(48, 58, 84);
    public static readonly Color Text1 = Color.FromArgb(240, 233, 216);
    public static readonly Color Muted = Color.FromArgb(167, 160, 142);
    public static readonly Color Accent = Color.FromArgb(217, 180, 90);
    public static readonly Color AccentHover = Color.FromArgb(236, 206, 132);
    public static readonly Color OnAccent = Color.FromArgb(30, 22, 8);
    public static readonly Color PanelHover = Color.FromArgb(34, 44, 66);
    public static readonly Color Input = Color.FromArgb(15, 21, 34);

    public static Font UiFont(float size, bool semibold = false) =>
        new(semibold ? "Segoe UI Semibold" : "Segoe UI", size);

    public static void ApplyForm(Form form)
    {
        form.BackColor = Background;
        form.ForeColor = Text1;
        form.Font = UiFont(10f);
        try
        {
            if (Environment.ProcessPath is { } exe)
                form.Icon = Icon.ExtractAssociatedIcon(exe);
        }
        catch (Exception error) when (error is ArgumentException or IOException)
        {
            // optional when running from dotnet run without an exe icon
        }
    }

    public static void ApplyToolStrip(ToolStrip strip)
    {
        strip.BackColor = Panel;
        strip.ForeColor = Text1;
        strip.GripStyle = ToolStripGripStyle.Hidden;
        strip.Renderer = new A2CoreToolStripRenderer();
        strip.Padding = new Padding(8, 4, 8, 4);
        foreach (ToolStripItem item in strip.Items)
        {
            item.ForeColor = Text1;
            item.BackColor = Panel;
            if (item is ToolStripButton button)
            {
                button.DisplayStyle = ToolStripItemDisplayStyle.Text;
                button.Margin = new Padding(0, 0, 12, 0);
            }
        }
    }

    public static void ApplyTextBox(TextBox box, bool multiline = false)
    {
        box.BackColor = Input;
        box.ForeColor = Text1;
        box.BorderStyle = BorderStyle.FixedSingle;
        if (multiline)
            box.Font = UiFont(9f);
    }

    public static void ApplyListBox(ListBox list)
    {
        list.BackColor = Input;
        list.ForeColor = Text1;
        list.BorderStyle = BorderStyle.FixedSingle;
        list.IntegralHeight = false;
    }

    public static void ApplyLabel(Label label, bool muted = false, bool semibold = false)
    {
        label.ForeColor = muted ? Muted : Text1;
        if (semibold)
            label.Font = UiFont(label.Font.Size, semibold: true);
    }

    public static void ApplyStatusLabel(Label label)
    {
        label.BackColor = Panel;
        label.ForeColor = Muted;
        label.Padding = new Padding(12, 0, 12, 0);
    }

    public static void ApplyDataGridView(DataGridView grid)
    {
        grid.BackgroundColor = Background;
        grid.GridColor = Line;
        grid.BorderStyle = BorderStyle.None;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        grid.RowHeadersVisible = false;
        grid.DefaultCellStyle.BackColor = Input;
        grid.DefaultCellStyle.ForeColor = Text1;
        grid.DefaultCellStyle.SelectionBackColor = PanelHover;
        grid.DefaultCellStyle.SelectionForeColor = Text1;
        grid.AlternatingRowsDefaultCellStyle.BackColor = Panel;
        grid.AlternatingRowsDefaultCellStyle.ForeColor = Text1;
        grid.AlternatingRowsDefaultCellStyle.SelectionBackColor = PanelHover;
        grid.AlternatingRowsDefaultCellStyle.SelectionForeColor = Text1;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Panel;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Accent;
        grid.ColumnHeadersDefaultCellStyle.Font = UiFont(9.5f, semibold: true);
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Panel;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
    }

    sealed class A2CoreToolStripRenderer : ToolStripProfessionalRenderer
    {
        public A2CoreToolStripRenderer() : base(new A2CoreColorTable()) { }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }

        protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
        {
            if (e.Item is not ToolStripButton button || !button.Enabled)
            {
                base.OnRenderButtonBackground(e);
                return;
            }

            var bounds = new Rectangle(Point.Empty, e.Item.Size);
            var hover = e.Item.Selected || e.Item.Pressed;
            var fill = hover ? PanelHover : Panel;
            using var brush = new SolidBrush(fill);
            e.Graphics.FillRectangle(brush, bounds);
            if (hover)
            {
                using var pen = new Pen(Accent);
                e.Graphics.DrawRectangle(pen, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
            }
        }
    }

    sealed class A2CoreColorTable : ProfessionalColorTable
    {
        public override Color ToolStripGradientBegin => Panel;
        public override Color ToolStripGradientMiddle => Panel;
        public override Color ToolStripGradientEnd => Panel;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Line;
        public override Color MenuItemSelected => PanelHover;
        public override Color MenuItemSelectedGradientBegin => PanelHover;
        public override Color MenuItemSelectedGradientEnd => PanelHover;
        public override Color MenuStripGradientBegin => Panel;
        public override Color MenuStripGradientEnd => Panel;
        public override Color ToolStripBorder => Line;
        public override Color ImageMarginGradientBegin => Panel;
        public override Color ImageMarginGradientMiddle => Panel;
        public override Color ImageMarginGradientEnd => Panel;
    }
}
