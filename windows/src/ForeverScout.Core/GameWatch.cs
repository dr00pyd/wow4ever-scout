namespace ForeverScout.Core;

/// <summary>Ett besked som fönstret visar överst tills det inte längre gäller (redan översatt).</summary>
/// <param name="Warning">Varning (något fungerar inte) i stället för en upplysning.</param>
public sealed record Notice(string Title, string? Body, bool Warning);

/// <summary>
/// 1.0.18: påminnelsen om <c>/reload</c>. När klienten byter addonfilerna medan spelet kör använder spelet de gamla
/// filerna tills spelaren skriver /reload eller startar om. Appen listar inga processer (se README, "Vad appen läser")
/// och kan därför inte veta om spelet kör just då – påminnelsen sätts vid varje byte av en redan installerad addon och
/// säger själv att inget behöver göras om spelet inte är igång. Den tas bort när
/// <list type="bullet">
/// <item>en uppladdning kommer från den nya addonversionen (eller en nyare),</item>
/// <item>spelet har sparat efter bytet (en sparning sker vid /reload, utloggning och avslut – vad som än följer på den
/// läser spelet in de nya filerna), eller</item>
/// <item>ett dygn har gått.</item>
/// </list>
/// </summary>
public static class ReloadReminder
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);
    /// <summary>En sparning räknas som "efter bytet" först så här långt efter det (filtider har grov upplösning på vissa diskar).</summary>
    public static readonly TimeSpan Margin = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Anropas efter att addonen har installerats. Bara ett byte av en redan installerad addon ger en påminnelse – en
    /// första installation kräver att spelet startas om, och det säger installationsbeskedet redan.
    /// </summary>
    public static bool OnInstalled(Settings s, string? previous, string? installed, DateTime nowUtc)
    {
        if (previous is null || installed is null || AddonInstaller.CompareVersions(installed, previous) == 0) return false;
        s.AddonReloadVersion = installed;
        s.AddonReloadSinceUtc = nowUtc;
        return true;
    }

    /// <summary>Versionen som väntar på /reload, eller null.</summary>
    public static string? Pending(Settings s, DateTime nowUtc) =>
        s.AddonReloadVersion is { Length: > 0 } v && s.AddonReloadSinceUtc is { } since && nowUtc - since < MaxAge ? v : null;

    /// <summary>En fil har laddats upp: kommer den från den nya addonversionen (eller en nyare) har spelet laddat den.</summary>
    public static bool OnUploaded(Settings s, string? addonInFile)
    {
        if (s.AddonReloadVersion is not { } v || addonInFile is null) return false;
        if (AddonInstaller.CompareVersions(addonInFile, v) < 0) return false;
        return Clear(s);
    }

    /// <summary>Spelet har sparat (SavedVariables) efter bytet: nästa gång gränssnittet laddas är det med de nya filerna.</summary>
    public static bool OnGameSaved(Settings s, DateTime? newestSaveUtc)
    {
        if (s.AddonReloadVersion is null || s.AddonReloadSinceUtc is not { } since || newestSaveUtc is not { } save) return false;
        return save > since + Margin && Clear(s);
    }

    /// <summary>Tar bort en påminnelse som är äldre än ett dygn (eller saknar tid).</summary>
    public static bool Expire(Settings s, DateTime nowUtc)
    {
        if (s.AddonReloadVersion is null && s.AddonReloadSinceUtc is null) return false;
        return Pending(s, nowUtc) is null && Clear(s);
    }

    public static bool Clear(Settings s)
    {
        bool had = s.AddonReloadVersion is not null || s.AddonReloadSinceUtc is not null;
        s.AddonReloadVersion = null;
        s.AddonReloadSinceUtc = null;
        return had;
    }
}

