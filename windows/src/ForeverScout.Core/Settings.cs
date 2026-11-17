using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForeverScout.Core;

public sealed class ConfirmedItem
{
    public string Kind { get; set; } = "";
    /// <summary>Serverns id (sträng i API:t, t.ex. "198").</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Relativ sökväg på sajten, eller null om posten inte har en sida.</summary>
    public string? Url { get; set; }

    /// <summary>Så många senaste fynd som "Mitt bidrag" visar – fler rymdes inte i rutan (2026-11-12: tredje raden
    /// klipptes).</summary>
    public const int Shown = 2;

    /// <summary>
    /// De senaste fynden att visa: samma namn bara en gång (en uppladdning kan bekräfta t.ex. ett quest och föremålet
    /// med samma namn – "Contingency Plan" stod tre gånger), högst <see cref="Shown"/>.
    /// </summary>
    public static List<ConfirmedItem> ForDisplay(IEnumerable<ConfirmedItem> items, int max = Shown)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<ConfirmedItem>();
        foreach (var it in items)
        {
            var name = (it.Name ?? "").Trim();
            if (name.Length == 0 || !seen.Add(name)) continue;
            list.Add(it);
            if (list.Count >= max) break;
        }
        return list;
    }
}

/// <summary>Inställningar och tillstånd i %APPDATA%\ForeverScout\settings.json (nyckeln ligger separat, krypterad).</summary>
public sealed class Settings
{
    public string? GameDir { get; set; }
    /// <summary>"auto", "sv" eller "en".</summary>
    public string Language { get; set; } = "auto";
    public bool AutoUpload { get; set; } = true;
    public bool UploadHotfix { get; set; } = true;
    /// <summary>1.0.14: "Skicka varelsecachen (creaturecache.wdb)" – på som standard, som hotfix-cachen.</summary>
    public bool UploadCreatureCache { get; set; } = true;
    /// <summary>1.0.15: "Ladda upp bilder tagna med /scout bild" – på som standard. Av: Screenshots listas och läses inte.</summary>
    public bool UploadShots { get; set; } = true;
    /// <summary>
    /// 1.0.15: bildposter ur addonens fil (nyckel = postens kod c): väntande och klara. En kod som finns här läggs aldrig
    /// i kön igen. Inga sökvägar – bara filnamnet (WoWScrnShot_…) som posten tog.
    /// </summary>
    public Dictionary<string, ShotState> Shots { get; set; } = new(StringComparer.Ordinal);
    public string? ContributorName { get; set; }
    public string? ContributorId { get; set; }

