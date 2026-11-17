using ForeverScout.Core;
using Xunit;

namespace ForeverScout.Tests;

/// <summary>
/// 2026-11-17: provsignering med reservnyckeln. Sätt SCOUT_RESERVE_TRIAL till en version.json som scout-sign.mjs har
/// signerat med reservnyckelns privata del (till en tillfällig --out). Testet visar då att den privata delen finns och
/// hör ihop med reservnyckeln som klienterna bär (UpdateKeys.BackupPublicKeyPem). Utan variabeln görs ingenting.
/// </summary>
public class ReserveKeyTrialTests
{
    [Fact]
    public void ATrialManifestSignedWithTheReserveKeyIsAcceptedAsTheReserveKey()
    {
        var path = Environment.GetEnvironmentVariable("SCOUT_RESERVE_TRIAL");
        if (string.IsNullOrEmpty(path)) return;
        var json = File.ReadAllText(path);
        var r = ReleaseVerifier.Verify(json, UpdateKeys.All);
        Assert.Equal(UpdateKeys.BackupFingerprint, r.Signer);
        Assert.True(r.SignedAll);
        Assert.Throws<ApiException>(() => ReleaseVerifier.Verify(json, UpdateKeys.PublicKeyPem)); // inte huvudnyckeln
    }
}
