using System.Diagnostics;
using System.Drawing.Drawing2D;
using Aion.Launcher;

namespace AionLauncher;

/// <summary>
/// The launcher window: a guided setup (sign in, get the server's game version, install), then Play.
/// Each step works out the next one itself; the player only confirms and answers Windows prompts.
/// </summary>
sealed class MainForm : Form
{
    // Aion's palette: deep night blue, gold, warm parchment text.
    static readonly Color Background = Color.FromArgb(11, 15, 26), Panel = Color.FromArgb(20, 27, 43), Line = Color.FromArgb(48, 58, 84),
        Text1 = Color.FromArgb(240, 233, 216), Muted = Color.FromArgb(167, 160, 142), Accent = Color.FromArgb(217, 180, 90),
        AccentHover = Color.FromArgb(236, 206, 132), OnAccent = Color.FromArgb(30, 22, 8), PanelHover = Color.FromArgb(34, 44, 66),
        Input = Color.FromArgb(15, 21, 34), Good = Color.FromArgb(126, 220, 160), Danger = Color.FromArgb(255, 138, 138);
    const string Game = "AION 2";
    static readonly Image? Emblem = LoadEmblem();

    static Image? LoadEmblem()
    {
        using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("emblem.png");
        return stream is null ? null : Image.FromStream(stream);
    }
    static readonly string[] Steps = ["Sign in", "Get the game", "Install", "Play"];
    const int SignInStep = 0, GameStep = 1, InstallStep = 2, PlayStep = 3;

