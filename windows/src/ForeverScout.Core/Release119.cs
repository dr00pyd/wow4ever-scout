using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForeverScout.Core;

/// <summary>
/// 1.0.19: funktioner som guiden kan stänga av för alla klienter utan nytt klientsläpp – det signerade fältet
/// <c>off</c> i versionsinfon (<c>scout-sign --off shotlist,shotclean</c>, bara med <c>signatureAll</c>). En avstängd
/// funktion körs inte, oavsett inställningen; inställningen ändras inte och gäller igen när funktionen slås på.
/// </summary>
public static class Features
{
    /// <summary>Daglig bildlista till addonen (<see cref="ShotListFile"/>).</summary>
    public const string ShotList = "shotlist";
    /// <summary>Städning av skickade <c>/scout bild</c>-bilder (<see cref="Settings.CleanUploadedShots"/>).</summary>
    public const string ShotClean = "shotclean";
    public const string Shots = "shots";
    public const string Auctions = "auctions";
    public const string Hotfix = "hotfix";
    public const string Creatures = "creatures";

    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal) { ShotList, ShotClean, Shots, Auctions, Hotfix, Creatures };

    /// <summary>Är funktionen på (inte avstängd i den senaste godtagna versionsinfon)?</summary>
    public static bool On(Settings s, string name) => !s.FeaturesOff.Contains(name, StringComparer.Ordinal);

    /// <summary>
    /// Sparar de avstängda funktionerna ur en godtagen versionsinfo. Bara signerade fält räknas: utan
    /// <c>signatureAll</c> är allt på. Returnerar true om listan ändrades (anroparen sparar inställningarna).
    /// </summary>
    public static bool Apply(Settings s, SignedRelease r)
    {
        var off = r.SignedAll ? r.Off.Where(Known.Contains).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList() : new List<string>();
        if (off.SequenceEqual(s.FeaturesOff, StringComparer.Ordinal)) return false;
        s.FeaturesOff = off;
        return true;
    }

    /// <summary>"shotlist,shotclean" ur det signerade fältet – bara kända namn, aldrig fler än de kända.</summary>
    public static IReadOnlyList<string> Parse(string? off)
    {
        if (string.IsNullOrEmpty(off) || off.Length > 200) return [];
        return off.Split(',').Select(x => x.Trim()).Where(Known.Contains).Distinct(StringComparer.Ordinal).ToList();
    }
}

/// <summary>
/// 1.0.19: meddelande från guiden till just den här spelaren (fältet <c>message</c> i /api/bidrag/jag:
/// <c>{ id, text }</c>, skrivet med scout-admin). Visas en gång per id – i lägerjournalen och som en bubbla.
/// </summary>
public static class PlayerMessages
{
    public const int MaxText = 300;

    /// <summary>Texten om meddelandet är nytt (och minns att det visats – anroparen sparar), annars null.</summary>
    public static string? Take(Settings s, MeResult me)
    {
        if (me.Message is not { ValueKind: JsonValueKind.Object } m) return null;
        if (!m.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number || !idEl.TryGetInt64(out var id) || id <= 0) return null;
        if (!m.TryGetProperty("text", out var tEl) || tEl.ValueKind != JsonValueKind.String) return null;
        if (s.MessageSeen is long seen && id <= seen) return null;
        var text = Clean(tEl.GetString());
        if (text is null) return null;
        s.MessageSeen = id;
        return text;
    }

    /// <summary>En rad utan styrtecken, högst <see cref="MaxText"/> tecken; tomt = null.</summary>
    internal static string? Clean(string? raw)
    {
        if (raw is null) return null;
        var t = new string(raw.Select(ch => char.IsControl(ch) || ch is (char)0x2028 or (char)0x2029 ? ' ' : ch).ToArray()).Trim();
        if (t.Length == 0) return null;
        return t.Length > MaxText ? t[..MaxText] : t;
    }
}

