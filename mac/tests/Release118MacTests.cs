using ForeverScout.Core;

namespace ForeverScout.Mac.Tests;

/// <summary>
/// 1.0.18, det som är eget för macOS- och Linux-appen (logiken i Core testas i den delade Release118Tests.cs):
/// att appen (ScoutController, delad av macOS och Linux) verkligen använder beta-kanalen, självuppdateringen på Linux
/// och de nya beskeden – och, på Linux, AppImage-filens rättigheter och $APPIMAGE.
/// </summary>
public class Release118MacTests
{
    private static string AppSource(string file)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "mac", "ForeverScout.Mac", file);
            if (File.Exists(p)) return File.ReadAllText(p);
        }
        throw new FileNotFoundException(file);
    }

    /// <summary>
    /// Beta-kanalen ("Betaversioner" i Inställningar, Windows sedan 1.0.13) fanns redan i macOS- och Linux-appen: rutan i
    /// inställningsfönstret och kanalen i alla tre anropen (addonen, mac-fälten, linux-fälten). Testet håller det så.
    /// </summary>
    [Fact]
    public void MacAndLinuxAppUseTheBetaChannelSetting()
    {
        var settings = AppSource("SettingsWindow.cs");
        Assert.Contains("Check(T(\"set.beta\"))", settings);
        Assert.Contains("_settings.Beta = _beta.IsChecked == true;", settings);
        var app = AppSource("ScoutController.cs");
        Assert.Contains("Api.VersionAsync(Settings.TrustedKeys, Settings.Beta), Settings.Beta)", app);
        // Plattformens fält hämtas från samma kanal, och godkänns (issued, signatureAll) för samma kanal – utanför den
        // try-sats som bara ska svälja "inget släpp för plattformen än".
        Assert.Contains("mac = await Api.MacVersionAsync(Settings.TrustedKeys, Settings.Beta);", app);
        Assert.Contains("if (mac.Issued != null) Settings.AcceptRelease(mac, Settings.Beta);", app);
        Assert.Contains("lin = await Api.LinuxVersionAsync(Settings.TrustedKeys, Settings.Beta);", app);
        Assert.Contains("if (lin.Issued != null) Settings.AcceptRelease(lin, Settings.Beta);", app);
        Assert.Contains("if (Settings.Beta != oldBeta) _ = CheckUpdates(interactive: false);", app); // bytt kanal: hämta direkt
        Assert.True(Strings.Has("set.beta") && Strings.Has("set.beta.hint"));
    }

    /// <summary>Linux-grenen laddar ner den signerade filen (kind linux), byter den via AppImageUpdater och startar om.</summary>
    [Fact]
    public void LinuxBranchDownloadsVerifiesAndSwapsTheAppImage()
    {
        var app = AppSource("ScoutController.cs");
        int a = app.IndexOf("#if LINUX", app.IndexOf("private async Task CheckUpdatesCore", StringComparison.Ordinal), StringComparison.Ordinal);
        int b = app.IndexOf("#else", a, StringComparison.Ordinal);
        var linux = app[a..b];
        Assert.Contains("AppImageUpdater.CanUpdate", linux);                    // bara när filen går att byta …
        Assert.Contains("TF(\"update.manual\", newVersion)", linux);            // … annars beskedet från före 1.0.18
        Assert.Contains("Api.DownloadReleaseFileAsync(lin, ReleaseKind.Linux, Settings.TrustedKeys", linux);
        Assert.True(linux.IndexOf("DownloadReleaseFileAsync", StringComparison.Ordinal) < linux.IndexOf("AppImageUpdater.Stage", StringComparison.Ordinal));
        Assert.True(linux.IndexOf("AppImageUpdater.Stage", StringComparison.Ordinal) < linux.IndexOf("AppImageUpdater.Swap", StringComparison.Ordinal));
        Assert.Contains("if (!interactive && Uploading)", linux);               // aldrig mitt i en uppladdning
        Assert.Contains("Settings.QuietUpdate", linux);                         // inget fönster över spelet efter omstarten
        Assert.True(Strings.Has("linux.update.manual") && Strings.Has("linux.update.manual.log") && Strings.Has("linux.update.inuse"));
        Assert.DoesNotContain("uppdaterar appen inte sig själv än", Strings.All["linux.update.manual"].Sv);
    }

    /// <summary>Fönstret och appen visar de nya raderna (delad kod för macOS och Linux).</summary>
    [Fact]
    public void WindowShowsPicturesPendingPointsAndTheNotice()
    {
        var window = AppSource("MainWindow.cs");
        Assert.Contains("Overview.PicturesLine(st)", window);
        Assert.Contains("Overview.PendingLine(st)", window);
        Assert.Contains("Links.Pictures(_app.Api)", window);
        Assert.Contains("public void ShowNotice()", window);
        var app = AppSource("ScoutController.cs");
        Assert.Contains("GameWatch.Tick(CurrentDir, Settings, DateTime.UtcNow)", app);
        Assert.Contains("AddonReceipt.AfterUpload(CurrentDir, Settings, report, DateTimeOffset.UtcNow)", app);
        Assert.Contains("Overview.Apply(Settings, me)", app);
        Assert.Contains("ReloadReminder.OnInstalled(Settings, previous, installed, DateTime.UtcNow)", app);
        Assert.Contains("Overview.PicturesPath(Strings.IsSv)", AppSource("Support.cs"));
    }

    // ------------------------------------------------------------------ bara på Linux (riktiga rättigheter, $APPIMAGE)

    private static byte[] FakeAppImage(byte fill)
    {
        var b = Enumerable.Repeat(fill, 256).ToArray();
        b[0] = 0x7F; b[1] = (byte)'E'; b[2] = (byte)'L'; b[3] = (byte)'F';
        b[8] = (byte)'A'; b[9] = (byte)'I'; b[10] = 2;
        return b;
    }

    [LinuxFact]
    public void TheNewAppImageKeepsTheOldFilesModeAndIsExecutable()
    {
        var dir = Directory.CreateTempSubdirectory("fs-118-appimage-");
        try
        {
            var image = Path.Combine(dir.FullName, "ForeverScout-x86_64.AppImage");
            File.WriteAllBytes(image, FakeAppImage(1));
            File.SetUnixFileMode(image, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
            SafeFs.OwnAppImageOverride = image;
            AppImageUpdater.Stage(FakeAppImage(2));
            AppImageUpdater.Swap();
            Assert.Equal(FakeAppImage(2), File.ReadAllBytes(image));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute, File.GetUnixFileMode(image));
        }
        finally
        {
            SafeFs.OwnAppImageOverride = null;
            dir.Delete(true);
        }
    }

    [LinuxFact]
    public void AReadOnlyFolderMeansNoSelfUpdate()
    {
        if (Environment.UserName == "root") return; // root får skriva överallt – då säger provet ingenting
        var dir = Directory.CreateTempSubdirectory("fs-118-readonly-");
        try
        {
            var image = Path.Combine(dir.FullName, "ForeverScout-x86_64.AppImage");
            File.WriteAllBytes(image, FakeAppImage(1));
            File.SetUnixFileMode(dir.FullName, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            SafeFs.OwnAppImageOverride = image;
            Assert.False(AppImageUpdater.CanUpdate());
            Assert.Equal(FakeAppImage(1), File.ReadAllBytes(image));
        }
        finally
        {
            SafeFs.OwnAppImageOverride = null;
            File.SetUnixFileMode(dir.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            dir.Delete(true);
        }
    }

    [LinuxFact]
    public void AppImageEnvironmentVariableNamesTheFileToReplace()
    {
        var old = Environment.GetEnvironmentVariable("APPIMAGE");
        var oldDir = Environment.GetEnvironmentVariable("APPDIR");
        try
        {
            Environment.SetEnvironmentVariable("APPIMAGE", "/home/spelare/Applications/ForeverScout-x86_64.AppImage");
            // Ärvd från ett annat program (ingen APPDIR, eller en APPDIR som inte är vår): används aldrig.
            Environment.SetEnvironmentVariable("APPDIR", null);
            Assert.Null(AppImageUpdater.Target);
            Environment.SetEnvironmentVariable("APPDIR", "/tmp/.mount_AnnatProgram");
            SafeFs.ProcessPathOverride = "/opt/forever-scout/ForeverScout";
            Assert.Null(AppImageUpdater.Target);
            // Vår egen avbild: binären ligger under monteringspunkten.
            Environment.SetEnvironmentVariable("APPDIR", "/tmp/.mount_ForeveAbC123");
            SafeFs.ProcessPathOverride = "/tmp/.mount_ForeveAbC123/usr/bin/ForeverScout";
            Assert.Equal("/home/spelare/Applications/ForeverScout-x86_64.AppImage", AppImageUpdater.Target);
            Environment.SetEnvironmentVariable("APPIMAGE", "relativ/ForeverScout.AppImage"); // inte en absolut sökväg: används inte
            Assert.Null(AppImageUpdater.Target);
            Environment.SetEnvironmentVariable("APPIMAGE", null);
            Assert.Null(AppImageUpdater.Target);
        }
        finally
        {
            SafeFs.ProcessPathOverride = null;
            Environment.SetEnvironmentVariable("APPIMAGE", old);
            Environment.SetEnvironmentVariable("APPDIR", oldDir);
        }
    }
}
