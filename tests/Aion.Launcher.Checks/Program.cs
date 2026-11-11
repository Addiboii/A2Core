// Checks of the launcher's core logic: Steam metadata parsing, the supported-build reference,
// settings/hosts/firewall text, and file placement. Uses temporary folders; when this PC has
// Steam with AION2 cached, the real appinfo.vdf is parsed too (read-only).
using System.Net;
using System.Text;
using Aion.Launcher;

var checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception("FAILED: " + label);
    checks++;
}

// ---------- Config ----------
var config = LauncherConfig.Load();
Check(config.Server.ToString() == config.ServerAddress && config.AccountUrl.StartsWith("https://"), "embedded config");
Check(config.BootstrapUrl(new string('A', 64)) == $"http://{config.ServerAddress}:{config.BootstrapPort}/c/{new string('A', 64)}", "bootstrap URL carries the code");
foreach (var bad in new[] { config with { ServerAddress = "example.com" }, config with { AccountUrl = "http://x" }, config with { SavedDirSuffix = "Steam" } })
{
    try { bad.Validate(); Check(false, "invalid config accepted"); } catch (InvalidDataException) { checks++; }
}

// ---------- Certificate pins (SPKI SHA-256) ----------
{
    using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
    var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=127.0.0.1", key, System.Security.Cryptography.HashAlgorithmName.SHA256);
    using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    using var renewed = request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(60));
    using var otherKey = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
    using var other = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=127.0.0.1", otherKey, System.Security.Cryptography.HashAlgorithmName.SHA256)
        .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    var spki = System.Security.Cryptography.SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo());
    var base64 = Convert.ToBase64String(spki);
    Check(CertificatePins.Base64(certificate) == base64 && base64.Length == 44, "pin is base64 SHA-256 of the SPKI");
    Check(CertificatePins.Parse(base64)!.SequenceEqual(spki) && CertificatePins.Parse(Convert.ToHexString(spki).ToLowerInvariant())!.SequenceEqual(spki)
        && CertificatePins.Parse("sha256//" + base64)!.SequenceEqual(spki), "pin parses from base64, hex and curl's sha256// form");
    Check(CertificatePins.Parse("zz") is null && CertificatePins.Parse(new string('a', 40)) is null && CertificatePins.Parse("") is null, "bad pins rejected (a 40-hex thumbprint is not a pin)");
    var pins = new[] { CertificatePins.Parse(base64)! };
    var errors = System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors | System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch;
    Check(CertificatePins.Accept(other, System.Net.Security.SslPolicyErrors.None, pins), "a normally trusted certificate passes");
    Check(CertificatePins.Accept(certificate, errors, pins), "a self-signed certificate passes when its key is pinned");
    Check(CertificatePins.Accept(renewed, errors, pins), "a renewed certificate with the same key keeps passing");
    Check(!CertificatePins.Accept(other, errors, pins), "another key with errors is refused");
    Check(!CertificatePins.Accept(certificate, errors, []), "no pins keeps the normal refusal");
    Check(!CertificatePins.Accept(null, System.Net.Security.SslPolicyErrors.RemoteCertificateNotAvailable, pins), "no certificate refused");
    Check(CertificatePins.Accept(other, errors, [new byte[32], CertificatePins.Parse(CertificatePins.Base64(other))!]), "several pins (key rollover)");
    (config with { CertificatePins = [base64, Convert.ToHexString(spki)] }).Validate();
    checks++;
    foreach (var bad in new[] { config with { CertificatePins = ["zz"] }, config with { CertificatePins = Enumerable.Repeat(base64, 9).ToArray() } })
    {
        try { bad.Validate(); Check(false, "invalid pins accepted"); } catch (InvalidDataException) { checks++; }
    }
    var json = $$"""{"name":"N","accountUrl":"https://127.0.0.1:8780","serverAddress":"127.0.0.1","bootstrapPort":8087,"savedDirSuffix":"Private","certificatePins":["{{base64}}"]}""";
    Check(System.Text.Json.JsonSerializer.Deserialize<LauncherConfig>(json)!.CertificatePins!.Single() == base64, "certificatePins round-trips through launcher.json");
}