/// <summary>
/// 1.0.19: den dagliga bildlistan. Addonen visar raden "Guiden saknar bild" för NPC:er som inte står i
/// <c>ShotList.lua</c>; förr byggdes listan bara när addonen släpptes och blev inaktuell. Nu hämtar klienten listan
/// (GET /api/scout/bildlista, bara NPC-id:n) högst en gång per <see cref="Every"/> och skriver om filen i addonens
/// egen mapp. Spelet läser den vid nästa inloggning eller /reload.
/// <list type="bullet">
/// <item>Bara när bildtipsen är på (addonen 1.0.30 speglar <c>/scout tips</c> i kontots fil, <see cref="Settings.ShotListWanted"/>)
/// och funktionen inte är avstängd (<see cref="Features.ShotList"/>).</item>
/// <item>Filen innehåller bara heltal och ett datum som klienten själv formaterar (<see cref="Render"/>) – ingen text från
/// servern hamnar i Lua-koden. Samma format som <c>scripts/build-addon-shotlist.mjs</c>.</item>
/// <item>När addonen uppdateras behålls en nyare lista än den som följer med addonen (<see cref="AddonInstaller.Install"/>).</item>
/// </list>
/// Storlek: 5 000 id ≈ 36 kB fil, ≈ 0,35 MB i spelet.
/// </summary>
public static partial class ShotListFile
{
    public const string FileName = "ShotList.lua";
    public static readonly TimeSpan Every = TimeSpan.FromHours(20);
    /// <summary>Fler id:n än så godtas inte (orimligt svar).</summary>
    public const int MaxIds = 50_000;
    private const int PerLine = 20;
    /// <summary>Första raden i en fil som klienten skrivit (den medskickade har en annan).</summary>
    internal const string Marker = "-- ForeverScout: daily picture list, written by the Forever Scout client - numbers only. Do not edit.";

    public sealed record Data(string Built, IReadOnlyList<long> Ids);

    public static string PathFor(GameDir dir) => Path.Combine(AddonInstaller.TargetDir(dir), FileName);

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex DateRx();

