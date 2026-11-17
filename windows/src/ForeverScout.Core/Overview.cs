using System.Text.Json;

namespace ForeverScout.Core;

/// <summary>
/// 1.0.18: det i översikten som är nytt och gemensamt för Windows, macOS och Linux – spelarens Scout-bilder
/// ("Bilder: 3 publicerade · 1 väntar på granskning · 0 avvisade" och länken Mina bilder) och poängen som väntar på
/// att en spelare till bekräftar. Siffrorna kommer från <c>GET /api/bidrag/jag</c> (valfria fält); saknas de visar
/// appen bara det den själv vet. Rena funktioner – fönstren visar bara texten.
/// </summary>
public static class Overview
{
    /// <summary>Övre gräns för tal från servern innan de visas eller skrivs i kvittot (aldrig negativa, aldrig orimliga).</summary>
    public const int Max = 999_999_999;

    public static int Clamp(long n) => (int)Math.Clamp(n, 0, Max);

    /// <summary>Ett heltal ur ett valfritt JSON-fält. Saknat fält, null, text, decimaltal och negativa tal ger null.</summary>
    public static int? Int(JsonElement? e)
    {
        if (e is not { ValueKind: JsonValueKind.Number } v) return null;
        if (!v.TryGetInt64(out var n) || n < 0) return null;
        return Clamp(n);
    }

    private static int? Int(JsonElement? obj, string name) =>
        obj is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) ? Int(v) : null;

    /// <summary>
    /// För över de valfria fälten ur svaret från /api/bidrag/jag till inställningarna. Ett fält som saknas nollställer
    /// det sparade värdet (en äldre server, eller en som slutat skicka det) – då visas det som appen själv vet.
    /// <c>media</c> räknas bara som känt när alla tre talen finns.
    /// </summary>
    public static void Apply(Settings s, MeResult me)
    {
        s.PendingPoints = Int(me.PendingPoints);
        s.PendingFinds = Int(me.PendingFinds);
        var (pub, pend, rej) = (Int(me.Media, "published"), Int(me.Media, "pending"), Int(me.Media, "rejected"));
        bool known = pub is not null && pend is not null && rej is not null;
        s.MediaPublished = known ? pub : null;
        s.MediaPending = known ? pend : null;
        s.MediaRejected = known ? rej : null;
    }

    /// <summary>Bilder som appen själv har skickat och servern tog emot (publicerade direkt eller lagda i granskningskön).</summary>
    public static int ShotsSent(Settings s) =>
        s.Shots.Values.Count(x => x is { Pending: false } && x.Status is "published" or "queued" or "ok");

    /// <summary>
    /// Raden om spelarens bilder. Med serverns siffror: publicerade, väntar på granskning, avvisade. Utan dem: antalet
    /// bilder som appen själv har skickat, eller en rad om hur man tar en bild.
    /// </summary>
    public static string PicturesLine(Settings s)
    {
        if (s.MediaPublished is int pub && s.MediaPending is int pend && s.MediaRejected is int rej)
            return Strings.F("pics.server", N(pub), N(pend), N(rej));
        int sent = ShotsSent(s);
        return sent > 0 ? Strings.F("pics.local", N(sent)) : Strings.S("pics.none");
    }

    /// <summary>"+40 poäng väntar på att en spelare till bekräftar (3 fynd)" – null när inget väntar eller servern inte skickar fältet.</summary>
    public static string? PendingLine(Settings s)
    {
        if (s.PendingPoints is not int p || p <= 0) return null;
        return s.PendingFinds is int f && f > 0 ? Strings.F("mine.pending", N(p), N(f)) : Strings.F("mine.pending.points", N(p));
    }

    /// <summary>Sidan Mina bilder på sajten (sluggarna följer lib/i18n/routes.ts: /bidra/bilder ↔ /en/contribute/pictures).</summary>
    public static string PicturesPath(bool sv) => sv ? "/bidra/bilder" : "/en/contribute/pictures";

    private static string N(int n) => n.ToString("N0", Strings.Culture);
}
