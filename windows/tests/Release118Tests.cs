using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForeverScout.Core;

namespace ForeverScout.Tests;

/// <summary>
/// 1.0.18 – allt nytt ligger i ForeverScout.Core, så samma tester körs för Windows (client/windows/tests) och för
/// macOS/Linux (client/mac/tests länkar in filen): bildraden och poängen som väntar (Overview), påminnelsen om
/// /reload (ReloadReminder), kvittot i spelet (AddonReceipt), varningen när spelet sparar utan att addonen gör det
/// (SilentSession), Linux-appens byte av AppImage-filen (AppImageUpdater) och beta-kanalen för Mac och Linux.
/// Allt körs mot en låtsasserver och en låtsad WoW-katalog i en temporär mapp – inget riktigt spel, ingen riktig server.
/// </summary>
public sealed class Release118Tests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 16, 20, 0, 0, DateTimeKind.Utc);
    private const string DefaultReceipt = "-- ForeverScout: empty default receipt\n";

    private readonly DirectoryInfo _tmp = TestEnv.Temp("fs-118-");
    private readonly GameDir _dir;
    private readonly string _svDir;
    private readonly string _sv;
    private readonly Settings _set;

    public Release118Tests()
    {
        Strings.Apply("sv");
        var game = Path.Combine(_tmp.FullName, "World of Warcraft", "_forever_");
        Directory.CreateDirectory(Path.Combine(game, "Interface", "AddOns"));
        var sv = Path.Combine(game, "WTF", "Account", "AAA", "SavedVariables");
        Directory.CreateDirectory(sv);
        File.WriteAllText(Path.Combine(sv, "ForeverScout.lua"), LuaParserTests.Sample);
        var real = SafeFs.SetGameDir(game)!;
        _dir = new GameDir(real, "_forever_");
        _svDir = Path.Combine(real, "WTF", "Account", "AAA", "SavedVariables");
        _sv = Path.Combine(_svDir, "ForeverScout.lua");
        _set = Fresh(Settings.Load());
    }

    public void Dispose()
    {
        SafeFs.OwnAppImageOverride = null;
        SafeFs.SetGameDir(null);
        Strings.Apply("sv");
        try { Fresh(_set).Save(); } catch (IOException) { }
        try { _tmp.Delete(true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Inställningarna delas av alla tester (en appdatakatalog) – nollställ det som testerna här rör.</summary>
    private static Settings Fresh(Settings s)
    {
        s.UploadHotfix = false; s.UploadCreatureCache = false; s.UploadShots = false; s.ShareAuctionPrices = false;
        s.UploadedSha.Clear(); s.UploadedSeq.Clear(); s.BlockedAddons.Clear(); s.Shots.Clear();
        s.RetryAttempts = 0; s.NextRetryUtc = null;
        s.PendingPoints = s.PendingFinds = s.MediaPublished = s.MediaPending = s.MediaRejected = null;
        s.AddonReloadVersion = null; s.AddonReloadSinceUtc = null;
        s.SilentSinceUtc = null; s.SilentDir = null; s.SilentWarnedUtc = null; s.SilentSaveUtc = null;
        return s;
    }

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    /// <summary>En minimal addon: .toc med versionen, huvudfilen och (från 1.0.26) den tomma standardfilen Receipt.lua.</summary>
    private static List<AddonInstaller.AddonFile> Addon(string version, bool withReceipt = true)
    {
        var list = new List<AddonInstaller.AddonFile>
        {
            new("ForeverScout.toc", Utf8($"## Interface: 16001\n## Version: {version}\n\n" + (withReceipt ? "Receipt.lua\n" : "") + "ForeverScout.lua\n")),
            new("ForeverScout.lua", Utf8("-- addon " + version)),
        };
        if (withReceipt) list.Add(new("Receipt.lua", Utf8(DefaultReceipt)));
        return list;
    }

    private string ReceiptPath => Path.Combine(_dir.AddOnsDir, "ForeverScout", "Receipt.lua");

    /// <summary>Låtsasserver: svarar med JSON per sökväg och antecknar vilka adresser som anropades.</summary>
    private sealed class Server : HttpMessageHandler
    {
        public readonly List<string> Calls = [];
        public string Me = """{"name":"Scout","points":120,"rank":4,"counts":{"npcs":10}}""";
        public string Upload = """{"accepted":{"npcs":3,"quests":2,"loot":0},"newConfirmed":[],"points":120,"rank":4,"lastSeq":417}""";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            lock (Calls) Calls.Add(req.RequestUri!.PathAndQuery);
            var path = req.RequestUri.AbsolutePath;
            if (path.EndsWith("/version")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var body = path.EndsWith("/jag") ? Me : Upload;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static (ApiClient Api, Server Srv) NewApi()
    {
        var srv = new Server();
        return (new ApiClient("https://guide.wow4ever.se", srv) { Key = "k" }, srv);
    }

    // ================================================================== 1. bilder och poäng som väntar

    [Fact]
    public async Task MeWithMediaAndPendingFillsTheOverview()
    {
        var (api, srv) = NewApi();
        srv.Me = """{"name":"Scout","points":120,"rank":4,"confirmed":7,"pendingPoints":40,"pendingFinds":3,"media":{"published":3,"pending":1,"rejected":0}}""";
        var me = await api.MeAsync();
        Overview.Apply(_set, me);
        Assert.Equal(40, _set.PendingPoints);
        Assert.Equal(3, _set.PendingFinds);
        Assert.Equal(3, _set.MediaPublished);
        Assert.Equal(1, _set.MediaPending);
        Assert.Equal(0, _set.MediaRejected);
        Assert.Equal("Bilder: 3 publicerade · 1 väntar på granskning · 0 avvisade", Overview.PicturesLine(_set));
        Assert.Equal("+40 poäng väntar på att en spelare till bekräftar (3 fynd)", Overview.PendingLine(_set));
        Strings.Apply("en");
        Assert.Equal("Pictures: 3 published · 1 awaiting review · 0 rejected", Overview.PicturesLine(_set));
        Assert.Equal("+40 points are waiting for one more player to confirm (3 finds)", Overview.PendingLine(_set));
        // "Bekräftat = minst två spelare" står redan i Mitt bidrag, direkt under raden om poängen som väntar.
        Assert.Contains("at least two players", Strings.S("mine.hint"));
        Strings.Apply("sv");
        Assert.Contains("minst två spelare", Strings.S("mine.hint"));
    }

    [Fact]
    public async Task MeWithoutTheNewFieldsShowsOnlyWhatTheAppKnows()
    {
        var (api, _) = NewApi(); // en server som inte kan fälten än
        _set.PendingPoints = 5; _set.MediaPublished = 9; _set.MediaPending = 9; _set.MediaRejected = 9; // gammalt sparat värde
        Overview.Apply(_set, await api.MeAsync());
        Assert.Null(_set.PendingPoints);
        Assert.Null(_set.MediaPublished);
        Assert.Null(Overview.PendingLine(_set));
        Assert.Equal(Strings.S("pics.none"), Overview.PicturesLine(_set));
        Assert.Contains("/scout bild", Overview.PicturesLine(_set));

        // Utan serverns siffror: antalet bilder appen själv har skickat och servern tagit emot.
        _set.Shots["a"] = new ShotState { Status = "published" };
        _set.Shots["b"] = new ShotState { Status = "queued" };
        _set.Shots["c"] = new ShotState { Status = "duplicate" };          // servern hade den redan
        _set.Shots["d"] = new ShotState { Status = "rejected:bad-image" };
        _set.Shots["e"] = new ShotState { Status = "expired" };
        _set.Shots["f"] = new ShotState();                                 // väntar
        Assert.Equal(2, Overview.ShotsSent(_set));
        Assert.Equal("Bilder: 2 skickade", Overview.PicturesLine(_set));
    }

    [Theory]
    [InlineData("""{"media":"nej","pendingPoints":"40","pendingFinds":null}""")]
    [InlineData("""{"media":{"published":3,"pending":1},"pendingPoints":-5,"pendingFinds":1.5}""")]   // ett tal saknas
    [InlineData("""{"media":{"published":3,"pending":"1","rejected":0},"pendingPoints":[1],"pendingFinds":{}}""")]
    [InlineData("""{"media":[3,1,0],"pendingPoints":true}""")]
    [InlineData("""{"media":null}""")]
    public async Task OddValuesInTheNewFieldsNeverBreakTheAnswer(string json)
    {
        var (api, srv) = NewApi();
        srv.Me = json;
        var me = await api.MeAsync(); // kastar inte – resten av svaret (poäng, plats) går fortfarande att använda
        Overview.Apply(_set, me);
        Assert.Null(_set.PendingPoints);
        Assert.Null(_set.PendingFinds);
        Assert.Null(_set.MediaPublished);
        Assert.Null(_set.MediaPending);
        Assert.Null(_set.MediaRejected);
        Assert.Null(Overview.PendingLine(_set));
    }

    [Fact]
    public void PendingPointsWithoutFindsAndZeroPending()
    {
        _set.PendingPoints = 15;
        Assert.Equal("+15 poäng väntar på att en spelare till bekräftar", Overview.PendingLine(_set));
        _set.PendingFinds = 0;
        Assert.Equal("+15 poäng väntar på att en spelare till bekräftar", Overview.PendingLine(_set));
        _set.PendingPoints = 0; _set.PendingFinds = 2;
        Assert.Null(Overview.PendingLine(_set)); // inget väntar: ingen rad
    }

    [Fact]
    public void MyPicturesLinkFollowsTheSiteRoutes()
    {
        Assert.Equal("/bidra/bilder", Overview.PicturesPath(sv: true));
        Assert.Equal("/en/contribute/pictures", Overview.PicturesPath(sv: false));
        foreach (var k in new[] { "pics.mine", "pics.server", "pics.local", "pics.none", "mine.pending", "mine.pending.points" }) Assert.True(Strings.Has(k), k);
    }

    // ================================================================== 2. påminnelsen om /reload

    [Fact]
    public void FirstInstallGivesNoReloadReminderButAnUpdateDoes()
    {
        Assert.False(ReloadReminder.OnInstalled(_set, previous: null, "1.0.26", Now)); // första installationen: spelet måste startas om
        Assert.Null(ReloadReminder.Pending(_set, Now));
        Assert.False(ReloadReminder.OnInstalled(_set, "1.0.26", "1.0.26", Now));       // samma version: inget byttes
        Assert.True(ReloadReminder.OnInstalled(_set, "1.0.25", "1.0.26", Now));
        Assert.Equal("1.0.26", ReloadReminder.Pending(_set, Now.AddHours(1)));

        var n = GameWatch.Current(_set, silentActive: true, Now.AddHours(1))!; // påminnelsen går före varningen
        Assert.Equal("Addonen uppdaterades till 1.0.26 – skriv /reload i spelet", n.Title);
        Assert.False(n.Warning);
        Assert.Contains("inte igång", n.Body);
        Strings.Apply("en");
        Assert.Equal("The addon was updated to 1.0.26 – type /reload in the game", GameWatch.Current(_set, false, Now)!.Title);
    }

    [Fact]
    public void ReminderGoesAwayOnAnUploadFromTheNewAddonVersion()
    {
        ReloadReminder.OnInstalled(_set, "1.0.25", "1.0.26", Now);
        Assert.False(ReloadReminder.OnUploaded(_set, "1.0.25")); // spelet kör fortfarande den gamla
        Assert.False(ReloadReminder.OnUploaded(_set, null));
        Assert.Equal("1.0.26", ReloadReminder.Pending(_set, Now));
        Assert.True(ReloadReminder.OnUploaded(_set, "1.0.26"));
        Assert.Null(ReloadReminder.Pending(_set, Now));
        Assert.Null(_set.AddonReloadSinceUtc);

        ReloadReminder.OnInstalled(_set, "1.0.25", "1.0.26", Now);
        Assert.True(ReloadReminder.OnUploaded(_set, "1.0.27")); // en ännu nyare räknas också
    }

    [Fact]
    public async Task ARealUploadFromTheNewVersionClearsTheReminder()
    {
        var (api, _) = NewApi();
        var up = new Uploader(api, _set);
        ReloadReminder.OnInstalled(_set, "0.9.0", "1.0.26", Now);
        var r1 = await up.SyncAsync(_dir, force: false);           // filen kommer från addon 1.0.0 (äldre än 1.0.26)
        Assert.Equal(1, r1.FilesUploaded);
        Assert.Equal("1.0.26", _set.AddonReloadVersion);

        File.WriteAllText(_sv, LuaParserTests.Sample.Replace("\"1.0.0\"", "\"1.0.26\"").Replace("417", "418"));
        var r2 = await up.SyncAsync(_dir, force: false);
        Assert.Equal(1, r2.FilesUploaded);
        Assert.Null(_set.AddonReloadVersion);
    }

    [Fact]
    public void ReminderGoesAwayWhenTheGameHasSavedAfterTheSwapOrAfterADay()
    {
        ReloadReminder.OnInstalled(_set, "1.0.25", "1.0.26", Now);
        Assert.False(ReloadReminder.OnGameSaved(_set, null));
        Assert.False(ReloadReminder.OnGameSaved(_set, Now.AddMinutes(-10))); // sparat före bytet: säger ingenting
        Assert.False(ReloadReminder.OnGameSaved(_set, Now.AddSeconds(2)));   // inom marginalen
        Assert.True(ReloadReminder.OnGameSaved(_set, Now.AddMinutes(3)));    // /reload, utloggning eller avslut efter bytet
        Assert.Null(ReloadReminder.Pending(_set, Now.AddMinutes(4)));

        ReloadReminder.OnInstalled(_set, "1.0.25", "1.0.26", Now);
        Assert.False(ReloadReminder.Expire(_set, Now.AddHours(23)));
        Assert.Null(ReloadReminder.Pending(_set, Now.AddHours(25)));
        Assert.True(ReloadReminder.Expire(_set, Now.AddHours(25)));
        Assert.Null(_set.AddonReloadVersion);
        Assert.False(ReloadReminder.Expire(_set, Now.AddHours(26)));
    }

    [Fact]
    public void TickClearsTheReminderFromTheFilesInTheGameFolder()
    {
        AddonInstaller.Install(_dir, Addon("1.0.26"));
        // Bytet skedde efter den senaste sparningen: påminnelsen står kvar.
        Touch(Now.AddMinutes(-30), Now.AddMinutes(-30));
        ReloadReminder.OnInstalled(_set, "1.0.25", "1.0.26", Now.AddMinutes(-5));
        var r = GameWatch.Tick(_dir, _set, Now);
        Assert.Contains("/reload", r.Notice!.Title);
        Assert.False(r.NewSilentWarning);
        // Spelet sparar (addonens fil skrivs): påminnelsen tas bort.
        Touch(Now.AddMinutes(1), Now.AddMinutes(1));
        Assert.Null(GameWatch.Tick(_dir, _set, Now.AddMinutes(2)).Notice);
        Assert.Null(Settings.Load().AddonReloadVersion); // sparat
    }

    // ================================================================== 3. kvittot i spelet

    [Fact]
    public void ReceiptIsNumbersOnlyAndRoundTrips()
    {
        var d = new AddonReceipt.Data(1794503193, 12, 1, 40, 3);
        var text = AddonReceipt.Render(d);
        Assert.Equal(
            "-- ForeverScout: receipt for the latest upload. Written by the Forever Scout client after each upload -\n"
            + "-- numbers only. Do not edit; the client replaces this file.\n"
            + "ForeverScoutReceipt = { v = 1, t = 1794503193, finds = 12, shots = 1, pending = 40, pendingFinds = 3 }\n", text);
        Assert.Equal(d, AddonReceipt.Parse(text));
        Assert.All(text, ch => Assert.True(ch < 128, "bara ASCII"));

        var plain = new AddonReceipt.Data(1794503193, 0, 2, null, null);
        Assert.EndsWith("ForeverScoutReceipt = { v = 1, t = 1794503193, finds = 0, shots = 2 }\n", AddonReceipt.Render(plain));
        Assert.Equal(plain, AddonReceipt.Parse(AddonReceipt.Render(plain)));

        // Orimliga tal kläms – aldrig ett minustecken, en exponent eller något annat än siffror i tabellen.
        var wild = AddonReceipt.Render(new AddonReceipt.Data(-5, int.MinValue, int.MaxValue, -1, int.MaxValue));
        var table = wild[wild.IndexOf("ForeverScoutReceipt", StringComparison.Ordinal)..];
        Assert.Equal("ForeverScoutReceipt = { v = 1, t = 0, finds = 0, shots = 999999999, pending = 0, pendingFinds = 999999999 }\n", table);
        Assert.Matches(@"\AForeverScoutReceipt = \{ (\w+ = \d+, )*\w+ = \d+ \}\n\z", table);
    }

    [Theory]
    [InlineData("")]
    [InlineData(DefaultReceipt)]
    [InlineData("ForeverScoutReceipt = { v = 2, t = 1794503193, finds = 1, shots = 1 }\n")]            // annan formatversion
    [InlineData("ForeverScoutReceipt = { v = 1, t = 0, finds = 1, shots = 1 }\n")]                     // ingen tid
    [InlineData("ForeverScoutReceipt = { v = 1, t = 1794503193, finds = -1, shots = 1 }\n")]
    [InlineData("ForeverScoutReceipt = { v = 1, t = 1794503193, finds = 1e9, shots = 1 }\n")]
    [InlineData("ForeverScoutReceipt = { v = 1, t = 1794503193, finds = \"12\", shots = 1 }\n")]
    [InlineData("ForeverScoutReceipt = { v = 1, t = 1794503193, finds = 1, shots = 1 }\nos.exit()\n")]  // kod efter tabellen
    [InlineData("os.exit()\nForeverScoutReceipt = { v = 1, t = 1794503193, finds = 1, shots = 1 }\n")]  // kod före tabellen
    [InlineData("ForeverScoutReceipt = { v = 1, t = 1794503193, finds = 1, shots = 1, x = RunScript('y') }\n")]
    [InlineData("ForeverScoutReceipt = { v = 1, t = 1794503193, finds = 1, shots = 1 } --[[\n]] error('x')\n")]
    [InlineData("--[[ ]] error('x')\nForeverScoutReceipt = { v = 1, t = 1794503193, finds = 1, shots = 1 }\n")]
    public void OnlyTheClientsOwnFormatIsAccepted(string text)
    {
        // Det sista fallet: "--[[ … ]] kod" ser ut som en kommentarsrad men är körbar Lua – inte heller det godtas.
        Assert.Null(AddonReceipt.Parse(text));
    }

    [Fact]
    public void ReceiptIsWrittenOnlyInsideTheInstalledAddonFolder()
    {
        var d = new AddonReceipt.Data(1794503193, 5, 0, null, null);
        Assert.False(AddonReceipt.Write(_dir, d)); // ingen addon installerad: ingenting skrivs
        Assert.False(Directory.Exists(Path.Combine(_dir.AddOnsDir, "ForeverScout")));

        AddonInstaller.Install(_dir, Addon("1.0.26"));
        Assert.Null(AddonReceipt.ReadInstalled(_dir)); // den tomma standardfilen är inget kvitto
        Assert.True(AddonReceipt.Write(_dir, d));
        Assert.Equal(AddonReceipt.Render(d), File.ReadAllText(ReceiptPath));
        Assert.Equal(d, AddonReceipt.ReadInstalled(_dir));
        Assert.False(File.Exists(ReceiptPath + ".tmp"));
        Assert.Equal(Path.Combine(_dir.AddOnsDir, "ForeverScout"), Path.GetDirectoryName(AddonReceipt.PathFor(_dir)));

        // Skyddet gäller som förut: samma filnamn i en annan addons mapp, i AddOns eller i WoW-katalogen nekas.
        foreach (var other in new[]
                 {
                     Path.Combine(_dir.AddOnsDir, "OtherAddon", "Receipt.lua"), Path.Combine(_dir.AddOnsDir, "Receipt.lua"),
                     Path.Combine(_dir.Path, "Receipt.lua"), Path.Combine(_dir.Path, "WTF", "Receipt.lua"),
                 })
            Assert.Throws<SafeFsException>(() => SafeFs.WriteAllBytes(other, [1]));
    }

    [Fact]
    public void AddonUpdateKeepsAFreshReceiptInsteadOfTheEmptyDefault()
    {
        AddonInstaller.Install(_dir, Addon("1.0.26"));
        var d = new AddonReceipt.Data(1794503193, 12, 1, 40, 3);
        Assert.True(AddonReceipt.Write(_dir, d));

        AddonInstaller.Install(_dir, Addon("1.0.27")); // uppdateringen kommer med den tomma standardfilen
        Assert.Equal("1.0.27", AddonInstaller.InstalledVersion(_dir));
        Assert.Equal(d, AddonReceipt.ReadInstalled(_dir));
        Assert.Equal(AddonReceipt.Render(d), File.ReadAllText(ReceiptPath));
        Assert.Equal("-- addon 1.0.27", File.ReadAllText(Path.Combine(_dir.AddOnsDir, "ForeverScout", "ForeverScout.lua")));
        Assert.False(Directory.Exists(Path.Combine(_dir.AddOnsDir, "ForeverScout.new")));
        Assert.False(Directory.Exists(Path.Combine(_dir.AddOnsDir, "ForeverScout.old")));
    }

    [Fact]
    public void AddonUpdateNeverCarriesOverAFileThatIsNotTheClientsReceipt()
    {
        AddonInstaller.Install(_dir, Addon("1.0.26"));
        // Någon (eller något) har ändrat filen: den kopieras aldrig vidare – den nya addonens standardfil gäller.
        File.WriteAllText(ReceiptPath, "ForeverScoutReceipt = { v = 1, t = 1794503193, finds = 1, shots = 1 }\nRunScript('x')\n");
        AddonInstaller.Install(_dir, Addon("1.0.27"));
        Assert.Equal(DefaultReceipt, File.ReadAllText(ReceiptPath));

        // En äldre addon utan Receipt.lua i paketet får ingen sådan fil av installationen.
        Assert.True(AddonReceipt.Write(_dir, new AddonReceipt.Data(1794503193, 1, 1, null, null)));
        AddonInstaller.Install(_dir, Addon("1.0.28", withReceipt: false));
        Assert.False(File.Exists(ReceiptPath));
    }

    [Fact]
    public async Task UploadWritesTheReceiptWithWhatTheServerReceived()
    {
        AddonInstaller.Install(_dir, Addon("1.0.26"));
        var (api, srv) = NewApi();
        srv.Me = """{"points":120,"pendingPoints":40,"pendingFinds":3}""";
        var up = new Uploader(api, _set);
        var report = await up.SyncAsync(_dir, force: false);
        Assert.Equal(1, report.FilesUploaded);
        Assert.Equal(5, report.RecordsAccepted); // 3 npcs + 2 quests + 0 loot ur serverns svar
        Overview.Apply(_set, await api.MeAsync());

        var at = new DateTimeOffset(Now);
        Assert.True(AddonReceipt.AfterUpload(_dir, _set, report, at));
        Assert.Equal(new AddonReceipt.Data(at.ToUnixTimeSeconds(), 5, 0, 40, 3), AddonReceipt.ReadInstalled(_dir));

        // Inget nytt skickat: kvittot rörs inte (det gäller fortfarande den senaste uppladdningen).
        var again = await up.SyncAsync(_dir, force: false);
        Assert.Equal(0, again.FilesUploaded);
        Assert.False(AddonReceipt.AfterUpload(_dir, _set, again, at.AddHours(1)));
        Assert.Equal(at.ToUnixTimeSeconds(), AddonReceipt.ReadInstalled(_dir)!.T);

        // Bara en bild mottagen räcker; okända väntande poäng utelämnas.
        _set.PendingPoints = null;
        Assert.True(AddonReceipt.AfterUpload(_dir, _set, new SyncReport { ShotsUploaded = 2 }, at.AddHours(2)));
        Assert.Equal(new AddonReceipt.Data(at.AddHours(2).ToUnixTimeSeconds(), 0, 2, null, null), AddonReceipt.ReadInstalled(_dir));
        Assert.False(AddonReceipt.AfterUpload(null, _set, new SyncReport { ShotsUploaded = 2 }, at));
    }

    [Fact]
    public void TheDefaultFileShippedWithTheAddonIsEmptyAndListedInBothTocFiles()
    {
        var addon = AddonSourceDir();
        var def = File.ReadAllText(Path.Combine(addon, "Receipt.lua"));
        Assert.Null(AddonReceipt.Parse(def));
        Assert.All(def.Split('\n'), line => Assert.True(line.Length == 0 || line.TrimEnd('\r').StartsWith("--", StringComparison.Ordinal), "bara kommentarer: " + line));
        Assert.DoesNotContain("--[", def, StringComparison.Ordinal);
        // Kvittot finns från addon 1.0.26; versionen själv höjs vid varje addonsläpp, så testet låser den inte.
        string? version = null;
        foreach (var toc in new[] { "ForeverScout.toc", "ForeverScout_Forever.toc" })
        {
            var lines = File.ReadAllLines(Path.Combine(addon, toc)).Select(l => l.Trim()).ToList();
            var v = lines.FirstOrDefault(l => l.StartsWith("## Version: ", StringComparison.Ordinal))?["## Version: ".Length..];
            Assert.NotNull(v);
            Assert.True(Version.Parse(v!) >= new Version(1, 0, 26), toc + ": version " + v + " ska vara minst 1.0.26");
            Assert.True(version is null || version == v, "båda .toc-filerna ska ha samma version");
            version = v;
            int r = lines.IndexOf("Receipt.lua"), m = lines.IndexOf("ForeverScout.lua");
            Assert.True(r >= 0 && m > r, toc + ": Receipt.lua ska stå före ForeverScout.lua");
        }
        Assert.Contains("local VERSION = \"" + version + "\"", File.ReadAllText(Path.Combine(addon, "ForeverScout.lua")));
    }

    private static string AddonSourceDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "addon", "ForeverScout");
            if (File.Exists(Path.Combine(p, "ForeverScout.toc"))) return p;
        }
        throw new DirectoryNotFoundException("client/addon/ForeverScout");
    }

    // ================================================================== 4. varningen när inget skickas

    private static SilentSession.SavedState St(DateTime? folder, DateTime? file) => new(folder, file);

    [Fact]
    public void NormalSaveGivesNoWarning()
    {
        _set.SilentSinceUtc = Now.AddHours(-2);
        // Spelet sparade för tio minuter sedan och addonens fil skrevs i samma veva (mappen strax efter filen).
        var ok = new[] { St(Now.AddMinutes(-10), Now.AddMinutes(-10).AddSeconds(-4)) };
        Assert.Null(SilentSession.SilentSave(ok, _set, Now));
        Assert.False(SilentSession.Evaluate(ok, _set, Now, out var changed));
        Assert.False(changed);
        // Filen skriven efter mappens tid (skriven på plats) är också en sparning.
        Assert.Null(SilentSession.SilentSave([St(Now.AddMinutes(-10), Now.AddMinutes(-9))], _set, Now));
    }

    [Fact]
    public void SaveWithoutTheAddonsFileWarnsOnce()
    {
        _set.SilentSinceUtc = Now.AddHours(-2);
        var silent = new[] { St(Now.AddMinutes(-10), Now.AddDays(-3)) }; // mappen ändrad, addonens fil orörd sedan i förrgår
        Assert.True(SilentSession.Evaluate(silent, _set, Now, out _));
        Assert.True(SilentSession.Active(silent, _set, Now));
        Assert.False(SilentSession.Evaluate(silent, _set, Now.AddSeconds(15), out var changed)); // nästa tick: samma sparning, ingen ny varning
        Assert.False(changed);

        // Filen saknas helt (addonen har aldrig varit laddad på det kontot): samma varning.
        var s2 = Fresh(new Settings());
        s2.SilentSinceUtc = Now.AddHours(-2);
        Assert.True(SilentSession.Evaluate([St(Now.AddMinutes(-10), null)], s2, Now, out _));

        var n = GameWatch.Current(_set, silentActive: true, Now)!;
        Assert.True(n.Warning);
        Assert.Equal(Strings.S("silent.title"), n.Title);
        foreach (var word in new[] { "inte ikryssad", "medan spelet var igång", "Fel WoW-katalog", "/scout" }) Assert.Contains(word, n.Body);
    }

    [Fact]
    public void NoWarningForOldUnfinishedOrAlreadyKnownSaves()
    {
        _set.SilentSinceUtc = Now.AddHours(-2);
        Assert.Null(SilentSession.SilentSave([St(Now.AddHours(-3), null)], _set, Now));    // före räkningens början
        Assert.Null(SilentSession.SilentSave([St(Now.AddSeconds(-20), null)], _set, Now)); // spelet skriver kanske fortfarande
        Assert.Null(SilentSession.SilentSave([St(null, Now.AddMinutes(-10))], _set, Now)); // ingen mapptid: inget att säga
        Assert.Null(SilentSession.SilentSave([], _set, Now));
        _set.SilentSinceUtc = Now.AddDays(-3);
        Assert.Null(SilentSession.SilentSave([St(Now.AddHours(-30), null)], _set, Now));   // äldre än ett dygn
        _set.SilentSinceUtc = null;
        Assert.Null(SilentSession.SilentSave([St(Now.AddMinutes(-10), null)], _set, Now)); // räkningen har inte börjat
    }

    [Fact]
    public void AtMostOneWarningPerSession()
    {
        _set.SilentSinceUtc = Now.AddHours(-2);
        Assert.True(SilentSession.Evaluate([St(Now.AddMinutes(-10), null)], _set, Now, out _));
        // Samma kväll: tre sparningar till utan addonens fil (/reload, karaktärsbyte, avslut) – ingen ny varning …
        foreach (var h in new[] { 1, 2, 3 })
            Assert.False(SilentSession.Evaluate([St(Now.AddHours(h), null)], _set, Now.AddHours(h).AddMinutes(5), out _));
        // … och ingen varning i efterhand när tystnaden är över, för sparningar som redan har bedömts.
        Assert.False(SilentSession.Evaluate([St(Now.AddHours(3), null)], _set, Now.AddHours(13), out _));
        Assert.False(SilentSession.Active([St(Now.AddHours(3), null)], _set, Now.AddHours(13)));
        // Nästa dag, ny spelsession, samma fel: en varning till.
        Assert.True(SilentSession.Evaluate([St(Now.AddHours(20), null)], _set, Now.AddHours(20).AddMinutes(5), out _));
    }

    [Fact]
    public void WarningEndsWhenTheAddonSavesAgain()
    {
        _set.SilentSinceUtc = Now.AddHours(-2);
        Assert.True(SilentSession.Evaluate([St(Now.AddMinutes(-10), null)], _set, Now, out _));
        Assert.True(SilentSession.Active([St(Now.AddMinutes(-10), Now.AddDays(-1))], _set, Now.AddMinutes(5)));
        // Spelaren kryssade i addonen och loggade ut: filen skrivs, varningen försvinner.
        var good = new[] { St(Now.AddMinutes(30), Now.AddMinutes(30)) };
        Assert.False(SilentSession.Evaluate(good, _set, Now.AddMinutes(35), out _));
        Assert.False(SilentSession.Active(good, _set, Now.AddMinutes(35)));
        Assert.Null(GameWatch.Current(_set, SilentSession.Active(good, _set, Now.AddMinutes(35)), Now.AddMinutes(35)));
    }

    /// <summary>Sätter ändringstiden på SavedVariables-mappen och på ForeverScout.lua (null = ta bort filen).</summary>
    private void Touch(DateTime folderUtc, DateTime? fileUtc)
    {
        if (fileUtc is { } f)
        {
            if (!File.Exists(_sv)) File.WriteAllText(_sv, LuaParserTests.Sample);
            File.SetLastWriteTimeUtc(_sv, f);
        }
        else File.Delete(_sv);
        Directory.SetLastWriteTimeUtc(_svDir, folderUtc);
    }

    [Fact]
    public void TickReadsOnlyTheFolderTimeAndTheAddonsFileAndWarnsOnce()
    {
        AddonInstaller.Install(_dir, Addon("1.0.26"));
        // Första ticket: räkningen börjar nu – det som hänt tidigare ger aldrig en varning.
        Touch(Now.AddMinutes(-20), Now.AddDays(-2));
        var first = GameWatch.Tick(_dir, _set, Now);
        Assert.False(first.NewSilentWarning);
        Assert.Null(first.Notice);
        Assert.Equal(Now, _set.SilentSinceUtc);

        var state = Assert.Single(SilentSession.Read(_dir));
        Assert.Equal(Now.AddMinutes(-20), state.FolderUtc);
        Assert.Equal(Now.AddDays(-2), state.FileUtc);

        // Spelet sparar en timme senare, men addonens fil skrivs inte.
        Touch(Now.AddHours(1), Now.AddDays(-2));
        Assert.False(GameWatch.Tick(_dir, _set, Now.AddHours(1).AddSeconds(30)).NewSilentWarning); // spelet skriver kanske fortfarande
        var warn = GameWatch.Tick(_dir, _set, Now.AddHours(1).AddMinutes(3));
        Assert.True(warn.NewSilentWarning);
        Assert.True(warn.Notice!.Warning);
        var next = GameWatch.Tick(_dir, _set, Now.AddHours(1).AddMinutes(4));
        Assert.False(next.NewSilentWarning); // en gång
        Assert.NotNull(next.Notice);         // men beskedet står kvar i fönstret
        Assert.NotNull(Settings.Load().SilentWarnedUtc); // och varningen minns över en omstart av appen

        // Addonen sparar: beskedet försvinner.
        Touch(Now.AddHours(2), Now.AddHours(2));
        var ok = GameWatch.Tick(_dir, _set, Now.AddHours(2).AddMinutes(3));
        Assert.False(ok.NewSilentWarning);
        Assert.Null(ok.Notice);
    }

    [Fact]
    public void NoWarningWithoutAnInstalledAddonOrWithoutAGameFolder()
    {
        _set.SilentSinceUtc = Now.AddHours(-2);
        _set.SilentDir = _dir.Path;
        Touch(Now.AddMinutes(-10), null);
        var r = GameWatch.Tick(_dir, _set, Now); // ingen addon installerad: "Addonen saknas" säger det redan
        Assert.False(r.NewSilentWarning);
        Assert.Null(r.Notice);
        var none = GameWatch.Tick(null, _set, Now);
        Assert.False(none.NewSilentWarning);
        Assert.Null(none.Notice);
    }

    [Fact]
    public void ChangingTheGameFolderStartsTheCountAgain()
    {
        _set.SilentSinceUtc = Now.AddDays(-5);
        _set.SilentDir = @"C:\annan\katalog";
        Assert.True(SilentSession.EnsureBaseline(_set, _dir, Now));
        Assert.Equal(Now, _set.SilentSinceUtc);
        Assert.Equal(_dir.Path, _set.SilentDir);
        Assert.False(SilentSession.EnsureBaseline(_set, _dir, Now.AddHours(1)));
        Assert.Equal(Now, _set.SilentSinceUtc);
    }

    [Fact]
    public void FolderTimeIsOnlyReadWhereListingIsAllowed()
    {
        Assert.NotNull(SafeFs.DirectoryWriteTimeUtc(_svDir));
        Assert.Null(SafeFs.DirectoryWriteTimeUtc(Path.Combine(_dir.Path, "WTF", "Account", "BBB", "SavedVariables"))); // finns inte
        // Karaktärernas kataloger, andra filer och allt utanför WoW-katalogen nekas som förut.
        Directory.CreateDirectory(Path.Combine(_dir.Path, "WTF", "Account", "AAA", "Realm", "Char"));
        Assert.Throws<SafeFsException>(() => SafeFs.DirectoryWriteTimeUtc(Path.Combine(_dir.Path, "WTF", "Account", "AAA", "Realm")));
        Assert.Throws<SafeFsException>(() => SafeFs.DirectoryWriteTimeUtc(Path.Combine(_dir.Path, "WTF", "Account", "AAA", "Realm", "Char")));
        Assert.Throws<SafeFsException>(() => SafeFs.DirectoryWriteTimeUtc(Path.Combine(_dir.Path, "Data")));
        Assert.Throws<SafeFsException>(() => SafeFs.DirectoryWriteTimeUtc(_tmp.FullName));
        // Andra addons filer i SavedVariables går fortfarande inte att läsa eller ens fråga om.
        File.WriteAllText(Path.Combine(_svDir, "Other.lua"), "x");
        Assert.Throws<SafeFsException>(() => SafeFs.Stat(Path.Combine(_svDir, "Other.lua")));
    }

    // ================================================================== 5. Linux: byte av AppImage-filen

    /// <summary>En fil som ser ut som en AppImage av typ 2 (ELF + "AI\x02"), fylld med ett känt värde.</summary>
    private static byte[] FakeAppImage(byte fill, int size = 256)
    {
        var b = Enumerable.Repeat(fill, size).ToArray();
        b[0] = 0x7F; b[1] = (byte)'E'; b[2] = (byte)'L'; b[3] = (byte)'F';
        b[8] = (byte)'A'; b[9] = (byte)'I'; b[10] = 2;
        return b;
    }

    private string NewAppImage()
    {
        var apps = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "Applications"));
        var image = Path.Combine(apps.FullName, "ForeverScout-x86_64.AppImage");
        File.WriteAllBytes(image, FakeAppImage(1));
        SafeFs.OwnAppImageOverride = image;
        return image;
    }

    [Fact]
    public void WithoutAnAppImageTheAppKeepsTellingTheUser()
    {
        SafeFs.OwnAppImageOverride = null;
        if (Environment.GetEnvironmentVariable("APPIMAGE") is null) Assert.Null(AppImageUpdater.Target); // inte startad som AppImage
        if (AppImageUpdater.Target is null)
        {
            Assert.False(AppImageUpdater.CanUpdate());
            Assert.False(AppImageUpdater.HasStaged);
            Assert.Equal(ErrorCodes.ExePath, Assert.Throws<ScoutException>(() => AppImageUpdater.Stage(FakeAppImage(2))).Code);
            AppImageUpdater.Cleanup(); // gör ingenting, kastar inte
        }
        // Filen finns inte (flyttad eller raderad medan appen kör): går inte att byta.
        SafeFs.OwnAppImageOverride = Path.Combine(_tmp.FullName, "borta", "ForeverScout-x86_64.AppImage");
        Assert.False(AppImageUpdater.CanUpdate());
    }

    [Fact]
    public void StageAndSwapReplaceTheAppImageInOneRename()
    {
        var image = NewAppImage();
        Assert.True(AppImageUpdater.CanUpdate());
        Assert.False(File.Exists(image + ".new")); // provet lämnar inget efter sig
        Assert.Equal(FakeAppImage(1), File.ReadAllBytes(image));

        AppImageUpdater.Stage(FakeAppImage(2));
        Assert.True(AppImageUpdater.HasStaged);
        Assert.Equal(FakeAppImage(1), File.ReadAllBytes(image)); // den körande filen är orörd tills bytet
        Assert.False(File.Exists(image + ".new.tmp"));
        if (!OperatingSystem.IsWindows()) Assert.True((File.GetUnixFileMode(image + ".new") & UnixFileMode.UserExecute) != 0);

        AppImageUpdater.Swap();
        Assert.Equal(FakeAppImage(2), File.ReadAllBytes(image));
        Assert.False(File.Exists(image + ".new"));
        Assert.False(AppImageUpdater.HasStaged);
        Assert.Equal(image, Assert.Single(Directory.GetFiles(Path.GetDirectoryName(image)!))); // inga rester
    }

    [Fact]
    public void OnlyARealAppImageIsStaged()
    {
        var image = NewAppImage();
        foreach (var bad in new[] { Utf8("MZ windows exe ............"), new byte[] { 0x1f, 0x8b, 8, 0 }, FakeAppImage(2)[..8], Array.Empty<byte>() })
        {
            var ex = Assert.Throws<UpdateStepException>(() => AppImageUpdater.Stage(bad));
            Assert.Equal(UpdateSteps.Stage, ex.Step);
            Assert.False(ex.FileInUse); // inte "filen används" – filen är fel
        }
        var elfOnly = FakeAppImage(2);
        elfOnly[8] = 0; // en vanlig ELF-binär utan AppImage-märket
        Assert.Throws<UpdateStepException>(() => AppImageUpdater.Stage(elfOnly));
        Assert.False(AppImageUpdater.HasStaged);
        Assert.Equal(FakeAppImage(1), File.ReadAllBytes(image));
        var swap = Assert.Throws<UpdateStepException>(AppImageUpdater.Swap); // inget att byta till
        Assert.Equal(UpdateSteps.SwapMove2, swap.Step);
        Assert.Equal(FakeAppImage(1), File.ReadAllBytes(image));
    }

    [Fact]
    public void OnlyTheAppImageAndItsNewFileMayBeTouched()
    {
        var image = NewAppImage();
        var dir = Path.GetDirectoryName(image)!;
        File.WriteAllText(Path.Combine(dir, "secret.txt"), "x");
        Assert.Throws<SafeFsException>(() => SafeFs.Check(Path.Combine(dir, "secret.txt"), SafeFs.Access.Probe, isDir: false));
        Assert.Throws<SafeFsException>(() => SafeFs.Check(Path.Combine(dir, "Other.AppImage"), SafeFs.Access.Write, isDir: false));
        Assert.Throws<SafeFsException>(() => SafeFs.Check(image + ".old", SafeFs.Access.Write, isDir: false));
        Assert.Throws<SafeFsException>(() => SafeFs.Check(dir, SafeFs.Access.Probe, isDir: true));
        SafeFs.Check(image, SafeFs.Access.Write, isDir: false);
        SafeFs.Check(image + ".new", SafeFs.Access.Write, isDir: false);
        SafeFs.Check(image + ".new.tmp", SafeFs.Access.Write, isDir: false);
        // Utan AppImage gäller ingen av reglerna.
        SafeFs.OwnAppImageOverride = null;
        if (AppImageUpdater.Target is null) Assert.Throws<SafeFsException>(() => SafeFs.Check(image, SafeFs.Access.Write, isDir: false));
    }

    [Fact]
    public void CleanupRemovesLeftoversFromAnInterruptedUpdate()
    {
        var image = NewAppImage();
        File.WriteAllBytes(image + ".new", [9]);
        File.WriteAllBytes(image + ".new.tmp", [9]);
        AppImageUpdater.Cleanup();
        Assert.False(File.Exists(image + ".new"));
        Assert.False(File.Exists(image + ".new.tmp"));
        Assert.Equal(FakeAppImage(1), File.ReadAllBytes(image));
    }

    /// <summary>
    /// Hela kedjan som på Windows och macOS: versionsinfon verifieras med signature5 och den publika nyckeln, filen mot
    /// sitt signerade SHA-256 och sin filsignatur – först därefter sparas och byts den. En ändrad fil byts aldrig in.
    /// </summary>
    [Fact]
    public void LinuxUpdateUsesTheSameSignatureCheckAsWindowsAndMac()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string Sign(byte[] d) => Convert.ToBase64String(key.SignData(d, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        var exe = Encoding.ASCII.GetBytes("MZ windows");
        var appImage = FakeAppImage(7, 4096);
        var (sha, lsha) = (ApiClient.Sha256Hex(exe), ApiClient.Sha256Hex(appImage));
        var n = new JsonObject
        {
            ["v"] = 1, ["issued"] = "2026-11-16T12:00:00.000Z",
            ["client"] = "1.0.18", ["clientFile"] = "ForeverScout.exe", ["sha256"] = sha,
            ["clientSig"] = Sign(ReleaseVerifier.FileMessage(ReleaseKind.Client, "1.0.18", sha)),
            ["linux"] = "1.0.18", ["linuxFile"] = "ForeverScout-x86_64.AppImage", ["linuxSha256"] = lsha,
            ["linuxSig"] = Sign(ReleaseVerifier.FileMessage(ReleaseKind.Linux, "1.0.18", lsha)),
        };
        foreach (var (name, fields) in new[] { ("signature", ReleaseVerifier.SignedFields), ("signature2", ReleaseVerifier.SignedFields2), ("signature3", ReleaseVerifier.SignedFields3), ("signature5", ReleaseVerifier.SignedFields5) })
        {
            using var doc = JsonDocument.Parse(n.ToJsonString());
            n[name] = Sign(Encoding.UTF8.GetBytes(ReleaseVerifier.Canonical(doc.RootElement, fields)));
        }
        var pem = key.ExportSubjectPublicKeyInfoPem();
        var release = ReleaseVerifier.VerifyLinux(n.ToJsonString(), pem);
        Assert.Equal("1.0.18", release.Linux);

        var image = NewAppImage();
        // En fil som inte är den signerade stoppas av filkontrollen, före Stage.
        var tampered = (byte[])appImage.Clone();
        tampered[100] ^= 1;
        Assert.Equal(ErrorCodes.ShaMismatch, Assert.ThrowsAny<ScoutException>(() => ReleaseVerifier.VerifyFile(tampered, ReleaseKind.Linux, release.LinuxFile!, pem)).Code);
        Assert.Equal(FakeAppImage(1), File.ReadAllBytes(image));

        ReleaseVerifier.VerifyFile(appImage, ReleaseKind.Linux, release.LinuxFile!, pem);
        AppImageUpdater.Stage(appImage);
        AppImageUpdater.Swap();
        Assert.Equal(appImage, File.ReadAllBytes(image));

        // Utan signature5 finns inget Linux-släpp att lita på.
        n.Remove("signature5");
        Assert.Equal(ErrorCodes.SignatureMissing, Assert.ThrowsAny<ScoutException>(() => ReleaseVerifier.VerifyLinux(n.ToJsonString(), pem)).Code);
    }

    // ================================================================== 6. beta-kanalen på Mac och Linux

    [Fact]
    public async Task MacAndLinuxAskTheBetaChannelWhenBetaIsOn()
    {
        var (api, srv) = NewApi();
        const string pem = "-----BEGIN PUBLIC KEY-----\nMFkw\n-----END PUBLIC KEY-----";
        foreach (var beta in new[] { false, true })
        {
            await Assert.ThrowsAnyAsync<ScoutException>(() => api.VersionAsync(pem, beta));
            await Assert.ThrowsAnyAsync<ScoutException>(() => api.MacVersionAsync(pem, beta));
            await Assert.ThrowsAnyAsync<ScoutException>(() => api.LinuxVersionAsync(pem, beta));
        }
        Assert.Equal(
            ["/api/bidrag/version", "/api/bidrag/version", "/api/bidrag/version",
             "/api/bidrag/version?kanal=beta", "/api/bidrag/version?kanal=beta", "/api/bidrag/version?kanal=beta"], srv.Calls);
    }

    [Fact]
    public void BetaChannelKeepsItsOwnIssuedStampForEveryPlatform()
    {
        var s = Fresh(Settings.Load());
        s.ReleaseIssued = null; s.ReleaseIssuedBeta = null;
        s.AcceptRelease(new SignedRelease { Issued = "2026-11-16T12:00:00.000Z" }, beta: true);
        Assert.Null(s.ReleaseIssued);
        Assert.Equal("2026-11-16T12:00:00.000Z", s.ReleaseIssuedBeta);
        // En äldre vanlig versionsinfo stoppas inte av beta-kanalens tidsstämpel (och tvärtom).
        s.AcceptRelease(new SignedRelease { Issued = "2026-11-10T12:00:00.000Z" }, beta: false);
        Assert.Equal("2026-11-10T12:00:00.000Z", s.ReleaseIssued);
        s.ReleaseIssued = null; s.ReleaseIssuedBeta = null;
        s.Save();
    }
}
