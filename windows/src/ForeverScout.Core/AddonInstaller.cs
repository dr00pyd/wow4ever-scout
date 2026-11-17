using System.IO.Compression;

namespace ForeverScout.Core;

/// <summary>Installerar/uppdaterar addonen i &lt;spel&gt;\Interface\AddOns\ForeverScout.</summary>
public static class AddonInstaller
{
    public const string AddonName = "ForeverScout";

    /// <summary>Filtyper som får finnas i addonen. Inget körbart.</summary>
    private static readonly HashSet<string> AllowedExt = new(StringComparer.OrdinalIgnoreCase)
        { ".lua", ".toc", ".xml", ".md", ".txt", ".tga", ".blp" };

    public sealed record AddonFile(string RelativePath, byte[] Data);

    public static string TargetDir(GameDir dir) => Path.Combine(dir.AddOnsDir, AddonName);

    /// <summary>"## Version: 1.0.0" ur .toc-filen.</summary>
    public static string? TocVersion(string tocText)
    {
        foreach (var line in tocText.Split('\n'))
        {
            var l = line.Trim();
            if (l.StartsWith("## Version:", StringComparison.OrdinalIgnoreCase)) return l[11..].Trim();
        }
        return null;
    }

    public static string? InstalledVersion(GameDir dir)
    {
        try
        {
            var toc = SafeFs.DirectoryExists(TargetDir(dir)) ? SafeFs.GetFiles(TargetDir(dir), "*.toc").OrderBy(f => f.Length).FirstOrDefault() : null;
            return toc is null ? null : TocVersion(SafeFs.ReadAllText(toc));
        }
        catch (Exception ex) when (ex is SafeFsException or IOException or UnauthorizedAccessException) { return null; }
    }

    public static string? BundledVersion(IReadOnlyList<AddonFile> files)
    {
        var toc = files.FirstOrDefault(f => f.RelativePath.EndsWith(".toc", StringComparison.OrdinalIgnoreCase) && !f.RelativePath.Contains('/'));
        return toc is null ? null : TocVersion(System.Text.Encoding.UTF8.GetString(toc.Data));
    }

    /// <summary>Jämför versioner "1.2.3" (saknad/ogiltig räknas som äldst).</summary>
    public static int CompareVersions(string? a, string? b)
    {
        static Version P(string? s) => Version.TryParse((s ?? "").Split('-', '+')[0], out var v) ? v : new Version(0, 0);
        return P(a).CompareTo(P(b));
    }

    /// <summary>Vad ska göras med addonen efter en versionskontroll? Se <see cref="Decide"/>.</summary>
    public enum UpdateAction
    {
        /// <summary>Inget att göra.</summary>
        None,
        /// <summary>Hämta och installera den signerade addonen (ingen addon finns, eller den signerade är nyare).</summary>
        Install,
        /// <summary>Den installerade versionen är spärrad och ingen nyare signerad finns: ladda inte upp, tala om det.</summary>
        BlockedWaiting,
    }

    /// <summary>
    /// L10: installera bara den signerade addonen om ingen addon finns eller om den är NYARE än den installerade.
    /// Spärrlistan (<c>blockedAddons</c>) och serverns <c>422 addon-blocked</c> (<paramref name="alsoBlocked"/>) är inte
    /// signerade – de får bara stoppa uppladdningen och visa ett meddelande, aldrig leda till en äldre addon. En
    /// nedgradering skulle kräva ett signerat fält, och något sådant finns inte i versionsinfon (1.0.7).
    /// </summary>
    public static UpdateAction Decide(string? installed, SignedRelease release, IEnumerable<string>? alsoBlocked = null)
    {
        bool releaseUsable = release.AddonFile is not null && !release.IsAddonBlocked(release.Addon);
        if (releaseUsable && (installed is null || CompareVersions(release.Addon, installed) > 0)) return UpdateAction.Install;
        bool installedBlocked = installed is not null
            && (release.IsAddonBlocked(installed) || (alsoBlocked?.Contains(installed, StringComparer.Ordinal) ?? false));
        return installedBlocked ? UpdateAction.BlockedWaiting : UpdateAction.None;
    }

    /// <summary>Behöver addonen installeras eller uppdateras?</summary>
    public static bool NeedsInstall(GameDir dir, IReadOnlyList<AddonFile> files) =>
        files.Count > 0 && (InstalledVersion(dir) is not { } inst || CompareVersions(BundledVersion(files), inst) > 0);

