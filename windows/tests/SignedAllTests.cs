using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForeverScout.Core;
using Old = ForeverScout.Tests.Compat102;

namespace ForeverScout.Tests;

/// <summary>
/// 1.0.18: <c>signatureAll</c> – en signatur över de fält som den signerade listan <c>signed</c> räknar upp, så att
/// nya fält och filtyper inte kräver ett nytt klientsläpp. Dessutom lägsta klientversion (<c>minClient</c>) och besked
/// (<c>notice</c>), som bara den signaturen täcker. Plattformsoberoende.
/// </summary>
public sealed class SignedAllTests : IDisposable
{
    /// <summary>Skriven av scripts/scout-sign.mjs (--min-client 1.0.18 --notice … --notice-en Hello) med en testnyckel.</summary>
    private const string NodeJson = """
{
  "v": 1,
  "minClient": "1.0.18",
  "notice": "Hej 🙂 å \"citat\"",
  "noticeEn": "Hello",
  "client": "1.0.18",
  "clientFile": "ForeverScout.exe",
  "sha256": "9bfb304b21d9fc9f2a1da2067947861c1fb0904db486d5040c93d4aed9241c1a",
  "clientSig": "/hSUbXNFX3kvuyw6EkJvHnMDZZCloxpN2ZtCXEaovgQABgTEibvON+2NYu86PIHtr2FZEwava3xdey1NVJB9aA==",
  "addon": "1.0.26",
  "addonFile": "ForeverScout-addon.zip",
  "addonSha256": "1cfb773b59d8bd0dc72729090120c2d4e6f295c764460334e419e7c3c37169ad",
  "addonSig": "x7H4smLknhhq9lUfzDDtu34mWOWA452tXybpSxAbXaOdgwF548YP0PXrb+RMG/jEAgr5GP6tjOaHdNbbdVhSnA==",
  "issued": "2026-11-16T22:25:58.139Z",
  "signed": "addon,addonFile,addonSha256,addonSig,client,clientFile,clientSig,dmg,dmgFile,dmgSha256,dmgSig,issued,linux,linuxFile,linuxSha256,linuxSig,lite,liteFile,liteSha256,liteSig,mac,macFile,macSha256,macSig,minClient,notice,noticeEn,setup,setupFile,setupSha256,setupSig,sha256,signed,v",
  "blockedAddons": [],
  "signature": "poc47O0F0s/V/uXkWTiwcZ/jn7pyD/i10oavZh1MLMU1zUlWAez30okRxHvaLmpLg12gDYHjRgbGAmnat00ITA==",
  "signature2": "zV45Mlgv3RR7mB3OnAkJgyVDL5xbTtbg+MoXZ55g86VOgkKmMTqCUbgUuJOr/j1NfE6xckheIkTDDRH2vMdwig==",
  "signature3": "NWKtPU3vYbj3KpGxTeOGIe2rWjDfGANMRoRS2Agz3Q2LLwfhvjeWJyVsDU/fIc64B3wfhrZgRYKKTmPUlxvIhA==",
  "signature4": "WoqKZNJTB8NWHMbwgnDL/erqc28nh2gkgOkl6yvwZ+WQtxcpfpJyGBxgWvfP+gYUyST92/mqeaLhLlC3fdmxBw==",
  "signature5": "Gm4exjYD6+GXMYUOcqP1m5VqfqXj6DtDc9MwW+uFc6fJORLbIMasC2Y4CoP1niAquehzK3qkPUUbEQBuur8neg==",
  "signatureAll": "JhT+3Cdk/MpFYfrIyf5mZqbnxJWIKJbLSRwYrV8uItIk1VoJ20G6RXWqqKgMF7Cki9to1FuCIaxjJ6vDcphE5w=="
}
""";

    private const string NodePem = """
-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEnDXz1Y97a1Tfq7l0k0Ty4fjYjAzs
i/fWuuXTIjvHiusWFI0LNm8RRFg8fepiNABhv1jUd+jP1UnS8hdD1iGCVQ==
-----END PUBLIC KEY-----
""";

