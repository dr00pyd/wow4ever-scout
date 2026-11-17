using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForeverScout.Core;

namespace ForeverScout.Tests;

/// <summary>
/// 1.0.19: funktionsflaggor (signerat fält "off"), meddelanden till en spelare, daglig bildlista, städning av skickade
/// bilder, inställningar som tål en trasig fil och okända fält, addonbyte som rullas tillbaka, User-Agent med variant,
/// felrapportlänken, symlänkstexten, LaunchAgent vid App Translocation och återförsök mot spelkatalogen.
/// </summary>
public sealed class Release119Tests : IDisposable
{
    private readonly TestSigner _signer = new();
    private readonly DirectoryInfo _tmp = TestEnv.Temp("fs-119-");

    public void Dispose()
    {
        _signer.Dispose();
        SafeFs.SetGameDir(null);
        SafeFs.ShotCleanAllowed = false;
        SafeFs.UseAppDataDir(TestEnv.AppData);
        try { _tmp.Delete(true); } catch (IOException) { }
    }

    private JsonObject SignAll(JsonObject n)
    {
        n.Remove("signatureAll");
        n["signed"] = string.Join(",", n.Select(p => p.Key).Append("signed").Where(k => !k.StartsWith("signature", StringComparison.Ordinal) && k != "blockedAddons")
            .Distinct().OrderBy(k => k, StringComparer.Ordinal));
        using var doc = JsonDocument.Parse(n.ToJsonString());
        n["signatureAll"] = _signer.Sign(Encoding.UTF8.GetBytes(ReleaseVerifier.Canonical(doc.RootElement, n["signed"]!.GetValue<string>().Split(','))));
        return n;
    }

    // ------------------------------------------------------------------ funktionsflaggor

    [Fact]
    public void OffIsReadOnlyWhenSignedAndOnlyKnownNames()
    {
        var n = _signer.Release("2026-11-18T10:00:00.000Z");
        n["off"] = "shotlist,kaffe,shotclean";
        var signed = ReleaseVerifier.Verify(SignAll(n).ToJsonString(), _signer.Pem);
        Assert.Equal(["shotlist", "shotclean"], signed.Off);

        // Utan signatureAll är fältet osignerat – och då finns det inte för klienten.
        var unsigned = _signer.Release("2026-11-18T10:00:00.000Z");
        unsigned["off"] = "shotlist";
        Assert.Empty(ReleaseVerifier.Verify(unsigned.ToJsonString(), _signer.Pem).Off);

        // Signerat, men utanför listan "signed" (någon lade till det efteråt): finns inte heller.
        var added = SignAll(_signer.Release("2026-11-18T10:00:00.000Z"));
        added["off"] = "shots";
        Assert.Empty(ReleaseVerifier.Verify(added.ToJsonString(), _signer.Pem).Off);
    }

    [Fact]
    public void AFeatureTurnedOffStopsItWithoutChangingTheSetting()
    {
        SafeFs.UseAppDataDir(_tmp.FullName);
        var s = Settings.Load();
        s.UploadHotfix = s.UploadCreatureCache = s.UploadShots = s.ShareAuctionPrices = true;
        Assert.True(s.HotfixOn && s.CreatureCacheOn && s.ShotsOn && s.AuctionsOn);

        var n = _signer.Release("2026-11-18T10:00:00.000Z");
        n["off"] = "hotfix,auctions";
        Assert.True(Features.Apply(s, ReleaseVerifier.Verify(SignAll(n).ToJsonString(), _signer.Pem)));
        Assert.False(s.HotfixOn);
        Assert.False(s.AuctionsOn);
        Assert.True(s.ShotsOn && s.CreatureCacheOn);
        Assert.True(s.UploadHotfix && s.ShareAuctionPrices); // inställningen står kvar

        // Nästa versionsinfo utan fältet: allt på igen. Samma lista två gånger = ingen ändring.
        var on = SignAll(_signer.Release("2026-11-18T11:00:00.000Z"));
        Assert.True(Features.Apply(s, ReleaseVerifier.Verify(on.ToJsonString(), _signer.Pem)));
        Assert.True(s.HotfixOn && s.AuctionsOn);
        Assert.False(Features.Apply(s, ReleaseVerifier.Verify(on.ToJsonString(), _signer.Pem)));
    }

