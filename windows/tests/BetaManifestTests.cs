using ForeverScout.Core;
using Xunit;

namespace ForeverScout.Tests;

/// <summary>1.0.13: den signerade beta-filen (public/scout/version-beta.json, scout-sign --channel beta) godtas av
/// samma verifiering som den vanliga och pekar på beta-filerna.</summary>
public class BetaManifestTests
{
    private static string Repo([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", ".."));

    [Fact]
    public void BetaManifest_VerifiesWithReleaseKey()
    {
        var path = Path.Combine(Repo(), "public", "scout", "version-beta.json");
        if (!File.Exists(path)) return; // ingen beta ute
        var r = ReleaseVerifier.Verify(File.ReadAllText(path), UpdateKeys.PublicKeyPem);
        Assert.Equal("ForeverScout-beta.exe", r.File(ReleaseKind.Client)?.FileName);
        Assert.Equal("ForeverScout-lite-beta.exe", r.File(ReleaseKind.Lite)?.FileName);
        Assert.Equal("ForeverScout-addon-beta.zip", r.File(ReleaseKind.Addon)?.FileName);
    }

    /// <summary>
    /// 1.0.18: hela kedjan mot de riktiga filerna, som klienten gör vid en uppdatering – versionsinfon verifieras med
    /// klientens nycklar (på Windows-, Mac- och Linuxvägen), och varje fil som ligger bredvid den kontrolleras mot sitt
    /// signerade SHA-256 och sin filsignatur. Filer som inte finns på byggdatorn (de stora, gitignorerade) hoppas över.
    /// </summary>
    [Theory]
    [InlineData("version.json")]
    [InlineData("version-beta.json")]
    public void TheSignedFilesNextToTheManifestVerifyTheWayTheClientDoesIt(string manifest)
    {
        var dir = Path.Combine(Repo(), "public", "scout");
        var path = Path.Combine(dir, manifest);
        if (!File.Exists(path)) return;
        var json = File.ReadAllText(path);
        var win = ReleaseVerifier.Verify(json, UpdateKeys.All);
        var mac = ReleaseVerifier.VerifyMac(json, UpdateKeys.All);
        var lin = ReleaseVerifier.VerifyLinux(json, UpdateKeys.All);
        Assert.Equal(UpdateKeys.PrimaryFingerprint, win.Signer);
        Assert.Equal(win.Client, lin.Client);
        Assert.Equal(win.Addon, mac.Addon);
        int checkedFiles = 0;
        foreach (var (kind, file) in new[] { (ReleaseKind.Client, win.ClientFile), (ReleaseKind.Lite, win.LiteFile), (ReleaseKind.Addon, win.AddonFile), (ReleaseKind.Mac, mac.MacFile), (ReleaseKind.Linux, lin.LinuxFile) })
        {
            if (file is null) continue;
            var local = Path.Combine(dir, file.FileName);
            if (!File.Exists(local)) continue;
            ReleaseVerifier.VerifyFile(File.ReadAllBytes(local), kind, file, UpdateKeys.All);
            checkedFiles++;
        }
        Assert.True(checkedFiles >= 2, $"bara {checkedFiles} filer kontrollerade"); // lite-exe och addon ligger alltid i repot
    }
}