    private readonly TestSigner _signer = new();
    private readonly DirectoryInfo _tmp = TestEnv.Temp("fs-all-");

    public void Dispose()
    {
        _signer.Dispose();
        try { _tmp.Delete(true); } catch (IOException) { }
        SafeFs.UseAppDataDir(TestEnv.AppData);
    }

    private static string Code(Action a) => Assert.IsAssignableFrom<ScoutException>(Record.Exception(a)).Code;

    /// <summary>Lägger till signed (alla fält utom signaturerna och blockedAddons, eller <paramref name="list"/>) och signatureAll.</summary>
    private JsonObject SignAll(JsonObject n, string? list = null)
    {
        n.Remove("signatureAll");
        n["signed"] = list ?? string.Join(",", n.Select(p => p.Key).Append("signed").Where(k => !k.StartsWith("signature", StringComparison.Ordinal) && k != "blockedAddons")
            .Distinct().OrderBy(k => k, StringComparer.Ordinal));
        using var doc = JsonDocument.Parse(n.ToJsonString());
        n["signatureAll"] = _signer.Sign(Encoding.UTF8.GetBytes(ReleaseVerifier.Canonical(doc.RootElement, n["signed"]!.GetValue<string>().Split(','))));
        return n;
    }

    private JsonObject Release(string issued = "2026-11-17T10:00:00.000Z") => SignAll(_signer.Release(issued));

    [Fact]
    public void ManifestSignedByTheRealScriptVerifiesOnEveryPlatform()
    {
        foreach (var r in new[] { ReleaseVerifier.Verify(NodeJson, NodePem), ReleaseVerifier.VerifyMac(NodeJson, NodePem), ReleaseVerifier.VerifyLinux(NodeJson, NodePem) })
        {
            Assert.True(r.SignedAll);
            Assert.Equal("1.0.18", r.Client);
            Assert.Equal("1.0.26", r.Addon);
            Assert.NotNull(r.ClientFile);
            Assert.NotNull(r.AddonFile);
            Assert.Equal("1.0.18", r.MinClient);
            // Tecken utanför BMP (ett surrogatpar) och citattecken: samma kanoniska form som JSON.stringify.
            Assert.Equal("Hej \U0001F642 å \"citat\"", r.Notice);
            Assert.Equal("Hello", r.NoticeEn);
            Assert.Equal(r.Notice, r.NoticeFor(swedish: true));
            Assert.Equal("Hello", r.NoticeFor(swedish: false));
            Assert.True(r.TooOld(new Version(1, 0, 17)));
            Assert.False(r.TooOld(new Version(1, 0, 18)));
            Assert.False(r.TooOld(new Version(1, 0, 18, 0)));
            Assert.False(r.TooOld(new Version(1, 1, 0)));
        }
        // Filsignaturerna är samma som förut.
        var rel = ReleaseVerifier.Verify(NodeJson, NodePem);
        ReleaseVerifier.VerifyFile(Encoding.ASCII.GetBytes("MZ fixture exe"), ReleaseKind.Client, rel.ClientFile!, NodePem);
        ReleaseVerifier.VerifyFile(Encoding.ASCII.GetBytes("PK fixture addon"), ReleaseKind.Addon, rel.AddonFile!, NodePem);
    }