// ---------- Two-step sign-in ----------
{
    var challenge = new string('c', 64);
    string? lastBody = null;
    using var twoStep = new AccountClient(config, new StubHandler(async request =>
    {
        lastBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
        return request.RequestUri!.PathAndQuery switch
        {
            "/api/accounts/login" => Json(200, $$"""{"requiresTotp":true,"challengeToken":"{{challenge}}","expiresAt":"2030-01-01T00:00:00Z"}"""),
            "/api/accounts/login/totp" => lastBody.Contains("\"code\":\"123456\"") || lastBody.Contains("\"recoveryCode\":\"ABCDE-12345\"")
                ? Json(200, """{"token":"TOKEN2","accountId":"a","expiresAt":"2030-01-01T00:00:00Z"}""") : Json(401, "{}"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }));
    Check(await twoStep.SignInAsync("player", "secret", remember: true) == challenge && !twoStep.SignedIn, "two-step account gets a challenge, not a session");
    try { await twoStep.CompleteTwoStepAsync(challenge, "000000"); Check(false, "wrong code accepted"); } catch (AccountException) { checks++; }
    await twoStep.CompleteTwoStepAsync(challenge, " 123456 ");
    Check(twoStep.SignedIn && twoStep.Token == "TOKEN2" && lastBody!.Contains("\"recoveryCode\":null"), "app code completes the sign-in");
    await twoStep.CompleteTwoStepAsync(challenge, "ABCDE-12345");
    Check(lastBody!.Contains("\"code\":null"), "a longer code is sent as a recovery code");
}

// ---------- Account API: kept session, launcher manifest and self-update ----------
var requests = new List<(string Method, string Path, string Body, string? Bearer)>();
var launcherBytes = "new launcher"u8.ToArray();
var launcherHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(launcherBytes));
static HttpResponseMessage Json(int status, string body) => new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
using (var account = new AccountClient(config, new StubHandler(async request =>
{
    var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
    requests.Add((request.Method.Method, request.RequestUri!.PathAndQuery, body, request.Headers.Authorization?.Parameter));
    return request.RequestUri.PathAndQuery switch
    {
        "/api/accounts/login" => Json(200, """{"token":"TOKEN1","accountId":"a","expiresAt":"2030-01-01T00:00:00Z"}"""),
        "/api/accounts/me" => request.Headers.Authorization?.Parameter == "TOKEN1" ? Json(200, """{"accountId":"a"}""") : Json(401, "{}"),
        "/api/downloads/launcher/manifest" => Json(200, $$"""{"sha256":"{{launcherHash}}","size":{{launcherBytes.Length}},"version":"0.4.0"}"""),
        "/api/downloads/launcher" => Json(200, """{"url":"/download/Launcher.exe?ticket=T"}"""),
        "/download/Launcher.exe?ticket=T" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(launcherBytes) },
        "/api/accounts/logout" => new HttpResponseMessage(HttpStatusCode.NoContent),
        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
    };
})))
{
    await account.SignInAsync("player", "secret", remember: true);
    Check(account.SignedIn && account.Token == "TOKEN1" && account.ExpiresAt == new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero)
        && requests[^1].Body.Contains("\"remember\":true"), "sign-in asks for a kept session");
    await account.SignOutAsync();
    Check(!account.SignedIn && await account.ResumeAsync("TOKEN1") && account.SignedIn && requests[^1].Bearer == "TOKEN1", "a kept session resumes");
    Check(!await account.ResumeAsync("STALE") && !account.SignedIn, "a stale session is refused");
    await account.ResumeAsync("TOKEN1");
    var manifest = await account.LauncherManifestAsync();
    Check(manifest is { Version: "0.4.0" } && manifest.Sha256 == launcherHash && manifest.Size == launcherBytes.Length, "launcher manifest");
    Check(LauncherUpdate.Needed(manifest!, new string('0', 64), launcherBytes.Length) && LauncherUpdate.Needed(manifest!, launcherHash, 1)
        && !LauncherUpdate.Needed(manifest!, launcherHash.ToUpperInvariant(), launcherBytes.Length), "update needed only when the file differs");
    var updateRoot = Directory.CreateTempSubdirectory("aion-launcher-update");
    try
    {
        var exe = Path.Combine(updateRoot.FullName, "Launcher.exe");
        File.WriteAllBytes(exe, "old launcher"u8.ToArray());
        var downloaded = exe + LauncherUpdate.NewSuffix;
        await account.DownloadLauncherAsync(downloaded, null);
        Check(await LauncherUpdate.HashAsync(downloaded) == launcherHash && requests[^1].Path == "/download/Launcher.exe?ticket=T" && requests[^1].Bearer == "TOKEN1",
            "launcher downloaded through a ticket link");
        LauncherUpdate.Swap(exe, downloaded);
        Check(File.ReadAllText(exe) == "new launcher" && File.ReadAllText(exe + LauncherUpdate.OldSuffix) == "old launcher" && !File.Exists(downloaded), "swap keeps the old exe aside");
        File.WriteAllBytes(downloaded, "partial"u8.ToArray());
        await LauncherUpdate.RemoveLeftoversAsync(exe);
        Check(File.Exists(exe) && !File.Exists(exe + LauncherUpdate.OldSuffix) && !File.Exists(downloaded), "leftovers removed");
    }
    finally { updateRoot.Delete(recursive: true); }
}