    // ------------------------------------------------------------------ meddelanden

    private static MeResult Me(string json) => JsonSerializer.Deserialize<MeResult>(json)!;

    [Fact]
    public void APlayerMessageIsShownOncePerId()
    {
        SafeFs.UseAppDataDir(_tmp.FullName);
        var s = Settings.Load();
        Assert.Null(PlayerMessages.Take(s, Me("""{"name":"x"}""")));
        Assert.Equal("Starta om spelet efter uppdateringen", PlayerMessages.Take(s, Me("""{"message":{"id":7,"text":"Starta om spelet\nefter uppdateringen"}}""")));
        Assert.Null(PlayerMessages.Take(s, Me("""{"message":{"id":7,"text":"Starta om spelet"}}""")));
        Assert.Null(PlayerMessages.Take(s, Me("""{"message":{"id":6,"text":"äldre"}}""")));
        Assert.Equal("nytt", PlayerMessages.Take(s, Me("""{"message":{"id":8,"text":"  nytt  "}}""")));
        // Konstiga svar visas aldrig och gör inte svaret oläsbart.
        foreach (var bad in new[] { """{"message":"text"}""", """{"message":{"id":"9","text":"x"}}""", """{"message":{"id":9,"text":"   "}}""", """{"message":{"id":-1,"text":"x"}}""", """{"message":{"id":9,"text":5}}""" })
            Assert.Null(PlayerMessages.Take(s, Me(bad)));
        Assert.Equal(300, PlayerMessages.Take(s, Me("{\"message\":{\"id\":10,\"text\":\"" + new string('a', 400) + "\"}}"))!.Length);
    }

    // ------------------------------------------------------------------ bildlistan

    [Fact]
    public void ShotListJsonIsValidatedStrictly()
    {
        var d = ShotListFile.FromJson("""{"built":"2026-11-18","n":3,"ids":[300,12,12,7]}""");
        Assert.NotNull(d);
        Assert.Equal("2026-11-18", d!.Built);
        Assert.Equal([7L, 12L, 300L], d.Ids);
        foreach (var bad in new[] { "", "[]", "{}", """{"built":"2026-11-18"}""", """{"built":"2026-13-40","ids":[]}""", """{"built":"igår","ids":[1]}""",
                     """{"built":"2026-11-18","ids":[1,"2"]}""", """{"built":"2026-11-18","ids":[0]}""", """{"built":"2026-11-18","ids":[1.5]}""",
                     """{"built":"2026-11-18","ids":[100000000]}""", """{"built":"2026-11-18","ids":"1,2"}""" })
            Assert.Null(ShotListFile.FromJson(bad));
    }

    [Fact]
    public void ShotListIsRenderedFromNumbersOnlyAndReadBackExactly()
    {
        var ids = Enumerable.Range(1, 45).Select(i => (long)i * 1000).ToList();
        var text = ShotListFile.Render(new ShotListFile.Data("2026-11-18", ids));
        Assert.StartsWith("-- ForeverScout: daily picture list", text);
        Assert.Contains("ForeverScoutShotList = {\n  built = \"2026-11-18\",\n  n = 45,\n  ids = [[\n", text);
        Assert.All(text.Split('\n').Where(l => !l.StartsWith("--")), l => Assert.Matches(@"^[A-Za-z ={}\[\],""0-9\-]*$", l));
        var back = ShotListFile.ParseOwn(text);
        Assert.Equal("2026-11-18", back!.Built);
        Assert.Equal(ids, back.Ids);
        Assert.Equal("2026-11-18", ShotListFile.BuiltOf(text));
        // Tom lista: samma format som build-addon-shotlist.mjs (ids = "").
        Assert.Empty(ShotListFile.ParseOwn(ShotListFile.Render(new ShotListFile.Data("2026-11-18", [])))!.Ids);
        // Den medskickade listan (annan första rad) och en ändrad fil godtas inte som klientens.
        var bundled = File.ReadAllText(Path.Combine(AddonDir(), "ShotList.lua"));
        Assert.Null(ShotListFile.ParseOwn(bundled));
        Assert.NotNull(ShotListFile.BuiltOf(bundled));
        Assert.Null(ShotListFile.ParseOwn(text.Replace("n = 45", "n = 45, x = os.exit()")));
        Assert.Null(ShotListFile.ParseOwn(text.Replace("1000,", "1000,print(1),")));
    }