    [Fact]
    public void OlderClientsStillVerifyTheSameManifest()
    {
        // 1.0.0–1.0.2 läser "signature", som skrivs som förut; de nya fälten rör dem inte.
        Assert.Equal("1.0.18", Old.ReleaseVerifier.Verify(NodeJson, NodePem).Client);
        // Och utan signatureAll (ett manifest från före 1.0.18) gäller plattformens gamla signatur – utan de nya fälten.
        var n = JsonNode.Parse(NodeJson)!.AsObject();
        n.Remove("signatureAll");
        var r = ReleaseVerifier.Verify(n.ToJsonString(), NodePem);
        Assert.False(r.SignedAll);
        Assert.Equal("1.0.18", r.Client);
        Assert.Null(r.MinClient);
        Assert.Null(r.Notice);
        Assert.Null(r.NoticeEn);
        Assert.False(r.TooOld(new Version(1, 0, 0)));
        // Samma på Mac och Linux: deras egna signaturer (signature3, signature5) skrivs som förut.
        foreach (var p in new[] { ReleaseVerifier.VerifyMac(n.ToJsonString(), NodePem), ReleaseVerifier.VerifyLinux(n.ToJsonString(), NodePem) })
        {
            Assert.False(p.SignedAll);
            Assert.Equal("1.0.18", p.Client);
            Assert.Null(p.MinClient);
        }
    }

    [Fact]
    public void ALoneSurrogateIsABrokenReplyNotACrash()
    {
        // Går att tolka som JSON men inte att läsa ut som sträng – ska bli ett vanligt fel som uppdateringskontrollen fångar.
        var json = NodeJson.Replace("\"Hello\"", "\"\\ud800\"");
        Assert.NotEqual(NodeJson, json);
        Assert.IsAssignableFrom<ScoutException>(Record.Exception(() => ReleaseVerifier.Verify(json, NodePem)));
        var list = NodeJson.Replace("\"signed\": \"addon,", "\"signed\": \"\\ud800,");
        Assert.NotEqual(NodeJson, list);
        Assert.IsAssignableFrom<ScoutException>(Record.Exception(() => ReleaseVerifier.Verify(list, NodePem)));
    }

    [Fact]
    public void BackslashesAndControlCharactersAreEscapedLikeJsonStringify()
    {
        // Samma bokstav för bokstav som JSON.stringify({a:…}) i Node (kontrollerat mot Node 2026-11-16).
        using var doc = JsonDocument.Parse("""{"a":"C:\\x \"q\" \u0001 \u007f \u2028 å \ud83d\ude42","b":7,"c":null}""");
        Assert.Equal("""{"a":"C:\\x \"q\" \u0001 \u007f \u2028 å \U0001F642","b":7,"c":null,"d":null}"""
            .Replace("\\u007f", "\u007f").Replace("\\u2028", "\u2028").Replace("\\U0001F642", "\U0001F642"),
            ReleaseVerifier.Canonical(doc.RootElement, ["a", "b", "c", "d"]));
    }

