using System.Globalization;
using System.Text.RegularExpressions;

namespace ForeverScout.Core;

/// <summary>
/// 1.0.18: kvittot i spelet. Addonen kan bara läsa filer som spelet laddar vid inloggning och /reload, så efter varje
/// lyckad uppladdning skriver klienten en liten Lua-fil i addonens egen mapp
/// (<c>Interface\AddOns\ForeverScout\Receipt.lua</c>, listad i .toc-filerna före ForeverScout.lua). Addonen (1.0.26)
/// skriver då vid nästa inloggning en rad: "Senast skickat …".
/// <list type="bullet">
/// <item>Filen innehåller bara heltal och en tidsstämpel som klienten själv skapar (<see cref="Render"/>) – ingen text
/// från servern, addonens fil eller användaren hamnar någonsin i Lua-koden.</item>
/// <item>Den skrivs bara i addonens egen mapp i WoW-katalogen (SafeFs tillåter inget annat) och bara när addonen
/// redan är installerad där.</item>
/// <item>Addonen levereras med en tom standardfil. När addonen installeras eller uppdateras behålls ett befintligt
/// kvitto (<see cref="AddonInstaller.Install"/>): det läses med <see cref="Parse"/>, som bara godtar exakt det format
/// klienten skriver, och skrivs om från talen – standardfilen ersätter aldrig ett färskt kvitto.</item>
/// </list>
/// </summary>
public static partial class AddonReceipt
{
    public const string FileName = "Receipt.lua";
    /// <summary>Formatversion (fältet v). Addonen ignorerar ett kvitto med en version den inte känner till.</summary>
    public const int Format = 1;

    /// <param name="T">Unixtid (sekunder) när uppladdningen blev klar – klientens klocka.</param>
    /// <param name="Finds">Poster som servern tog emot i uppladdningen.</param>
    /// <param name="Shots">Bilder som servern tog emot i uppladdningen.</param>
    /// <param name="Pending">Poäng som väntar på att en spelare till bekräftar (null = servern skickar inte fältet).</param>
    /// <param name="PendingFinds">Fynd som väntar på bekräftelse (null = okänt).</param>
    public sealed record Data(long T, int Finds, int Shots, int? Pending, int? PendingFinds);

    public static string PathFor(GameDir dir) => Path.Combine(AddonInstaller.TargetDir(dir), FileName);

    /// <summary>Filens innehåll: två kommentarsrader och en tabell med bara heltal. Samma tal ger alltid samma byte.</summary>
    public static string Render(Data d)
    {
        static string N(long n) => Math.Clamp(n, 0, Overview.Max).ToString(CultureInfo.InvariantCulture);
        var sb = new System.Text.StringBuilder();
        sb.Append("-- ForeverScout: receipt for the latest upload. Written by the Forever Scout client after each upload -\n");
        sb.Append("-- numbers only. Do not edit; the client replaces this file.\n");
        sb.Append("ForeverScoutReceipt = { v = ").Append(Format)
          .Append(", t = ").Append(Math.Clamp(d.T, 0, 99_999_999_999L).ToString(CultureInfo.InvariantCulture))
          .Append(", finds = ").Append(N(d.Finds))
          .Append(", shots = ").Append(N(d.Shots));
        if (d.Pending is int p) sb.Append(", pending = ").Append(N(p));
        if (d.PendingFinds is int pf) sb.Append(", pendingFinds = ").Append(N(pf));
        sb.Append(" }\n");
        return sb.ToString();
    }

    [GeneratedRegex(@"\A(?:--(?!\[)[^\r\n]*\r?\n)*ForeverScoutReceipt = \{ v = 1, t = ([0-9]{1,11}), finds = ([0-9]{1,9}), shots = ([0-9]{1,9})(?:, pending = ([0-9]{1,9}))?(?:, pendingFinds = ([0-9]{1,9}))? \}\r?\n?\z")]
    private static partial Regex Rx();

    /// <summary>
    /// Läser ett kvitto som klienten själv har skrivit. Bara exakt det formatet godtas – allt annat (standardfilen, en
    /// fil någon ändrat för hand, en annan formatversion) ger null. Ingen Lua tolkas.
    /// </summary>
    public static Data? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 2000) return null;
        var m = Rx().Match(text);
        if (!m.Success) return null;
        static int? I(Group g) => g.Success ? int.Parse(g.Value, CultureInfo.InvariantCulture) : null;
        var t = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        if (t <= 0) return null;
        return new Data(t, I(m.Groups[2])!.Value, I(m.Groups[3])!.Value, I(m.Groups[4]), I(m.Groups[5]));
    }

    /// <summary>Kvittot i den installerade addonen, eller null (ingen addon, standardfilen, oläsbart).</summary>
    public static Data? ReadInstalled(GameDir dir)
    {
        try
        {
            var path = PathFor(dir);
            if (!SafeFs.FileExists(path)) return null;
            return Parse(System.Text.Encoding.UTF8.GetString(SafeFs.ReadAllBytes(path, 4096)).TrimStart((char)0xFEFF));
        }
        catch (Exception ex) when (ex is ScoutException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Skriver kvittot (atomärt: temporärfil + namnbyte) – bara om addonen är installerad. False om det inte gick;
    /// ett kvitto som inte kan skrivas är aldrig ett fel för uppladdningen.
    /// </summary>
    public static bool Write(GameDir dir, Data d)
    {
        try
        {
            if (AddonInstaller.InstalledVersion(dir) is null) return false;
            SafeFs.WriteAllBytes(PathFor(dir), System.Text.Encoding.ASCII.GetBytes(Render(d)));
            return true;
        }
        catch (Exception ex) when (ex is ScoutException or IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Efter en uppladdning: skriv kvittot om servern tog emot minst en fil eller bild. Poängen som väntar tas ur
    /// inställningarna (senaste svaret från /api/bidrag/jag – anropa efter att det har hämtats).
    /// </summary>
    public static bool AfterUpload(GameDir? dir, Settings s, SyncReport report, DateTimeOffset now)
    {
        if (dir is null || (report.FilesUploaded == 0 && report.ShotsUploaded == 0)) return false;
        return Write(dir, new Data(now.ToUnixTimeSeconds(), Overview.Clamp(report.RecordsAccepted), Overview.Clamp(report.ShotsUploaded),
            s.PendingPoints, s.PendingPoints is null ? null : s.PendingFinds));
    }
}