// ---------- Supported build ----------
var build = SupportedBuild.Embedded;
Check(build.ManifestId == "586322693862358479" && build.Files.Count == build.FileCount && build.FileCount > 1000, "supported build reference");
Check(build.Files.Single(f => f.Path == build.Executable).Sha256 == "7dfbcde8d97f42a7cef9261ad7ceef7f1e1cd773c7b2b4ca3c7cd54005389530", "supported AION2.exe hash");
Check(build.Files.Count(f => f.Quick) > 500 && build.DownloadCommand == "download_depot 3393110 3393111 586322693862358479", "quick set and download command");

// ---------- Text KeyValues ----------
var kv = KeyValuesText.Parse("""
"libraryfolders"
{
	"0" { "path" "C:\\Program Files (x86)\\Steam" "apps" { "1" "2" } }
	// comment
	"1" { "path" "G:\\SteamLibrary" }
}
""");
Check(kv.Object("libraryfolders")!.Object("1")!.String("path") == @"G:\SteamLibrary", "libraryfolders parse with escapes and comments");

// ---------- Settings file ----------
var ini = GamePlatformIni.WithBootstrap("[/Script/Aion2.AionGamePlatformUserSettings]\r\nFunnelLogHistory=1\r\n\r\n[/Script/Aion2.AionGamePlatformSettings]\r\nOld=1\r\n", "http://1.2.3.4:8087/c/AB");
Check(ini.Contains("FunnelLogHistory=1") && !ini.Contains("Old=1") && ini.Contains("ConfigurationServerUrl=\"http://1.2.3.4:8087/c/AB\""), "settings section replaced, others kept");
Check(GamePlatformIni.WithBootstrap(ini, "http://1.2.3.4:8087/c/CD").Split("[/Script/Aion2.AionGamePlatformSettings]").Length == 2, "settings section appears once");
Check(GamePlatformIni.PathFor(@"C:\L", "A2CorePrivate").Replace('\\', '/').EndsWith("AION2/Saved_A2CorePrivate/Config/Windows/GamePlatform.ini"), "private saved folder");

// ---------- Hosts ----------
var server = IPAddress.Parse("203.0.113.5");
var original = "127.0.0.1 localhost\r\n# keep me\r\n";
var withBlock = HostsFile.With(original, server);
Check(HostsFile.HasBlock(withBlock) && withBlock.Contains("203.0.113.5 bladnoch.ncsoft.com") && withBlock.Contains("# keep me"), "hosts block added");
Check(HostsFile.With(withBlock, server).Split("bladnoch").Length == 2, "hosts block replaced, not duplicated");
Check(HostsFile.Without(withBlock) == original && !HostsFile.HasBlock(HostsFile.Without(withBlock)), "hosts block removed exactly");
var legacy = original + "# >>> AION2 private server (added by AionLauncher; removed when the game closes)\r\n203.0.113.5 bladnoch.ncsoft.com\r\n# <<< AION2 private server\r\n";
Check(HostsFile.HasBlock(legacy) && HostsFile.Without(legacy) == original, "hosts block from an older launcher removed");
Check(!withBlock.Contains("AION", StringComparison.OrdinalIgnoreCase), "hosts block does not name the game");

// ---------- Firewall ----------
var ranges = FirewallRules.BlockedRanges(server);
Check(ranges.SequenceEqual(["0.0.0.0-126.255.255.255", "128.0.0.0-203.0.113.4", "203.0.113.6-255.255.255.255", "::2-ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff"]), "firewall leaves loopback and the server open");
Check(FirewallRules.BlockedRanges(IPAddress.Parse("10.0.0.1"))[0] == "0.0.0.0-10.0.0.0" && FirewallRules.BlockedRanges(IPAddress.Parse("10.0.0.1"))[1] == "10.0.0.2-126.255.255.255", "server below loopback");
var commands = FirewallRules.Commands([@"C:\G\AION2.exe"], server).ToList();
Check(commands.Count == 4 && commands[0].Contains("delete rule") && commands[1].Contains($"delete rule name=\"{FirewallRules.LegacyRuleName}\"")
    && commands[2].Contains("dir=out") && commands[3].Contains("dir=in") && commands[2].Contains("program=\"C:\\G\\AION2.exe\""), "firewall commands (legacy rule deleted too)");