    [Theory]
    [InlineData("minClient", "1.0.99")]
    [InlineData("notice", "Annat besked")]
    [InlineData("client", "9.9.9")]
    [InlineData("issued", "2030-01-01T00:00:00.000Z")]
    [InlineData("signed", "issued,signed,v")]
    public void AChangedSignedFieldIsRejectedOnEveryPlatform(string field, string value)
    {
        var n = JsonNode.Parse(NodeJson)!.AsObject();
        n[field] = value;
        var json = n.ToJsonString();
        Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => ReleaseVerifier.Verify(json, NodePem)));
        Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => ReleaseVerifier.VerifyMac(json, NodePem)));
        Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => ReleaseVerifier.VerifyLinux(json, NodePem)));
    }

    [Fact]
    public void ARemovedSignedFieldIsRejected()
    {
        var n = JsonNode.Parse(NodeJson)!.AsObject();
        n.Remove("minClient");
        Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => ReleaseVerifier.Verify(n.ToJsonString(), NodePem)));
    }

    [Fact]
    public void ABrokenSignatureAllNeverFallsBackToTheOldSignature()
    {
        var n = _signer.Release("2026-11-17T10:00:00.000Z");
        Assert.NotNull(ReleaseVerifier.Verify(n.ToJsonString(), _signer.Pem).ClientFile); // signature2 stämmer
        SignAll(n);
        var sig = Convert.FromBase64String(n["signatureAll"]!.GetValue<string>());
        sig[10] ^= 1;
        n["signatureAll"] = Convert.ToBase64String(sig);
        Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => ReleaseVerifier.Verify(n.ToJsonString(), _signer.Pem)));
        // signatureAll utan listan är också ogiltig.
        n.Remove("signed");
        Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => ReleaseVerifier.Verify(n.ToJsonString(), _signer.Pem)));
        // Signerad med en annan nyckel.
        using var other = new TestSigner();
        Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => ReleaseVerifier.Verify(Release().ToJsonString(), other.Pem)));
    }

    [Fact]
    public void ANewSignedFieldNeedsNoNewClient()
    {
        // Poängen med signatureAll: ett fält (en filtyp) som klienten inte känner till signeras och godtas ändå.
        var n = _signer.Release("2026-11-17T10:00:00.000Z");
        n["arm"] = "1.0.19";
        n["armFile"] = "ForeverScout-arm64.exe";
        n["armSha256"] = new string('a', 64);
        var r = ReleaseVerifier.Verify(SignAll(n).ToJsonString(), _signer.Pem);
        Assert.True(r.SignedAll);
        Assert.Equal("1.0.7", r.Client);
        // … och går inte att ändra efteråt.
        n["armFile"] = "Annan.exe";
        Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => ReleaseVerifier.Verify(n.ToJsonString(), _signer.Pem)));
    }

    [Fact]
    public void AFieldOutsideTheSignedListIsNotTrusted()
    {
        var n = Release();
        // Tillagda EFTER signeringen, utanför listan: signaturen stämmer fortfarande, men fälten finns inte för klienten.
        n["minClient"] = "9.9.9";
        n["notice"] = "Osignerat besked";
        n["mac"] = "9.9.9";
        var r = ReleaseVerifier.Verify(n.ToJsonString(), _signer.Pem);
        Assert.True(r.SignedAll);
        Assert.Null(r.MinClient);
        Assert.Null(r.Notice);
        Assert.Null(r.Mac);
        Assert.False(r.TooOld(new Version(1, 0, 0)));
        // blockedAddons är osignerat som förut (bara för att stoppa uppladdningen).
        n["blockedAddons"] = new JsonArray("1.0.5");
        Assert.True(ReleaseVerifier.Verify(n.ToJsonString(), _signer.Pem).IsAddonBlocked("1.0.5"));
    }

    [Theory]
    [InlineData("v,signed,issued")]                 // inte i bokstavsordning
    [InlineData("issued,issued,signed,v")]          // dubblett
    [InlineData("issued,signed")]                   // v saknas (och för kort)
    [InlineData("client,signed,v")]                 // issued saknas
    [InlineData("client,issued,v")]                 // signed saknas
    [InlineData("issued,signature2,signed,v")]      // ett signaturfält
    [InlineData("issued,signed,v,")]                // tomt namn
    [InlineData("issued,signed,v,x y")]             // ogiltigt namn
    [InlineData("__proto__,issued,signed,v")]       // ogiltigt namn
    public void AMalformedSignedListIsRejectedEvenWhenCorrectlySigned(string list)
    {
        var n = SignAll(_signer.Release("2026-11-17T10:00:00.000Z"), list);
        Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => ReleaseVerifier.Verify(n.ToJsonString(), _signer.Pem)));
    }

    [Fact]
    public void ASignedListThatIsNotAStringIsRejected()
    {
        var n = Release();
        n["signed"] = new JsonArray("issued", "signed", "v");
        Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => ReleaseVerifier.Verify(n.ToJsonString(), _signer.Pem)));
    }

    [Fact]
    public void NoticeIsOneCleanLineAndAnInvalidMinimumMeansNoLimit()
    {
        var n = _signer.Release("2026-11-17T10:00:00.000Z");
        n["notice"] = "  rad ett\nrad två\t" + new string('x', 400);
        n["noticeEn"] = "   ";
        n["minClient"] = "inte en version";
        var r = ReleaseVerifier.Verify(SignAll(n).ToJsonString(), _signer.Pem);
        Assert.StartsWith("rad ett rad två x", r.Notice);
        Assert.Equal(ReleaseVerifier.MaxNotice, r.Notice!.Length);
        Assert.Null(r.NoticeEn);
        Assert.Equal(r.Notice, r.NoticeFor(swedish: false)); // det andra språket när det egna saknas
        Assert.Null(r.MinClient);
        Assert.False(r.TooOld(new Version(0, 0, 1)));
    }

    [Fact]
    public void OnceSeenSignatureAllIsRequiredAndThatSurvivesRestart()
    {
        var dir = Path.Combine(_tmp.FullName, "appdata");
        var old1 = ReleaseVerifier.Verify(_signer.Release("2026-11-17T08:00:00.000Z").ToJsonString(), _signer.Pem);
        var all = ReleaseVerifier.Verify(Release("2026-11-17T09:00:00.000Z").ToJsonString(), _signer.Pem);
        var old2 = ReleaseVerifier.Verify(_signer.Release("2026-11-17T10:00:00.000Z").ToJsonString(), _signer.Pem);

        var s = Settings.Load(dir);
        s.AcceptRelease(old1); // före första signatureAll: den gamla signaturen räcker
        Assert.False(s.ReleaseSignedAll);
        s.AcceptRelease(all);
        Assert.True(s.ReleaseSignedAll);
        // En nyare versionsinfo UTAN signatureAll (någon har plockat bort den) godtas inte längre.
        Assert.Equal(ErrorCodes.SignatureMissing, Code(() => s.AcceptRelease(old2)));
        Assert.Equal("2026-11-17T09:00:00.000Z", s.ReleaseIssued);

        var again = Settings.Load(dir);
        Assert.True(again.ReleaseSignedAll);
        Assert.Equal(ErrorCodes.SignatureMissing, Code(() => again.AcceptRelease(old2)));
        again.AcceptRelease(all);
        // Kanalerna är skilda: beta har inte sett någon signatureAll än.
        Assert.False(again.ReleaseSignedAllBeta);
        again.AcceptRelease(old2, beta: true);
        again.AcceptRelease(ReleaseVerifier.Verify(Release("2026-11-17T11:00:00.000Z").ToJsonString(), _signer.Pem), beta: true);
        Assert.True(Settings.Load(dir).ReleaseSignedAllBeta);
    }

    [Fact]
    public void ANoticeIsShownOncePerTextAndForgottenWhenItIsRemoved()
    {
        var s = Settings.Load(Path.Combine(_tmp.FullName, "appdata2"));
        SignedRelease With(string? sv, string? en) => new() { SignedAll = true, Notice = sv, NoticeEn = en };
        Assert.Null(s.TakeNotice(With(null, null), swedish: true));
        Assert.Equal("Raiden öppnar 9 december", s.TakeNotice(With("Raiden öppnar 9 december", "The raid opens 9 December"), swedish: true));
        Assert.Null(s.TakeNotice(With("Raiden öppnar 9 december", "The raid opens 9 December"), swedish: true));
        Assert.Equal("Nytt besked", s.TakeNotice(With("Nytt besked", null), swedish: true));
        Assert.Null(s.TakeNotice(With(null, null), swedish: true)); // borttaget: inget att visa, men glömt
        Assert.Equal("Nytt besked", s.TakeNotice(With("Nytt besked", null), swedish: true));
        Assert.Equal("Nytt besked", Settings.Load(Path.Combine(_tmp.FullName, "appdata2")).NoticeSeen);
    }

    [Fact]
    public void TheClientNoLongerRefusesAtTheServersOldFiveMegabytes()
    {
        // Servern avgör (413 too-large) – klientens tak är bara ett skydd mot orimliga filer.
        Assert.True(ApiClient.MaxUploadBytes >= 32L * 1024 * 1024);
        Assert.True(ApiClient.MaxUploadBytes <= 64L * 1024 * 1024); // SafeFs läser högst 64 MB
        Assert.Contains("client-too-old", ErrorCodes.Server);
        Assert.True(Strings.Has("srv.client-too-old"));
    }
}
