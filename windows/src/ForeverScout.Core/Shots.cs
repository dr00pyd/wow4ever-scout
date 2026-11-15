using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ForeverScout.Core;

/// <summary>
/// 1.0.15: en post ur <c>ForeverScoutDB.shots</c> – addonen skriver en sådan varje gång spelaren tar en bild med
/// <c>/scout bild</c> (eller tangenten): vad bilden föreställer, när den togs och en slumpkod. Bara en bild som en
/// sådan post pekar ut laddas upp; spelarens övriga skärmbilder läses aldrig.
/// </summary>
/// <param name="Kind">"npc" eller "object".</param>
/// <param name="Id">NPC-id (krävs för npc) eller objektets id om spelet gav det.</param>
/// <param name="Time">Unix-tid (sekunder) när spelet meddelade att bilden sparats.</param>
/// <param name="Code">Slumpkod, 8 tecken a–z0–9 – postens nyckel hos både klienten och servern.</param>
public sealed record ShotRecord(string Kind, long? Id, string Name, long Time, long? Map, double? X, double? Y, string Code)
{
    /// <summary>Högst så många poster läses ur en fil (addonen sparar högst 100).</summary>
    public const int MaxPerFile = 100;

    /// <summary>Läser <c>shots</c> ur en parsad ForeverScoutDB. Trasiga poster hoppas över – aldrig ett undantag.</summary>
    public static List<ShotRecord> Read(LuaTable db)
    {
        var list = new List<ShotRecord>();
        if (db.Table("shots") is not { } shots) return list;
        foreach (var v in shots.Array())
        {
            if (list.Count >= MaxPerFile) break;
            if (v is not LuaTable t) continue;
            var kind = t.Str("k");
            var code = t.Str("c");
            if (kind is not ("npc" or "object") || code is null || !IsCode(code)) continue;
            if (t.Int("t") is not long time || time <= 0) continue;
            var id = t.Int("id");
            if (id is <= 0) id = null;
            if (kind == "npc" && id is null) continue;
            var name = (t.Str("n") ?? "").Trim();
            if (name.Length > 120) name = name[..120];
            list.Add(new ShotRecord(kind, id, name, time, t.Int("m"), t.Num("x"), t.Num("y"), code));
        }
        return list;
    }

    /// <summary>Exakt 8 tecken a–z0–9.</summary>
    public static bool IsCode(string code) => code.Length == 8 && code.All(ch => ch is (>= 'a' and <= 'z') or (>= '0' and <= '9'));
}

/// <summary>En fil i spelets Screenshots-katalog: bara namn och ändringstid (innehållet är inte läst).</summary>
public sealed record ShotFile(string Path, DateTime LastWriteUtc)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// Parar ihop addonens poster med spelets skärmbildsfiler. Ren funktion (testas utan filsystem), så att samma regler
/// kan användas av Mac- och Linuxklienterna:
/// <list type="bullet">
/// <item>Filnamnets tid (<c>WoWScrnShot_MMDDYY_HHMMSS</c>, lokal tid) inom ±3 s från postens tid – annars filens
/// ändringstid inom ±3 s.</item>
/// <item>Två poster får aldrig samma fil: närmast i tid vinner, och en fil som redan använts av en tidigare post
/// (<c>taken</c>) väljs aldrig.</item>
/// <item>Ingen träff = posten väntar; är den äldre än 24 timmar ges den upp (<see cref="Expired"/>).</item>
/// </list>
/// </summary>
public static class ShotMatcher
{
    public const int ToleranceSeconds = 3;
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromHours(24);

