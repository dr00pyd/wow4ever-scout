using System.Net;
using System.Text;
using ForeverScout.Core;
using ForeverScout.Tests;
using SkiaSharp;

namespace ForeverScout.Mac.Tests;

/// <summary>
/// 1.0.17: bilder tagna med /scout bild på macOS och Linux. Kodaren (SkiaShotEncoder – Skia, som Avalonia redan har
/// med sig) ska ge samma utsnitt och mått som Windows-klientens GdiShotEncoder (client/windows/tests/ShotTests.cs),
/// utan metadata, och hela kedjan (addonens post → spelets fil i Screenshots → förminskad JPEG → POST /api/bidrag/bild)
/// ska fungera med en spelkatalog i ett Wine-prefix. Körs på Windows, macOS och Linux – Skia finns för alla tre.
/// </summary>
public sealed class ShotEncoderTests : IDisposable
{
    private readonly DirectoryInfo _tmp = TestEnv.Temp("fs-skia-");
    /// <summary>Fast tidszon (UTC+1 utan sommartid), så att testet inte beror på datorns.</summary>
    private static readonly TimeZoneInfo Tz = TimeZoneInfo.CreateCustomTimeZone("scout-test", TimeSpan.FromHours(1), "scout-test", "scout-test");
    private const long T0 = 1794000000;

    public void Dispose()
    {
        SafeFs.SetGameDir(null);
        try { _tmp.Delete(true); } catch (IOException) { }
    }

    // ------------------------------------------------------------------ utsnittet (ren funktion)

    [Theory]
    // bredd, höjd → utsnitt (x, y, bredd, höjd) → färdig bild. Samma mått som GdiShotEncoder ger i ShotTests.cs.
    [InlineData(3840, 2160, 949, 246, 1942, 1339, 1600, 1103)] // 4K: mitten, förminskad till 1600
    [InlineData(1280, 720, 316, 82, 647, 446, 647, 446)]       // liten: mitten, aldrig uppåt
    [InlineData(3440, 1440, 1072, 164, 1295, 893, 1295, 893)]  // ultrabred: samma utsnitt som 16:9
    [InlineData(2001, 1000, 551, 114, 899, 620, 899, 620)]     // udda bredd (TGA-testet)
    [InlineData(1920, 1080, 474, 123, 972, 670, 972, 670)]
    [InlineData(7680, 4320, 1898, 493, 3883, 2678, 1600, 1103)] // 8K
    [InlineData(600, 1000, 0, 114, 600, 620, 600, 620)]         // smalare än utsnittet: hela bredden
    [InlineData(1, 1, 0, 0, 1, 1, 1, 1)]
    public void TheCropIsTheSameAsOnWindows(int sw, int sh, int x, int y, int cw, int ch, int w, int h)
    {
        Assert.Equal(new ShotCrop(x, y, cw, ch, w, h), ShotCrop.For(sw, sh, Shots.MaxWidth));
    }

    [Fact]
    public void TheCropNeverLeavesThePictureAndNeverScalesUp()
    {
        foreach (var (sw, sh) in new[] { (1024, 768), (1366, 768), (2560, 1080), (5120, 1440), (3840, 1600), (800, 600), (1080, 1920), (3, 2), (16384, 16384) })
            foreach (var max in new[] { 1600, 1, 100000 })
            {
                var c = ShotCrop.For(sw, sh, max);
                Assert.True(c.X >= 0 && c.Y >= 0 && c.Width >= 1 && c.Height >= 1, $"{sw}x{sh}");
                Assert.True(c.X + c.Width <= sw && c.Y + c.Height <= sh, $"{sw}x{sh}");
                Assert.True(c.OutWidth >= 1 && c.OutHeight >= 1 && c.OutWidth <= c.Width && c.OutWidth <= Math.Max(1, max), $"{sw}x{sh}");
                Assert.Equal((sw - c.Width) / 2, c.X); // vågrätt centrerat
                Assert.True(c.Y <= sh - c.Height - c.Y, $"{sw}x{sh}"); // lodrätt: ovanför mitten
            }
    }

    // ------------------------------------------------------------------ kodaren