// ---------- File checks and placement ----------
var root = Directory.CreateTempSubdirectory("aion-launcher-checks");
try
{
    var source = Path.Combine(root.FullName, "source");
    var files = new Dictionary<string, byte[]> { ["Aion2/Binaries/Win64/AION2.exe"] = "exe"u8.ToArray(), ["Aion2/Content/Paks/a.utoc"] = "toc"u8.ToArray(), ["Aion2/Content/Paks/a.ucas"] = "big data"u8.ToArray() };
    foreach (var (path, bytes) in files) { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(source, path))!); File.WriteAllBytes(Path.Combine(source, path), bytes); }
    var test = build with
    {
        Files = files.Select(f => new BuildFile(f.Key, f.Value.Length, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(f.Value)), !f.Key.EndsWith(".ucas"))).ToList(),
        TotalBytes = files.Values.Sum(v => v.Length), FileCount = files.Count,
    };
    Check((await ClientFiles.CheckAsync(source, test, CheckLevel.Full)).Ok, "full check passes");
    Check(ClientFiles.PresentBytes(source, test) == test.TotalBytes, "download progress: all present");
    File.WriteAllBytes(Path.Combine(source, "Aion2/Content/Paks/a.utoc"), "toc and more"u8.ToArray());
    Check(ClientFiles.PresentBytes(source, test) == test.TotalBytes, "download progress: oversized file counts its expected size");
    File.WriteAllBytes(Path.Combine(source, "Aion2/Content/Paks/a.utoc"), "toc"u8.ToArray());
    File.WriteAllBytes(Path.Combine(source, "Aion2/Content/Paks/a.ucas"), "BIG DATA"u8.ToArray());
    var quick = await ClientFiles.CheckAsync(source, test, CheckLevel.Quick);
    var full = await ClientFiles.CheckAsync(source, test, CheckLevel.Full);
    Check(quick.Ok && !full.Ok && full.Problem!.Contains("a.ucas"), "quick check skips data files; full check finds the change");
    File.WriteAllBytes(Path.Combine(source, "Aion2/Content/Paks/a.ucas"), "big data"u8.ToArray());
    foreach (var mode in OperatingSystem.IsWindows() ? new[] { ClientFiles.Mode.Copy, ClientFiles.Mode.HardLink } : [ClientFiles.Mode.Copy])
    {
        var target = Path.Combine(root.FullName, mode.ToString());
        await ClientFiles.PlaceAsync(source, target, test, mode);
        Check((await ClientFiles.CheckAsync(target, test, CheckLevel.Full)).Ok, $"placed by {mode}");
    }
    var moved = Path.Combine(root.FullName, "moved");
    await ClientFiles.PlaceAsync(source, moved, test, ClientFiles.Mode.Move);
    Check((await ClientFiles.CheckAsync(moved, test, CheckLevel.Full)).Ok && !File.Exists(Path.Combine(source, "Aion2/Content/Paks/a.ucas")), "placed by move");
    Check(!(await ClientFiles.CheckAsync(source, test, CheckLevel.Sizes)).Ok, "missing files fail the size check");
}
finally { root.Delete(recursive: true); }