    /// <summary>Tiden i filnamnet (lokal tid, utan tidszon), eller null om namnet inte är spelets.</summary>
    public static DateTime? NameTime(string fileName)
    {
        if (!WowLocator.IsScreenshotName(fileName)) return null;
        var stamp = fileName.Substring(WowLocator.ScreenshotPrefix.Length, 13).Replace("_", ""); // MMDDYY + HHMMSS
        return DateTime.TryParseExact(stamp, "MMddyyHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : null;
    }

    /// <summary>Postens kod → filens sökväg, för de poster som fick en fil.</summary>
    /// <param name="posts">Poster som väntar (kod och unix-tid).</param>
    /// <param name="files">Filerna i Screenshots (<see cref="WowLocator.ScreenshotFiles"/>).</param>
    /// <param name="tz">Tidszonen som spelet namnger filerna i (datorns).</param>
    /// <param name="taken">Filnamn som tidigare poster redan har tagit.</param>
    public static Dictionary<string, string> Match(IEnumerable<(string Code, long Time)> posts, IEnumerable<ShotFile> files,
        TimeZoneInfo tz, IReadOnlySet<string>? taken = null)
    {
        var free = files.Where(f => WowLocator.IsScreenshotName(f.Name) && taken?.Contains(f.Name) != true).ToList();
        var pairs = new List<(int Tier, double Diff, long Time, string Code, ShotFile File)>();
        foreach (var (code, time) in posts)
        {
            DateTime utc;
            try { utc = DateTimeOffset.FromUnixTimeSeconds(time).UtcDateTime; }
            catch (ArgumentOutOfRangeException) { continue; }
            var local = TimeZoneInfo.ConvertTimeFromUtc(utc, tz);
            foreach (var f in free)
            {
                if (NameTime(f.Name) is { } nt && Math.Abs((nt - local).TotalSeconds) <= ToleranceSeconds)
                    pairs.Add((0, Math.Abs((nt - local).TotalSeconds), time, code, f));
                else if (Math.Abs((f.LastWriteUtc - utc).TotalSeconds) <= ToleranceSeconds)
                    pairs.Add((1, Math.Abs((f.LastWriteUtc - utc).TotalSeconds), time, code, f));
            }
        }
        // Filnamnsträffar före ändringstid, sedan närmast i tid; resten (tid, kod, namn) bara för att utfallet ska vara stabilt.
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in pairs.OrderBy(p => p.Tier).ThenBy(p => p.Diff).ThenBy(p => p.Time)
                     .ThenBy(p => p.Code, StringComparer.Ordinal).ThenBy(p => p.File.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (result.ContainsKey(p.Code) || used.Contains(p.File.Path)) continue;
            used.Add(p.File.Path);
            result[p.Code] = p.File.Path;
        }
        return result;
    }

    /// <summary>Är posten äldre än 24 timmar? Då ges den upp i stället för att prövas i evighet.</summary>
    public static bool Expired(long time, DateTimeOffset now) => now.ToUnixTimeSeconds() - time > (long)GiveUpAfter.TotalSeconds;
}

/// <summary>
/// Klientens minne av en bildpost (settings.json, nyckel = postens kod): väntar den, eller är den klar? En klar post
/// behåller bara tiden, utfallet och filnamnet den tog (så att ingen annan post kan ta samma fil).
/// </summary>
public sealed class ShotState
{
    public const string PendingStatus = "pending";

    public string K { get; set; } = "npc";
    public long? Id { get; set; }
    public string? N { get; set; }
    public long T { get; set; }
    public long? M { get; set; }
    public double? X { get; set; }
    public double? Y { get; set; }
    /// <summary>Addonversionen i filen som posten lästes ur.</summary>
    public string? Addon { get; set; }
    /// <summary>"pending", eller utfallet: published, queued, duplicate, rejected:&lt;kod&gt;, expired.</summary>
    public string Status { get; set; } = PendingStatus;
    /// <summary>Filnamnet (utan sökväg) som posten tog.</summary>
    public string? File { get; set; }

    [System.Text.Json.Serialization.JsonIgnore] public bool Pending => Status == PendingStatus;

    /// <summary>Posten är klar (skickad, avvisad eller uppgiven): bara tid, utfall och filnamn sparas.</summary>
    public void Finish(string status, string? fileName)
    {
        Status = status;
        File = fileName;
        N = null; Id = null; M = null; X = null; Y = null; Addon = null;
    }