    [Fact]
    public void TheNativeSkiaLibraryLoads()
    {
        // Kodaren svarar null om Skia inte går att ladda (bilden hoppas då över) – här ska felet synas med sin orsak.
        try
        {
            using var bmp = new SKBitmap(new SKImageInfo(2, 2, SKColorType.Rgba8888, SKAlphaType.Opaque));
            Assert.Equal(2, bmp.Width);
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or EntryPointNotFoundException or BadImageFormatException)
        {
            Assert.Fail("libSkiaSharp gick inte att ladda (i en Linux-container utan skrivbord: apt-get install -y libfontconfig1). " + ex.Message);
        }
    }

    /// <summary>Blå bild med en guldruta i mitten, som JPEG eller PNG.</summary>
    private static byte[] Image(int w, int h, SKEncodedImageFormat format)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bmp))
        using (var gold = new SKPaint { Color = new SKColor(255, 215, 0) })
        {
            canvas.Clear(new SKColor(40, 90, 160));
            canvas.DrawRect(SKRect.Create(w / 4, h / 4, w / 2, h / 2), gold);
        }
        using var data = bmp.Encode(format, 90);
        return data.ToArray();
    }

    /// <summary>Lägger en EXIF-bildbeskrivning (APP1) och en kommentar (COM) direkt efter JPEG-filens inledning.</summary>
    private static byte[] WithMetadata(byte[] jpeg, string description, string comment)
    {
        Assert.Equal((0xFF, 0xD8), (jpeg[0], jpeg[1]));
        var text = Encoding.ASCII.GetBytes(description + "\0");
        var tiff = new MemoryStream();
        tiff.Write([(byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0]);          // TIFF, little endian, första katalogen på 8
        tiff.Write([1, 0]);                                              // en post
        tiff.Write([0x0E, 0x01, 2, 0]);                                  // 0x010E ImageDescription, typ ASCII
        tiff.Write(BitConverter.GetBytes(text.Length));
        tiff.Write(BitConverter.GetBytes(26));                           // texten ligger efter katalogen
        tiff.Write([0, 0, 0, 0]);                                        // ingen fler katalog
        tiff.Write(text);
        byte[] exif = [.. "Exif\0\0"u8, .. tiff.ToArray()];
        var com = Encoding.ASCII.GetBytes(comment);
        var ms = new MemoryStream();
        ms.Write(jpeg, 0, 2);
        ms.Write([0xFF, 0xE1, (byte)((exif.Length + 2) >> 8), (byte)(exif.Length + 2)]);
        ms.Write(exif);
        ms.Write([0xFF, 0xFE, (byte)((com.Length + 2) >> 8), (byte)(com.Length + 2)]);
        ms.Write(com);
        ms.Write(jpeg, 2, jpeg.Length - 2);
        return ms.ToArray();
    }

    /// <summary>JPEG-filens segment fram till bilddatan (markörens andra byte).</summary>
    private static List<int> Segments(byte[] jpeg)
    {
        var list = new List<int>();
        int p = 2;
        while (p + 4 <= jpeg.Length && jpeg[p] == 0xFF)
        {
            int marker = jpeg[p + 1];
            list.Add(marker);
            if (marker == 0xDA) break; // bilddatan börjar
            p += 2 + (jpeg[p + 2] << 8 | jpeg[p + 3]);
        }
        return list;
    }

    private static (int W, int H, bool Jpeg, SKColor Mid) Inspect(byte[] jpeg)
    {
        using var data = SKData.CreateCopy(jpeg);
        using var codec = SKCodec.Create(data);
        Assert.NotNull(codec);
        using var bmp = SKBitmap.Decode(codec);
        return (bmp.Width, bmp.Height, codec.EncodedFormat == SKEncodedImageFormat.Jpeg, bmp.GetPixel(bmp.Width / 2, bmp.Height / 2));
    }

    [Fact]
    public void LargeScreenshotIsCroppedAndScaledTo1600WideJpegWithoutMetadata()
    {
        var src = WithMetadata(Image(3840, 2160, SKEncodedImageFormat.Jpeg), "HEMLIG-METADATA", "HEMLIG-KOMMENTAR");
        Assert.Contains("HEMLIG-METADATA", Encoding.Latin1.GetString(src));
        Assert.Contains(0xE1, Segments(src));
        var copy = (byte[])src.Clone();
        var jpeg = new SkiaShotEncoder().Encode(src, Shots.MaxWidth, Shots.Quality)!;
        var (w, h, isJpeg, mid) = Inspect(jpeg);
        Assert.Equal((1600, 1103, true), (w, h, isJpeg)); // mitten av 3840×2160: 1942×1339 → 1600 bred
        var text = Encoding.Latin1.GetString(jpeg);
        Assert.DoesNotContain("HEMLIG-METADATA", text);
        Assert.DoesNotContain("HEMLIG-KOMMENTAR", text);
        Assert.DoesNotContain("Exif", text);
        // Bara JFIF-huvudet (APP0) och det som behövs för att avkoda bilden: ingen EXIF/XMP (APP1), färgprofil (APP2),
        // övriga APP-segment eller kommentar (COM).
        Assert.All(Segments(jpeg), m => Assert.True(m is 0xE0 or 0xDB or 0xC0 or 0xC2 or 0xC4 or 0xDD or 0xDA, "segment 0x" + m.ToString("X2")));
        Assert.InRange(mid.Red, 235, 255); // guld i mitten: bilden är rätt vänd och inte tom
        Assert.InRange(mid.Blue, 0, 30);
        Assert.True(jpeg.LongLength < ApiClient.MaxShotBytes);
        Assert.Equal(copy, src); // indata rörs inte
    }

    [Fact]
    public void SmallAndUltrawideScreenshotsKeepTheirShape()
    {
        var small = Inspect(new SkiaShotEncoder().Encode(Image(1280, 720, SKEncodedImageFormat.Png), 1600, 82)!);
        Assert.Equal((647, 446, true), (small.W, small.H, small.Jpeg)); // mitten, aldrig uppåt
        var wide = Inspect(new SkiaShotEncoder().Encode(Image(3440, 1440, SKEncodedImageFormat.Jpeg), 1600, 82)!);
        Assert.Equal((1295, 893), (wide.W, wide.H)); // ultrabred: samma utsnitt som 16:9
        var huge = Inspect(new SkiaShotEncoder().Encode(Image(7680, 4320, SKEncodedImageFormat.Jpeg), 1600, 82)!);
        Assert.Equal((1600, 1103), (huge.W, huge.H)); // 8K: mer än en halvering (mipmappar)
        Assert.InRange(huge.Mid.Red, 235, 255);
    }

    [Fact]
    public void TheCropSitsAboveTheMiddle()
    {
        // Övre halvan röd, nedre blå, 1000 hög: utsnittet är raderna 114–733, så gränsen hamnar på rad 386 av 620.
        using var bmp = new SKBitmap(new SKImageInfo(2000, 1000, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bmp))
        using (var blue = new SKPaint { Color = new SKColor(0, 0, 255) })
        {
            canvas.Clear(new SKColor(255, 0, 0));
            canvas.DrawRect(SKRect.Create(0, 500, 2000, 500), blue);
        }
        using var png = bmp.Encode(SKEncodedImageFormat.Png, 100);
        using var data = SKData.CreateCopy(new SkiaShotEncoder().Encode(png.ToArray(), 1600, 95)!);
        using var result = SKBitmap.Decode(data);
        Assert.Equal((899, 620), (result.Width, result.Height));
        Assert.True(result.GetPixel(450, 375).Red > 200 && result.GetPixel(450, 375).Blue < 60);
        Assert.True(result.GetPixel(450, 397).Blue > 200 && result.GetPixel(450, 397).Red < 60);
    }

    [Fact]
    public void GarbageIsNotAPicture()
    {
        Assert.Null(new SkiaShotEncoder().Encode([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20], 1600, 82));
        Assert.Null(new SkiaShotEncoder().Encode([], 1600, 82));
        Assert.Null(new SkiaShotEncoder().Encode(Encoding.UTF8.GetBytes("<html>inte en bild</html>"), 1600, 82));
        Assert.Null(new SkiaShotEncoder().Encode(Image(1280, 720, SKEncodedImageFormat.Jpeg)[..300], 1600, 82)); // avhuggen JPEG
        // Andra bildformat än spelets (JPEG, PNG, TGA) tas inte emot, även om Skia kan läsa dem.
        Assert.Null(new SkiaShotEncoder().Encode(Image(1280, 720, SKEncodedImageFormat.Webp), 1600, 82));
    }

    /// <summary>En TGA som spelet skriver den: 24 bitar, nedifrån och upp. Övre halvan röd, nedre blå.</summary>
    private static byte[] Tga(int w, int h, bool rle, int bpp = 24, bool topDown = false)
    {
        var ms = new MemoryStream();
        ms.Write([0, 0, (byte)(rle ? 10 : 2), 0, 0, 0, 0, 0, 0, 0, 0, 0, (byte)w, (byte)(w >> 8), (byte)h, (byte)(h >> 8), (byte)bpp, (byte)(topDown ? 0x20 : 0)]);
        for (int y = 0; y < h; y++)
        {
            bool red = topDown ? y < h / 2 : y >= h / 2; // nedifrån och upp: filens första rader är bildens nedersta
            byte[] px = bpp == 32 ? (red ? [0, 0, 255, 0] : [255, 0, 0, 0]) : red ? [0, 0, 255] : [255, 0, 0]; // BGR(A), alfa 0 ska inte spela roll
            if (!rle) { for (int x = 0; x < w; x++) ms.Write(px); continue; }
            for (int left = w; left > 0;)
            {
                int n = Math.Min(128, left);
                if (left % 2 == 0) { ms.WriteByte((byte)(0x80 | (n - 1))); ms.Write(px); }        // upprepningspaket
                else { ms.WriteByte((byte)(n - 1)); for (int i = 0; i < n; i++) ms.Write(px); }   // råpaket
                left -= n;
            }
        }
        return ms.ToArray();
    }

    [Theory]
    [InlineData(false, 24, false)]
    [InlineData(true, 24, false)]
    [InlineData(false, 32, false)]
    [InlineData(true, 32, true)]
    public void TgaScreenshotsAreDecoded(bool rle, int bpp, bool topDown)
    {
        var jpeg = new SkiaShotEncoder().Encode(Tga(2001, 1000, rle, bpp, topDown), 1600, 82);
        Assert.NotNull(jpeg);
        using var data = SKData.CreateCopy(jpeg!);
        using var bmp = SKBitmap.Decode(data);
        Assert.Equal((899, 620), (bmp.Width, bmp.Height));
        var top = bmp.GetPixel(800, 100);
        var bottom = bmp.GetPixel(800, 600); // utsnittet är 899×620
        Assert.True(top.Red > 200 && top.Blue < 50, top.ToString());
        Assert.True(bottom.Blue > 200 && bottom.Red < 50, bottom.ToString());
        // Avhuggen fil: inte en bild.
        Assert.Null(new SkiaShotEncoder().Encode(Tga(2001, 1000, rle, bpp, topDown)[..5000], 1600, 82));
    }

    // ------------------------------------------------------------------ hela kedjan med den riktiga kodaren

    private static string NameAt(long t, string ext = ".jpg") =>
        "WoWScrnShot_" + TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeSeconds(t).UtcDateTime, Tz).ToString("MMddyy'_'HHmmss") + ext;

    private const string Lua = "\nForeverScoutDB = {\n[\"addon\"] = \"1.0.24\",\n[\"seq\"] = 7,\n[\"npcs\"] = {\n},\n[\"shots\"] = {\n"
        + "{\n[\"k\"] = \"npc\",\n[\"id\"] = 1234,\n[\"n\"] = \"Lars\",\n[\"t\"] = 1794000000,\n[\"m\"] = 1429,\n[\"x\"] = 43.21,\n[\"y\"] = 65.4,\n[\"c\"] = \"abcd1234\",\n}, -- [1]\n"
        + "},\n}\n";

    /// <summary>
    /// Spelkatalogen som den ligger på Linux (ett Wine-prefix, här Lutris: …/Games/battlenet/drive_c/Program Files (x86)/
    /// World of Warcraft/_forever_) med addonens fil (en post) och Screenshots: rätt bild och spelarens egen bild.
    /// </summary>
    private (GameDir Dir, string Match, string Own) Game(byte[] picture)
    {
        var game = Path.Combine(_tmp.FullName, "Games", "battlenet", "drive_c", "Program Files (x86)", "World of Warcraft", "_forever_");
        Directory.CreateDirectory(Path.Combine(game, "Interface", "AddOns"));
        var sv = Path.Combine(game, "WTF", "Account", "KONTO", "SavedVariables");
        Directory.CreateDirectory(sv);
        File.WriteAllText(Path.Combine(sv, "ForeverScout.lua"), Lua);
        var shots = Path.Combine(game, "Screenshots");
        Directory.CreateDirectory(shots);
        File.WriteAllBytes(Path.Combine(shots, NameAt(T0 + 1)), picture);
        File.WriteAllBytes(Path.Combine(shots, "semester.jpg"), picture); // spelarens egen bild
        var real = SafeFs.SetGameDir(game)!;
        return (new GameDir(real, "_forever_"), Path.Combine(real, "Screenshots", NameAt(T0 + 1)), Path.Combine(real, "Screenshots", "semester.jpg"));
    }

    private sealed class FakeServer : HttpMessageHandler
    {
        public List<(string Path, byte[] Body, string? Type)> Calls { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var body = req.Content is null ? [] : await req.Content.ReadAsByteArrayAsync(ct);
            var path = req.RequestUri!.AbsolutePath;
            lock (Calls) Calls.Add((path, body, req.Content?.Headers.ContentType?.MediaType));
            var json = path.EndsWith("/bild") ? """{"ok":true,"status":"published"}""" : "{}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public void OnlyTheGamesOwnScreenshotIsPreparedInAWinePrefix()
    {
        var picture = Image(1920, 1080, SKEncodedImageFormat.Jpeg);
        var (dir, match, own) = Game(picture);
        var file = Assert.Single(WowLocator.ScreenshotFiles(dir)); // inte semester.jpg
        Assert.Equal(match, file.Path);
        var img = Shots.Prepare(match, new SkiaShotEncoder());
        Assert.Equal(ApiClient.Sha256Hex(picture), img.Sha);
        Assert.Equal((972, 670, true), (Inspect(img.Jpeg!).W, Inspect(img.Jpeg!).H, Inspect(img.Jpeg!).Jpeg));
        Assert.Throws<SafeFsException>(() => Shots.Prepare(own, new SkiaShotEncoder()));
        Assert.Equal(picture, SafeFs.ReadAllBytes(match)); // originalet rörs inte
    }

    [Fact]
    public async Task ThePictureIsMatchedScaledAndSentWithTheRealEncoder()
    {
        var picture = WithMetadata(Image(2560, 1440, SKEncodedImageFormat.Jpeg), "HEMLIG-METADATA", "HEMLIG-KOMMENTAR");
        var (dir, match, _) = Game(picture);
        var srv = new FakeServer();
        using var api = new ApiClient("https://guide.wow4ever.se", srv) { Key = "k" };
        var set = Settings.Load();
        Assert.True(new Settings().UploadShots); // på som standard
        set.UploadHotfix = false;
        set.UploadCreatureCache = false;
        set.ShareAuctionPrices = false;
        set.UploadShots = true;
        set.Shots.Clear(); set.UploadedSha.Clear(); set.UploadedSeq.Clear(); set.BlockedAddons.Clear();
        set.RetryAttempts = 0; set.NextRetryUtc = null;
        var up = new Uploader(api, set) { ShotEncoder = new SkiaShotEncoder(), ShotTimeZone = Tz, ShotNow = () => DateTimeOffset.FromUnixTimeSeconds(T0 + 600) };

        var report = await up.SyncAsync(dir, force: false);
        Assert.Null(report.Error);
        Assert.Equal(1, report.ShotsUploaded);
        var call = Assert.Single(srv.Calls, c => c.Path == "/api/bidrag/bild");
        Assert.Equal("multipart/form-data", call.Type);
        var body = Encoding.Latin1.GetString(call.Body);
        Assert.Contains("\"c\":\"abcd1234\"", body);
        Assert.Contains("\"sha\":\"" + ApiClient.Sha256Hex(picture) + "\"", body); // originalfilens sha256
        Assert.DoesNotContain("HEMLIG-METADATA", body);
        Assert.DoesNotContain("HEMLIG-KOMMENTAR", body);
        // Filen i formuläret är den förminskade JPEG-bilden: utsnittet ur 2560×1440 är 1295×893.
        int start = body.IndexOf("ÿØÿ", StringComparison.Ordinal);
        Assert.True(start > 0);
        int end = body.LastIndexOf("ÿÙ", StringComparison.Ordinal);
        var sent = Inspect(call.Body[start..(end + 2)]);
        Assert.Equal((1295, 893, true), (sent.W, sent.H, sent.Jpeg));
        Assert.True(call.Body.Length < ApiClient.MaxShotBytes);
        Assert.Equal("published", set.Shots["abcd1234"].Status);
        Assert.Equal(picture, SafeFs.ReadAllBytes(match)); // originalet rörs inte

        // Andra gången finns inget kvar att skicka.
        await up.SyncAsync(dir, force: false);
        Assert.Single(srv.Calls, c => c.Path == "/api/bidrag/bild");
    }

    [Fact]
    public async Task SwitchedOffMeansScreenshotsIsNeverRead()
    {
        var (dir, _, _) = Game(Image(1280, 720, SKEncodedImageFormat.Jpeg));
        var srv = new FakeServer();
        using var api = new ApiClient("https://guide.wow4ever.se", srv) { Key = "k" };
        var set = Settings.Load();
        set.UploadHotfix = false;
        set.UploadCreatureCache = false;
        set.ShareAuctionPrices = false;
        set.UploadShots = false;
        set.Shots.Clear(); set.UploadedSha.Clear(); set.UploadedSeq.Clear(); set.BlockedAddons.Clear();
        set.RetryAttempts = 0; set.NextRetryUtc = null;
        var up = new Uploader(api, set) { ShotEncoder = new SkiaShotEncoder(), ShotTimeZone = Tz, ShotNow = () => DateTimeOffset.FromUnixTimeSeconds(T0 + 600) };
        var report = await up.SyncAsync(dir, force: false);
        Assert.Equal(0, report.ShotsUploaded);
        Assert.DoesNotContain(srv.Calls, c => c.Path == "/api/bidrag/bild");
        Assert.Empty(set.Shots);
        var preview = UploadPreview.Build(api, dir, set);
        Assert.Contains("/scout", preview);
        Assert.DoesNotContain("/api/bidrag/bild", preview);
    }

    // ------------------------------------------------------------------ inställningen och texterna

    [Fact]
    public void TheSettingUsesTheSameTextsAsWindows()
    {
        foreach (var k in new[] { "set.shots", "set.shots.hint", "journal.shots" })
        {
            Assert.True(Strings.All.TryGetValue(k, out var t), k);
            Assert.False(string.IsNullOrWhiteSpace(t.Sv));
            Assert.False(string.IsNullOrWhiteSpace(t.En));
            // Ingen egen mac.-/linux.-variant: Texts.T ger Core-texten, samma som i Windows-klienten.
            Assert.False(Strings.Has("mac." + k) || Strings.Has("linux." + k) || Texts.Local.ContainsKey("mac." + k) || Texts.Local.ContainsKey("linux." + k), k);
        }
        Assert.Equal(Strings.S("set.shots"), Texts.T("set.shots"));
    }

    [Fact]
    public void BothAppsUseTheSkiaEncoderAndShowTheSetting()
    {
        // Linux-appen bygger på samma källfiler (ForeverScout.Linux.csproj länkar in dem), så en koppling räcker.
        string? src = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null && src is null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "ForeverScout.Mac", "ScoutController.cs"))) src = Path.Combine(d.FullName, "ForeverScout.Mac");
        Assert.True(src != null, "hittar inte client/mac/ForeverScout.Mac");
        Assert.Contains("ShotEncoder = new SkiaShotEncoder()", File.ReadAllText(Path.Combine(src, "ScoutController.cs")));
        Assert.Contains("\"journal.shots\"", File.ReadAllText(Path.Combine(src, "ScoutController.cs")));
        var settings = File.ReadAllText(Path.Combine(src, "SettingsWindow.cs"));
        Assert.Contains("T(\"set.shots\")", settings);
        Assert.Contains("T(\"set.shots.hint\")", settings);
        Assert.Contains("_settings.UploadShots = _shots.IsChecked == true", settings);
    }
}