/// <summary>
/// 1.0.18: "spelet sparade, men addonen sparade ingenting". Spelet skriver alla laddade addons filer i
/// <c>WTF\Account\&lt;konto&gt;\SavedVariables</c> vid /reload, utloggning och avslut, och då ändras mappens ändringstid.
/// Har mappen ändrats men <c>ForeverScout.lua</c> inte skrivits i samma veva (eller saknas) var addonen inte laddad:
/// inte ikryssad, installerad medan spelet var igång, eller så är det inte den här katalogen spelaren spelar från.
/// <para>Bara två saker läses, och båda får appen redan läsa: mappens ändringstid (inget i mappen öppnas, inga andra
/// filnamn läses) och ändringstiden för ForeverScout.lua. Inga processer listas. Det som inte går att se så är ett spel
/// som körs utan att ha sparat alls, och ett spel som sparar i en helt annan katalog än den valda.</para>
/// Högst en varning per spelsession: efter en varning är det tyst i <see cref="Quiet"/>, och sparningar under den tiden
/// ger ingen ny varning senare.
/// </summary>
public static class SilentSession
{
    /// <summary>Spelet får så här lång tid på sig att skriva klart alla filer innan mappen bedöms.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(90);
    /// <summary>Så långt före mappens ändringstid får ForeverScout.lua vara skriven och ändå räknas som samma sparning.</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(3);
    /// <summary>Efter en varning: ingen ny på så här länge ("högst en gång per spelsession").</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromHours(12);
    /// <summary>En sparning som är äldre än så här varnar appen inte om (den var inte igång när det hände).</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    /// <summary>En kontokatalog: när spelet senast skrev i SavedVariables-mappen och när ForeverScout.lua senast skrevs.</summary>
    public readonly record struct SavedState(DateTime? FolderUtc, DateTime? FileUtc);

    /// <summary>Läser tiderna för varje kontokatalog (via SafeFs). Kontonamnet används aldrig.</summary>
    public static List<SavedState> Read(GameDir dir)
    {
        var list = new List<SavedState>();
        try
        {
            foreach (var acc in SafeFs.GetDirectories(dir.AccountDir))
            {
                var sv = Path.Combine(acc, "SavedVariables");
                DateTime? folder = null, file = null;
                try
                {
                    var f = SafeFs.DirectoryWriteTimeUtc(sv);
                    file = SafeFs.Stat(Path.Combine(sv, "ForeverScout.lua"))?.LastWriteUtc;
                    folder = f; // först när filen gått att fråga om: en länkad fil (nekas) ska inte se ut som "sparade inte"
                }
                catch (SafeFsException) { file = null; }
                if (folder is not null || file is not null) list.Add(new SavedState(folder, file));
            }
        }
        catch (SafeFsException) { }
        return list;
    }

    /// <summary>Den senaste sparningen över alla konton (mappen eller filen), eller null.</summary>
    public static DateTime? Newest(IReadOnlyList<SavedState> accounts)
    {
        DateTime? newest = null;
        foreach (var a in accounts)
            foreach (var t in new[] { a.FolderUtc, a.FileUtc })
                if (t is { } v && (newest is null || v > newest)) newest = v;
        return newest;
    }

    /// <summary>Räkningen börjar när funktionen först körs och börjar om när spelkatalogen byts – äldre sparningar räknas aldrig.</summary>
    public static bool EnsureBaseline(Settings s, GameDir dir, DateTime nowUtc)
    {
        if (s.SilentSinceUtc is not null && string.Equals(s.SilentDir, dir.Path, StringComparison.OrdinalIgnoreCase)) return false;
        s.SilentSinceUtc = nowUtc;
        s.SilentDir = dir.Path;
        return true;
    }

    /// <summary>
    /// Den senaste sparningen där spelet skrev i mappen men addonen inte skrev sin fil – och som appen inte redan har
    /// bedömt. Null om allt ser ut som det ska.
    /// </summary>
    public static DateTime? SilentSave(IReadOnlyList<SavedState> accounts, Settings s, DateTime nowUtc)
    {
        if (s.SilentSinceUtc is not { } since) return null;
        DateTime? hit = null;
        foreach (var a in accounts)
        {
            if (a.FolderUtc is not { } folder || folder <= since) continue;
            var age = nowUtc - folder;
            if (age < Settle || age > MaxAge) continue;                          // skrivs kanske fortfarande, eller för gammal
            if (a.FileUtc is { } file && folder - file <= Tolerance) continue;   // addonen sparade i samma veva
            if (s.SilentSaveUtc is { } done && folder <= done) continue;         // redan bedömd
            if (hit is null || folder > hit) hit = folder;
        }
        return hit;
    }