    /// <summary>Addonens källmapp i repot (client/addon/ForeverScout).</summary>
    private static string AddonDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "client", "addon", "ForeverScout"))) d = d.Parent;
        return Path.Combine(d!.FullName, "client", "addon", "ForeverScout");
    }

    [Fact]
    public void ShotListIsDueOnlyWhenWantedAndOld()
    {
        SafeFs.UseAppDataDir(_tmp.FullName);
        var s = Settings.Load();
        var now = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(ShotListFile.Due(s, addonInstalled: true, now));
        Assert.False(ShotListFile.Due(s, addonInstalled: false, now));
        s.ShotListAtUtc = now.AddHours(-19);
        Assert.False(ShotListFile.Due(s, true, now));
        s.ShotListAtUtc = now.AddHours(-20);
        Assert.True(ShotListFile.Due(s, true, now));
        s.ShotListAtUtc = now.AddDays(3); // klockan har gått bakåt: vänta inte i dagar
        Assert.True(ShotListFile.Due(s, true, now));
        s.ShotListAtUtc = null;
        s.ShotListWanted = false; // /scout tips off
        Assert.False(ShotListFile.Due(s, true, now));
        s.ShotListWanted = true;
        s.FeaturesOff = ["shotlist"];
        Assert.False(ShotListFile.Due(s, true, now));
    }

    [Fact]
    public void TipsOffInTheAddonFileIsSeenByTheClient()
    {
        Assert.True(Uploader.Peek(Encoding.UTF8.GetBytes("ForeverScoutDB = { v = 1, addon = \"1.0.30\", tipsOff = true }")).TipsOff);
        Assert.False(Uploader.Peek(Encoding.UTF8.GetBytes("ForeverScoutDB = { v = 1, addon = \"1.0.30\" }")).TipsOff);
        Assert.False(Uploader.Peek(Encoding.UTF8.GetBytes("ForeverScoutDB = { v = 1, tipsOff = \"true\" }")).TipsOff);
    }

    /// <summary>En spelkatalog med addonen installerad (version <paramref name="addon"/>, medskickad lista daterad <paramref name="listBuilt"/>).</summary>
    private GameDir Game(string addon = "1.0.30", string listBuilt = "2026-11-17")
    {
        var game = Path.Combine(_tmp.FullName, "World of Warcraft", "_forever_");
        var target = Path.Combine(game, "Interface", "AddOns", "ForeverScout");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "ForeverScout.toc"), "## Version: " + addon + "\n");
        File.WriteAllText(Path.Combine(target, "ShotList.lua"), "-- medskickad\nForeverScoutShotList = {\n  built = \"" + listBuilt + "\",\n  n = 1,\n  ids = [[\n5\n]],\n}\n");
        Directory.CreateDirectory(Path.Combine(game, "WTF", "Account"));
        var real = SafeFs.SetGameDir(game)!;
        return new GameDir(real, "_forever_");
    }

    private static List<AddonInstaller.AddonFile> Bundle(string version, string listBuilt) =>
    [
        new("ForeverScout.toc", Encoding.UTF8.GetBytes("## Version: " + version + "\n")),
        new("ForeverScout.lua", Encoding.UTF8.GetBytes("local VERSION = \"" + version + "\"\n")),
        new("ShotList.lua", Encoding.UTF8.GetBytes("-- medskickad\nForeverScoutShotList = {\n  built = \"" + listBuilt + "\",\n  n = 1,\n  ids = [[\n9\n]],\n}\n")),
    ];

    [Fact]
    public void ANewerDailyListSurvivesAnAddonUpdateButAnOlderOneDoesNot()
    {
        SafeFs.UseAppDataDir(_tmp.FullName);
        var dir = Game();
        Assert.True(ShotListFile.Write(dir, new ShotListFile.Data("2026-11-20", [1, 2, 3])));
        AddonInstaller.Install(dir, Bundle("1.0.31", "2026-11-18"));
        Assert.Equal("1.0.31", AddonInstaller.InstalledVersion(dir));
        Assert.Equal([1L, 2L, 3L], ShotListFile.ReadInstalledOwn(dir)!.Ids); // nyare än den medskickade: behålls

        AddonInstaller.Install(dir, Bundle("1.0.32", "2026-11-25"));
        Assert.Null(ShotListFile.ReadInstalledOwn(dir));                    // den medskickade är nyare: den gäller
        Assert.Contains("built = \"2026-11-25\"", File.ReadAllText(ShotListFile.PathFor(dir)));
    }

    [Fact]
    public async Task TheDailyListIsFetchedWrittenAndNeverReplacedByAnOlderOne()
    {
        SafeFs.UseAppDataDir(_tmp.FullName);
        var dir = Game();
        var srv = new ListServer { Body = """{"built":"2026-11-18","n":2,"ids":[42,7]}""" };
        using var api = new ApiClient("https://guide.wow4ever.se", srv);
        var s = Settings.Load();
        s.ShotListAtUtc = null; s.ShotListWanted = null; s.FeaturesOff = [];
        var now = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(2, await ShotListFile.RefreshAsync(api, dir, s, now));
        Assert.Equal("/api/scout/bildlista", Assert.Single(srv.Paths));
        Assert.Null(srv.Auth);                                              // ingen nyckel till en offentlig lista
        Assert.Equal([7L, 42L], ShotListFile.ReadInstalledOwn(dir)!.Ids);
        Assert.Null(await ShotListFile.RefreshAsync(api, dir, s, now.AddHours(1))); // inte dags än
        Assert.Single(srv.Paths);

        srv.Body = """{"built":"2026-11-10","n":1,"ids":[1]}""";            // äldre än den som ligger där
        Assert.Null(await ShotListFile.RefreshAsync(api, dir, s, now.AddDays(1)));
        Assert.Equal([7L, 42L], ShotListFile.ReadInstalledOwn(dir)!.Ids);

        srv.Body = "<html>";                                                 // trasigt svar: inget skrivs
        Assert.Null(await ShotListFile.RefreshAsync(api, dir, s, now.AddDays(2)));
        Assert.Equal([7L, 42L], ShotListFile.ReadInstalledOwn(dir)!.Ids);
    }

    private sealed class ListServer : HttpMessageHandler
    {
        public string Body { get; set; } = "{}";
        public List<string> Paths { get; } = new();
        public string? Auth { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Paths.Add(req.RequestUri!.AbsolutePath);
            Auth = req.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(Body, Encoding.UTF8, "application/json") });
        }
    }

    // ------------------------------------------------------------------ addonbytet rullas tillbaka

    [Fact]
    public void AFailedAddonSwapPutsTheOldAddonBack()
    {
        SafeFs.UseAppDataDir(_tmp.FullName);
        var dir = Game("1.0.30");
        // Ett paket vars .toc-filer säger olika versioner: det som går att läsa efter bytet är inte det som skulle
        // installeras – kontrollen efter bytet slår larm och den gamla addonen läggs tillbaka.
        var broken = Bundle("1.0.31", "2026-11-18");
        broken.Insert(0, new("ForeverScout_Forever.toc", Encoding.UTF8.GetBytes("## Version: 0.0.1" + "\n")));
        var ex = Assert.IsAssignableFrom<ScoutException>(Record.Exception(() => AddonInstaller.Install(dir, broken)));
        Assert.Equal(ErrorCodes.AddonInvalid, ex.Code);
        Assert.Equal("1.0.30", AddonInstaller.InstalledVersion(dir));
        Assert.False(Directory.Exists(AddonInstaller.TargetDir(dir) + ".old"));
        // Nästa installation (ett riktigt paket) fungerar och städar.
        AddonInstaller.Install(dir, Bundle("1.0.31", "2026-11-18"));
        Assert.Equal("1.0.31", AddonInstaller.InstalledVersion(dir));
        Assert.False(Directory.Exists(AddonInstaller.TargetDir(dir) + ".new"));
    }

    // ------------------------------------------------------------------ inställningarna

    [Fact]
    public void SettingsKeepUnknownFieldsAndFallBackToTheBackup()
    {
        var appdata = Path.Combine(_tmp.FullName, "appdata");
        Directory.CreateDirectory(appdata);
        var path = Path.Combine(appdata, "settings.json");
        File.WriteAllText(path, """{ "GameDir": "C:\\wow", "FromTheFuture": { "a": [1, 2] }, "Beta": true }""");
        var s = Settings.Load(appdata);
        Assert.True(s.Beta);
        s.Points = 5;
        s.Save();
        var saved = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal(2, saved["FromTheFuture"]!["a"]![1]!.GetValue<int>()); // en nyare versions fält finns kvar
        Assert.True(File.Exists(path + ".bak"));                             // förra filen sparad som kopia

        // Halvskriven fil: den senaste goda kopian läses i stället för att allt glöms.
        s.Points = 6;
        s.Save();
        File.WriteAllText(path, "{ \"GameDir\": \"C:\\\\w");
        var back = Settings.Load(appdata);
        Assert.Equal(@"C:\wow", back.GameDir);
        Assert.Equal(5, back.Points);
        Assert.True(back.Beta);
        // En trasig fil skrivs aldrig över kopian.
        back.Save();
        Assert.Equal(5, Settings.Load(appdata).Points);
    }

    [Fact]
    public void NewSettingsHaveSafeDefaults()
    {
        var appdata = Path.Combine(_tmp.FullName, "fresh");
        Directory.CreateDirectory(appdata);
        var s = Settings.Load(appdata);
        Assert.False(s.CleanUploadedShots);  // städningen är av tills spelaren slår på den
        Assert.True(s.ShowInDock);           // Mac: ikonen i Dock är på
        Assert.Null(s.ShotListWanted);
        Assert.Empty(s.FeaturesOff);
        Assert.Null(s.MessageSeen);
    }

    // ------------------------------------------------------------------ städningen i SafeFs

    [Fact]
    public void OnlyTheGamesOwnScreenshotsCanBeDeletedAndOnlyWhenTurnedOn()
    {
        var dir = Game();
        var shots = Path.Combine(dir.Path, "Screenshots");
        Directory.CreateDirectory(shots);
        var shot = Path.Combine(shots, "WoWScrnShot_111826_101010.jpg");
        var own = Path.Combine(shots, "semester.jpg");
        File.WriteAllBytes(shot, [1]);
        File.WriteAllBytes(own, [1]);

        SafeFs.ShotCleanAllowed = false;
        Assert.Throws<SafeFsException>(() => SafeFs.DeleteOwnScreenshot(shot));
        Assert.True(File.Exists(shot));

        SafeFs.ShotCleanAllowed = true;
        Assert.Throws<SafeFsException>(() => SafeFs.DeleteOwnScreenshot(own));
        Assert.Throws<SafeFsException>(() => SafeFs.DeleteOwnScreenshot(Path.Combine(dir.Path, "WTF", "WoWScrnShot_111826_101010.jpg")));
        // Även med städningen på får inget annat skriva i Screenshots.
        Assert.Throws<SafeFsException>(() => SafeFs.WriteAllBytes(shot, [0]));
        Assert.Throws<SafeFsException>(() => SafeFs.DeleteFile(shot));
        SafeFs.DeleteOwnScreenshot(shot);
        Assert.False(File.Exists(shot));
        Assert.True(File.Exists(own));
    }

    [Fact]
    public void ACleanedShotIsOnlyOneTheServerReceived()
    {
        var appdata = Path.Combine(_tmp.FullName, "clean");
        Directory.CreateDirectory(appdata);
        var s = Settings.Load(appdata);
        Assert.False(ShotCleaner.ShouldClean(s, "published"));
        s.CleanUploadedShots = true;
        foreach (var ok in new[] { "published", "queued", "duplicate", "ok" }) Assert.True(ShotCleaner.ShouldClean(s, ok), ok);
        foreach (var no in new[] { "rejected:too-large", "expired", "pending", "" }) Assert.False(ShotCleaner.ShouldClean(s, no), no);
        s.FeaturesOff = ["shotclean"];
        Assert.False(ShotCleaner.ShouldClean(s, "published"));
    }

    // ------------------------------------------------------------------ övrigt

    [Fact]
    public void UserAgentNamesPlatformAndVariantAndNothingElse()
    {
        Assert.Matches(@"^ForeverScout/\d+\.\d+\.\d+ \((windows|mac|linux); (full|lite|store)\)$", ApiClient.UserAgent);
        Assert.EndsWith("(" + ApiClient.Platform + "; " + BuildInfo.Variant + ")", ApiClient.UserAgent);
    }

    [Fact]
    public void BugLinkCarriesPlatformAndVersion()
    {
        Assert.Matches(@"^\?plattform=(store|lite|full|mac|linux)&version=\d+\.\d+\.\d+$", BugLink.Query);
    }

    [Fact]
    public void ASymlinkGetsItsOwnExplanation()
    {
        Strings.Apply("sv");
        Assert.Contains("symbolisk länk", Strings.Error(new SafeFsException("reparse-point", @"C:\x")));
        Assert.Equal(Strings.S("err.access-denied"), Strings.Error(new SafeFsException("outside", @"C:\x")));
        Strings.Apply("en");
        Assert.Contains("symbolic link", Strings.Error(new SafeFsException("reparse-point", @"C:\x")));
        Strings.Apply("sv");
    }

    [Fact]
    public void LaunchAgentUsesTheBundleIdWhenTheAppIsTranslocated()
    {
        Assert.Equal(["/usr/bin/open", "-g", "-a", "/Applications/Forever Scout.app", "--args", "--tray"],
            SafeFs.LaunchAgentArgs("/Applications/Forever Scout.app", null));
        Assert.Equal(["/usr/bin/open", "-g", "-b", SafeFs.MacBundleId, "--args", "--tray"],
            SafeFs.LaunchAgentArgs("/private/var/folders/xy/T/AppTranslocation/ABC/d/Forever Scout.app", null));
    }

    [Fact]
    public void TheChosenGameFolderIsRetriedOnceAMinute()
    {
        var appdata = Path.Combine(_tmp.FullName, "retry");
        Directory.CreateDirectory(appdata);
        var s = Settings.Load(appdata);
        var now = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(GameDirRetry.Due(s, haveGameDir: false, DateTime.MinValue, now)); // ingen vald katalog
        s.GameDir = @"E:\Spel\World of Warcraft\_forever_";
        s.ConfirmGameDir();
        Assert.True(GameDirRetry.Due(s, false, now.AddMinutes(-1), now));
        Assert.False(GameDirRetry.Due(s, false, now.AddSeconds(-30), now));
        Assert.False(GameDirRetry.Due(s, true, DateTime.MinValue, now));                // katalogen finns redan
    }
}