    // Nycklarna nedan är Privacy.FileKey(sökväg) – aldrig sökvägen, eftersom den innehåller kontonamnet.
    /// <summary>Senast uppladdade seq per SavedVariables-fil.</summary>
    public Dictionary<string, long> UploadedSeq { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>SHA-256 för senast uppladdade innehåll per SavedVariables-fil.</summary>
    public Dictionary<string, string> UploadedSha { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>SHA-256 för senast skickade DBCache.bin per fil.</summary>
    public Dictionary<string, string> HotfixSha { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>1.0.14: SHA-256 för senast skickade creaturecache.wdb per fil.</summary>
    public Dictionary<string, string> CreatureCacheSha { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// "Dela auktionspriser (Auctionator/Auctioneer)" – av som standard. Bara när den är på får SafeFs läsa
    /// Auctionator.lua/Auc-Stat-Simple.lua, och då skickas bara föremål, realm/fraktion, dag, lägsta pris och antal.
    /// </summary>
    public bool ShareAuctionPrices { get; set; }
    /// <summary>SHA-256 för senast hanterade auktionsfil (nyckel = Privacy.FileKey(sökväg)) – oförändrad fil läses inte om.</summary>
    public Dictionary<string, string> AuctionSha { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Vattenmärke per auktionsfil: senaste dag ("yyyy-MM-dd") som skickats. Nästa gång skickas den dagen och nyare.</summary>
    public Dictionary<string, string> AuctionDay { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Offline-kö: antal misslyckade försök i rad och när nästa försök görs (null = inget väntar).</summary>
    public int RetryAttempts { get; set; }
    public DateTime? NextRetryUtc { get; set; }

    /// <summary>"Pausa automatisk uppladdning" i fältmenyn (tillfälligt, utan att ändra AutoUpload).</summary>
    public bool AutoUploadPaused { get; set; }
    /// <summary>Stäng-dialogen: fråga inte igen, och vad som då ska hända ("exit" eller "tray").</summary>
    public bool DontAskOnClose { get; set; }
    public string CloseAction { get; set; } = "tray";
    /// <summary>Har tipset "körs i bakgrunden" visats en gång?</summary>
    public bool TrayHintShown { get; set; }
    /// <summary>1.0.12: frågan "Starta med Windows?" har ställts (välkomstskärmen eller en gång efter uppdatering).</summary>
    public bool AutostartAsked { get; set; }
    /// <summary>Spärrade addonversioner från senaste versionsinfo.</summary>
    public List<string> BlockedAddons { get; set; } = new();

    public DateTime? LastUploadUtc { get; set; }
    public int? Points { get; set; }
    public int? Rank { get; set; }
    /// <summary>Misslyckade självuppdateringar i rad (från 2 erbjuds nedladdningssidan). Nollas när en uppdatering lyckas.</summary>
    public int UpdateFailures { get; set; }
    /// <summary>Sätts (till den gamla versionen) precis före Swap; den nya versionen visar då fönstret och "Uppdaterad till …".</summary>
    public string? JustUpdatedFrom { get; set; }
    /// <summary>1.0.10: uppdateringen gjordes automatiskt medan fönstret var stängt – visa inte fönstret efter omstarten (spelet kan vara igång).</summary>
    public bool QuietUpdate { get; set; }
    /// <summary>Versionen som körde senast (1.0.6+). Skiljer den sig från den som körs nu har appen uppdaterats.</summary>
    public string? LastRunVersion { get; set; }
    /// <summary>Från /api/bidrag/jag: poster som servern godtagit (summa av counts) resp. bekräftade (fältet "confirmed", om det finns).</summary>
    public int? AcceptedTotal { get; set; }
    public int? ConfirmedTotal { get; set; }
    public List<ConfirmedItem> LastConfirmed { get; set; } = new();
    /// <summary>1.0.18, från /api/bidrag/jag (valfria fält): poäng och fynd som väntar på att en spelare till bekräftar.
    /// Null tills servern skickar dem – då visas ingenting.</summary>
    public int? PendingPoints { get; set; }
    public int? PendingFinds { get; set; }
    /// <summary>1.0.18, från /api/bidrag/jag (valfritt fält "media"): spelarens Scout-bilder – publicerade, väntar på
    /// granskning och avvisade. Null tills servern skickar fältet; då visas bara antalet bilder appen själv har skickat.</summary>
    public int? MediaPublished { get; set; }
    public int? MediaPending { get; set; }
    public int? MediaRejected { get; set; }
    /// <summary>1.0.18: addonversionen som klienten bytte till medan en äldre fanns på plats – spelet laddar den först
    /// efter /reload eller omstart (<see cref="ReloadReminder"/>). Null = ingen påminnelse.</summary>
    public string? AddonReloadVersion { get; set; }
    /// <summary>1.0.18: när addonfilerna byttes (se <see cref="AddonReloadVersion"/>).</summary>
    public DateTime? AddonReloadSinceUtc { get; set; }
    /// <summary>1.0.18: från den här tidpunkten räknas spelets sparningar i <see cref="SilentSession"/> (sätts första
    /// gången funktionen körs och när spelkatalogen byts) – äldre sparningar ger aldrig någon varning.</summary>
    public DateTime? SilentSinceUtc { get; set; }
    /// <summary>1.0.18: spelkatalogen som <see cref="SilentSinceUtc"/> gäller (byts katalogen börjar räkningen om).</summary>
    public string? SilentDir { get; set; }
    /// <summary>1.0.18: när varningen "spelet sparade men addonen sparade inget" senast visades (högst en per spelsession).</summary>
    public DateTime? SilentWarnedUtc { get; set; }
    /// <summary>1.0.18: sparningen (SavedVariables-mappens ändringstid) som den senaste varningen gällde.</summary>
    public DateTime? SilentSaveUtc { get; set; }
    /// <summary>L10: högsta signerade "issued" i en godtagen versionsinfo. Äldre versionsinfo avvisas (ingen återuppspelning).</summary>
    public string? ReleaseIssued { get; set; }
    /// <summary>1.0.13: beta-kanalen ("Betaversioner" i Inställningar) – appen och addonen hämtas från
    /// /api/bidrag/version?kanal=beta (scout-sign --channel beta). Av som standard.</summary>
    public bool Beta { get; set; }
    /// <summary>1.0.13: som <see cref="ReleaseIssued"/>, men för beta-kanalen (egen signerad fil, egen tidsstämpel).</summary>
    public string? ReleaseIssuedBeta { get; set; }

    /// <summary>1.0.18: versionsinfon har haft <c>signatureAll</c> – därefter krävs den (<see cref="AcceptRelease"/>).</summary>
    public bool ReleaseSignedAll { get; set; }
    public bool ReleaseSignedAllBeta { get; set; }

    /// <summary>1.0.18: reservnyckeln har återkallat huvudnyckeln – bara reservnyckeln godtas därefter (<see cref="AcceptRelease(SignedRelease, bool)"/>).</summary>
    /// Sparas som den återkallade nyckelns FINGERAVTRYCK, inte som ja/nej: ett senare klientsläpp med en ny
    /// huvudnyckel ska inte ärva återkallelsen av den gamla.
    public string? RevokedKey { get; set; }

    /// <summary>Är den här klientens inbyggda huvudnyckel återkallad?</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool PrimaryKeyRevoked => RevokedKey != null && string.Equals(RevokedKey, UpdateKeys.PrimaryFingerprint, StringComparison.Ordinal);

    /// <summary>1.0.18: det besked från guiden (<see cref="SignedRelease.Notice"/>) som senast visats – visas en gång.</summary>
    public string? NoticeSeen { get; set; }
    /// <summary>
    /// L11: den spelkatalog som användaren valt eller bekräftat. En automatiskt hittad katalog används först när den
    /// bekräftats (en gång). Null i inställningar från ≤ 1.0.6 – då frågar 1.0.7 en gång.
    /// </summary>
    public string? GameDirConfirmed { get; set; }
    /// <summary>L11: en automatiskt hittad katalog som användaren svarat Nej på – den föreslås inte igen.</summary>
    public string? GameDirDeclined { get; set; }

    /// <summary>1.0.19: avstängda funktioner ur den senaste godtagna versionsinfon (<see cref="Features"/>).</summary>
    public List<string> FeaturesOff { get; set; } = new();
    /// <summary>1.0.19: högsta meddelande-id som visats (<see cref="PlayerMessages"/>).</summary>
    public long? MessageSeen { get; set; }
    /// <summary>1.0.19: "Ta bort skickade /scout bild-bilder från datorn" – av som standard (<see cref="ShotCleaner"/>).</summary>
    public bool CleanUploadedShots { get; set; }
    /// <summary>1.0.19: vill addonen ha bildlistan? Från kontots fil (addonen 1.0.30 speglar /scout tips i <c>tipsOff</c>):
    /// false = tipsen är avstängda, då hämtas ingen lista. Null = okänt (äldre addon) – listan hämtas.</summary>
    public bool? ShotListWanted { get; set; }
    /// <summary>1.0.19: när bildlistan senast hämtades (eller försöktes hämtas).</summary>
    public DateTime? ShotListAtUtc { get; set; }
    /// <summary>1.0.19, macOS: visa appen i Dock (på som standard) – menyradsikonen kan hamna bakom kameraskåran eller
    /// döljas av macOS, och då syns inte att appen körs. Gäller efter omstart av appen.</summary>
    public bool ShowInDock { get; set; } = true;

    /// <summary>
    /// 1.0.19: fält som den här versionen inte känner till (skrivna av en nyare version) behålls när filen sparas –
    /// förr försvann de om man gick tillbaka till en äldre version och sedan uppdaterade igen.
    /// </summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; set; }

    /// <summary>Uppladdning av hotfix-cachen: inställningen och inte avstängd av guiden.</summary>
    [JsonIgnore] public bool HotfixOn => UploadHotfix && Features.On(this, Features.Hotfix);
    [JsonIgnore] public bool CreatureCacheOn => UploadCreatureCache && Features.On(this, Features.Creatures);
    [JsonIgnore] public bool ShotsOn => UploadShots && Features.On(this, Features.Shots);
    [JsonIgnore] public bool AuctionsOn => ShareAuctionPrices && Features.On(this, Features.Auctions);

    [JsonIgnore] public string FilePath { get; private set; } = "";

    /// <summary>Är den sparade spelkatalogen vald eller bekräftad av användaren?</summary>
    public bool IsGameDirConfirmed() =>
        !string.IsNullOrEmpty(GameDir) && string.Equals(GameDir, GameDirConfirmed, StringComparison.OrdinalIgnoreCase);

    /// <summary>Användaren har valt (Inställningar) eller bekräftat (frågan) <see cref="GameDir"/>.</summary>
    public void ConfirmGameDir()
    {
        GameDirConfirmed = GameDir;
        if (string.Equals(GameDirDeclined, GameDir, StringComparison.OrdinalIgnoreCase)) GameDirDeclined = null;
    }

    /// <summary>
    /// 1.0.18: beskedet från guiden om det är nytt (visas en gång per text), annars null. Tas beskedet bort ur
    /// versionsinfon glöms det, så att samma text kan visas igen om den kommer tillbaka. Sparar själv.
    /// </summary>
    public string? TakeNotice(SignedRelease release, bool swedish)
    {
        var note = release.NoticeFor(swedish);
        if (string.Equals(note, NoticeSeen, StringComparison.Ordinal)) return null;
        NoticeSeen = note;
        Save();
        return note;
    }

    /// <summary>
    /// L10: godkänner en (signaturkontrollerad) versionsinfo bara om dess "issued" inte är äldre än den senaste vi sett,
    /// och sparar det nya högsta värdet. Kastar <see cref="ApiException"/> (release-stale) annars.
    /// </summary>
    public SignedRelease AcceptRelease(SignedRelease release, bool beta = false) =>
        AcceptRelease(release, beta, UpdateKeys.PrimaryFingerprint, UpdateKeys.BackupFingerprint);

    /// <summary>
    /// De nycklar som versionsinfon och filerna ska verifieras mot: huvudnyckeln och reservnyckeln, eller bara
    /// reservnyckeln när den har återkallat huvudnyckeln (<see cref="PrimaryKeyRevoked"/>).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string TrustedKeys => UpdateKeys.Trusted(PrimaryKeyRevoked);

    /// <summary>Som <see cref="AcceptRelease(SignedRelease, bool)"/> med nycklarnas fingeravtryck angivna (tester).</summary>
    internal SignedRelease AcceptRelease(SignedRelease release, bool beta, string primary, string? backup)
    {
        // Huvudnyckeln är återkallad: en versionsinfo som den signerat godtas aldrig mer (anroparna verifierar då
        // redan bara mot reservnyckeln – det här är samma regel en gång till).
        bool revoked = RevokedKey != null && string.Equals(RevokedKey, primary, StringComparison.Ordinal);
        if (revoked && string.Equals(release.Signer, primary, StringComparison.Ordinal)) throw new ApiException(ErrorCodes.SignatureInvalid);
        // Återkallelse: bara reservnyckeln kan återkalla, bara huvudnyckeln kan återkallas, och fältet ska vara
        // signerat (signatureAll). Den prövas FÖRE "issued"-golvet: golvet kan vara satt av den läckta huvudnyckeln
        // (en versionsinfo daterad långt fram i tiden), och då hade reservnyckelns återkallelse avvisats som för
        // gammal för alltid. När återkallelsen sparas börjar därför båda kanalernas golv om från den här
        // versionsinfon – det som sattes under den läckta nyckeln går inte att lita på. Gäller sedan för alltid.
        if (!revoked && backup != null && release.SignedAll
            && !string.Equals(primary, backup, StringComparison.Ordinal)
            && string.Equals(release.Signer, backup, StringComparison.Ordinal)
            && string.Equals(release.RevokedKey, primary, StringComparison.Ordinal)
            && ReleaseVerifier.TryParseIssued(release.Issued, out _))
        {
            RevokedKey = primary;
            ReleaseIssued = release.Issued;
            ReleaseIssuedBeta = release.Issued;
            if (beta) ReleaseSignedAllBeta = true; else ReleaseSignedAll = true;
            Save();
            return release;
        }
        // 1.0.18: har kanalen en gång haft signatureAll krävs den därefter – annars kunde någon plocka bort den och
        // därmed de fält som bara den signerar (lägsta version, besked).
        if ((beta ? ReleaseSignedAllBeta : ReleaseSignedAll) && !release.SignedAll) throw new ApiException(ErrorCodes.SignatureMissing);
        var seen = beta ? ReleaseIssuedBeta : ReleaseIssued;
        var highest = ReleaseVerifier.AcceptIssued(release.Issued, seen);
        bool learned = release.SignedAll && !(beta ? ReleaseSignedAllBeta : ReleaseSignedAll);
        if (learned) { if (beta) ReleaseSignedAllBeta = true; else ReleaseSignedAll = true; }
        if (learned || !string.Equals(highest, seen, StringComparison.Ordinal))
        {
            if (beta) ReleaseIssuedBeta = highest; else ReleaseIssued = highest;
            Save();
        }
        return release;
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>%APPDATA%\ForeverScout (se <see cref="SafeFs.AppDataDir"/>).</summary>
    public static string DataDir => SafeFs.AppDataDir;

    /// <param name="dir">Annan datakatalog (tester) – sätter SafeFs appdatakatalog.</param>
    public static Settings Load(string? dir = null)
    {
        if (dir != null) SafeFs.UseAppDataDir(dir);
        var path = Path.Combine(DataDir, "settings.json");
        // 1.0.19: en trasig settings.json (strömavbrott mitt i en sparning, disken full) betydde förr att ALLT glömdes –
        // spelkatalogen, vad som redan skickats, bildposterna. Nu läses den senaste goda kopian (settings.json.bak).
        Settings s = TryRead(path) ?? TryRead(path + ".bak") ?? new();
        s.UploadedSeq = new(s.UploadedSeq, StringComparer.OrdinalIgnoreCase);
        s.HotfixSha = new(s.HotfixSha, StringComparer.OrdinalIgnoreCase);
        s.CreatureCacheSha = new(s.CreatureCacheSha ?? new(), StringComparer.OrdinalIgnoreCase);
        s.UploadedSha = new(s.UploadedSha, StringComparer.OrdinalIgnoreCase);
        s.Shots = new((s.Shots ?? new()).Where(p => p.Value is not null), StringComparer.Ordinal);
        s.AuctionSha = new(s.AuctionSha ?? new(), StringComparer.OrdinalIgnoreCase);
        s.AuctionDay = new(s.AuctionDay ?? new(), StringComparer.OrdinalIgnoreCase);
        s.FilePath = path;
        return s;
    }

    private static Settings? TryRead(string path)
    {
        try { return SafeFs.FileExists(path) ? JsonSerializer.Deserialize<Settings>(SafeFs.ReadAllText(path), Json) : null; }
        catch (Exception ex) when (ex is JsonException or IOException or SafeFsException or NotSupportedException) { return null; }
    }

    private readonly object _lock = new();

    public void Save()
    {
        lock (_lock)
        {
            // Uppladdningen ändrar listor på en annan tråd (UploadedSha, Shots): ändras en mitt i serialiseringen
            // kastar den InvalidOperationException. Försök igen – nästa sparning tar ändå med allt.
            string json;
            for (int attempt = 0; ; attempt++)
            {
                try { json = JsonSerializer.Serialize(this, Json); break; }
                catch (InvalidOperationException) when (attempt < 4) { Thread.Sleep(15); }
            }
            // 1.0.19: den förra filen blir settings.json.bak om den går att läsa – så finns alltid en god kopia.
            try
            {
                if (SafeFs.FileExists(FilePath) && TryRead(FilePath) is not null) SafeFs.WriteAllText(FilePath + ".bak", SafeFs.ReadAllText(FilePath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SafeFsException) { }
            SafeFs.WriteAllText(FilePath, json);
        }
    }
}
