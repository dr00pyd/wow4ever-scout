using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using ForeverScout.Core;

namespace ForeverScout.Tests;

/// <summary>
/// 1.0.15: bilder tagna med /scout bild. Bara den skärmbild som en post i addonens fil pekar ut (filnamnets tid ±3 s,
/// annars ändringstiden ±3 s) läses och skickas; två poster tar aldrig samma fil; en post utan fil ges upp efter 24 h;
/// övriga filer i Screenshots nekas av SafeFs.
/// </summary>
public sealed class ShotTests : IDisposable
{
    private readonly DirectoryInfo _tmp = TestEnv.Temp("fs-shot-");
    /// <summary>Fast tidszon (UTC+1 utan sommartid), så att testet inte beror på datorns.</summary>
    private static readonly TimeZoneInfo Tz = TimeZoneInfo.CreateCustomTimeZone("scout-test", TimeSpan.FromHours(1), "scout-test", "scout-test");
    private const long T0 = 1794000000;

    public void Dispose()
    {
        SafeFs.SetGameDir(null);
        try { _tmp.Delete(true); } catch (IOException) { }
    }

    /// <summary>Spelets filnamn för en bild tagen vid unix-tiden <paramref name="t"/> (lokal tid i <see cref="Tz"/>).</summary>
    private static string NameAt(long t, string ext = ".jpg") =>
        "WoWScrnShot_" + TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeSeconds(t).UtcDateTime, Tz).ToString("MMddyy'_'HHmmss") + ext;

    private static DateTime Utc(long t) => DateTimeOffset.FromUnixTimeSeconds(t).UtcDateTime;

    /// <summary>En fil vars ändringstid ligger långt från alla poster (bara namnet kan ge träff).</summary>
    private static ShotFile Named(long t) => new(@"C:\wow\_forever_\Screenshots\" + NameAt(t), Utc(T0 - 500000));

    // ------------------------------------------------------------------ matchning (ren funktion)

    [Fact]
    public void ScreenshotNamesAreExactlyTheGamesOwn()
    {
        Assert.True(WowLocator.IsScreenshotName("WoWScrnShot_111526_203344.jpg"));
        Assert.True(WowLocator.IsScreenshotName("wowscrnshot_111526_203344.TGA"));
        Assert.True(WowLocator.IsScreenshotName("WoWScrnShot_111526_203344.png"));
        Assert.True(WowLocator.IsScreenshotName("WoWScrnShot_111526_203344.jpeg"));
        foreach (var bad in new[] { "semester.jpg", "WoWScrnShot_111526_203344.jpg.exe", "WoWScrnShot_111526_203344.bmp", "WoWScrnShot_111526-203344.jpg",
                     "WoWScrnShot_1115260203344.jpg", "WoWScrnShot_11152x_203344.jpg", "WoWScrnShot_111526_203344", "xWoWScrnShot_111526_203344.jpg", "" })
            Assert.False(WowLocator.IsScreenshotName(bad), bad);
        Assert.Equal(new DateTime(2026, 11, 15, 20, 33, 44), ShotMatcher.NameTime("WoWScrnShot_111526_203344.jpg"));
        Assert.Null(ShotMatcher.NameTime("WoWScrnShot_139926_256199.jpg")); // rätt form, omöjlig tid
        Assert.Null(ShotMatcher.NameTime("semester.jpg"));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(3, true)]
    [InlineData(-3, true)]
    [InlineData(4, false)]
    [InlineData(-4, false)]
    [InlineData(3600, false)] // rätt minut och sekund men fel timme (fel tidszon) ger ingen träff
    public void FileNameTimeMustBeWithinThreeSeconds(int offset, bool expected)
    {
        var file = Named(T0 + offset);
        var m = ShotMatcher.Match([("aaaaaaaa", T0)], [file], Tz);
        Assert.Equal(expected, m.ContainsKey("aaaaaaaa"));
        if (expected) Assert.Equal(file.Path, m["aaaaaaaa"]);
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(-3, true)]
    [InlineData(4, false)]
    public void ModifiedTimeIsTheFallbackWhenTheNameDoesNotMatch(int offset, bool expected)
    {
        // Namnet säger en helt annan tid (t.ex. klockan ställdes om), men filen skrevs när posten togs.
        var file = new ShotFile(@"C:\wow\_forever_\Screenshots\" + NameAt(T0 - 86400 * 3), Utc(T0 + offset));
        Assert.Equal(expected, ShotMatcher.Match([("aaaaaaaa", T0)], [file], Tz).ContainsKey("aaaaaaaa"));
    }

    [Fact]
    public void FileNameMatchBeatsModifiedTime()
    {
        var byMtime = new ShotFile(@"C:\s\" + NameAt(T0 - 9000), Utc(T0));
        var byName = Named(T0 + 2);
        var m = ShotMatcher.Match([("aaaaaaaa", T0)], [byMtime, byName], Tz);
        Assert.Equal(byName.Path, m["aaaaaaaa"]);
    }

    [Fact]
    public void OnlyTheGamesOwnFileNamesCanMatch()
    {
        // En egen bild i mappen med exakt rätt ändringstid får aldrig väljas.
        var own = new ShotFile(@"C:\wow\_forever_\Screenshots\semester.jpg", Utc(T0));
        Assert.Empty(ShotMatcher.Match([("aaaaaaaa", T0)], [own], Tz));
    }

    [Fact]
    public void TwoPostsNeverTakeTheSameFile()
    {
        // Två poster två sekunder isär, en enda fil mitt emellan: exakt en post får den.
        var one = Named(T0 + 1);
        var m = ShotMatcher.Match([("aaaaaaaa", T0), ("bbbbbbbb", T0 + 2)], [one], Tz);
        Assert.Single(m);
        Assert.Equal(one.Path, m["aaaaaaaa"]); // lika nära: den äldre posten vinner (stabilt utfall)

        // Två filer: var och en får sin närmaste, oavsett ordning på indata.
        var a = Named(T0);
        var b = Named(T0 + 2);
        foreach (var files in new[] { new[] { a, b }, new[] { b, a } })
        {
            m = ShotMatcher.Match([("bbbbbbbb", T0 + 2), ("aaaaaaaa", T0)], files, Tz);
            Assert.Equal(a.Path, m["aaaaaaaa"]);
            Assert.Equal(b.Path, m["bbbbbbbb"]);
        }

        // Tre poster i en skur, två filer: två olika filer delas ut, den tredje posten blir utan.
        m = ShotMatcher.Match([("aaaaaaaa", T0), ("bbbbbbbb", T0 + 1), ("cccccccc", T0 + 2)], [a, b], Tz);
        Assert.Equal(2, m.Count);
        Assert.Equal(2, m.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void AFileTakenByAnEarlierPostIsNeverChosenAgain()
    {
        var file = Named(T0 + 1);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { file.Name.ToUpperInvariant() };
        Assert.Empty(ShotMatcher.Match([("bbbbbbbb", T0 + 2)], [file], Tz, taken));
        Assert.Single(ShotMatcher.Match([("bbbbbbbb", T0 + 2)], [file], Tz, new HashSet<string>()));
    }

    [Fact]
    public void APostOlderThan24HoursIsGivenUp()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(T0);
        Assert.False(ShotMatcher.Expired(T0 - 60, now));
        Assert.False(ShotMatcher.Expired(T0 - 24 * 3600, now));
        Assert.True(ShotMatcher.Expired(T0 - 24 * 3600 - 1, now));
        Assert.False(ShotMatcher.Expired(T0 + 500, now)); // klockan före: inte uppgiven
    }

    // ------------------------------------------------------------------ addonens poster

    private static string Lua(params string[] shots) =>
        "\nForeverScoutDB = {\n[\"addon\"] = \"1.0.20\",\n[\"seq\"] = 7,\n[\"npcs\"] = {\n},\n[\"shots\"] = {\n" + string.Join("", shots) + "},\n}\n";

    private static string Shot(string code, long t, string kind = "npc", string id = "[\"id\"] = 1234,\n", string name = "Lars") =>
        "{\n[\"k\"] = \"" + kind + "\",\n" + id + "[\"n\"] = \"" + name + "\",\n[\"t\"] = " + t + ",\n[\"m\"] = 1429,\n[\"x\"] = 43.21,\n[\"y\"] = 65.4,\n[\"c\"] = \"" + code + "\",\n}, -- [1]\n";

    [Fact]
    public void ShotsAreReadFromTheAddonFileAndBrokenEntriesSkipped()
    {
        var lua = Lua(
            Shot("abcd1234", T0),
            Shot("objekt01", T0 + 10, kind: "object", id: "", name: "Mailbox"),
            Shot("ABCD1234", T0),                          // versaler: inte addonens kod
            Shot("kort", T0),                              // fel längd
            Shot("spelare1", T0, kind: "player"),          // okänd typ
            Shot("utanid01", T0, id: ""),                  // npc utan id
            Shot("utantid1", 0));                          // ingen tid
        var peek = Uploader.Peek(Encoding.UTF8.GetBytes(lua));
        Assert.True(peek.Complete && peek.OnlyOurs);
        Assert.Equal(["abcd1234", "objekt01"], peek.Shots!.Select(s => s.Code));
        var npc = peek.Shots![0];
        Assert.Equal(new ShotRecord("npc", 1234, "Lars", T0, 1429, 43.21, 65.4, "abcd1234"), npc);
        Assert.Null(peek.Shots![1].Id);

        // En fil utan shots (äldre addon) ger en tom lista, inget fel.
        Assert.Empty(Uploader.Peek("ForeverScoutDB = { addon = \"1.0.19\", seq = 1 }"u8.ToArray()).Shots!);
    }

    [Fact]
    public void MetaJsonHasTheContractFieldsAndOmitsWhatTheAddonDidNotHave()
    {
        var st = new ShotState { K = "npc", Id = 1234, N = "Lars \"den store\"", T = T0, M = 1429, X = 43.214, Y = 65.4, Addon = "1.0.20" };
        using var doc = System.Text.Json.JsonDocument.Parse(st.MetaJson("abcd1234", "ff00"));
        var r = doc.RootElement;
        Assert.Equal(["k", "id", "n", "t", "m", "x", "y", "c", "addon", "sha"], r.EnumerateObject().Select(p => p.Name));
        Assert.Equal("npc", r.GetProperty("k").GetString());
        Assert.Equal(1234, r.GetProperty("id").GetInt64());
        Assert.Equal("Lars \"den store\"", r.GetProperty("n").GetString());
        Assert.Equal(T0, r.GetProperty("t").GetInt64());
        Assert.Equal(43.21, r.GetProperty("x").GetDouble());
        Assert.Equal("abcd1234", r.GetProperty("c").GetString());
        Assert.Equal("1.0.20", r.GetProperty("addon").GetString());
        Assert.Equal("ff00", r.GetProperty("sha").GetString());

        var obj = new ShotState { K = "object", N = "Mailbox", T = T0 };
        using var doc2 = System.Text.Json.JsonDocument.Parse(obj.MetaJson("objekt01", "aa"));
        Assert.Equal(["k", "n", "t", "c", "addon", "sha"], doc2.RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void OnByDefaultAndTextsExistInBothLanguages()
    {
        var s = new Settings();
        Assert.True(s.UploadShots);
        Assert.Empty(s.Shots);
        foreach (var k in new[] { "set.shots", "set.shots.hint", "journal.shots", "err." + ErrorCodes.ShotTooLarge })
        {
            Assert.True(Strings.All.TryGetValue(k, out var t), k);
            Assert.False(string.IsNullOrWhiteSpace(t.Sv));
            Assert.False(string.IsNullOrWhiteSpace(t.En));
        }
    }

    // ------------------------------------------------------------------ filer och uppladdning

    private static readonly byte[] Original = [0xFF, 0xD8, 1, 2, 3, 4, 5, 6, 7, 8, 9];
    private static readonly byte[] Other = [0xFF, 0xD8, 9, 9, 9];

    /// <summary>_forever_ med addonens fil (två poster) och Screenshots: rätt bild, en annan av spelets bilder och en egen bild.</summary>
    private (GameDir Dir, string Shots, string Match, string Sv) Game(params string[] shots)
    {
        var game = Path.Combine(_tmp.FullName, "World of Warcraft", "_forever_");
        Directory.CreateDirectory(Path.Combine(game, "Interface", "AddOns"));
        var sv = Path.Combine(game, "WTF", "Account", "KONTO", "SavedVariables");
        Directory.CreateDirectory(sv);
        File.WriteAllText(Path.Combine(sv, "ForeverScout.lua"), Lua(shots.Length > 0 ? shots : [Shot("abcd1234", T0)]));
        var dir = Path.Combine(game, "Screenshots");
        Directory.CreateDirectory(Path.Combine(dir, "Gamla"));
        var match = Path.Combine(dir, NameAt(T0 + 1));
        File.WriteAllBytes(match, Original);
        File.WriteAllBytes(Path.Combine(dir, NameAt(T0 - 7200)), Other);          // spelets bild, men ingen post pekar på den
        File.WriteAllBytes(Path.Combine(dir, "semester.jpg"), Other);              // spelarens egen bild
        File.WriteAllBytes(Path.Combine(dir, "Gamla", NameAt(T0)), Other);         // underkatalog – läses aldrig
        foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) File.SetLastWriteTimeUtc(f, Utc(T0 - 900000));
        var real = SafeFs.SetGameDir(game)!;
        return (new GameDir(real, "_forever_"), Path.Combine(real, "Screenshots"), Path.Combine(real, "Screenshots", NameAt(T0 + 1)),
            Path.Combine(real, "WTF", "Account", "KONTO", "SavedVariables", "ForeverScout.lua"));
    }

    private sealed class FakeEncoder : IShotEncoder
    {
        public List<byte[]> Seen { get; } = new();
        public bool Undecodable { get; set; }
        public byte[]? Encode(byte[] original, int maxWidth, int quality)
        {
            lock (Seen) Seen.Add(original);
            Assert.Equal((1600, 82), (maxWidth, quality));
            Assert.Equal(ThreadPriority.Lowest, Thread.CurrentThread.Priority);
            Assert.True(Thread.CurrentThread.IsBackground);
            return Undecodable ? null : [.. "LITEN-JPEG:"u8, .. original];
        }
    }

    private sealed class FakeServer : HttpMessageHandler
    {
        public List<(string Path, string Body, string? Type, string? Auth)> Calls { get; } = new();
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string Error { get; set; } = """{"error":"x","code":"invalid"}""";
        public string Ok { get; set; } = """{"ok":true,"status":"published"}""";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var body = req.Content is null ? "" : Encoding.Latin1.GetString(await req.Content.ReadAsByteArrayAsync(ct));
            var path = req.RequestUri!.AbsolutePath;
            lock (Calls) Calls.Add((path, body, req.Content?.Headers.ContentType?.MediaType, req.Headers.Authorization?.ToString()));
            if (path.EndsWith("/bild"))
                return new HttpResponseMessage(Status) { Content = new StringContent(Status == HttpStatusCode.OK ? Ok : Error, Encoding.UTF8, "application/json") };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        }
        public List<(string Path, string Body, string? Type, string? Auth)> Shots() => Calls.Where(c => c.Path == "/api/bidrag/bild").ToList();
    }

    private static (Uploader Up, FakeServer Srv, Settings Set, FakeEncoder Enc, ApiClient Api) NewUploader(bool on = true, long now = T0 + 600)
    {
        var srv = new FakeServer();
        var api = new ApiClient("https://guide.wow4ever.se", srv) { Key = "k" };
        var set = Settings.Load();
        set.UploadHotfix = false;
        set.UploadCreatureCache = false;
        set.ShareAuctionPrices = false;
        set.UploadShots = on;
        set.Shots.Clear(); set.UploadedSha.Clear(); set.UploadedSeq.Clear(); set.BlockedAddons.Clear();
        set.RetryAttempts = 0; set.NextRetryUtc = null;
        var enc = new FakeEncoder();
        var up = new Uploader(api, set) { ShotEncoder = enc, ShotTimeZone = Tz, ShotNow = () => DateTimeOffset.FromUnixTimeSeconds(now) };
        return (up, srv, set, enc, api);
    }

    [Fact]
    public void OnlyTheGamesOwnScreenshotsCanBeListedOrRead()
    {
        var (dir, shots, match, _) = Game();
        var files = WowLocator.ScreenshotFiles(dir);
        Assert.Equal(2, files.Count); // spelets två bilder – inte semester.jpg, inte underkatalogen
        Assert.All(files, f => Assert.True(WowLocator.IsScreenshotName(f.Name)));
        Assert.Equal(Original, SafeFs.ReadAllBytes(match));
        Assert.Throws<SafeFsException>(() => SafeFs.ReadAllBytes(Path.Combine(shots, "semester.jpg")));
        Assert.Throws<SafeFsException>(() => SafeFs.ReadAllBytes(Path.Combine(shots, "Gamla", NameAt(T0))));
        Assert.Throws<SafeFsException>(() => SafeFs.GetDirectories(Path.Combine(shots, "Gamla")));
        // Originalfilen får aldrig skrivas, flyttas eller raderas.
        Assert.Throws<SafeFsException>(() => SafeFs.WriteAllBytes(match, [0]));
        Assert.Throws<SafeFsException>(() => SafeFs.DeleteFile(match));
        Assert.Throws<SafeFsException>(() => SafeFs.MoveFile(match, match + ".bak"));
        Assert.Throws<SafeFsException>(() => SafeFs.WriteAllBytes(Path.Combine(shots, NameAt(T0 + 50)), [0]));
    }

    [Fact]
    public void NoScreenshotsFolderGivesEmptyList()
    {
        var game = Path.Combine(_tmp.FullName, "World of Warcraft", "_forever_");
        Directory.CreateDirectory(Path.Combine(game, "Interface"));
        var real = SafeFs.SetGameDir(game)!;
        Assert.Empty(WowLocator.ScreenshotFiles(new GameDir(real, "_forever_")));
    }

    [Fact]
    public async Task TheMatchingPictureIsSentOnceAndTheOriginalIsUntouched()
    {
        var (dir, _, match, _) = Game();
        var (up, srv, set, enc, _) = NewUploader();
        var report = await up.SyncAsync(dir, force: false);
        Assert.Null(report.Error);
        Assert.Equal(1, report.FilesUploaded);
        Assert.Equal(1, report.ShotsUploaded);
        Assert.False(report.NothingToDo);

        // Datan först, sedan bilden – med samma nyckel.
        Assert.Equal(["/api/bidrag/ladda-upp", "/api/bidrag/bild"], srv.Calls.Select(c => c.Path));
        var call = Assert.Single(srv.Shots());
        Assert.Equal("multipart/form-data", call.Type);
        Assert.Equal("Bearer k", call.Auth);
        Assert.Contains("name=\"meta\"", call.Body);
        Assert.Contains("name=\"bild\"; filename=\"bild.jpg\"", call.Body);
        Assert.Contains("image/jpeg", call.Body);
        Assert.Contains("\"c\":\"abcd1234\"", call.Body);
        Assert.Contains("\"k\":\"npc\",\"id\":1234,\"n\":\"Lars\",\"t\":" + T0 + ",\"m\":1429,\"x\":43.21,\"y\":65.4", call.Body);
        Assert.Contains("\"addon\":\"1.0.20\"", call.Body);
        Assert.Contains("\"sha\":\"" + ApiClient.Sha256Hex(Original) + "\"", call.Body); // originalfilens sha256
        Assert.Contains("LITEN-JPEG:", call.Body);                                         // den förminskade bilden, inte originalet som fil
        Assert.DoesNotContain("KONTO", call.Body);
        Assert.DoesNotContain("WoWScrnShot", call.Body);

        // Bara den utpekade filen lästes.
        Assert.Equal(Original, Assert.Single(enc.Seen));
        Assert.Equal(Original, File.ReadAllBytes(match));
        Assert.Equal(Utc(T0 - 900000), File.GetLastWriteTimeUtc(match));

        var st = set.Shots["abcd1234"];
        Assert.Equal(("published", Path.GetFileName(match)), (st.Status, st.File));
        Assert.Null(st.N);

        // Nästa körning: inget skickas igen, och Screenshots läses inte.
        srv.Calls.Clear(); enc.Seen.Clear();
        report = await up.SyncAsync(dir, force: false);
        Assert.Empty(srv.Calls);
        Assert.Empty(enc.Seen);
        Assert.True(report.NothingToDo);

        // Tillståndet överlever en omstart.
        Assert.Equal("published", Settings.Load().Shots["abcd1234"].Status);
    }

    [Fact]
    public async Task ASecondPostCannotTakeAFileThatAnEarlierPostUsed()
    {
        var (dir, _, _, sv) = Game();
        var (up, srv, set, _, _) = NewUploader();
        await up.SyncAsync(dir, force: false);
        Assert.Single(srv.Shots());

        // En ny post två sekunder senare, men spelet sparade ingen ny fil: den gamla filen (inom ±3 s) får inte tas.
        File.WriteAllText(sv, Lua(Shot("abcd1234", T0), Shot("efgh5678", T0 + 2)));
        srv.Calls.Clear();
        var report = await up.SyncAsync(dir, force: false);
        Assert.Null(report.Error);
        Assert.Empty(srv.Shots());
        Assert.True(set.Shots["efgh5678"].Pending);
    }

    [Fact]
    public async Task NothingIsReadOrSentWhenTurnedOff()
    {
        var (dir, _, _, _) = Game();
        var (up, srv, set, enc, api) = NewUploader(on: false);
        var report = await up.SyncAsync(dir, force: false);
        Assert.Null(report.Error);
        Assert.Empty(srv.Shots());
        Assert.Empty(enc.Seen);
        Assert.Empty(set.Shots);
        var preview = UploadPreview.Build(api, dir, set);
        Assert.DoesNotContain("/api/bidrag/bild", preview);

        set.UploadShots = true;
        Assert.Contains("/api/bidrag/bild", UploadPreview.Build(api, dir, set));
    }

    [Fact]
    public async Task NoEncoderMeansNoPictures()
    {
        var (dir, _, _, _) = Game();
        var (up, srv, _, _, _) = NewUploader();
        up.ShotEncoder = null; // Mac/Linux tills vidare
        var report = await up.SyncAsync(dir, force: false);
        Assert.Null(report.Error);
        Assert.Empty(srv.Shots());
    }

    [Fact]
    public async Task ConflictMeansTryAgainAtTheNextUpload()
    {
        var (dir, _, _, _) = Game();
        var (up, srv, set, _, _) = NewUploader();
        srv.Status = HttpStatusCode.Conflict;
        srv.Error = """{"error":"x","code":"shot-unknown","retry":true}""";
        var report = await up.SyncAsync(dir, force: false);
        Assert.Null(report.Error);
        Assert.False(report.WillRetry);
        Assert.Equal(0, report.ShotsUploaded);
        Assert.True(set.Shots["abcd1234"].Pending);

        srv.Status = HttpStatusCode.OK;
        srv.Ok = """{"ok":true,"status":"queued"}""";
        srv.Calls.Clear();
        report = await up.SyncAsync(dir, force: false);
        Assert.Single(srv.Shots());
        Assert.Equal(1, report.ShotsUploaded);
        Assert.Equal("queued", set.Shots["abcd1234"].Status);
    }

    [Fact]
    public async Task ConflictIsGivenUpAfter24Hours()
    {
        var (dir, _, _, _) = Game();
        var (up, srv, set, _, _) = NewUploader(now: T0 + 25 * 3600);
        srv.Status = HttpStatusCode.Conflict;
        srv.Error = """{"error":"x","code":"shot-unknown","retry":true}""";
        await up.SyncAsync(dir, force: false);
        Assert.Single(srv.Shots()); // filen fanns, så ett försök görs även efter 24 h
        Assert.Equal("expired", set.Shots["abcd1234"].Status);
        srv.Calls.Clear();
        await up.SyncAsync(dir, force: false);
        Assert.Empty(srv.Shots());
    }

    [Fact]
    public async Task ARejectedPictureIsNotSentAgain()
    {
        var (dir, _, match, _) = Game();
        var (up, srv, set, _, _) = NewUploader();
        srv.Status = HttpStatusCode.BadRequest;
        var report = await up.SyncAsync(dir, force: false);
        Assert.False(report.WillRetry);
        Assert.Equal(0, report.ShotsUploaded);
        Assert.Equal(("rejected:invalid", Path.GetFileName(match)), (set.Shots["abcd1234"].Status, set.Shots["abcd1234"].File));

        srv.Status = HttpStatusCode.OK;
        srv.Calls.Clear();
        await up.SyncAsync(dir, force: false);
        Assert.Empty(srv.Shots());
    }

    [Fact]
    public async Task AnInvalidKeyKeepsThePictureWaiting()
    {
        var (dir, _, _, _) = Game();
        var (up, srv, set, _, _) = NewUploader();
        srv.Status = HttpStatusCode.Unauthorized;
        srv.Error = """{"error":"x","code":"unauthorized"}""";
        var report = await up.SyncAsync(dir, force: false);
        Assert.NotNull(report.Error);
        Assert.True(set.Shots["abcd1234"].Pending);
    }

    [Fact]
    public async Task ServerErrorIsRetriedWithBackoff()
    {
        var (dir, _, _, _) = Game();
        var (up, srv, set, _, _) = NewUploader();
        srv.Status = HttpStatusCode.BadGateway;
        var report = await up.SyncAsync(dir, force: false);
        Assert.True(report.WillRetry);
        Assert.True(set.Shots["abcd1234"].Pending);
        Assert.True(set.RetryAttempts > 0);

        srv.Status = HttpStatusCode.OK;
        srv.Calls.Clear();
        report = await up.SyncAsync(dir, force: false);
        Assert.Equal(["/api/bidrag/bild"], srv.Calls.Select(c => c.Path)); // datan är redan mottagen – bara bilden skickas
        Assert.Equal(1, report.ShotsUploaded);
        Assert.Equal(0, set.RetryAttempts);
    }

    [Fact]
    public async Task APostWithoutAFileWaitsAndIsGivenUpAfter24Hours()
    {
        // Posten togs en timme efter filerna i mappen: ingen fil stämmer.
        var (dir, _, _, _) = Game(Shot("senare01", T0 + 3600));
        var (up, srv, set, enc, _) = NewUploader(now: T0 + 3600 + 23 * 3600);
        await up.SyncAsync(dir, force: false);
        Assert.Empty(srv.Shots());
        Assert.Empty(enc.Seen);
        Assert.True(set.Shots["senare01"].Pending);

        up.ShotNow = () => DateTimeOffset.FromUnixTimeSeconds(T0 + 3600 + 24 * 3600 + 5);
        await up.SyncAsync(dir, force: false);
        Assert.Empty(srv.Shots());
        Assert.Equal("expired", set.Shots["senare01"].Status);
        Assert.Null(set.Shots["senare01"].File);

        // Dyker en fil upp efteråt prövas posten inte igen.
        File.WriteAllBytes(Path.Combine(dir.ScreenshotsDir, NameAt(T0 + 3600)), Original);
        await up.SyncAsync(dir, force: false);
        Assert.Empty(srv.Shots());
    }

    [Fact]
    public async Task AFileThatIsNotAPictureIsNeverSent()
    {
        var (dir, _, _, _) = Game();
        var (up, srv, set, enc, _) = NewUploader();
        enc.Undecodable = true;
        var report = await up.SyncAsync(dir, force: false);
        Assert.Null(report.Error);
        Assert.Empty(srv.Shots());
        Assert.True(set.Shots["abcd1234"].Pending);
    }

    [Fact]
    public async Task TooLargeIsRefusedBeforeSending()
    {
        var srv = new FakeServer();
        var api = new ApiClient("https://guide.wow4ever.se", srv) { Key = "k" };
        var ex = await Assert.ThrowsAsync<ApiException>(() => api.UploadShotAsync("{}", new byte[ApiClient.MaxShotBytes + 1]));
        Assert.Equal(ErrorCodes.ShotTooLarge, ex.Code);
        Assert.Empty(srv.Calls);
    }

    [Fact]
    public void TheQueueForgetsTheOldestFinishedPostsFirst()
    {
        var set = new Settings();
        for (int i = 0; i < Shots.MaxRemembered; i++)
        {
            var st = new ShotState { T = T0 + i };
            if (i > 0) st.Finish("published", null); // den allra äldsta väntar fortfarande
            set.Shots["x" + i.ToString("0000000")] = st;
        }
        Assert.Equal(2, Shots.Queue(set, [new ShotRecord("npc", 1, "A", T0 + 9000, null, null, null, "nyapost1"), new ShotRecord("npc", 2, "B", T0 + 9001, null, null, null, "nyapost2")], "1.0.20"));
        Assert.Equal(Shots.MaxRemembered, set.Shots.Count);
        Assert.True(set.Shots.ContainsKey("x0000000"));  // väntande glöms aldrig
        Assert.False(set.Shots.ContainsKey("x0000001")); // äldsta klara först
        Assert.False(set.Shots.ContainsKey("x0000002"));
        Assert.True(set.Shots["nyapost1"].Pending);
        // En känd kod läggs aldrig i kön igen.
        Assert.Equal(0, Shots.Queue(set, [new ShotRecord("npc", 1, "A", T0, null, null, null, "nyapost1")], "1.0.20"));
    }

    // ------------------------------------------------------------------ förminskningen (GDI+)

    private static byte[] Image(int w, int h, ImageFormat format, string? description = null)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(40, 90, 160));
            g.FillRectangle(Brushes.Gold, w / 4, h / 4, w / 2, h / 2);
        }
        if (description != null)
        {
            var item = (PropertyItem)RuntimeHelpers.GetUninitializedObject(typeof(PropertyItem));
            item.Id = 0x010E; // ImageDescription
            item.Type = 2;
            item.Value = Encoding.ASCII.GetBytes(description + "\0");
            item.Len = item.Value.Length;
            bmp.SetPropertyItem(item);
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, format);
        return ms.ToArray();
    }

    private static (int W, int H, bool Jpeg, int[] Props, Color Mid) Inspect(byte[] jpeg)
    {
        using var ms = new MemoryStream(jpeg);
        using var bmp = new Bitmap(ms);
        return (bmp.Width, bmp.Height, bmp.RawFormat.Guid == ImageFormat.Jpeg.Guid, bmp.PropertyIdList, bmp.GetPixel(bmp.Width / 2, bmp.Height / 2));
    }

    [Fact]
    public void LargeScreenshotIsScaledTo1600WideJpegWithoutMetadata()
    {
        var src = Image(3840, 2160, ImageFormat.Jpeg, description: "HEMLIG-METADATA");
        Assert.Contains("HEMLIG-METADATA", Encoding.Latin1.GetString(src));
        var copy = (byte[])src.Clone();
        var jpeg = new GdiShotEncoder().Encode(src, Shots.MaxWidth, Shots.Quality)!;
        var (w, h, isJpeg, props, mid) = Inspect(jpeg);
        Assert.Equal((1600, 1103, true), (w, h, isJpeg)); // mitten av 3840×2160: 1942×1339 → 1600 bred
        Assert.DoesNotContain(0x010E, props);
        Assert.DoesNotContain("HEMLIG-METADATA", Encoding.Latin1.GetString(jpeg));
        Assert.InRange(mid.R, 235, 255); // guld i mitten: bilden är rätt vänd och inte tom
        Assert.InRange(mid.B, 0, 30);
        Assert.True(jpeg.LongLength < ApiClient.MaxShotBytes);
        Assert.Equal(copy, src); // indata rörs inte
    }

    [Fact]
    public void SmallAndUltrawideScreenshotsKeepTheirShape()
    {
        var small = Inspect(new GdiShotEncoder().Encode(Image(1280, 720, ImageFormat.Png), 1600, 82)!);
        Assert.Equal((647, 446, true), (small.W, small.H, small.Jpeg)); // mitten, aldrig uppåt
        var wide = Inspect(new GdiShotEncoder().Encode(Image(3440, 1440, ImageFormat.Jpeg), 1600, 82)!);
        Assert.Equal((1295, 893), (wide.W, wide.H)); // ultrabred: samma utsnitt som 16:9
    }

    [Fact]
    public void GarbageIsNotAPicture()
    {
        Assert.Null(new GdiShotEncoder().Encode([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20], 1600, 82));
        Assert.Null(new GdiShotEncoder().Encode([], 1600, 82));
        Assert.Null(new GdiShotEncoder().Encode(Encoding.UTF8.GetBytes("<html>inte en bild</html>"), 1600, 82));
    }

    /// <summary>En TGA som spelet skriver den: 24 bitar, nedifrån och upp. Övre halvan röd, nedre blå.</summary>
    private static byte[] Tga(int w, int h, bool rle)
    {
        var ms = new MemoryStream();
        ms.Write([0, 0, (byte)(rle ? 10 : 2), 0, 0, 0, 0, 0, 0, 0, 0, 0, (byte)w, (byte)(w >> 8), (byte)h, (byte)(h >> 8), 24, 0]);
        for (int y = 0; y < h; y++)
        {
            byte[] bgr = y < h / 2 ? [255, 0, 0] : [0, 0, 255]; // filens första rader är bildens nedersta
            if (!rle) { for (int x = 0; x < w; x++) ms.Write(bgr); continue; }
            for (int left = w; left > 0;)
            {
                int n = Math.Min(128, left);
                if (left % 2 == 0) { ms.WriteByte((byte)(0x80 | (n - 1))); ms.Write(bgr); }       // upprepningspaket
                else { ms.WriteByte((byte)(n - 1)); for (int i = 0; i < n; i++) ms.Write(bgr); }   // råpaket
                left -= n;
            }
        }
        return ms.ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TgaScreenshotsAreDecoded(bool rle)
    {
        var jpeg = new GdiShotEncoder().Encode(Tga(2001, 1000, rle), 1600, 82);
        Assert.NotNull(jpeg);
        using var bmp = new Bitmap(new MemoryStream(jpeg!));
        Assert.Equal((899, 620), (bmp.Width, bmp.Height));
        var top = bmp.GetPixel(800, 100);
        var bottom = bmp.GetPixel(800, 600); // utsnittet är 899×620
        Assert.True(top.R > 200 && top.B < 50, top.ToString());
        Assert.True(bottom.B > 200 && bottom.R < 50, bottom.ToString());
        // Avhuggen fil: inte en bild.
        Assert.Null(new GdiShotEncoder().Encode(Tga(2001, 1000, rle)[..5000], 1600, 82));
    }
}
