using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForeverScout.Core;

namespace ForeverScout.Tests;

/// <summary>
/// 1.0.18: reservnyckeln. Varje signatur godtas om den stämmer mot huvudnyckeln ELLER reservnyckeln, och en
/// versionsinfo som reservnyckeln signerat kan återkalla huvudnyckeln (<c>primaryRevoked</c>) – för alltid, och bara åt
/// det hållet. Plattformsoberoende (kompileras även in i Mac/Linux-testerna).
/// </summary>
public sealed class ReserveKeyTests : IDisposable
{
    // Skrivet av scripts/scout-sign.mjs med två engångsnycklar (--genkey ×2): signerat med nyckel 2,
    // --revoke-primary <nyckel 1:s publika .pem>, --exe en fil med innehållet "MZ fixture exe".
    // Fingeravtrycken är de som skriptet skrev ut ("signerad med nyckeln …" / "ÅTERKALLAR huvudnyckeln …").
    private const string NodePem1 = """
-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEjwBfXS2ycZjKAGYCxM5pSDuJ60Rr
AdKrpdeZcubQ9lHtT1DbGSDOrd0CM6E2wJkNTc+0cy3LbaS5UijSTqe/8w==
-----END PUBLIC KEY-----
""";

    private const string NodePem2 = """
-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE5JweBdOaeHdZXILI8iCSTsbD9T1B
ehM1N7YR51L+vfloVVk+8RRTZ1fQ030EYyvk++eCWcfqtCFg0jQj9bjqOw==
-----END PUBLIC KEY-----
""";

    private const string NodeFingerprint1 = "a09781edfdb140378d696ec6c3ea2881d70107d7a6fc975ca065f046a9407169";
    private const string NodeFingerprint2 = "d98e139f178e7fbad432de5df087746bdcf97aadae84a7ad69722af9096d1c4b";

    private const string NodeRevokeJson = """
{
  "v": 1,
  "primaryRevoked": "a09781edfdb140378d696ec6c3ea2881d70107d7a6fc975ca065f046a9407169",
  "client": "1.0.18",
  "clientFile": "ForeverScout.exe",
  "sha256": "9bfb304b21d9fc9f2a1da2067947861c1fb0904db486d5040c93d4aed9241c1a",
  "clientSig": "4yxK5r2ENqCZ4almeaXBSZlyeSxLj+3YAJozAhhZFbtkMspbU4iK+rHS8IUfl+pfc/11Qorx5f866YJQciJ97w==",
  "issued": "2026-11-16T23:27:13.797Z",
  "signed": "addon,addonFile,addonSha256,addonSig,client,clientFile,clientSig,dmg,dmgFile,dmgSha256,dmgSig,issued,linux,linuxFile,linuxSha256,linuxSig,lite,liteFile,liteSha256,liteSig,mac,macFile,macSha256,macSig,primaryRevoked,setup,setupFile,setupSha256,setupSig,sha256,signed,v",
  "blockedAddons": [],
  "signature": "hPnxLIGzAhSiq+DUh48agx775Ol+fu9WVetRCE4k+izHi115SSSxmVWL8ziCu8ObPJ5vGyraG4dya8C7no7KGw==",
  "signature2": "x8LyiSZPOzI3G5QU6YEUJI/e5chVI/V9+oug0Bed6S+9xZtN3cuzll1gxrfvGgWGxf6gKJuSOT5allbmQdr/lw==",
  "signature3": "FnCratAXFrUy2wBg1sfK31Vyg44hUqWVNai1pOV9OMHfmtbRGEES3iX0UohH2C127ZiDbrs8F2WSUtfTTBwcJQ==",
  "signature4": "o4C488ecac/s7aKiJyGwKClYaoQsynTb0pBCglDPg1iuQSnXR1rZXVWzMjiKZj8AoI169uaJNcMs63euZypvVQ==",
  "signature5": "ZnUyGDizEJ4sCSbVUO9jy+PMiqYLIUN5BfW98FWKENs1kPqrmuemz9rPmgJzIoAjJNDS46Rm5MbGeBibSM7IBw==",
  "signatureAll": "hrbP6+nz1ZQm4OHBKKlzYN/TUGphGlPh6IqYLj05nOPDt5RGYj6D14WoEEIAzHlmj0HXpnFwK4AAACJir3MovA=="
}
""";

    private const string Placeholder = "__SCOUT_PUBLIC_KEY_PLACEHOLDER__";

    /// <summary>"Huvudnyckeln" och "reservnyckeln" i testerna (engångsnycklar).</summary>
    private readonly TestSigner _primary = new();
    private readonly TestSigner _backup = new();
    private readonly DirectoryInfo _tmp = TestEnv.Temp("fs-reserve-");
    private int _dirs;

    public void Dispose()
    {
        _primary.Dispose();
        _backup.Dispose();
        try { _tmp.Delete(true); } catch (IOException) { }
        SafeFs.UseAppDataDir(TestEnv.AppData);
    }

    private static string Code(Action a) => Assert.IsAssignableFrom<ScoutException>(Record.Exception(a)).Code;