    /// <summary>Svaret från /api/bidrag/bildlista: <c>{ built, n, ids }</c>. Null om något är fel – då skrivs inget.</summary>
    public static Data? FromJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            if (!r.TryGetProperty("built", out var b) || b.ValueKind != JsonValueKind.String || b.GetString() is not { } built || !ValidDate(built)) return null;
            if (!r.TryGetProperty("ids", out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() > MaxIds) return null;
            var ids = new SortedSet<long>();
            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Number || !e.TryGetInt64(out var id) || id <= 0 || id > 99_999_999) return null;
                ids.Add(id);
            }
            return new Data(built, ids.ToList());
        }
        catch (JsonException) { return null; }
    }

    private static bool ValidDate(string s) =>
        DateRx().IsMatch(s) && DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    /// <summary>Filens innehåll – samma tabell som den medskickade listan, bara heltal och datumet.</summary>
    public static string Render(Data d)
    {
        var ids = d.Ids.Where(i => i > 0).Distinct().OrderBy(i => i).ToList();
        var sb = new StringBuilder();
        sb.Append(Marker).Append('\n');
        sb.Append("-- NPCs that already have a picture in the guide (published or awaiting review). Replaced about once a day.\n");
        sb.Append("ForeverScoutShotList = {\n");
        sb.Append("  built = \"").Append(ValidDate(d.Built) ? d.Built : "2000-01-01").Append("\",\n");
        sb.Append("  n = ").Append(ids.Count.ToString(CultureInfo.InvariantCulture)).Append(",\n");
        if (ids.Count == 0) sb.Append("  ids = \"\",\n");
        else
        {
            sb.Append("  ids = [[\n");
            for (int i = 0; i < ids.Count; i += PerLine)
            {
                sb.Append(string.Join(",", ids.Skip(i).Take(PerLine).Select(x => x.ToString(CultureInfo.InvariantCulture))));
                sb.Append(i + PerLine < ids.Count ? ",\n" : "\n");
            }
            sb.Append("]],\n");
        }
        sb.Append("}\n");
        return sb.ToString();
    }

    [GeneratedRegex(@"\A-- ForeverScout: daily picture list, written by the Forever Scout client - numbers only\. Do not edit\.\r?\n(?:--(?!\[)[^\r\n]*\r?\n)*ForeverScoutShotList = \{\r?\n  built = ""(\d{4}-\d{2}-\d{2})"",\r?\n  n = (\d{1,6}),\r?\n  ids = (?:""""|\[\[\r?\n([0-9,\r\n]*)\]\]),\r?\n\}\r?\n?\z")]
    private static partial Regex OwnRx();

    [GeneratedRegex(@"built = ""(\d{4}-\d{2}-\d{2})""")]
    private static partial Regex BuiltRx();

    /// <summary>
    /// En lista som klienten själv har skrivit. Bara exakt det formatet godtas (allt annat – den medskickade listan,
    /// en fil som någon ändrat – ger null). Ingen Lua tolkas.
    /// </summary>
    public static Data? ParseOwn(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 2_000_000) return null;
        var m = OwnRx().Match(text);
        if (!m.Success || !ValidDate(m.Groups[1].Value)) return null;
        var ids = new List<long>();
        foreach (var part in m.Groups[3].Value.Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0 || ids.Count >= MaxIds) return null;
            ids.Add(id);
        }
        return new Data(m.Groups[1].Value, ids);
    }

    /// <summary>Datumet (<c>built</c>) i en lista i addonens format, vem som än skrivit den; null om det saknas.</summary>
    public static string? BuiltOf(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var m = BuiltRx().Match(text.Length > 4000 ? text[..4000] : text);
        return m.Success && ValidDate(m.Groups[1].Value) ? m.Groups[1].Value : null;
    }

    /// <summary>Listan i den installerade addonen om klienten skrivit den, annars null.</summary>
    public static Data? ReadInstalledOwn(GameDir dir)
    {
        try
        {
            var path = PathFor(dir);
            if (!SafeFs.FileExists(path)) return null;
            return ParseOwn(Encoding.UTF8.GetString(SafeFs.ReadAllBytes(path, 2_000_000)).TrimStart((char)0xFEFF));
        }
        catch (Exception ex) when (ex is ScoutException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Ska listan hämtas nu? Addonen installerad, tipsen på, funktionen på och senaste hämtningen äldre än <see cref="Every"/>.</summary>
    public static bool Due(Settings s, bool addonInstalled, DateTime nowUtc) =>
        addonInstalled && s.ShotListWanted != false && Features.On(s, Features.ShotList)
        && (s.ShotListAtUtc is not { } at || nowUtc - at >= Every || at > nowUtc + TimeSpan.FromHours(1));

    /// <summary>
    /// Skriver listan i addonens mapp (bara om addonen är installerad). False om det inte gick; en lista som inte kan
    /// skrivas är aldrig ett fel för appen.
    /// </summary>
    public static bool Write(GameDir dir, Data d)
    {
        try
        {
            if (AddonInstaller.InstalledVersion(dir) is null) return false;
            SafeFs.WriteAllBytes(PathFor(dir), Encoding.ASCII.GetBytes(Render(d)));
            return true;
        }
        catch (Exception ex) when (ex is ScoutException or IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Hämtar och skriver listan om det är dags. Tiden sparas även när hämtningen misslyckas (nästa försök om
    /// <see cref="Every"/>), så att en server som inte svarar inte frågas var femtonde sekund. Returnerar antalet id:n
    /// som skrevs, eller null om inget skrevs.
    /// </summary>
    public static async Task<int?> RefreshAsync(ApiClient api, GameDir? dir, Settings s, DateTime nowUtc, CancellationToken ct = default)
    {
        if (dir is null || !Due(s, AddonInstaller.InstalledVersion(dir) is not null, nowUtc)) return null;
        s.ShotListAtUtc = nowUtc;
        try { s.Save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SafeFsException) { }
        Data? data;
        try { data = FromJson(await api.ShotListJsonAsync(ct)); }
        catch (ApiException ex)
        {
            UploadLog.Append("bildlista", 0, "", "error " + ex.Code);
            return null;
        }
        if (data is null) { UploadLog.Append("bildlista", 0, "", "error bad-response"); return null; }
        // En äldre lista än den som redan ligger där (t.ex. serverns klocka) skriver aldrig över den.
        try
        {
            var current = BuiltOf(Encoding.UTF8.GetString(SafeFs.ReadAllBytes(PathFor(dir), 2_000_000)));
            if (current is not null && string.CompareOrdinal(current, data.Built) > 0) return null;
        }
        catch (Exception ex) when (ex is ScoutException or IOException or UnauthorizedAccessException) { }
        if (!Write(dir, data)) return null;
        UploadLog.Append("bildlista", 0, "", $"ok n={data.Ids.Count} built={data.Built}");
        return data.Ids.Count;
    }
}

/// <summary>
/// 1.0.19: valfri städning (av som standard, <see cref="Settings.CleanUploadedShots"/>): en skärmbild tagen med
/// <c>/scout bild</c> raderas från datorn när servern har tagit emot den (published, queued eller duplicate). Bara
/// spelets egna filnamn i Screenshots, bara filen som posten tog, och bara medan inställningen är på
/// (<see cref="SafeFs.DeleteOwnScreenshot"/>). Avvisade och uppgivna bilder rörs aldrig.
/// </summary>
public static class ShotCleaner
{
    /// <summary>Utfall som betyder att servern har bilden.</summary>
    public static bool Received(string outcome) => outcome is "published" or "queued" or "duplicate" or "ok";

    /// <summary>Ska bilden raderas efter det här utfallet?</summary>
    public static bool ShouldClean(Settings s, string outcome) =>
        s.CleanUploadedShots && Features.On(s, Features.ShotClean) && Received(outcome);

    /// <summary>Raderar filen. False (och en rad i loggen) om det inte gick – aldrig ett fel för uppladdningen.</summary>
    public static bool Clean(string path, string code)
    {
        try
        {
            SafeFs.DeleteOwnScreenshot(path);
            UploadLog.Append("bild", 0, "", $"cleaned {code}");
            return true;
        }
        catch (Exception ex) when (ex is ScoutException or IOException or UnauthorizedAccessException)
        {
            UploadLog.Append("bild", 0, "", $"clean-failed {code} " + (ex is ScoutException se ? se.Code : ErrorCodes.Io));
            return false;
        }
    }
}

/// <summary>
/// 1.0.19: länken till felrapporten fyller i plattform och version (formuläret på /bidra läser
/// <c>?plattform=&amp;version=</c>, components/scout/BugReport.tsx) – spelaren behöver inte veta vilken variant den har.
/// </summary>
public static class BugLink
{
    /// <summary>Formulärets värde: store, lite, full, mac eller linux.</summary>
    public static string Platform => OperatingSystem.IsMacOS() ? "mac" : OperatingSystem.IsLinux() ? "linux" : BuildInfo.Variant;

    /// <summary>"?plattform=store&amp;version=1.0.19".</summary>
    public static string Query => "?plattform=" + Platform + "&version=" + BuildInfo.Version.ToString(3);
}

/// <summary>
/// 1.0.19: spelkatalogen som användaren har valt men som inte fanns när appen startade (en extern disk eller en
/// nätverksdisk som kommer upp senare, en Mac-volym som monteras efter inloggning). Förr väntade appen till nästa
/// omstart; nu prövas katalogen igen en gång i minuten.
/// </summary>
public static class GameDirRetry
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

    /// <summary>Ska den valda katalogen prövas igen nu?</summary>
    public static bool Due(Settings s, bool haveGameDir, DateTime lastTryUtc, DateTime nowUtc) =>
        !haveGameDir && s.IsGameDirConfirmed() && nowUtc - lastTryUtc >= Every;
}
