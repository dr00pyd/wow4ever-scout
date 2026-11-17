namespace ForeverScout.Core;

/// <summary>
/// 1.0.18, Linux: byter AppImage-filen mot en ny version. Motsvarigheten till <see cref="SelfUpdater"/> (Windows) och
/// <see cref="MacBundleUpdater"/> (macOS).
/// <list type="number">
/// <item>Filen (linuxFile) har redan kontrollerats mot den signerade versionsinfon (signature5): SHA-256 och vår
/// ECDSA-filsignatur (<see cref="ApiClient.DownloadReleaseFileAsync"/>) – samma kontroll och samma publika nyckel
/// (<see cref="UpdateKeys"/>) som på Windows och macOS.</item>
/// <item><see cref="Stage"/>: filen måste se ut som en AppImage (ELF och AppImage-märket), sparas som
/// <c>&lt;AppImage&gt;.new</c> bredvid den körande filen (samma filsystem) och görs körbar.</item>
/// <item><see cref="Swap"/>: ett enda namnbyte ersätter den gamla filen atomärt. Den körande appen fortsätter från den
/// gamla filen (den är redan monterad) tills den avslutas; anroparen startar sedan den nya med <c>--restarted</c>.</item>
/// </list>
/// Bara när appen körs som AppImage ($APPIMAGE) och filen går att byta (<see cref="CanUpdate"/>). Annars – uppackad
/// tar.gz, skrivskyddad katalog – säger appen till och länkar till nedladdningssidan, som före 1.0.18. Inga skript,
/// ingen root, inget laddas ner och körs utanför den signerade filen.
/// </summary>
public static class AppImageUpdater
{
    /// <summary>AppImage-filen som appen körs från, eller null.</summary>
    public static string? Target => SafeFs.OwnAppImage;

    public static string Staged => (Target ?? throw new ScoutException(ErrorCodes.ExePath)) + ".new";

    /// <summary>En AppImage av typ 2: ELF-huvud och märket "AI\x02" på byte 8–10.</summary>
    public static bool LooksLikeAppImage(ReadOnlySpan<byte> data) =>
        data.Length > 16 && data[0] == 0x7F && data[1] == (byte)'E' && data[2] == (byte)'L' && data[3] == (byte)'F'
        && data[8] == (byte)'A' && data[9] == (byte)'I' && data[10] == 2;

    /// <summary>
    /// Går filen att byta? Den ska finnas, och katalogen ska gå att skriva i – det prövas genom att skapa och ta bort
    /// en tom <c>&lt;AppImage&gt;.new</c>. Att öppna själva filen för skrivning går inte: den körs.
    /// </summary>
    public static bool CanUpdate()
    {
        try
        {
            if (Target is not { } image || !SafeFs.FileExists(image)) return false;
            SafeFs.WriteAllBytes(image + ".new", []);
            Cleanup();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ScoutException) { return false; }
    }

    /// <summary>Tar bort rester från ett avbrutet försök (bästa försök).</summary>
    public static void Cleanup()
    {
        if (Target is not { } image) return;
        foreach (var f in new[] { image + ".new.tmp", image + ".new" })
        {
            try { SafeFs.DeleteFile(f); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SafeFsException) { }
        }
    }

    private static UpdateStepException Fail(string step, Exception ex, string path) => new(step, ex, Path.GetFileName(path));

    /// <summary>Sparar den nya filen som &lt;AppImage&gt;.new och gör den körbar.</summary>
    public static void Stage(byte[] image)
    {
        var target = Target ?? throw new ScoutException(ErrorCodes.ExePath);
        // Räknas som ett misslyckat försök (UpdateFailures), så att appen inte hämtar samma fil var tredje timme för alltid.
        if (!LooksLikeAppImage(image)) throw Fail(UpdateSteps.Stage, new InvalidDataException("not-appimage"), target);
        try
        {
            Cleanup();
            SafeFs.WriteAllBytes(Staged, image);
            SafeFs.CopyOwnAppImageMode();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw Fail(UpdateSteps.Stage, ex, target); }
    }

    public static bool HasStaged => Target is not null && SafeFs.FileExists(Staged);

    /// <summary>Byter filen (ett atomärt namnbyte). Misslyckas det ligger den gamla filen orörd kvar.</summary>
    public static void Swap() => Swap(null);

    /// <summary>
    /// Som <see cref="Swap()"/>, men filen på disk kontrolleras först mot den signerade filens SHA-256: mellan
    /// nedladdningen och bytet (frågan "Uppdatera nu?" kan stå öppen länge) får ingen ha bytt ut &lt;AppImage&gt;.new.
    /// </summary>
    public static void Swap(string? expectedSha256)
    {
        var target = Target ?? throw new ScoutException(ErrorCodes.ExePath);
        if (!HasStaged) throw Fail(UpdateSteps.SwapMove2, new FileNotFoundException(), target);
        if (expectedSha256 is not null)
        {
            string onDisk;
            try { onDisk = ApiClient.Sha256Hex(SafeFs.ReadAllBytes(Staged, 600L * 1024 * 1024)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ScoutException) { Cleanup(); throw Fail(UpdateSteps.SwapMove2, ex, target); }
            if (!string.Equals(onDisk, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                Cleanup();
                throw Fail(UpdateSteps.SwapMove2, new InvalidDataException("staged-changed"), target);
            }
        }
        try { SafeFs.ReplaceOwnAppImage(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Cleanup();
            throw Fail(UpdateSteps.SwapMove2, ex, target);
        }
    }
}
