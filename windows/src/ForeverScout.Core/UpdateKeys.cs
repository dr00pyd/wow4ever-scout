namespace ForeverScout.Core;

/// <summary>
/// Publik nyckel (ECDSA P-256, SPKI-PEM) för signerade släpp. Skapas med
/// <c>node scripts/scout-sign.mjs --genkey &lt;katalog&gt;</c>; klistra in utskriften nedan i stället för platshållaren.
/// Release-bygget avbryts (se ForeverScout.Core.csproj, målet ScoutKeyGuard) så länge platshållaren står kvar.
/// Utan riktig nyckel vägrar klienten installera uppdateringar (ingen reserv).
/// </summary>
public static class UpdateKeys
{
    public const string PublicKeyPem = """
-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEhi/2w4zEy5kqlaBpgtbmIuhXKsAo
P41EShGig47sfIt1S3TcJLW/fMuUEv9vG50x1nUwaAGfiK4ycufysxyqqw==
-----END PUBLIC KEY-----
""";

    /// <summary>
    /// 1.0.18: reservnyckeln. Klienten godtar en signatur från huvudnyckeln ELLER reservnyckeln, så att släppen kan
    /// fortsätta om huvudnyckeln går förlorad. Den privata delen ska förvaras skilt från huvudnyckeln (inte på
    /// samma dator). Reservnyckeln kan dessutom återkalla huvudnyckeln (<see cref="Settings.AcceptRelease"/>) om den
    /// läckt – huvudnyckeln kan aldrig återkalla reservnyckeln.
    /// </summary>
    public const string BackupPublicKeyPem = """
-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAERxkLMSFcMG8aIOwjStHpP5E85YwL
5Ab9mg09GmLav5QIXtpaoFEWZbHdRNOA2F2foAcXe07bY/6b5W7NnuRCQw==
-----END PUBLIC KEY-----
""";

    /// <summary>Båda nycklarna (huvudnyckeln först).</summary>
    public static string All => PublicKeyPem + "\n" + BackupPublicKeyPem;

    /// <summary>De nycklar klienten litar på: båda, eller bara reservnyckeln när huvudnyckeln är återkallad.</summary>
    public static string Trusted(bool primaryRevoked) => primaryRevoked ? BackupPublicKeyPem : All;

    public static string PrimaryFingerprint => ReleaseVerifier.Fingerprint(PublicKeyPem);
    public static string BackupFingerprint => ReleaseVerifier.Fingerprint(BackupPublicKeyPem);
}