    /// <summary>
    /// Bedömer läget och säger om en ny varning ska visas nu. En tyst sparning under <see cref="Quiet"/> efter en
    /// varning noteras bara (så att den inte ger en varning i efterhand).
    /// </summary>
    public static bool Evaluate(IReadOnlyList<SavedState> accounts, Settings s, DateTime nowUtc, out bool changed)
    {
        changed = false;
        if (SilentSave(accounts, s, nowUtc) is not { } save) return false;
        changed = true;
        s.SilentSaveUtc = save;
        if (s.SilentWarnedUtc is { } warned && nowUtc - warned < Quiet && nowUtc >= warned) return false;
        s.SilentWarnedUtc = nowUtc;
        return true;
    }

    /// <summary>
    /// Gäller varningen fortfarande? Ja tills addonen har sparat efter den tysta sparningen, eller tills
    /// <see cref="Quiet"/> har gått.
    /// </summary>
    public static bool Active(IReadOnlyList<SavedState> accounts, Settings s, DateTime nowUtc)
    {
        if (s.SilentWarnedUtc is not { } warned || s.SilentSaveUtc is not { } save) return false;
        if (nowUtc - warned >= Quiet || nowUtc < warned) return false;
        return !accounts.Any(a => a.FileUtc is { } file && file > save);
    }
}

/// <summary>
/// 1.0.18: det appen ser av spelet genom filerna – en gång per tick (var 15:e sekund), samma kod för Windows, macOS
/// och Linux. Sköter påminnelsen om /reload och varningen när spelet sparar utan att addonen gör det, och ger fönstret
/// det besked som gäller just nu.
/// </summary>
public static class GameWatch
{
    /// <param name="Notice">Beskedet som ska stå i fönstret (null = inget).</param>
    /// <param name="NewSilentWarning">En ny varning: visa den en gång som bubbla och rad i lägerjournalen.</param>
    public readonly record struct Result(Notice? Notice, bool NewSilentWarning);

    public static Result Tick(GameDir? dir, Settings s, DateTime nowUtc)
    {
        bool changed = ReloadReminder.Expire(s, nowUtc);
        bool warn = false, silent = false;
        if (dir is not null)
        {
            changed |= SilentSession.EnsureBaseline(s, dir, nowUtc);
            List<SilentSession.SavedState> accounts;
            try { accounts = SilentSession.Read(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { accounts = []; }
            changed |= ReloadReminder.OnGameSaved(s, SilentSession.Newest(accounts));
            if (SilentSession.SilentSave(accounts, s, nowUtc) is { } save)
            {
                // Addonens .toc läses bara när det finns något att bedöma (inte vid varje tick – appen ligger i fältet i dagar).
                if (AddonInstaller.InstalledVersion(dir) is not null)
                {
                    warn = SilentSession.Evaluate(accounts, s, nowUtc, out var c);
                    changed |= c;
                }
                else
                {
                    // Utan installerad addon finns redan ett tydligare besked ("Addonen saknas"): sparningen noteras bara,
                    // så att den inte ger en varning i efterhand.
                    s.SilentSaveUtc = save;
                    changed = true;
                }
            }
            silent = SilentSession.Active(accounts, s, nowUtc);
        }
        if (changed)
        {
            try { s.Save(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SafeFsException) { }
        }
        return new Result(Current(s, silent, nowUtc), warn);
    }

    /// <summary>Beskedet som gäller: påminnelsen om /reload går före varningen (den säger vad som ska göras nu).</summary>
    public static Notice? Current(Settings s, bool silentActive, DateTime nowUtc)
    {
        if (ReloadReminder.Pending(s, nowUtc) is { } v)
            return new Notice(Strings.F("reload.notice", v), Strings.S("reload.notice.body"), Warning: false);
        if (silentActive) return new Notice(Strings.S("silent.title"), Strings.S("silent.body"), Warning: true);
        return null;
    }
}