// ---------- Update by pieces (ClientPatch): only what the folder does not hold is fetched ----------
{
    var patchRoot = Directory.CreateTempSubdirectory("aion-patch-");
    try
    {
        const int piece = 4096;
        var random = new Random(3);
        byte[] Block() { var b = new byte[piece]; random.NextBytes(b); return b; }
        static string Sha(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(data));
        static ManifestFile Describe(byte[] content, int size)
        {
            var chunks = new List<ChunkRef>();
            for (var at = 0; at < content.Length; at += size)
            {
                var length = Math.Min(size, content.Length - at);
                chunks.Add(new(at, length, Sha(content.AsSpan(at, length))));
            }
            return new(content.Length, chunks);
        }
        byte[] a = Block(), b = Block(), c = Block(), d = Block(), e = Block(), f = Block();
        // Old build: pak = a b c, other = d e, gone = f. New build: pak = b X c a (pieces moved, one new, longer),
        // other = d e (unchanged), moved = e d (pieces from another file), fresh = new file.
        var x = Block();
        var oldFiles = new Dictionary<string, byte[]> { ["Aion2/pak.pak"] = [.. a, .. b, .. c], ["Aion2/other.bin"] = [.. d, .. e], ["Aion2/gone.bin"] = f };
        var newFiles = new Dictionary<string, byte[]> { ["Aion2/pak.pak"] = [.. b, .. x, .. c, .. a], ["Aion2/other.bin"] = [.. d, .. e], ["Aion2/moved.bin"] = [.. e, .. d], ["Aion2/fresh.bin"] = [.. x, .. x[..100]] };
        InstalledManifest Manifest(string id, Dictionary<string, byte[]> files) =>
            new(id, files.ToDictionary(kv => kv.Key, kv => Describe(kv.Value, piece), StringComparer.OrdinalIgnoreCase));
        var oldManifest = Manifest("1", oldFiles);
        var newManifest = Manifest("2", newFiles);
        var folder = Path.Combine(patchRoot.FullName, "game");
        foreach (var (path, content) in oldFiles)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(folder, path))!);
            File.WriteAllBytes(Path.Combine(folder, path), content);
        }
        // The player's own Steam copy hard-linked into the folder: patching must never write into it.
        var steamFolder = Path.Combine(patchRoot.FullName, "steam");
        var steamCopy = Path.Combine(steamFolder, "Aion2/pak.pak");
        var linked = OperatingSystem.IsWindows();
        if (linked)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(steamCopy)!);
            File.WriteAllBytes(steamCopy, oldFiles["Aion2/pak.pak"]);
            File.Delete(Path.Combine(folder, "Aion2/pak.pak"));
            var steamBuild = new SupportedBuild(0, 0, "0", "Aion2/pak.pak", "", 1, oldFiles["Aion2/pak.pak"].Length,
                [new BuildFile("Aion2/pak.pak", oldFiles["Aion2/pak.pak"].Length, "", false)]);
            await ClientFiles.PlaceAsync(steamFolder, folder, steamBuild, ClientFiles.Mode.HardLink);
        }

        oldManifest.Save(folder);
        var loaded = InstalledManifest.Load(folder)!;
        Check(loaded.ManifestId == "1" && loaded.Files["aion2/PAK.pak"].Chunks.Count == 3 && loaded.Matches(folder),
            "installed-build record round-trips (paths case-insensitive)");
        Check(ClientPatch.Unchanged(oldManifest.Files["Aion2/other.bin"], newManifest.Files["Aion2/other.bin"])
              && !ClientPatch.Unchanged(oldManifest.Files["Aion2/pak.pak"], newManifest.Files["Aion2/pak.pak"])
              && !ClientPatch.Unchanged(null, newManifest.Files["Aion2/fresh.bin"]), "unchanged = same size and same pieces at the same places");
        Check(ClientPatch.BytesToFetch(oldManifest, newManifest) == piece + piece + 100 && ClientPatch.BytesToFetch(null, newManifest) == newFiles.Values.Sum(v => (long)v.Length),
            "to fetch: only pieces the old build lacks (x twice and its tail), everything without a record");
        Check(ClientPatch.Removed(oldManifest, newManifest).SequenceEqual(["Aion2/gone.bin"]) && ClientPatch.Removed(null, newManifest).Count == 0,
            "only files of the old record that the new build dropped are removed");

        var fetched = 0L;
        var bySha = newFiles.Values.SelectMany(v => Describe(v, piece).Chunks.Select(ch => (ch.Sha1, Data: v.AsSpan((int)ch.Offset, ch.Length).ToArray())))
            .GroupBy(p => p.Sha1).ToDictionary(g => g.Key, g => g.First().Data);
        Task<int> Fetch(ChunkRef chunk, byte[] buffer, CancellationToken _)
        {
            Interlocked.Add(ref fetched, chunk.Length);
            bySha[chunk.Sha1].CopyTo(buffer, 0);
            return Task.FromResult(chunk.Length);
        }
        var sources = ClientPatch.Index(oldManifest);
        long reported = 0;
        foreach (var (path, file) in newManifest.Files)
        {
            if (ClientPatch.Unchanged(oldManifest.Files.GetValueOrDefault(path), file)) continue;
            await ClientPatch.PatchFileAsync(folder, path, file, sources, Fetch, n => Interlocked.Add(ref reported, n), 4, CancellationToken.None);
        }
        Check(newFiles.All(kv => File.ReadAllBytes(Path.Combine(folder, kv.Key)).SequenceEqual(kv.Value)), "every changed file is the new build's afterwards");
        Check(fetched == piece * 2 + 100, $"only the missing pieces were fetched ({fetched} bytes, not {newFiles.Values.Sum(v => v.Length)})");
        Check(!Directory.EnumerateFiles(folder, "*" + ClientPatch.PartSuffix, SearchOption.AllDirectories).Any(), "no .patch files are left behind");
        if (linked) Check(File.ReadAllBytes(steamCopy).SequenceEqual(oldFiles["Aion2/pak.pak"]), "the player's own copy of a file is never written into");

        // A folder without a record: pieces at the same place are kept, the rest fetched; an interrupted .patch is resumed.
        var bare = Path.Combine(patchRoot.FullName, "bare");
        Directory.CreateDirectory(Path.Combine(bare, "Aion2"));
        File.WriteAllBytes(Path.Combine(bare, "Aion2/pak.pak"), [.. b, .. a, .. c, .. a]);   // pieces 0, 2 and 3 already right
        fetched = 0;
        var pak = newManifest.Files["Aion2/pak.pak"];
        Check(!await ClientPatch.AlreadyMatchesAsync(bare, "Aion2/pak.pak", pak, CancellationToken.None), "a differing file is not taken as done");
        File.WriteAllBytes(Path.Combine(bare, "Aion2/pak.pak" + ClientPatch.PartSuffix), [.. new byte[piece], .. x, .. new byte[piece * 2]]);   // x already fetched before
        await ClientPatch.PatchFileAsync(bare, "Aion2/pak.pak", pak, new Dictionary<string, ChunkSource>(), Fetch, _ => { }, 4, CancellationToken.None);
        Check(File.ReadAllBytes(Path.Combine(bare, "Aion2/pak.pak")).SequenceEqual(newFiles["Aion2/pak.pak"]) && fetched == 0,
            "without a record: same-place pieces and the interrupted .patch's piece are reused, nothing fetched");
        Check(await ClientPatch.AlreadyMatchesAsync(bare, "Aion2/pak.pak", pak, CancellationToken.None), "a file holding every piece is taken as done");

        // A bad piece from the network is refused, and the old file stays as it was.
        var broken = Path.Combine(patchRoot.FullName, "broken");
        Directory.CreateDirectory(Path.Combine(broken, "Aion2"));
        File.WriteAllBytes(Path.Combine(broken, "Aion2/fresh.bin"), a);
        try
        {
            await ClientPatch.PatchFileAsync(broken, "Aion2/fresh.bin", newManifest.Files["Aion2/fresh.bin"], new Dictionary<string, ChunkSource>(),
                (chunk, buffer, _) => { Array.Clear(buffer); return Task.FromResult(chunk.Length); }, _ => { }, 2, CancellationToken.None);
            Check(false, "a piece that does not match its id was accepted");
        }
        catch (InvalidDataException) { checks++; }
        Check(File.ReadAllBytes(Path.Combine(broken, "Aion2/fresh.bin")).SequenceEqual(a), "a failed patch leaves the old file untouched");
        Check(SupportedBuild.Embedded.PreviousManifests is ["1699273097574069541"], "the supported build names the earlier build to match old folders against");
    }
    finally { patchRoot.Delete(recursive: true); }
}

// ---------- Real Steam cache (this PC, read-only) ----------
var steamRoot = Environment.GetEnvironmentVariable("AION_STEAM_ROOT") ?? @"C:\Program Files (x86)\Steam";
var steam = new SteamInstall(steamRoot);
if (File.Exists(steam.AppInfoPath))
{
    var app = AppInfo.Read(File.ReadAllBytes(steam.AppInfoPath), SteamInstall.AppId);
    var (executable, arguments) = AppInfo.LaunchArguments(app, "A2CorePrivate");
    Check(executable.Equals("AION2/Binaries/Win64/AION2.exe", StringComparison.OrdinalIgnoreCase), "real appinfo: launch executable");
    Check(arguments.Contains("-saveddirsuffix=A2CorePrivate") && arguments.Any(a => a.StartsWith("-lp:")) && arguments.Contains("-steam"), "real appinfo: launch arguments with our suffix");
    Check(steam.Libraries().Count >= 1 && steam.FindApp() is { } found && Directory.Exists(found.Folder), "real Steam: libraries and AION2 install");
    Console.WriteLine("Real Steam cache parsed.");
}

Console.WriteLine($"{checks} launcher checks passed.");

/// <summary>An account server that answers from a function (no network).</summary>
sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request);
}