    private static string Repo([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", ".."));

    private string PrimaryFp => ReleaseVerifier.Fingerprint(_primary.Pem);
    private string BackupFp => ReleaseVerifier.Fingerprint(_backup.Pem);
    private string Both => _primary.Pem + "\n" + _backup.Pem;

    /// <summary>En ny, tom inställningskatalog.</summary>
    private string NewDir() => Path.Combine(_tmp.FullName, "appdata" + ++_dirs);

    /// <summary>Lägger till signed (alla fält utom signaturerna och blockedAddons, eller <paramref name="list"/>) och signatureAll.</summary>
    private static JsonObject SignAll(TestSigner signer, JsonObject n, string? list = null)
    {
        n.Remove("signatureAll");
        n["signed"] = list ?? string.Join(",", n.Select(p => p.Key).Append("signed").Where(k => !k.StartsWith("signature", StringComparison.Ordinal) && k != "blockedAddons")
            .Distinct().OrderBy(k => k, StringComparer.Ordinal));
        using var doc = JsonDocument.Parse(n.ToJsonString());
        n["signatureAll"] = signer.Sign(Encoding.UTF8.GetBytes(ReleaseVerifier.Canonical(doc.RootElement, n["signed"]!.GetValue<string>().Split(','))));
        return n;
    }

    /// <summary>Versionsinfo med signatureAll, signerad av <paramref name="signer"/>; <paramref name="revokes"/> = fältet primaryRevoked (signerat).</summary>
    private SignedRelease Manifest(TestSigner signer, string issued, string? revokes = null)
    {
        var n = signer.Release(issued);
        if (revokes != null) n["primaryRevoked"] = revokes;
        return ReleaseVerifier.Verify(SignAll(signer, n).ToJsonString(), Both);
    }

    // ---- 1. Två nycklar: signaturen godtas om den stämmer mot någon av dem ----

    [Fact]
    public void AManifestSignedByEitherKeyVerifiesAndNamesItsSigner()
    {
        Assert.NotEqual(PrimaryFp, BackupFp);
        foreach (var (signer, other) in new[] { (_primary, _backup), (_backup, _primary) })
        {
            var fp = ReleaseVerifier.Fingerprint(signer.Pem);
            var json = SignAll(signer, signer.Release("2026-11-17T10:00:00.000Z")).ToJsonString();
            // Båda ordningarna, och nyckeln ensam – på alla plattformar (signatureAll).
            foreach (var pem in new[] { signer.Pem + "\n" + other.Pem, other.Pem + "\n" + signer.Pem, signer.Pem })
                foreach (var r in new[] { ReleaseVerifier.Verify(json, pem), ReleaseVerifier.VerifyMac(json, pem), ReleaseVerifier.VerifyLinux(json, pem) })
                {
                    Assert.True(r.SignedAll);
                    Assert.Equal(fp, r.Signer);
                    Assert.Null(r.RevokedKey);
                    Assert.Equal("1.0.7", r.Client);
                }
            // Bara den ANDRA nyckeln: ogiltig signatur.
            Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => ReleaseVerifier.Verify(json, other.Pem)));
            Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => ReleaseVerifier.VerifyMac(json, other.Pem)));
            Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => ReleaseVerifier.VerifyLinux(json, other.Pem)));
        }
    }

    [Fact]
    public void ALegacyManifestSignedByEitherKeyVerifiesAndNamesItsSigner()
    {
        // Utan signatureAll (signature2): samma regel, och Signer sätts ändå.
        foreach (var (signer, other) in new[] { (_primary, _backup), (_backup, _primary) })
        {
            var json = signer.Release("2026-11-17T10:00:00.000Z").ToJsonString();
            foreach (var pem in new[] { signer.Pem + "\n" + other.Pem, other.Pem + "\n" + signer.Pem })
            {
                var r = ReleaseVerifier.Verify(json, pem);
                Assert.False(r.SignedAll);
                Assert.Equal(ReleaseVerifier.Fingerprint(signer.Pem), r.Signer);
            }
            Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => ReleaseVerifier.Verify(json, other.Pem)));
        }
    }

    [Fact]
    public void AFileSignedByEitherKeyVerifiesButNotUnderTheOtherKeyAlone()
    {
        var exe = Encoding.ASCII.GetBytes("MZ exe");
        var zip = Encoding.ASCII.GetBytes("PK addon 1.0.5");
        foreach (var (signer, other) in new[] { (_primary, _backup), (_backup, _primary) })
        {
            var rel = ReleaseVerifier.Verify(SignAll(signer, signer.Release("2026-11-17T10:00:00.000Z")).ToJsonString(), Both);
            foreach (var pem in new[] { signer.Pem + "\n" + other.Pem, other.Pem + "\n" + signer.Pem, signer.Pem })
            {
                ReleaseVerifier.VerifyFile(exe, ReleaseKind.Client, rel.ClientFile!, pem);
                ReleaseVerifier.VerifyFile(zip, ReleaseKind.Addon, rel.AddonFile!, pem);
            }
            Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => ReleaseVerifier.VerifyFile(exe, ReleaseKind.Client, rel.ClientFile!, other.Pem)));
            Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => ReleaseVerifier.VerifyFile(zip, ReleaseKind.Addon, rel.AddonFile!, other.Pem)));
            // Fel innehåll fälls på SHA-256 före signaturen, som förut.
            Assert.Equal(ErrorCodes.ShaMismatch, Code(() => ReleaseVerifier.VerifyFile(Encoding.ASCII.GetBytes("MZ annan"), ReleaseKind.Client, rel.ClientFile!, Both)));
        }
    }

    [Fact]
    public void ASignatureFromAThirdKeyIsRejectedByBothKeys()
    {
        using var third = new TestSigner();
        var json = SignAll(third, third.Release("2026-11-17T10:00:00.000Z")).ToJsonString();
        Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => ReleaseVerifier.Verify(json, Both)));
        Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => ReleaseVerifier.Verify(third.Release("2026-11-17T10:00:00.000Z").ToJsonString(), Both)));
        var file = ReleaseVerifier.Verify(json, third.Pem).ClientFile!;
        Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => ReleaseVerifier.VerifyFile(Encoding.ASCII.GetBytes("MZ exe"), ReleaseKind.Client, file, Both)));
    }

    // ---- 2. Fingeravtrycket: samma värde som scout-sign.mjs ----

    [Fact]
    public void FingerprintMatchesTheSigningScript()
    {
        Assert.Equal(NodeFingerprint1, ReleaseVerifier.Fingerprint(NodePem1));
        Assert.Equal(NodeFingerprint2, ReleaseVerifier.Fingerprint(NodePem2));
        // Flera nycklar i texten: den första.
        Assert.Equal(NodeFingerprint1, ReleaseVerifier.Fingerprint(NodePem1 + "\n" + NodePem2));
        Assert.Equal(NodeFingerprint2, ReleaseVerifier.Fingerprint(NodePem2 + "\n" + NodePem1));
        // Radslut och omgivande blanksteg påverkar inte värdet (det räknas på DER-formen, inte på texten).
        Assert.Equal(NodeFingerprint1, ReleaseVerifier.Fingerprint("\r\n  " + NodePem1.Replace("\n", "\r\n") + "\r\n"));
        // Och det är SHA-256 över nyckelns SubjectPublicKeyInfo.
        using var k = ECDsa.Create();
        k.ImportFromPem(NodePem1);
        Assert.Equal(NodeFingerprint1, Convert.ToHexString(SHA256.HashData(k.ExportSubjectPublicKeyInfo())).ToLowerInvariant());
    }

    [Fact]
    public void ARevocationSignedByTheRealScriptIsReadOnEveryPlatform()
    {
        foreach (var pem in new[] { NodePem1 + "\n" + NodePem2, NodePem2 + "\n" + NodePem1, NodePem2 })
            foreach (var r in new[] { ReleaseVerifier.Verify(NodeRevokeJson, pem), ReleaseVerifier.VerifyMac(NodeRevokeJson, pem), ReleaseVerifier.VerifyLinux(NodeRevokeJson, pem) })
            {
                Assert.True(r.SignedAll);
                Assert.Equal(NodeFingerprint2, r.Signer);
                Assert.Equal(NodeFingerprint1, r.RevokedKey);
                Assert.Equal("1.0.18", r.Client);
            }
        // Nyckeln som återkallas har inte signerat något här.
        Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => ReleaseVerifier.Verify(NodeRevokeJson, NodePem1)));
        var rel = ReleaseVerifier.Verify(NodeRevokeJson, NodePem1 + "\n" + NodePem2);
        ReleaseVerifier.VerifyFile(Encoding.ASCII.GetBytes("MZ fixture exe"), ReleaseKind.Client, rel.ClientFile!, NodePem2);
        Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => ReleaseVerifier.VerifyFile(Encoding.ASCII.GetBytes("MZ fixture exe"), ReleaseKind.Client, rel.ClientFile!, NodePem1)));
        // Ett ändrat fingeravtryck fäller signaturen (fältet är signerat).
        var n = JsonNode.Parse(NodeRevokeJson)!.AsObject();
        n["primaryRevoked"] = NodeFingerprint2;
        Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => ReleaseVerifier.Verify(n.ToJsonString(), NodePem1 + "\n" + NodePem2)));

        // Och klienten lyder: nyckel 1 är huvudnyckel, nyckel 2 reservnyckel.
        var s = Settings.Load(NewDir());
        s.AcceptRelease(rel, false, NodeFingerprint1, NodeFingerprint2);
        Assert.Equal(NodeFingerprint1, s.RevokedKey);
    }

    [Fact]
    public void RevokedKeyIsOnlyReadFromASignedListAndMustBeAFingerprint()
    {
        // Signerat och i listan.
        Assert.Equal(PrimaryFp, Manifest(_backup, "2026-11-17T10:00:00.000Z", PrimaryFp).RevokedKey);
        // Tillagt efter signeringen, utanför listan: signaturen stämmer, men fältet finns inte för klienten.
        var outside = SignAll(_backup, _backup.Release("2026-11-17T10:00:00.000Z"));
        outside["primaryRevoked"] = PrimaryFp;
        var r = ReleaseVerifier.Verify(outside.ToJsonString(), Both);
        Assert.True(r.SignedAll);
        Assert.Null(r.RevokedKey);
        // Utan signatureAll är fältet osignerat.
        var legacy = _backup.Release("2026-11-17T10:00:00.000Z");
        legacy["primaryRevoked"] = PrimaryFp;
        Assert.Null(ReleaseVerifier.Verify(legacy.ToJsonString(), Both).RevokedKey);
        // Inte ett fingeravtryck (64 hex): inget.
        Assert.Null(Manifest(_backup, "2026-11-17T10:00:00.000Z", "ja").RevokedKey);
        Assert.Null(Manifest(_backup, "2026-11-17T10:00:00.000Z", PrimaryFp[..63]).RevokedKey);
        Assert.Null(Manifest(_backup, "2026-11-17T10:00:00.000Z", PrimaryFp + "0").RevokedKey);
    }

    // ---- 3. Återkallelsen (Settings.AcceptRelease) ----

    [Fact]
    public void TheBackupKeyRevokesThePrimaryAndThatSurvivesRestart()
    {
        var dir = NewDir();
        var s = Settings.Load(dir);
        Assert.False(s.RevokedKey == PrimaryFp);
        Assert.Equal(UpdateKeys.All, s.TrustedKeys);

        // Före återkallelsen godtas båda nycklarna.
        s.AcceptRelease(Manifest(_primary, "2026-11-17T08:00:00.000Z"), false, PrimaryFp, BackupFp);
        s.AcceptRelease(Manifest(_backup, "2026-11-17T09:00:00.000Z"), false, PrimaryFp, BackupFp);
        Assert.False(s.RevokedKey == PrimaryFp);

        s.AcceptRelease(Manifest(_backup, "2026-11-17T10:00:00.000Z", PrimaryFp), false, PrimaryFp, BackupFp);
        Assert.True(s.RevokedKey == PrimaryFp);
        Assert.Equal("2026-11-17T10:00:00.000Z", s.ReleaseIssued);
        // Därefter litar klienten bara på reservnyckeln.

        var again = Settings.Load(dir);
        Assert.True(again.RevokedKey == PrimaryFp);
        Assert.Equal("2026-11-17T10:00:00.000Z", again.ReleaseIssued);
        // För alltid: en senare versionsinfo från reservnyckeln UTAN fältet ångrar ingenting.
        again.AcceptRelease(Manifest(_backup, "2026-11-17T11:00:00.000Z"), false, PrimaryFp, BackupFp);
        Assert.True(again.RevokedKey == PrimaryFp);
        Assert.True(Settings.Load(dir).RevokedKey == PrimaryFp);
    }

    [Fact]
    public void TrustedKeysAreBothOrOnlyTheBackup()
    {
        Assert.Equal(UpdateKeys.BackupPublicKeyPem, UpdateKeys.Trusted(true));
        Assert.DoesNotContain(UpdateKeys.PublicKeyPem.Trim(), UpdateKeys.Trusted(true));
        Assert.Equal(UpdateKeys.All, UpdateKeys.Trusted(false));
        Assert.Contains(UpdateKeys.PublicKeyPem.Trim(), UpdateKeys.Trusted(false));
        Assert.Contains(UpdateKeys.BackupPublicKeyPem.Trim(), UpdateKeys.Trusted(false));
        Assert.Equal(UpdateKeys.BackupFingerprint, ReleaseVerifier.Fingerprint(UpdateKeys.Trusted(true)));
        Assert.Equal(UpdateKeys.PrimaryFingerprint, ReleaseVerifier.Fingerprint(UpdateKeys.Trusted(false)));
    }

    [Fact]
    public void AfterRevocationThePrimaryKeyIsRejectedAndTheBackupKeyAccepted()
    {
        var dir = NewDir();
        var s = Settings.Load(dir);
        s.AcceptRelease(Manifest(_backup, "2026-11-17T10:00:00.000Z", PrimaryFp), false, PrimaryFp, BackupFp);
        Assert.True(s.RevokedKey == PrimaryFp);

        // Huvudnyckeln: avvisas även med nyare "issued", med och utan signatureAll, i båda kanalerna – och inget sparas.
        var newer = Manifest(_primary, "2026-11-18T10:00:00.000Z");
        var legacy = ReleaseVerifier.Verify(_primary.Release("2026-11-18T11:00:00.000Z").ToJsonString(), Both);
        Assert.Equal(PrimaryFp, newer.Signer);
        Assert.Equal(PrimaryFp, legacy.Signer);
        Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => s.AcceptRelease(newer, false, PrimaryFp, BackupFp)));
        Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => s.AcceptRelease(legacy, false, PrimaryFp, BackupFp)));
        Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => s.AcceptRelease(newer, true, PrimaryFp, BackupFp)));
        Assert.Equal("2026-11-17T10:00:00.000Z", s.ReleaseIssued);
        Assert.Equal("2026-11-17T10:00:00.000Z", s.ReleaseIssuedBeta); // återkallelsen satte golvet på båda kanalerna
        // Huvudnyckeln kan inte heller "återkalla tillbaka".
        Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => s.AcceptRelease(Manifest(_primary, "2026-11-18T12:00:00.000Z", BackupFp), false, PrimaryFp, BackupFp)));

        // Reservnyckeln: godtas som vanligt, i båda kanalerna.
        s.AcceptRelease(Manifest(_backup, "2026-11-17T12:00:00.000Z"), false, PrimaryFp, BackupFp);
        Assert.Equal("2026-11-17T12:00:00.000Z", s.ReleaseIssued);
        s.AcceptRelease(Manifest(_backup, "2026-11-17T12:30:00.000Z"), true, PrimaryFp, BackupFp);
        Assert.Equal("2026-11-17T12:30:00.000Z", s.ReleaseIssuedBeta);

        // Efter omstart gäller samma sak.
        var again = Settings.Load(dir);
        Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => again.AcceptRelease(newer, false, PrimaryFp, BackupFp)));
        again.AcceptRelease(Manifest(_backup, "2026-11-17T13:00:00.000Z"), false, PrimaryFp, BackupFp);
        Assert.Equal("2026-11-17T13:00:00.000Z", again.ReleaseIssued);
        Assert.True(again.RevokedKey == PrimaryFp);
    }

    [Fact]
    public void ThePrimaryKeyCanNeverRevoke()
    {
        var dir = NewDir();
        var s = Settings.Load(dir);
        // Signerad av huvudnyckeln och pekar ut huvudnyckeln (nyckeln som återkallar sig själv).
        var self = Manifest(_primary, "2026-11-17T10:00:00.000Z", PrimaryFp);
        Assert.Equal(PrimaryFp, self.RevokedKey);
        s.AcceptRelease(self, false, PrimaryFp, BackupFp);
        Assert.False(s.RevokedKey == PrimaryFp);
        // Signerad av huvudnyckeln och pekar ut reservnyckeln.
        var attack = Manifest(_primary, "2026-11-17T11:00:00.000Z", BackupFp);
        Assert.Equal(BackupFp, attack.RevokedKey);
        s.AcceptRelease(attack, false, PrimaryFp, BackupFp);
        Assert.False(s.RevokedKey == PrimaryFp);
        Assert.Equal(UpdateKeys.All, s.TrustedKeys);
        // Reservnyckeln godtas fortfarande, och huvudnyckeln också.
        s.AcceptRelease(Manifest(_backup, "2026-11-17T12:00:00.000Z"), false, PrimaryFp, BackupFp);
        s.AcceptRelease(Manifest(_primary, "2026-11-17T13:00:00.000Z"), false, PrimaryFp, BackupFp);
        Assert.Equal("2026-11-17T13:00:00.000Z", s.ReleaseIssued);
        Assert.False(Settings.Load(dir).RevokedKey == PrimaryFp);
    }

    [Fact]
    public void TheBackupKeyOnlyRevokesWithASignedFieldNamingThePrimary()
    {
        var dir = NewDir();
        var s = Settings.Load(dir);
        using var third = new TestSigner();

        // Pekar ut någon annan nyckel (en tredje, och reservnyckeln själv).
        s.AcceptRelease(Manifest(_backup, "2026-11-17T10:00:00.000Z", ReleaseVerifier.Fingerprint(third.Pem)), false, PrimaryFp, BackupFp);
        Assert.False(s.RevokedKey == PrimaryFp);
        s.AcceptRelease(Manifest(_backup, "2026-11-17T10:10:00.000Z", BackupFp), false, PrimaryFp, BackupFp);
        Assert.False(s.RevokedKey == PrimaryFp);

        // Fältet finns men står inte i den signerade listan (tillagt efteråt).
        var outside = SignAll(_backup, _backup.Release("2026-11-17T10:20:00.000Z"));
        outside["primaryRevoked"] = PrimaryFp;
        var o = ReleaseVerifier.Verify(outside.ToJsonString(), Both);
        Assert.True(o.SignedAll);
        Assert.Equal(BackupFp, o.Signer);
        s.AcceptRelease(o, false, PrimaryFp, BackupFp);
        Assert.False(s.RevokedKey == PrimaryFp);

        // Inget fält alls.
        s.AcceptRelease(Manifest(_backup, "2026-11-17T10:30:00.000Z"), false, PrimaryFp, BackupFp);
        Assert.False(s.RevokedKey == PrimaryFp);
        Assert.False(Settings.Load(dir).RevokedKey == PrimaryFp);
        Assert.Equal("2026-11-17T10:30:00.000Z", s.ReleaseIssued);
    }

    [Fact]
    public void ALegacyManifestWithoutSignatureAllNeverRevokes()
    {
        var dir = NewDir();
        var s = Settings.Load(dir);
        var n = _backup.Release("2026-11-17T10:00:00.000Z");
        n["primaryRevoked"] = PrimaryFp;
        var legacy = ReleaseVerifier.Verify(n.ToJsonString(), Both);
        Assert.False(legacy.SignedAll);
        Assert.Equal(BackupFp, legacy.Signer);
        s.AcceptRelease(legacy, false, PrimaryFp, BackupFp);
        Assert.False(s.RevokedKey == PrimaryFp);
        // Inte ens om någon skulle fylla i RevokedKey utan signatureAll (verifieraren gör det aldrig).
        s.AcceptRelease(new SignedRelease { Issued = "2026-11-17T10:30:00.000Z", Signer = BackupFp, RevokedKey = PrimaryFp, SignedAll = false }, false, PrimaryFp, BackupFp);
        Assert.False(s.RevokedKey == PrimaryFp);
        Assert.False(Settings.Load(dir).RevokedKey == PrimaryFp);
    }

    [Fact]
    public void NoRevocationWhenPrimaryAndBackupAreTheSameKeyOrThereIsNoBackup()
    {
        var dir = NewDir();
        var s = Settings.Load(dir);
        var rel = Manifest(_backup, "2026-11-17T10:00:00.000Z", BackupFp);
        // Samma fingeravtryck som huvud- och reservnyckel: nyckeln kan inte återkalla sig själv.
        s.AcceptRelease(rel, false, BackupFp, BackupFp);
        Assert.False(s.RevokedKey == PrimaryFp);
        // Ingen reservnyckel alls.
        s.AcceptRelease(Manifest(_backup, "2026-11-17T10:30:00.000Z", PrimaryFp), false, PrimaryFp, null);
        Assert.False(s.RevokedKey == PrimaryFp);
        Assert.False(Settings.Load(dir).RevokedKey == PrimaryFp);
        // … och samma nyckel godtas fortfarande efteråt.
        s.AcceptRelease(Manifest(_backup, "2026-11-17T11:00:00.000Z", BackupFp), false, BackupFp, BackupFp);
        Assert.Equal("2026-11-17T11:00:00.000Z", s.ReleaseIssued);
    }

    /// <summary>
    /// Återkallelsen prövas före "issued"-golvet. Den som har den läckta huvudnyckeln kan annars signera en
    /// versionsinfo daterad långt fram i tiden och därmed göra reservnyckelns återkallelse "för gammal" för alltid.
    /// </summary>
    [Fact]
    public void AFloorSetByTheLeakedPrimaryCannotBlockTheRevocation()
    {
        var dir = NewDir();
        var s = Settings.Load(dir);
        // Angriparen med huvudnyckeln: en versionsinfo daterad år 9999, på båda kanalerna.
        s.AcceptRelease(Manifest(_primary, "9999-12-31T23:59:59.999Z"), false, PrimaryFp, BackupFp);
        s.AcceptRelease(Manifest(_primary, "9999-12-31T23:59:59.999Z"), true, PrimaryFp, BackupFp);
        Assert.Equal("9999-12-31T23:59:59.999Z", s.ReleaseIssued);
        // En vanlig versionsinfo från reservnyckeln är nu "för gammal" …
        Assert.Equal(ErrorCodes.ReleaseStale, Code(() => s.AcceptRelease(Manifest(_backup, "2026-11-18T10:00:00.000Z"), false, PrimaryFp, BackupFp)));
        // … men återkallelsen går igenom, och golven börjar om från den.
        s.AcceptRelease(Manifest(_backup, "2026-11-18T11:00:00.000Z", PrimaryFp), false, PrimaryFp, BackupFp);
        Assert.True(s.RevokedKey == PrimaryFp);
        Assert.Equal("2026-11-18T11:00:00.000Z", s.ReleaseIssued);
        Assert.Equal("2026-11-18T11:00:00.000Z", s.ReleaseIssuedBeta);
        var again = Settings.Load(dir);
        Assert.True(again.RevokedKey == PrimaryFp);
        // Reservnyckelns släpp fungerar igen på båda kanalerna, huvudnyckelns aldrig mer – inte ens med år 9999.
        again.AcceptRelease(Manifest(_backup, "2026-11-18T12:00:00.000Z"), false, PrimaryFp, BackupFp);
        again.AcceptRelease(Manifest(_backup, "2026-11-18T12:00:00.000Z"), true, PrimaryFp, BackupFp);
        Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => again.AcceptRelease(Manifest(_primary, "9999-12-31T23:59:59.999Z"), false, PrimaryFp, BackupFp)));
        // När återkallelsen väl är sparad gäller golvet som vanligt, även för reservnyckeln.
        Assert.Equal(ErrorCodes.ReleaseStale, Code(() => again.AcceptRelease(Manifest(_backup, "2026-11-18T11:30:00.000Z", PrimaryFp), false, PrimaryFp, BackupFp)));
    }

    /// <summary>
    /// Återkallelsen sparas som den återkallade nyckelns fingeravtryck. Ett senare klientsläpp med en NY huvudnyckel
    /// (och samma reservnyckel) ärver den inte: den nya huvudnyckeln godtas, den gamla aldrig.
    /// </summary>
    [Fact]
    public void ARevocationDoesNotCarryOverToANewPrimaryKey()
    {
        var dir = NewDir();
        var s = Settings.Load(dir);
        s.AcceptRelease(Manifest(_backup, "2026-11-18T10:00:00.000Z", PrimaryFp), false, PrimaryFp, BackupFp);
        Assert.True(s.RevokedKey == PrimaryFp);
        // Nästa klientsläpp: ny huvudnyckel, samma reservnyckel, samma settings.json.
        using var next = new TestSigner();
        var nextFp = ReleaseVerifier.Fingerprint(next.Pem);
        var upgraded = Settings.Load(dir);
        var signedByNext = ReleaseVerifier.Verify(SignAll(next, next.Release("2026-11-19T10:00:00.000Z")).ToJsonString(), next.Pem + "\n" + _backup.Pem);
        Assert.Equal(nextFp, signedByNext.Signer);
        upgraded.AcceptRelease(signedByNext, false, nextFp, BackupFp);
        upgraded.AcceptRelease(Manifest(_backup, "2026-11-19T11:00:00.000Z"), false, nextFp, BackupFp);
        Assert.Equal("2026-11-19T11:00:00.000Z", upgraded.ReleaseIssued);
        // Den riktiga klienten bär andra nycklar än testets: återkallelsen av testnyckeln gäller inte dem.
        Assert.False(upgraded.PrimaryKeyRevoked);
        Assert.Equal(UpdateKeys.All, upgraded.TrustedKeys);
    }

    [Fact]
    public void ARevocationWithoutSignatureAllOnAChannelThatRequiresItIsRejected()
    {
        var s = Settings.Load(NewDir());
        s.AcceptRelease(Manifest(_backup, "2026-11-17T10:00:00.000Z"), false, PrimaryFp, BackupFp);
        Assert.True(s.ReleaseSignedAll);
        var n = _backup.Release("2026-11-17T11:00:00.000Z");
        n["primaryRevoked"] = PrimaryFp;
        Assert.Equal(ErrorCodes.SignatureMissing, Code(() => s.AcceptRelease(ReleaseVerifier.Verify(n.ToJsonString(), Both), false, PrimaryFp, BackupFp)));
        Assert.False(s.RevokedKey == PrimaryFp);
    }

    [Fact]
    public void ARevocationWithAnAlreadySeenIssuedIsStillSaved()
    {
        // Samma "issued" som redan godtagits (inget nytt högsta värde) – återkallelsen ska ändå sparas.
        var dir = NewDir();
        var s = Settings.Load(dir);
        s.AcceptRelease(Manifest(_backup, "2026-11-17T10:00:00.000Z"), false, PrimaryFp, BackupFp);
        s.AcceptRelease(Manifest(_backup, "2026-11-17T10:00:00.000Z", PrimaryFp), false, PrimaryFp, BackupFp);
        Assert.True(s.RevokedKey == PrimaryFp);
        Assert.True(Settings.Load(dir).RevokedKey == PrimaryFp);
    }

    [Fact]
    public void ARevocationInTheBetaManifestCounts()
    {
        // Beta-filen bär en kopia av fältet (scout-sign.mjs): återkallelsen gäller hela klienten, inte en kanal.
        var dir = NewDir();
        var s = Settings.Load(dir);
        s.AcceptRelease(Manifest(_backup, "2026-11-17T10:00:00.000Z", PrimaryFp), true, PrimaryFp, BackupFp);
        Assert.True(s.RevokedKey == PrimaryFp);
        Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => s.AcceptRelease(Manifest(_primary, "2026-11-17T11:00:00.000Z"), false, PrimaryFp, BackupFp)));
        Assert.True(Settings.Load(dir).RevokedKey == PrimaryFp);
    }

    // ---- 4. Nyckeluppsättningen ----

    [Fact]
    public void UpToFourKeysAreAcceptedAndFiveAreNot()
    {
        using var k3 = new TestSigner();
        using var k4 = new TestSigner();
        using var k5 = new TestSigner();
        var json = SignAll(k4, k4.Release("2026-11-17T10:00:00.000Z")).ToJsonString();
        var four = string.Join("\n", _primary.Pem, _backup.Pem, k3.Pem, k4.Pem);
        Assert.Equal(ReleaseVerifier.Fingerprint(k4.Pem), ReleaseVerifier.Verify(json, four).Signer);
        var five = four + "\n" + k5.Pem;
        Assert.Equal(ErrorCodes.NoPublicKey, Code(() => ReleaseVerifier.Verify(json, five)));
        Assert.Equal(ErrorCodes.NoPublicKey, Code(() => ReleaseVerifier.VerifyMac(json, five)));
        Assert.Equal(ErrorCodes.NoPublicKey, Code(() => ReleaseVerifier.VerifyLinux(json, five)));
        Assert.Equal(ErrorCodes.NoPublicKey, Code(() => ReleaseVerifier.Fingerprint(five)));
        var file = ReleaseVerifier.Verify(json, four).ClientFile!;
        Assert.Equal(ErrorCodes.NoPublicKey, Code(() => ReleaseVerifier.VerifyFile(Encoding.ASCII.GetBytes("MZ exe"), ReleaseKind.Client, file, five)));
        // Samma nyckel fem gånger är också fem block.
        Assert.Equal(ErrorCodes.NoPublicKey, Code(() => ReleaseVerifier.Verify(json, string.Join("\n", Enumerable.Repeat(k4.Pem, 5)))));
    }

    [Theory]
    [InlineData("-----BEGIN PUBLIC KEY-----\nAAAA\n-----END PUBLIC KEY-----")]                      // giltig base64, ingen nyckel
    [InlineData("-----BEGIN PUBLIC KEY-----\nMFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE\n-----END PUBLIC KEY-----")] // avhuggen nyckel
    [InlineData("-----BEGIN PUBLIC KEY-----\nA\n-----END PUBLIC KEY-----")]                         // inte ens base64
    [InlineData("-----BEGIN PUBLIC KEY-----\n!!!\n-----END PUBLIC KEY-----")]                       // tecken som inte är base64: får inte hoppas över
    [InlineData("-----BEGIN PUBLIC KEY-----\nAAAA")]                                               // ingen slutrad
    [InlineData("-----BEGIN CERTIFICATE-----\nAAAA\n-----END CERTIFICATE-----")]             // något annat än en publik nyckel
    public void OneBrokenKeyMakesTheWholeSetInvalid(string garbage)
    {
        var json = SignAll(_primary, _primary.Release("2026-11-17T10:00:00.000Z")).ToJsonString();
        Assert.NotNull(ReleaseVerifier.Verify(json, _primary.Pem).ClientFile);
        // Den giltiga nyckeln har signerat – men uppsättningen är trasig, oavsett ordning.
        foreach (var pem in new[] { _primary.Pem + "\n" + garbage, garbage + "\n" + _primary.Pem, garbage })
        {
            Assert.Equal(ErrorCodes.NoPublicKey, Code(() => ReleaseVerifier.Verify(json, pem)));
            Assert.Equal(ErrorCodes.NoPublicKey, Code(() => ReleaseVerifier.VerifyMac(json, pem)));
            Assert.Equal(ErrorCodes.NoPublicKey, Code(() => ReleaseVerifier.VerifyLinux(json, pem)));
            Assert.Equal(ErrorCodes.NoPublicKey, Code(() => ReleaseVerifier.Fingerprint(pem)));
            var file = ReleaseVerifier.Verify(json, _primary.Pem).ClientFile!;
            Assert.Equal(ErrorCodes.NoPublicKey, Code(() => ReleaseVerifier.VerifyFile(Encoding.ASCII.GetBytes("MZ exe"), ReleaseKind.Client, file, pem)));
        }
    }

    [Fact]
    public void AKeyThatIsNotP256IsNotAKey()
    {
        var json = SignAll(_primary, _primary.Release("2026-11-17T10:00:00.000Z")).ToJsonString();
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using var p521 = ECDsa.Create(ECCurve.NamedCurves.nistP521);
        using var rsa = RSA.Create(2048);
        foreach (var bad in new[] { p384.ExportSubjectPublicKeyInfoPem(), p521.ExportSubjectPublicKeyInfoPem(), rsa.ExportSubjectPublicKeyInfoPem() })
            foreach (var pem in new[] { bad, _primary.Pem + "\n" + bad, bad + "\n" + _primary.Pem })
            {
                Assert.Equal(ErrorCodes.NoPublicKey, Code(() => ReleaseVerifier.Verify(json, pem)));
                Assert.Equal(ErrorCodes.NoPublicKey, Code(() => ReleaseVerifier.Fingerprint(pem)));
            }
    }

    [Fact]
    public void ThePlaceholderIsStillNoKey()
    {
        var json = SignAll(_primary, _primary.Release("2026-11-17T10:00:00.000Z")).ToJsonString();
        foreach (var pem in new[] { Placeholder, "", "   ", Placeholder + "\n" + Placeholder })
        {
            Assert.False(ReleaseVerifier.HasKey(pem));
            Assert.Equal(ErrorCodes.NoPublicKey, Code(() => ReleaseVerifier.Verify(json, pem)));
            Assert.Equal(ErrorCodes.NoPublicKey, Code(() => ReleaseVerifier.VerifyMac(json, pem)));
            Assert.Equal(ErrorCodes.NoPublicKey, Code(() => ReleaseVerifier.VerifyLinux(json, pem)));
            Assert.Equal(ErrorCodes.NoPublicKey, Code(() => ReleaseVerifier.Fingerprint(pem)));
        }
        Assert.Equal(ErrorCodes.NoPublicKey, Code(() => ReleaseVerifier.Verify(json, null!)));
        // Nyckeln kontrolleras före svaret: även ett trasigt svar ger "ingen nyckel".
        Assert.Equal(ErrorCodes.NoPublicKey, Code(() => ReleaseVerifier.Verify("inte json", Placeholder)));
    }

    // ---- 5. De riktiga inbyggda nycklarna ----

    [Fact]
    public void TheEmbeddedKeysAreTwoDifferentP256Keys()
    {
        Assert.Matches("^[0-9a-f]{64}$", UpdateKeys.PrimaryFingerprint);
        Assert.Matches("^[0-9a-f]{64}$", UpdateKeys.BackupFingerprint);
        Assert.NotEqual(UpdateKeys.PrimaryFingerprint, UpdateKeys.BackupFingerprint);
        Assert.True(ReleaseVerifier.HasKey(UpdateKeys.PublicKeyPem));
        Assert.True(ReleaseVerifier.HasKey(UpdateKeys.BackupPublicKeyPem));
        // Båda nycklarna tolkas ihop, huvudnyckeln först.
        Assert.Equal(UpdateKeys.PrimaryFingerprint, ReleaseVerifier.Fingerprint(UpdateKeys.All));
        Assert.Equal(UpdateKeys.BackupFingerprint, ReleaseVerifier.Fingerprint(UpdateKeys.BackupPublicKeyPem + "\n" + UpdateKeys.PublicKeyPem));
        Assert.Equal(UpdateKeys.BackupFingerprint, ReleaseVerifier.Fingerprint(UpdateKeys.Trusted(primaryRevoked: true)));
    }

    [Theory]
    [InlineData("version.json")]
    [InlineData("version-beta.json")]
    public void TheCheckedInManifestsStillVerifyWithBothKeys(string name)
    {
        var path = Path.Combine(Repo(), "public", "scout", name);
        if (!File.Exists(path)) return; // ingen sådan kanal ute
        var json = File.ReadAllText(path);
        var r = ReleaseVerifier.Verify(json, UpdateKeys.All);
        Assert.NotNull(r.ClientFile);
        Assert.Contains(r.Signer, new[] { UpdateKeys.PrimaryFingerprint, UpdateKeys.BackupFingerprint });
        // Samma svar som med nyckeln som faktiskt signerade, ensam.
        var alone = r.Signer == UpdateKeys.PrimaryFingerprint ? UpdateKeys.PublicKeyPem : UpdateKeys.BackupPublicKeyPem;
        Assert.Equal(r.Client, ReleaseVerifier.Verify(json, alone).Client);
        Assert.Equal(r.Signer, ReleaseVerifier.Verify(json, alone).Signer);
        // Inget ute återkallar något.
        Assert.Null(r.RevokedKey);
    }

    [Fact]
    public void TheRealAcceptReleaseUsesTheEmbeddedFingerprints()
    {
        // Riktiga nycklar kan inte signera i ett test – men den publika AcceptRelease ska jämföra med just deras
        // fingeravtryck. En versionsinfo "från reservnyckeln" byggs därför för hand.
        var dir = NewDir();
        var s = Settings.Load(dir);
        // Testnycklarnas fingeravtryck betyder ingenting för den riktiga klienten.
        s.AcceptRelease(Manifest(_backup, "2026-11-17T08:00:00.000Z", PrimaryFp));
        s.AcceptRelease(Manifest(_backup, "2026-11-17T08:10:00.000Z", UpdateKeys.PrimaryFingerprint));
        // Huvudnyckeln kan inte återkalla reservnyckeln eller sig själv.
        s.AcceptRelease(new SignedRelease { SignedAll = true, Issued = "2026-11-17T08:20:00.000Z", Signer = UpdateKeys.PrimaryFingerprint, RevokedKey = UpdateKeys.BackupFingerprint });
        s.AcceptRelease(new SignedRelease { SignedAll = true, Issued = "2026-11-17T08:30:00.000Z", Signer = UpdateKeys.PrimaryFingerprint, RevokedKey = UpdateKeys.PrimaryFingerprint });
        Assert.False(s.PrimaryKeyRevoked);
        Assert.Equal(UpdateKeys.All, s.TrustedKeys);

        s.AcceptRelease(new SignedRelease { SignedAll = true, Issued = "2026-11-17T09:00:00.000Z", Signer = UpdateKeys.BackupFingerprint, RevokedKey = UpdateKeys.PrimaryFingerprint });
        Assert.True(s.PrimaryKeyRevoked);
        Assert.Equal(UpdateKeys.BackupPublicKeyPem, s.TrustedKeys);
        Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => s.AcceptRelease(new SignedRelease { SignedAll = true, Issued = "2026-11-17T10:00:00.000Z", Signer = UpdateKeys.PrimaryFingerprint })));
        s.AcceptRelease(new SignedRelease { SignedAll = true, Issued = "2026-11-17T10:00:00.000Z", Signer = UpdateKeys.BackupFingerprint });

        // Med de nycklar klienten nu litar på verifierar inte längre det huvudnyckeln signerat.
        var again = Settings.Load(dir);
        Assert.True(again.PrimaryKeyRevoked);
        var path = Path.Combine(Repo(), "public", "scout", "version.json");
        if (!File.Exists(path)) return;
        var json = File.ReadAllText(path);
        if (ReleaseVerifier.Verify(json, UpdateKeys.All).Signer == UpdateKeys.PrimaryFingerprint)
            Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => ReleaseVerifier.Verify(json, again.TrustedKeys)));
    }
}