    readonly Workflow workflow;
    SteamInstall? steam = Workflow.FindSteam();
    readonly Rail rail = new() { Dock = DockStyle.Left };
    readonly Label heading = new() { Dock = DockStyle.Top, Font = new Font("Segoe UI Semibold", 17F) };
    readonly Label status = new() { Dock = DockStyle.Fill, ForeColor = Muted, TextAlign = ContentAlignment.MiddleLeft };
    readonly Bar progress = new() { Dock = DockStyle.Top, Visible = false };
    // No scrolling: the window grows to fit each page instead (Fit).
    readonly FlowLayoutPanel stack = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = false };
    readonly Panel content = new() { Dock = DockStyle.Fill };
    readonly Panel footer = new() { Dock = DockStyle.Bottom };
    readonly System.Windows.Forms.Timer timer = new();
    bool fitting;
    EventHandler? tick;
    CancellationTokenSource? work;
    int step;
    /// <summary>Set when the updated launcher has been started: this one closes without ending the session.</summary>
    bool handingOver;

    readonly bool uninstall;

    public MainForm(Workflow workflow, bool uninstall = false)
    {
        this.workflow = workflow;
        this.uninstall = uninstall;
        Text = $"{workflow.Config.Name} {Install.Version}";
        // The exe's icon (icon.ico) in the title bar and taskbar.
        try { if (Environment.ProcessPath is { } exe) Icon = Icon.ExtractAssociatedIcon(exe); } catch (Exception error) when (error is ArgumentException or IOException) { }
        ClientSize = new Size(S(800), S(540));
        MinimumSize = new Size(S(760), S(500));
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Background;
        ForeColor = Text1;
        Font = new Font("Segoe UI", 10F);

        rail.Width = S(220);
        rail.BackColor = Panel;
        rail.Paint += PaintRail;

        content.Padding = new Padding(S(36), S(28), S(36), S(12));
        heading.Height = S(48);
        progress.Height = S(8);
        footer.Height = S(56); footer.Padding = new Padding(0, S(6), 0, 0);
        footer.Controls.Add(status); footer.Controls.Add(progress);
        content.Controls.Add(stack); content.Controls.Add(footer); content.Controls.Add(heading);
        Controls.Add(content); Controls.Add(rail);
        // Every page is built by adding controls to the stack; once they are in, the window is grown to show them all.
        stack.ControlAdded += (_, _) => { if (IsHandleCreated) BeginInvoke(Fit); };
        stack.Layout += (_, _) => { if (IsHandleCreated && !fitting) BeginInvoke(Fit); };
        DpiChanged += (_, _) => BeginInvoke(Fit);
        // The page follows the window: inputs, wide buttons and bars take the page's width, labels wrap to it.
        stack.Resize += (_, _) => Stretch();

        FormClosing += (_, e) =>
        {
            if (work is not null && MessageBox.Show(this, "Stop the current task and close?", Text, MessageBoxButtons.YesNo) == DialogResult.No) { e.Cancel = true; return; }
            work?.Cancel();
            // A kept session ("Keep me signed in") stays valid for the next start; otherwise the session ends here.
            if (workflow.Account.SignedIn && !handingOver && workflow.Settings.Session() is null) Task.Run(() => workflow.Account.SignOutAsync()).Wait(TimeSpan.FromSeconds(3));
        };
        Shown += async (_, _) => { Fit(); if (uninstall) ShowUninstall(); else await StartAsync(); };
    }

    int S(int pixels) => LogicalToDeviceUnits(pixels);
    /// <summary>The width page controls take: the page's current width (so they follow the window), at least a readable minimum.</summary>
    int ContentWidth => Math.Max(S(320), stack.ClientSize.Width - S(8));

    /// <summary>Re-applies <see cref="ContentWidth"/> to the current page's controls after the window changed size.</summary>
    void Stretch()
    {
        var width = ContentWidth;
        foreach (Control control in stack.Controls)
        {
            switch (control)
            {
                case Label label: label.MaximumSize = new Size(width, 0); break;   // LinkLabel too
                case TextBox or Bar: control.Width = width; break;
                case Button { Tag: "wide" }: control.Width = width; break;
                case FlowLayoutPanel { Tag: "requirement" } row:
                    row.Controls[1].Width = width - S(24) - (row.Controls.Count > 2 ? S(130) : 0);
                    break;
            }
        }
    }

    // ---------- Rail geometry (shared by the painter and Fit) ----------

    int RailTextTop => Emblem is null ? S(22) : S(18) + S(72) + S(10);
    int RailStepsTop => RailTextTop + S(100);
    int RailStepGap => S(56);
    /// <summary>The height the rail needs to show every step.</summary>
    int RailHeight => RailStepsTop + Steps.Length * RailStepGap + S(8);

    // ---------- Title bar in the launcher's colours ----------

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ThemeTitleBar();
        // The constructor's sizes were computed before the window knew its monitor's DPI; redo them now.
        MinimumSize = new Size(S(760), S(520));
        ClientSize = new Size(Math.Max(ClientSize.Width, S(800)), Math.Max(ClientSize.Height, Math.Max(S(560), RailHeight)));
    }

    /// <summary>
    /// The window frame follows the page: dark title bar with the launcher's panel colour, text and
    /// border (Windows 11; Windows 10 gets the dark title bar only). The system's own buttons stay,
    /// so snapping, dragging and the window menu work as usual.
    /// </summary>
    void ThemeTitleBar()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return;
        var dark = 1;
        DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));          // DWMWA_USE_IMMERSIVE_DARK_MODE
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        var caption = ColorRef(Panel); var text = ColorRef(Text1); var border = ColorRef(Line);
        DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int));       // DWMWA_CAPTION_COLOR
        DwmSetWindowAttribute(Handle, 36, ref text, sizeof(int));          // DWMWA_TEXT_COLOR
        DwmSetWindowAttribute(Handle, 34, ref border, sizeof(int));        // DWMWA_BORDER_COLOR
    }

    static int ColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Grows the window (never shrinks it) so the current page's controls are all in view, whatever
    /// the DPI, fonts or text lengths come to; keeps the window inside the screen's working area.
    /// </summary>
    void Fit()
    {
        if (fitting || IsDisposed || !IsHandleCreated) return;
        fitting = true;
        try
        {
            var controls = stack.Controls.Cast<Control>().Where(c => c.Visible).ToList();
            if (controls.Count == 0) return;
            var pageHeight = controls.Sum(c => c.Height + c.Margin.Vertical);
            var pageWidth = controls.Max(c => c.Width + c.Margin.Horizontal);
            var neededHeight = content.Padding.Vertical + heading.Height + footer.Height + pageHeight + S(8);
            var neededWidth = rail.Width + content.Padding.Horizontal + pageWidth + S(8);
            neededHeight = Math.Max(neededHeight, RailHeight);
            if (neededHeight <= ClientSize.Height && neededWidth <= ClientSize.Width) return;
            var area = Screen.FromControl(this).WorkingArea;
            var frame = Size - ClientSize;
            ClientSize = new Size(Math.Min(Math.Max(neededWidth, ClientSize.Width), area.Width - frame.Width),
                Math.Min(Math.Max(neededHeight, ClientSize.Height), area.Height - frame.Height));
            if (Bottom > area.Bottom) Top = Math.Max(area.Top, area.Bottom - Height);
            if (Right > area.Right) Left = Math.Max(area.Left, area.Right - Width);
        }
        finally { fitting = false; }
    }

    // ---------- Flow ----------

    /// <summary>A downloaded copy first offers to install itself; then a kept session continues without the sign-in page, and the program looks for its own update.</summary>
    async Task StartAsync()
    {
        if (!Install.Running && !workflow.Settings.NoInstall) { ShowInstallOffer(); return; }
        await ContinueAsync();
    }

    // ---------- Install / uninstall ----------

    void ShowInstallOffer()
    {
        Page(SignInStep, $"Install {Install.Name}", $"{Install.Name} is set up for your Windows account: it goes to {Install.Folder}, "
            + "with a shortcut on the desktop and in the Start menu, and shows in Apps & features for removal. No administrator rights are needed.");
        if (Install.Present) AddLabel("A copy is already installed there; this one replaces it.", muted: true);
        var install = AddButton("Install", primary: true, null, wide: true);
        AddLink("Run without installing", async () =>
        {
            workflow.Settings.NoInstall = true;
            workflow.Settings.Save();
            await ContinueAsync();
        });
        AcceptButton = install;
        install.Click += async (_, _) =>
        {
            string? installed = null;
            var ok = await RunAsync(async _ => { SetStatus("Installing…"); installed = await Task.Run(Install.Run); }, install);
            if (!ok) return;
            handingOver = true;
            Process.Start(new ProcessStartInfo(installed!) { UseShellExecute = true, WorkingDirectory = Install.Folder });
            Close();
        };
    }

    /// <summary>Started from Apps &amp; features: the same removal as the Play page's Uninstall, without signing in.</summary>
    void ShowUninstall()
    {
        Page(PlayStep, $"Remove {Install.Name}", $"This removes {Install.Name} from your Windows account: the program, its shortcuts, "
            + "the firewall rules and settings. You can also delete the private game copy. Your Steam copy of the game is not touched.");
        var remove = AddButton("Remove", primary: true, null, wide: true);
        AddButton("Cancel", primary: false, () => { Close(); return Task.CompletedTask; });
        remove.Click += async (_, _) => { if (await UninstallAsync(remove)) Close(); };
    }

    /// <summary>
    /// Firewall rules, hosts lines, settings, optionally the game copy; and the installed program
    /// (shortcuts, Apps &amp; features entry, folder) when there is one. True when done.
    /// </summary>
    async Task<bool> UninstallAsync(params Control[] disable)
    {
        var answer = workflow.Settings.InstallFolder is { } copy && Directory.Exists(copy)
            ? MessageBox.Show(this, $"Also delete the private game copy ({copy})?\n\nYes deletes it (it can be set up again later). No keeps the files.",
                Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question)
            : DialogResult.No;
        if (answer == DialogResult.Cancel) return false;
        return await RunAsync(async _ =>
        {
            SetStatus("Removing the firewall rules (approve the Windows prompt)…");
            await Elevated.RunAsync("--cleanup");
            if (answer == DialogResult.Yes && workflow.Settings.InstallFolder is { } folder && Directory.Exists(folder))
            {
                SetStatus("Deleting the private copy…");
                await Task.Run(() => Directory.Delete(folder, recursive: true));
            }
            if (workflow.Account.SignedIn) await workflow.Account.SignOutAsync();
            LauncherSettings.Delete();
            workflow.Settings.InstallFolder = null;
            workflow.Settings.VerifiedStamp = null;
            workflow.Settings.SetSession(null, null);
            if (Install.Present || Install.Running) { SetStatus("Removing the program…"); Install.Remove(); }
            SetStatus("Removed.");
        }, disable);
    }

    /// <summary>A kept session continues without the sign-in page; then the program looks for its own update.</summary>
    async Task ContinueAsync()
    {
        if (workflow.Settings.Session() is { } saved)
        {
            Page(SignInStep, "Signing in", null);
            SetStatus($"Continuing as {workflow.Settings.Username}…");
            var resumed = false;
            var ok = await RunAsync(async ct => resumed = await workflow.Account.ResumeAsync(saved, ct));
            if (ok && !resumed)
            {
                workflow.Settings.SetSession(null, null);
                workflow.Settings.Save();
                ShowSignIn("Your saved sign-in has ended. Sign in again.");
                return;
            }
            if (!ok) { ShowSignIn(status.Text); return; }
        }
        await AfterSignInAsync();
    }

    /// <summary>Signed in: offer the server's newer launcher when there is one, then carry on.</summary>
    async Task AfterSignInAsync()
    {
        if (workflow.Account.SignedIn && await OfferUpdateAsync()) return;
        await NextAsync();
    }

    /// <summary>Works out where the player is and shows that step.</summary>
    async Task NextAsync()
    {
        if (!workflow.Account.SignedIn) { ShowSignIn(); return; }
        if (workflow.Installed) { ShowPlay(); return; }
        steam ??= Workflow.FindSteam();
        if (steam is null) { ShowNoSteam(); return; }
        Page(GameStep, "Looking for the game", "Checking your Steam library for the version this server needs…");
        ClientSource? source = null;
        if (!await RunAsync(async _ => source = await workflow.FindSourceAsync(steam)))
        {
            AddButton("Try again", primary: true, async () => await NextAsync());
            return;
        }
        if (source is not null) ShowInstall(source);
        else ShowDownload();
    }

    // ---------- Sign in ----------

    void ShowSignIn(string? message = null)
    {
        Page(SignInStep, "Sign in", "Use the account you were given for this server.");
        AddLabel("Username");
        var username = AddInput(); username.Text = workflow.Settings.Username ?? "";
        AddLabel("Password");
        var password = AddInput(password: true);
        var keep = AddCheck("Keep me signed in on this PC", workflow.Settings.KeepSignedIn);
        var signIn = AddButton("Sign in", primary: true, null, wide: true);
        AddLink("Forgot your password or need help? Open the account page", () => Open(workflow.Config.AccountUrl));
        AcceptButton = signIn;
        ActiveControl = username.Text.Length > 0 ? password : username;
        if (message is not null) SetStatus(message, error: true);

        signIn.Click += async (_, _) =>
        {
            if (username.Text.Trim().Length == 0 || password.Text.Length == 0) { SetStatus("Enter your username and password.", error: true); return; }
            var ok = await RunAsync(async ct =>
            {
                SetStatus("Signing in…");
                await workflow.Account.SignInAsync(username.Text.Trim(), password.Text, keep.Checked, ct);
                workflow.Settings.Username = username.Text.Trim();
                workflow.Settings.KeepSignedIn = keep.Checked;
                workflow.Settings.SetSession(keep.Checked ? workflow.Account.Token : null, workflow.Account.ExpiresAt);
                workflow.Settings.Save();
            }, username, password, keep, signIn);
            password.Clear();
            if (ok) { SetStatus(""); await AfterSignInAsync(); }
        };
    }

    // ---------- Launcher update ----------

    /// <summary>
    /// When the server's launcher differs from this exe, shows it and offers to install it; the
    /// player decides. True when a page was shown (the player continues from there) or the new
    /// launcher has taken over; false to carry on as usual.
    /// </summary>
    async Task<bool> OfferUpdateAsync()
    {
        var exe = Environment.ProcessPath;
        if (exe is null || Environment.GetEnvironmentVariable("AION_LAUNCHER_NO_UPDATE") is { Length: > 0 }) return false;
        Page(SignInStep, "Checking for updates", null);
        AccountClient.LauncherManifest? manifest = null;
        string? own = null;
        var checkedOk = await RunAsync(async ct =>
        {
            SetStatus("Checking for a newer version…");
            manifest = await workflow.Account.LauncherManifestAsync(ct);
            if (manifest is not null) own = await LauncherUpdate.HashAsync(exe, ct);
        });
        // A failed check (server unreachable, unreadable exe) is not worth stopping for.
        if (!checkedOk || manifest is null || !LauncherUpdate.Needed(manifest, own!, new FileInfo(exe).Length)) return false;

        var version = manifest.Version is { } v ? $" ({v})" : "";
        Page(SignInStep, $"A new version of {Install.Name} is available", $"The server has a newer version{version}. Install it to get the latest fixes: "
            + "it is downloaded and started in place of this one, and your settings and game files are kept.");
        AddLabel($"Installed version: {Install.Version}", muted: true);
        var install = AddButton("Install update", primary: true, null, wide: true);
        AddButton("Not now", primary: false, async () => await NextAsync());
        AcceptButton = install;
        install.Click += async (_, _) => await InstallUpdateAsync(exe, manifest);
        return true;
    }

    async Task InstallUpdateAsync(string exe, AccountClient.LauncherManifest manifest)
    {
        Page(SignInStep, $"Updating {Install.Name}", "The new version is downloaded and checked, then started in place of this one.");
        var downloaded = exe + LauncherUpdate.NewSuffix;
        var ok = await RunAsync(async ct =>
        {
            SetStatus("Downloading the new version…");
            await workflow.Account.DownloadLauncherAsync(downloaded, ProgressReporter(), ct);
            SetStatus("Checking the download…");
            if (await LauncherUpdate.HashAsync(downloaded, ct) != manifest.Sha256.ToLowerInvariant())
                throw new LauncherException("The download did not match what the server announced.");
            LauncherUpdate.Swap(exe, downloaded);
        });
        if (!ok)
        {
            try { if (File.Exists(downloaded)) File.Delete(downloaded); } catch (IOException) { }
            var reason = status.Text;
            Page(SignInStep, "The update was not installed", $"{reason} You can download the new version from the account page instead; "
                + "this one keeps working in the meantime.");
            AddButton("Continue with this version", primary: true, async () => await NextAsync());
            AddLink("Open the account page", () => Open(workflow.Config.AccountUrl));
            return;
        }
        SetStatus("Starting the new version…");
        handingOver = true;
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
        Close();
    }

    // ---------- Get the game ----------

    void ShowNoSteam()
    {
        Page(GameStep, "Steam is needed", "The game comes from your own Steam account. Install Steam, sign in, then come back here: "
            + $"{Install.Name} continues by itself.");
        AddButton("Get Steam", primary: true, () => { Open("https://store.steampowered.com/about/"); return Task.CompletedTask; });
        Every(3000, async () =>
        {
            if ((steam = Workflow.FindSteam()) is not null) { StopTimer(); await NextAsync(); }
        });
    }

    void ShowDownload(string? message = null)
    {
        var resuming = workflow.Settings.DownloadFolder is not null;
        Page(GameStep, resuming ? "Continue the download" : "Download the game", "This server runs one specific version of the game (about 88 GB). "
            + "A2Core Launcher downloads it from Steam with your own Steam account. Your normal Steam copy of the game is not changed.");
        var folder = workflow.DownloadFolder();
        var spaceRow = AddRequirement(null);
        spaceRow.Set(folder is not null, folder is null
            ? $"No drive has {Gb(workflow.Build.TotalBytes)} free. Free up space, then press Check again."
            : $"Saving to {folder} ({Gb(workflow.StillNeeded(folder))} to download, {Gb(Workflow.FreeBytes(folder))} free).");
        var start = AddButton(resuming ? "Continue download" : "Sign in to Steam and download", primary: true, null, wide: true);
        start.Visible = folder is not null;
        if (folder is null) AddButton("Check again", primary: false, () => { ShowDownload(); return Task.CompletedTask; });
        AddLabel("You sign in by scanning a QR code with the Steam app on your phone. A2Core Launcher never sees your Steam password.", muted: true);
        AddLink("I already have this version in another folder", ChooseExistingCopy);
        if (message is not null) SetStatus(message, error: true);
        start.Click += async (_, _) => await DownloadAsync();
        // An interrupted download with a saved Steam sign-in continues by itself.
        if (resuming && message is null && workflow.Settings.SteamSignIn() is not null && folder is not null)
            BeginInvoke(async () => await DownloadAsync());
    }

    async Task DownloadAsync()
    {
        Page(GameStep, "Downloading from Steam", null);
        // Sign-in view: the QR code and how to scan it.
        var qrBox = new PictureBox { Width = S(220), Height = S(220), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White, Margin = new Padding(0, 0, 0, S(12)), Visible = false };
        var qrHelp = AddLabel("", muted: false);
        stack.Controls.Add(qrBox);
        stack.Controls.SetChildIndex(qrBox, 0);
        qrHelp.Text = "Connecting to Steam…";
        // Download view.
        var bar = new Bar { Width = ContentWidth, Height = S(10), Margin = new Padding(0, S(4), 0, S(6)), Visible = false };
        stack.Controls.Add(bar);
        var amount = AddLabel("");
        var note = AddLabel($"You can close {Install.Name} at any time; the download continues where it stopped when you open it again.", muted: true);
        note.Visible = false;
        var pause = AddButton("Pause", primary: false, null);
        pause.Visible = false;

        var samples = new Queue<(DateTime At, long Bytes)>();
        void ShowQr(string url)
        {
            if (InvokeRequired) { BeginInvoke(() => ShowQr(url)); return; }
            var old = qrBox.Image;
            qrBox.Image = QrBitmap(url);
            old?.Dispose();
            qrBox.Visible = true;
            qrHelp.Text = "Scan this code to sign in to Steam:\n   1.  Open the Steam app on your phone.\n   2.  Tap the Steam Guard shield, then Scan a QR code.\n   3.  Approve the sign-in.";
            SetStatus("Waiting for you to approve the sign-in in the Steam app…");
        }
        var progress = new Progress<SteamDownload.Status>(p =>
        {
            if (!bar.Visible)
            {
                qrBox.Visible = false; bar.Visible = true; note.Visible = true; pause.Visible = true;
                qrHelp.Text = "Signed in to Steam.";
            }
            bar.Value = (int)(p.Bytes * 1000 / Math.Max(1, p.Total));
            var now = DateTime.Now;
            samples.Enqueue((now, p.Bytes));
            while (samples.Count > 2 && now - samples.Peek().At > TimeSpan.FromSeconds(20)) samples.Dequeue();
            var (firstAt, firstBytes) = samples.Peek();
            var rate = (now - firstAt).TotalSeconds > 2 ? (p.Bytes - firstBytes) / (now - firstAt).TotalSeconds : 0;
            amount.Text = $"{Gb(p.Bytes)} of {Gb(p.Total)}" + (rate > 0 ? $"  ·  {rate / 1e6:0.0} MB/s  ·  about {Remaining((p.Total - p.Bytes) / rate)} left" : "");
            SetStatus(p.Phase);
        });

        string? folder = null;
        using var cancel = new CancellationTokenSource();
        pause.Click += (_, _) => cancel.Cancel();
        var ok = await RunAsync(async ct =>
        {
            using var both = CancellationTokenSource.CreateLinkedTokenSource(ct, cancel.Token);
            folder = await workflow.DownloadAsync(ShowQr, progress, both.Token);
        });
        if (!ok)
        {
            // The status line holds the error (or "Cancelled."); offer to continue.
            var reason = status.Text == "Cancelled." ? "Paused. Press Continue download to carry on." : status.Text;
            ShowDownload(reason);
            return;
        }
        Page(InstallStep, "Installing", "Downloaded. A2Core Launcher checks the files and adds firewall rules so the game can reach only this server. "
            + "Windows asks for permission once: choose Yes.");
        if (await RunAsync(ct => workflow.AdoptAsync(folder!, s => SetStatus(s), ProgressReporter(), ct))) await NextAsync();
        else AddButton("Try again", primary: true, async () => await NextAsync());
    }

    static string Remaining(double seconds) => seconds switch
    {
        < 90 => "1 minute",
        < 3600 => $"{seconds / 60:0} minutes",
        _ => $"{seconds / 3600:0.0} hours",
    };

    /// <summary>The QR code for Steam's sign-in URL, drawn module by module (QRCoder for the matrix).</summary>
    static Bitmap QrBitmap(string text)
    {
        using var generator = new QRCoder.QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
        var matrix = data.ModuleMatrix;
        const int scale = 8;
        var bitmap = new Bitmap(matrix.Count * scale, matrix.Count * scale);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.White);
        for (var y = 0; y < matrix.Count; y++)
            for (var x = 0; x < matrix.Count; x++)
                if (matrix[y][x]) g.FillRectangle(Brushes.Black, x * scale, y * scale, scale, scale);
        return bitmap;
    }

    async Task ChooseExistingCopy()
    {
        using var dialog = new FolderBrowserDialog { Description = "Choose the folder with this server's game version: the one that contains the Aion2 and Engine folders" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        StopTimer();
        Page(InstallStep, "Installing", "Checking that folder, then adding firewall rules so the game can reach only this server. "
            + "Windows asks for permission once: choose Yes.");
        if (await RunAsync(ct => workflow.AdoptAsync(dialog.SelectedPath, s => SetStatus(s), ProgressReporter(), ct))) await NextAsync();
        else AddButton("Back", primary: true, async () => await NextAsync());
    }

    // ---------- Install ----------

    void ShowInstall(ClientSource source)
    {
        Page(InstallStep, "Installing", $"Found the server's version in {source.Description}. A2Core Launcher puts it in its own folder "
            + $"({Workflow.TargetFor(source.Folder)}) and adds firewall rules so the game can reach only this server.");
        AddLabel("Windows asks for permission once at the end: choose Yes.", muted: true);
        async Task Install()
        {
            if (await RunAsync(ct => workflow.InstallAsync(source, s => SetStatus(s), ProgressReporter(), ct))) { await NextAsync(); return; }
            AddButton("Try again", primary: true, async () => await NextAsync());
        }
        BeginInvoke(async () => await Install());
    }

    // ---------- Play ----------

    void ShowPlay()
    {
        Page(PlayStep, "Ready to play", null);
        var who = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, S(14)) };
        var signedIn = new Label { AutoSize = true, ForeColor = Muted, Text = $"Signed in as {workflow.Settings.Username}.", Margin = new Padding(0, S(2), S(6), 0) };
        var signOut = new LinkLabel { AutoSize = true, Text = "Sign out", LinkColor = Accent, ActiveLinkColor = Accent, Margin = new Padding(0, S(2), 0, 0) };
        who.Controls.AddRange([signedIn, signOut]);
        stack.Controls.Add(who);
        AddLabel("Client version", muted: true, size: 9F).Margin = new Padding(0, 0, 0, S(4));
        var versions = ClientVersions.All(workflow.Config);
        var versionBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Width = ContentWidth, BackColor = Input, ForeColor = Text1,
            Font = new Font("Segoe UI", 10.5F), Margin = new Padding(0, 0, 0, S(6)), Cursor = Cursors.Hand,
        };
        foreach (var version in versions) versionBox.Items.Add(version.Name);
        versionBox.SelectedIndex = Math.Max(0, versions.ToList().FindIndex(v => v.Id == workflow.Version.Id));
        stack.Controls.Add(versionBox);
        var versionNote = AddLabel("", muted: true, size: 9F);
        void DescribeVersion()
        {
            var version = workflow.Version;
            var folder = version.IsSteam ? ClientVersions.SteamFolder(version, workflow.Config, steam ??= Workflow.FindSteam()) : version.ClientPath ?? workflow.Settings.InstallFolder;
            versionNote.Text = version.IsSteam
                ? $"Game files: {folder ?? "Steam copy not found (set steamClientPath in launcher.json)"}. Used as-is: not changed, no patch paks; the server supports 2.0.6.0, so a newer client may not connect."
                : $"Game files: {folder}";
        }
        DescribeVersion();
        versionBox.SelectedIndexChanged += (_, _) =>
        {
            workflow.Version = versions[versionBox.SelectedIndex];
            try { ClientVersions.SaveSelection(Path.Combine(AppContext.BaseDirectory, "launcher.json"), workflow.Config, workflow.Version.Id); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { SetStatus("The choice was not saved: " + error.Message, true); }
            DescribeVersion();
        };
        var patchPaks = AddCheck("Apply client patch paks (z_A2Core_Patch-Windows_P) before launch", workflow.Settings.AllowClientPatchPaks);
        patchPaks.Margin = new Padding(0, 0, 0, S(8));
        var play = AddButton("Play", primary: true, null, wide: true);
        play.Height = S(56); play.Font = new Font("Segoe UI Semibold", 14F);
        AcceptButton = play;
        AddLabel($"Each time you press Play, Windows asks for permission: {Install.Name} points the game's online services at this "
            + "server while the game runs and removes them afterwards.", muted: true);

        var tools = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, S(18), 0, S(4)) };
        var verify = MakeButton("Check all files"); var repair = MakeButton("Repair firewall"); var uninstall = MakeButton("Uninstall");
        foreach (var button in new[] { verify, repair, uninstall }) { button.Width = S(150); button.Height = S(34); }
        tools.Controls.AddRange([verify, repair, uninstall]);
        stack.Controls.Add(tools);
        Control[] all = [play, verify, repair, uninstall, signOut, versionBox];

        signOut.LinkClicked += async (_, _) =>
        {
            await workflow.Account.SignOutAsync();
            workflow.Settings.SetSession(null, null);
            workflow.Settings.Save();
            ShowSignIn();
        };
        play.Click += async (_, _) =>
        {
            workflow.Settings.AllowClientPatchPaks = patchPaks.Checked;
            workflow.Settings.Save();
            if ((steam ??= Workflow.FindSteam()) is not { } found) { ShowNoSteam(); return; }
            var ok = await RunAsync(async ct =>
            {
                using var game = await workflow.PlayAsync(found, s => SetStatus(s), ProgressReporter(), ct);
                SetStatus("Playing. A2Core Launcher waits here until the game closes.");
                WindowState = FormWindowState.Minimized;
                await game.WaitForExitAsync(CancellationToken.None);
                // The client may hand over to child processes; wait for the whole copy to close.
                while (Elevated.GameRunning(workflow.GameFolder ?? workflow.Settings.InstallFolder!)) await Task.Delay(2000, CancellationToken.None);
                WindowState = FormWindowState.Normal;
                SetStatus("Game closed.");
            }, all);
            if (!ok) WindowState = FormWindowState.Normal;
        };
        verify.Click += async (_, _) => await RunAsync(async ct =>
        {
            SetStatus("Checking every game file (several minutes)…");
            var result = await workflow.VerifyAllAsync(ProgressReporter(), ct);
            SetStatus(result.Ok ? $"All {result.Checked} files match the server's version." : $"Problem: {result.Problem}. Uninstall and set up again to repair.", !result.Ok);
        }, all);
        repair.Click += async (_, _) => await RunAsync(async _ =>
        {
            SetStatus("Restoring the firewall rules (approve the Windows prompt)…");
            SetStatus(await workflow.RepairFirewallAsync() ? "Firewall rules restored." : "Repair was cancelled or failed.");
        }, all);
        uninstall.Click += async (_, _) =>
        {
            if (MessageBox.Show(this, $"Remove {Install.Name} from this PC?\n\nThis removes the program and its shortcuts, the firewall rules and the settings. "
                + "Your Steam copy of the game is not touched.", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            if (await UninstallAsync(all)) Close();
        };
    }

    // ---------- Page building ----------

    void Page(int newStep, string title, string? intro)
    {
        StopTimer();
        step = newStep;
        rail.Invalidate();
        heading.Text = title;
        AcceptButton = null;
        // Disposed after the current event: Page often runs from a click on one of these controls.
        var old = stack.Controls.Cast<Control>().ToArray();
        stack.Controls.Clear();
        BeginInvoke(() => { foreach (var control in old) control.Dispose(); });
        SetStatus("");
        if (intro is not null) AddLabel(intro, muted: true).Margin = new Padding(0, 0, 0, S(16));
    }

    void Every(int milliseconds, Func<Task> action)
    {
        StopTimer();
        var running = false;
        tick = async (_, _) =>
        {
            if (running || work is not null) return;
            running = true;
            try { await action(); } finally { running = false; }
        };
        timer.Interval = milliseconds;
        timer.Tick += tick;
        timer.Start();
    }

    void StopTimer()
    {
        timer.Stop();
        if (tick is not null) timer.Tick -= tick;
        tick = null;
    }

    Button MakeButton(string text, bool primary = false)
    {
        var button = new Button
        {
            Text = text, AutoSize = false, Height = S(40), Width = S(170), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            BackColor = primary ? Accent : Panel, ForeColor = primary ? OnAccent : Text1,
            Font = new Font("Segoe UI Semibold", 10F), Margin = new Padding(0, 0, S(8), 0),
        };
        button.FlatAppearance.BorderColor = primary ? Accent : Line;
        button.FlatAppearance.MouseOverBackColor = primary ? AccentHover : PanelHover;
        button.EnabledChanged += (_, _) => button.BackColor = button.Enabled ? (primary ? Accent : Panel) : Line;
        return button;
    }

    Button AddButton(string text, bool primary, Func<Task>? onClick, bool wide = false)
    {
        var button = MakeButton(text, primary);
        if (wide) { button.Width = ContentWidth; button.Tag = "wide"; }
        button.Margin = new Padding(0, S(4), 0, S(12));
        if (onClick is not null) button.Click += async (_, _) => await onClick();
        stack.Controls.Add(button);
        return button;
    }

    Label AddLabel(string text, bool muted = false, float size = 10F)
    {
        var label = new Label { Text = text, AutoSize = true, UseMnemonic = false, MaximumSize = new Size(ContentWidth, 0), ForeColor = muted ? Muted : Text1, Font = new Font("Segoe UI", size), Margin = new Padding(0, 0, 0, S(8)) };
        stack.Controls.Add(label);
        return label;
    }

    CheckBox AddCheck(string text, bool isChecked)
    {
        var check = new CheckBox { Text = text, AutoSize = true, Checked = isChecked, ForeColor = Text1, Margin = new Padding(0, 0, 0, S(12)), Cursor = Cursors.Hand };
        stack.Controls.Add(check);
        return check;
    }

    TextBox AddInput(bool password = false)
    {
        var input = new TextBox { Width = ContentWidth, BackColor = Input, ForeColor = Text1, BorderStyle = BorderStyle.FixedSingle, UseSystemPasswordChar = password, Margin = new Padding(0, 0, 0, S(12)), Font = new Font("Segoe UI", 11F) };
        stack.Controls.Add(input);
        return input;
    }

    void AddLink(string text, Func<Task> onClick)
    {
        var link = new LinkLabel { Text = text, AutoSize = true, LinkColor = Accent, ActiveLinkColor = Accent, Margin = new Padding(0, S(4), 0, S(8)) };
        link.LinkClicked += async (_, _) => await onClick();
        stack.Controls.Add(link);
    }

    void AddLink(string text, Action onClick) => AddLink(text, () => { onClick(); return Task.CompletedTask; });

    /// <summary>A checklist line: a mark, what is (or is not) in place, and an optional button that fixes it.</summary>
    sealed class Requirement(Label mark, Label text, Button? fix)
    {
        public Button? Fix => fix;
        public bool Ok { get; private set; }
        public void Set(bool ok, string message)
        {
            Ok = ok;
            mark.Text = ok ? "✓" : "✗";
            mark.ForeColor = ok ? Good : Danger;
            text.Text = message;
            if (fix is not null) fix.Visible = !ok;
        }
    }

    Requirement AddRequirement(string? fixText)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, S(8)), Tag = "requirement" };
        var mark = new Label { AutoSize = false, Width = S(24), Height = S(30), Font = new Font("Segoe UI Semibold", 12F), TextAlign = ContentAlignment.MiddleLeft };
        var text = new Label { AutoSize = false, Width = fixText is null ? ContentWidth - S(24) : ContentWidth - S(24) - S(130), Height = S(30), TextAlign = ContentAlignment.MiddleLeft };
        row.Controls.AddRange([mark, text]);
        Button? fix = null;
        if (fixText is not null)
        {
            fix = MakeButton(fixText); fix.Width = S(124); fix.Height = S(30); fix.Margin = Padding.Empty;
            row.Controls.Add(fix);
        }
        stack.Controls.Add(row);
        return new Requirement(mark, text, fix);
    }

    void SetStatus(string text, bool error = false)
    {
        if (InvokeRequired) { BeginInvoke(() => SetStatus(text, error)); return; }
        status.Text = text; status.ForeColor = error ? Danger : Muted;
    }

    IProgress<(long Done, long Total)> ProgressReporter() => new Progress<(long Done, long Total)>(p =>
    {
        progress.Visible = true;
        progress.Value = p.Total == 0 ? 0 : (int)Math.Clamp(p.Done * 1000 / p.Total, 0, 1000);
    });

    /// <summary>Runs one task with the given controls disabled; errors go to the status line. True when it finished.</summary>
    async Task<bool> RunAsync(Func<CancellationToken, Task> action, params Control[] disable)
    {
        foreach (var control in disable) control.Enabled = false;
        using var cancel = new CancellationTokenSource();
        work = cancel;
        try { await action(cancel.Token); return true; }
        catch (OperationCanceledException) { SetStatus("Cancelled."); return false; }
        catch (AccountException error) when (!workflow.Account.SignedIn && step != SignInStep)
        {
            // The server ended the session (or it was signed out elsewhere): a kept one is no use any more.
            workflow.Settings.SetSession(null, null);
            workflow.Settings.Save();
            ShowSignIn(error.Message);
            return false;
        }
        catch (Exception error) when (error is LauncherException or AccountException or SteamDownloadException or InvalidDataException or IOException
            or UnauthorizedAccessException or TimeoutException or HttpRequestException or SteamKit2.SteamKitWebRequestException or SteamKit2.Authentication.AuthenticationException)
        { SetStatus(error.Message, error: true); return false; }
        finally
        {
            if (work == cancel) work = null;
            progress.Visible = false;
            foreach (var control in disable) if (!control.IsDisposed) control.Enabled = true;
        }
    }

    static string Gb(long bytes) => $"{bytes / 1e9:0.0} GB";

    static void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

    // ---------- Step rail ----------

    sealed class Rail : System.Windows.Forms.Panel
    {
        public Rail() { DoubleBuffered = true; ResizeRedraw = true; }
    }

    /// <summary>A flat progress bar in the launcher's colours (Value 0-1000).</summary>
    sealed class Bar : Control
    {
        int value;
        public Bar() { DoubleBuffered = true; ResizeRedraw = true; }
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int Value { get => value; set { this.value = Math.Clamp(value, 0, 1000); Invalidate(); } }
        protected override void OnPaint(PaintEventArgs e)
        {
            using var back = new SolidBrush(Line);
            using var fill = new SolidBrush(Accent);
            e.Graphics.FillRectangle(back, ClientRectangle);
            e.Graphics.FillRectangle(fill, 0, 0, Width * value / 1000, Height);
        }
    }

    void PaintRail(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // Night sky fading into the page, with a thin gold edge.
        using (var sky = new LinearGradientBrush(rail.ClientRectangle, Color.FromArgb(28, 34, 56), Background, LinearGradientMode.Vertical))
            g.FillRectangle(sky, rail.ClientRectangle);
        using (var edge = new LinearGradientBrush(new Rectangle(rail.Width - S(2), 0, S(2), rail.Height), Accent, Color.FromArgb(0, Accent), LinearGradientMode.Vertical))
            g.FillRectangle(edge, rail.Width - S(2), 0, S(2), rail.Height);
        // The emblem, then the game and server names under it.
        var textTop = RailTextTop;
        if (Emblem is not null)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(Emblem, new Rectangle(S(24), S(18), S(72), S(72)));
        }
        TextRenderer.DrawText(g, Game, new Font("Segoe UI Black", 15F), new Point(S(22), textTop), Accent);
        TextRenderer.DrawText(g, workflow.Config.Name, new Font("Segoe UI Semibold", 11F), new Rectangle(S(24), textTop + S(34), rail.Width - S(36), S(44)), Text1,
            TextFormatFlags.WordBreak);
        TextRenderer.DrawText(g, "v" + Install.Version, new Font("Segoe UI", 8.5F), new Point(S(24), textTop + S(62)), Muted);
        var top = RailStepsTop; var gap = RailStepGap; var size = S(28); var x = S(24);
        for (var i = 0; i < Steps.Length; i++)
        {
            var y = top + i * gap;
            if (i < Steps.Length - 1)
                using (var pen = new Pen(i < step ? Accent : Line, S(2))) g.DrawLine(pen, x + size / 2, y + size + S(4), x + size / 2, y + gap - S(4));
            var circle = new Rectangle(x, y, size, size);
            var done = i < step; var current = i == step;
            if (done) using (var brush = new SolidBrush(Accent)) g.FillEllipse(brush, circle);
            else using (var pen = new Pen(current ? Accent : Line, S(2))) g.DrawEllipse(pen, circle);
            TextRenderer.DrawText(g, done ? "✓" : (i + 1).ToString(), new Font("Segoe UI Semibold", 10F), circle,
                done ? OnAccent : current ? Accent : Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, Steps[i], new Font(current ? "Segoe UI Semibold" : "Segoe UI", 10.5F), new Point(x + size + S(14), y + S(4)),
                current ? Text1 : done ? Muted : Color.FromArgb(110, 106, 94));
        }
    }
}