    /// <summary>
    /// Fältet <c>meta</c> till POST /api/bidrag/bild: <c>{ k, id, n, t, m, x, y, c, addon, sha }</c>. Fält som addonen
    /// inte hade (objekt utan id, ingen kartposition) utelämnas.
    /// </summary>
    /// <param name="sha">SHA-256 (hex) av den ursprungliga filen.</param>
    public string MetaJson(string code, string sha)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("k", K);
            if (Id is long id) w.WriteNumber("id", id);
            w.WriteString("n", N ?? "");
            w.WriteNumber("t", T);
            if (M is long m) w.WriteNumber("m", m);
            if (X is double x && double.IsFinite(x)) w.WriteNumber("x", Math.Round(x, 2));
            if (Y is double y && double.IsFinite(y)) w.WriteNumber("y", Math.Round(y, 2));
            w.WriteString("c", code);
            w.WriteString("addon", Addon ?? "");
            w.WriteString("sha", sha);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}

/// <summary>
/// Förminskar en skärmbild. Plattformens egen bildkod (Windows: GDI+ via System.Drawing, som WinForms redan laddar) –
/// Core har inga bildberoenden. Mac/Linux: ingen kodare än, och då skickas inga bilder.
/// </summary>
public interface IShotEncoder
{
    /// <summary>
    /// Avkodar bilden, förminskar den till högst <paramref name="maxWidth"/> px bredd (aldrig uppåt) och kodar den som
    /// JPEG utan metadata. Null om filen inte går att avkoda som bild.
    /// </summary>
    byte[]? Encode(byte[] original, int maxWidth, int quality);
}

/// <summary>Kön av bildposter och arbetet med en bild (läsa, hasha, förminska).</summary>
public static class Shots
{
    public const int MaxWidth = 1600;
    public const int Quality = 82;
    /// <summary>Blir bilden ändå större än serverns gräns provas lägre kvalitet, i den här ordningen.</summary>
    private static readonly int[] Qualities = [Quality, 70, 55];
    /// <summary>Största originalfil som läses (en okomprimerad TGA i 4K är ca 33 MB).</summary>
    public const long MaxSourceBytes = 48L * 1024 * 1024;
    /// <summary>Så många poster minns klienten (addonen sparar högst 100 per konto).</summary>
    public const int MaxRemembered = 300;

    /// <summary>Lägger nya poster i kön. En kod som klienten redan känner (väntande eller klar) rörs inte.</summary>
    /// <returns>Antal nya poster.</returns>
    public static int Queue(Settings settings, IEnumerable<ShotRecord>? records, string? addonVersion)
    {
        if (records is null) return 0;
        int added = 0;
        foreach (var r in records)
        {
            if (settings.Shots.ContainsKey(r.Code)) continue;
            settings.Shots[r.Code] = new ShotState { K = r.Kind, Id = r.Id, N = r.Name, T = r.Time, M = r.Map, X = r.X, Y = r.Y, Addon = addonVersion };
            added++;
        }
        if (added > 0) Prune(settings);
        return added;
    }

    /// <summary>Håller minnet litet: de äldsta klara posterna glöms först (väntande poster glöms aldrig här).</summary>
    public static void Prune(Settings settings)
    {
        int over = settings.Shots.Count - MaxRemembered;
        if (over <= 0) return;
        foreach (var code in settings.Shots.Where(p => !p.Value.Pending).OrderBy(p => p.Value.T).Take(over).Select(p => p.Key).ToList())
            settings.Shots.Remove(code);
    }

    /// <summary>En bild klar att skickas: originalets sha256 och den förminskade JPEG-filen (null = gick inte att avkoda eller få liten nog).</summary>
    public sealed record Prepared(string Sha, long SourceBytes, byte[]? Jpeg);

    /// <summary>
    /// Läser originalfilen (via <see cref="SafeFs"/>, som bara släpper igenom spelets egna skärmbildsnamn), hashar den
    /// och förminskar den. Originalfilen rörs aldrig.
    /// </summary>
    public static Prepared Prepare(string path, IShotEncoder encoder)
    {
        var bytes = SafeFs.ReadAllBytes(path, MaxSourceBytes);
        var sha = ApiClient.Sha256Hex(bytes);
        foreach (var q in Qualities)
        {
            var jpeg = encoder.Encode(bytes, MaxWidth, q);
            if (jpeg is null) break;
            if (jpeg.LongLength <= ApiClient.MaxShotBytes) return new Prepared(sha, bytes.LongLength, jpeg);
        }
        return new Prepared(sha, bytes.LongLength, null);
    }

    /// <summary>
    /// Kör arbetet på en egen bakgrundstråd med lägsta prioritet, så att spelet aldrig får vänta på bildförminskningen.
    /// Anroparen väntar på resultatet innan nästa bild startas – en bild i taget.
    /// </summary>
    public static Task<T> RunLowPriority<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        })
        { IsBackground = true, Priority = ThreadPriority.Lowest, Name = "scout-shot" };
        thread.Start();
        return tcs.Task;
    }
}