    /// <summary>Skriver filerna till en temporär katalog bredvid och byter sedan ut den gamla (atomärt så långt det går).</summary>
    public static void Install(GameDir dir, IReadOnlyList<AddonFile> files)
    {
        if (files.Count == 0) throw new ScoutException(ErrorCodes.AddonMissing);
        SafeFs.CreateDirectory(dir.AddOnsDir);
        var target = TargetDir(dir);
        var staging = target + ".new";
        var old = target + ".old";
        SafeFs.DeleteDirectory(staging);
        SafeFs.CreateDirectory(staging);
        var stagingFull = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
        // 1.0.18: kvittot i spelet (Receipt.lua) skrivs av klienten efter varje uppladdning. Den nya addonen kommer med
        // en tom standardfil – ett befintligt kvitto behålls, annars skulle addonen efter en uppdatering säga att inget
        // har skickats. Kvittot läses strikt (bara klientens eget format) och skrivs om från talen, aldrig kopierat.
        var receipt = AddonReceipt.ReadInstalled(dir);
        // 1.0.19: en daglig bildlista som klienten skrivit och som är nyare än den som följer med addonen behålls (läst
        // strikt, skriven om från talen – samma princip som kvittot).
        var ownList = ShotListFile.ReadInstalledOwn(dir);
        foreach (var f in files)
        {
            var rel = f.RelativePath.Replace('/', Path.DirectorySeparatorChar);
            if (!AllowedExt.Contains(Path.GetExtension(rel))) continue;
            var dest = Path.GetFullPath(Path.Combine(staging, rel));
            if (!dest.StartsWith(stagingFull, StringComparison.OrdinalIgnoreCase)) continue; // ingen ../
            SafeFs.CreateDirectory(Path.GetDirectoryName(dest)!);
            bool isReceipt = receipt is not null && string.Equals(f.RelativePath, AddonReceipt.FileName, StringComparison.OrdinalIgnoreCase);
            bool keepList = ownList is not null && string.Equals(f.RelativePath, ShotListFile.FileName, StringComparison.OrdinalIgnoreCase)
                && string.CompareOrdinal(ownList.Built, ShotListFile.BuiltOf(System.Text.Encoding.UTF8.GetString(f.Data)) ?? "") > 0;
            SafeFs.WriteAllBytes(dest, isReceipt ? System.Text.Encoding.ASCII.GetBytes(AddonReceipt.Render(receipt!))
                : keepList ? System.Text.Encoding.ASCII.GetBytes(ShotListFile.Render(ownList!)) : f.Data);
        }
        SafeFs.DeleteDirectory(old);
        bool hadOld = SafeFs.DirectoryExists(target);
        if (hadOld) SafeFs.MoveDirectory(target, old);
        try
        {
            SafeFs.MoveDirectory(staging, target);
            // Den nya addonen ska gå att läsa (rätt .toc) innan den gamla tas bort.
            if (BundledVersion(files) is { } want && CompareVersions(InstalledVersion(dir), want) != 0) throw new ScoutException(ErrorCodes.AddonInvalid);
        }
        catch (Exception ex) when (hadOld && ex is IOException or UnauthorizedAccessException or ScoutException)
        {
            // 1.0.19: bytet misslyckades halvvägs (spelet eller en virusskanner höll en fil, disken full) – förr kunde
            // spelaren stå helt utan addon. Nu läggs den gamla tillbaka och felet visas; nästa kontroll försöker igen.
            Rollback(target, old, staging);
            throw;
        }
        try { SafeFs.DeleteDirectory(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Lägger tillbaka den gamla addonen efter ett misslyckat byte (bästa försök).</summary>
    private static void Rollback(string target, string old, string staging)
    {
        try
        {
            if (!SafeFs.DirectoryExists(old)) return;
            if (SafeFs.DirectoryExists(target))
            {
                SafeFs.DeleteDirectory(staging);
                SafeFs.MoveDirectory(target, staging); // den halvfärdiga nya – städas vid nästa installation
            }
            SafeFs.MoveDirectory(old, target);
            UploadLog.Append("addon", 0, "", "rollback ok");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ScoutException)
        {
            UploadLog.Append("addon", 0, "", "rollback failed " + ex.GetType().Name);
        }
    }

    /// <summary>Läser en addon-zip (från servern, redan sha256-kontrollerad). Mappen "ForeverScout/" i zip:en tas bort ur sökvägen.</summary>
    public static List<AddonFile> FromZip(byte[] zip)
    {
        try { return ReadZip(zip); }
        catch (InvalidDataException ex) { throw new ScoutException(ErrorCodes.AddonInvalid, inner: ex); }
    }

    private static List<AddonFile> ReadZip(byte[] zip)
    {
        var list = new List<AddonFile>();
        using var za = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        long total = 0;
        foreach (var e in za.Entries)
        {
            if (string.IsNullOrEmpty(e.Name)) continue;
            var rel = e.FullName.Replace('\\', '/');
            if (rel.StartsWith(AddonName + "/", StringComparison.OrdinalIgnoreCase)) rel = rel[(AddonName.Length + 1)..];
            if (rel.Contains("..") || rel.StartsWith('/')) continue;
            if (!AllowedExt.Contains(Path.GetExtension(rel))) continue;
            total += e.Length;
            if (total > 20 * 1024 * 1024) throw new ScoutException(ErrorCodes.AddonTooLarge);
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            list.Add(new AddonFile(rel, ms.ToArray()));
        }
        if (BundledVersion(list) is null) throw new ScoutException(ErrorCodes.AddonInvalid);
        return list;
    }
}
