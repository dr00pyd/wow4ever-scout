# Forever Scout – Linux-klient

Samma app som macOS-klienten (`client/mac/README.md`): installerar addonen **ForeverScout**, bevakar
`ForeverScout.lua` och laddar upp den (plus `DBCache.bin`) när spelet har sparat, och visar vad bidraget ledde till.
Kontrakt: `docs/briefs/SCOUT-KONTRAKT.md`. Släpps som **en AppImage** (x64), så distributionen spelar ingen roll.

## Teknikval
- **Samma källkod som Mac-appen.** `ForeverScout.Linux.csproj` länkar in källfilerna i `client/mac/ForeverScout.Mac`
  (Avalonia 12.1.4, gränssnittet i C# utan XAML) och sätter konstanten `LINUX`. Egna filer här är bara `Program.cs`
  (start) och `KeychainStore.cs` (nyckelfilen). De få ställen där Linux skiljer sig står under `#if LINUX` i de delade
  filerna; Mac-bygget definierar inte konstanten och är oförändrat.
- **Samma ForeverScout.Core** (`client/windows/src/ForeverScout.Core`): SafeFs, Lua-parsern, uppladdningskön,
  signaturkontrollen, addoninstallationen och texterna. Linux-delarna väljs med `OperatingSystem.IsLinux()`.
- **AppImage, inte Flatpak.** Spelet ligger i ett Wine-prefix någonstans i hemmappen (Lutris, Steam, Bottles …). En
  Flatpak-sandlåda ser inte de mapparna utan att användaren öppnar dem en och en, och appen ska dessutom skriva
  addonen dit. En AppImage är en vanlig fil som körs som användaren, utan installation och utan root (Steam Deck har
  skrivskyddat system).
- **x64.** WoW körs genom Wine, som i praktiken bara finns för x64. `build.sh --arch arm64` bygger men släpps inte.

## Vad appen läser och skriver på Linux
All fil- och processåtkomst går genom `SafeFs.cs` (samma statiska vakt, `StaticGuardTests`, som nu också granskar
`client/linux/ForeverScout.Linux`). Reglerna i spelkatalogen är exakt de som gäller på Windows och macOS.

| Sökväg | Åtkomst |
|---|---|
| `<spelkatalog>/Interface/AddOns/ForeverScout/**` (+ `.new`/`.old` under bytet) | läsa/skriva |
| `<spelkatalog>/WTF/Account/*/SavedVariables/ForeverScout.lua` | läsa |
| `…/SavedVariables/Auctionator.lua`, `…/Auc-Stat-Simple.lua` | läsa, **bara** när "Dela auktionspriser" är på (av som standard) |
| `<spelkatalog>/Cache/ADB/*/DBCache.bin` | läsa |
| `<spelkatalog>/Cache/WDB/*/creaturecache.wdb` | läsa (varelsecachen, 1.0.14 – kan stängas av i Inställningar) |
| `<spelkatalog>/Screenshots/WoWScrnShot_MMDDYY_HHMMSS.jpg`/`.jpeg`/`.png`/`.tga` | läsa (1.0.17 – bara spelets egna skärmbilder, och av dem bara den som en `/scout bild`-post i addonens fil pekar ut; kan stängas av i Inställningar) |
| `Interface`, `WTF/Account/*`, `…/SavedVariables`, `Cache/ADB/*`, `Cache/WDB/*`, `Screenshots` | bara existens och listning |
| `~/.config/ForeverScout/**` (katalogen 0700; `$XDG_CONFIG_HOME` följs) | `settings.json`, `uploads.log`, `instance.lock`, `instance.sock`, `contributor-key` (0600) |
| `~/.config/autostart/se.wow4ever.foreverscout.desktop` | när "Starta vid inloggning" är på – från 1.0.12 förvalt på välkomstskärmen (går att kryssa ur), annars av tills du kryssar i den i Inställningar |
| AppImage-filen själv (`$APPIMAGE`) och `<fil>.new` bredvid den | 1.0.18: självuppdateringen – bara de två filerna (och `.new.tmp` under skrivningen), inget annat i mappen |

**Var spelet söks.** Spelet körs genom Wine, så det ligger i ett Wine-prefix:
`<prefix>/drive_c/Program Files (x86)/World of Warcraft/_forever_` (eller `Program Files`). Appen provar dessa prefix:

| Startare | Prefix |
|---|---|
| Lutris | `~/Games/battlenet`, `~/Games/battle.net`, `~/Games/blizzard-battlenet`, `~/Games/world-of-warcraft`, `~/Games/wow` |
| Steam/Proton | `<Steam>/steamapps/compatdata/*/pfx` för `~/.local/share/Steam`, `~/.steam/steam` och Flatpak-Steam |
| Bottles | `~/.var/app/com.usebottles.bottles/data/bottles/bottles/*`, `~/.local/share/bottles/bottles/*` |
| Faugus Launcher | `~/Faugus/*` |
| Wine | `~/.wine` |

Under Steam, Bottles och Faugus har varje prefix ett eget namn. Där listas **bara katalognamnen** i den kända
föräldern (högst 200, inga filer läses). En hittad katalog används först när användaren bekräftat den en gång
(`linux.gamedir.confirm`), precis som på macOS. Allt annat väljs i Inställningar.

**Nyckeln** ligger i `~/.config/ForeverScout/contributor-key` (0600 i en katalog med 0700). Linux har ingen
nyckelring som finns på alla skrivbord (Steam Deck i spelläge, fönsterhanterare utan Secret Service), och en nyckel
som inte går att läsa vid inloggning hade stoppat uppladdningen i tysthet.

## Skillnader mot macOS-appen
- **Fönstret visas vid start** (utom med `--tray`, som autostart använder). Alla skrivbord har inte fältikoner – GNOME
  kräver ett tillägg. Startas appen en gång till visar den körande instansen sitt fönster.
- **Självuppdatering från 1.0.18** (`ForeverScout.Core/AppImageUpdater.cs`), med samma kontroll som Windows och macOS:
  versionsinfon verifieras med `signature5` och den publika nyckeln i `UpdateKeys.cs`, och AppImage-filen mot sitt
  signerade SHA-256 och sin filsignatur. Filen måste se ut som en AppImage (ELF och märket `AI\x02`), sparas som
  `<AppImage>.new` bredvid den körande filen, får samma rättigheter som den gamla och byts in med **ett** namnbyte
  (`rename(2)` ersätter målet atomärt; den körande appen fortsätter från den gamla filen tills den avslutas). Sedan
  startas den nya filen med `--restarted`. Som på Windows: tyst var 3:e timme, aldrig mitt i en uppladdning, och efter
  tre misslyckade försök i rad bara som en rad i lägerjournalen.
  **Bara när appen körs som AppImage (`$APPIMAGE`) och filen går att byta** (det prövas genom att skapa och ta bort en
  tom `<AppImage>.new`). Annars – uppackad tar.gz, skrivskyddad mapp – säger appen till och länkar till
  nedladdningssidan, som före 1.0.18. Addonen uppdateras automatiskt som på de andra plattformarna.
- **I en AppImage** körs binären från en tillfällig montering (`/tmp/.mount_…`). Autostart och omstart pekar därför på
  själva AppImage-filen (`$APPIMAGE`, `SafeFs.LinuxLaunchPath`).

## 1.0.12
Samma ändringar som macOS-appen (se "1.0.12" i `client/mac/README.md`): "Starta vid inloggning (rekommenderas)" förvald
på välkomstskärmen och en fråga en gång till befintliga användare som själva startar appen (aldrig med `--tray` eller
efter `--restarted`), ärlig status och statusrad, "Välj WoW-katalog" som huvudknapp utan spelkatalog (utan
mappsymbol – alla skrivbord har inte ett färgemoji-typsnitt), ingen dialog vid autostart (rad i lägerjournalen och
fältikonen med röd prick i stället), addonens fel ligger kvar efter nästa synk, och Enter väljer guldknappen.

## 1.0.18
Samma som Windows 1.0.18 (se "1.0.18" i `client/windows/README.md`): bildraden med länken Mina bilder, poäng som väntar
på bekräftelse, påminnelsen om `/reload` (rad i lägerjournalen och besked överst i fönstret – Linux och macOS har inga
bubblor), kvittot i spelet (`Receipt.lua` i addonens mapp) och varningen när spelet sparar utan att addonen gör det.
Dessutom självuppdateringen ovan. Beta-kanalen ("Betaversioner" i Inställningar) fanns redan och gäller även
självuppdateringen: `scout-sign.mjs --channel beta --linux <AppImage> --linux-version <v>`.

**Inte provkört på Linux.** Koden för bytet är körd på Windows mot en låtsasfil (`Release118Tests`); rättigheterna,
den skrivskyddade mappen och `$APPIMAGE` testas av tre `[LinuxFact]`-tester som bara körs på Linux (`build.sh` kör dem i
byggcontainern). Ändringstiden på `SavedVariables`-mappen (varningen) är inte provad i ett Wine-prefix.

## 1.0.17 – bilder tagna med `/scout bild`
Samma som Windows 1.0.15–1.0.17 och macOS-appen (se "1.0.17" i `client/mac/README.md`); 1.0.15 och 1.0.16 släpptes
aldrig för Linux. Bilden som en post i addonens fil pekar ut letas upp i `<spelkatalog>/Screenshots` (i Wine-prefixet,
samma katalog som `WTF` och `Interface` ligger i), beskärs till skärmens mitt, förminskas till högst 1600 px och
skickas som JPEG utan metadata. Inställningen "Ladda upp bilder tagna med /scout bild" är på som standard.
- **Kodaren är `SkiaShotEncoder.cs` i macOS-appens källfiler (SkiaSharp)** – Skia är det Avalonia redan ritar med, så
  `SkiaSharp.dll` och `libSkiaSharp.so` (libjpeg-turbo och libpng inbyggda) fanns redan i AppImage-filen. Inget nytt
  paket, inget nytt systembibliotek hos användaren och inget nytt i byggcontainern: fillistan i paketet är oförändrad
  och det växer bara med koden i `ForeverScout.dll` (några kB).
- Filnamnets tid är lokal tid. Wine ger spelet datorns tidszon, så `WoWScrnShot_MMDDYY_HHMMSS` stämmer med postens
  tid; skulle ett prefix köra med en annan tidszon används filens ändringstid (±3 s) i stället.
- Katalogen måste heta exakt `Screenshots` (så skriver spelet den); Linux filsystem skiljer på stora och små bokstäver.

## Bygga
```bash
client/linux/build.sh                 # tester + dotnet publish linux-x64 + dist/ForeverScout-linux-x64-<v>.tar.gz
```
Går att köra på Linux, macOS och Windows (Git Bash); kräver .NET SDK 10 och Python 3 (packar tar.gz med rätt
rättigheter även från NTFS). **Från 1.0.17** laddar testerna Skia (`ShotEncoderTests`): körs `build.sh` i en
Linux-container utan skrivbord behöver den `libfontconfig1`, som `libSkiaSharp.so` länkar mot
(`apt-get install -y libfontconfig1`) – annars faller testet `TheNativeSkiaLibraryLoads` med just den upplysningen.
På Windows och macOS behövs inget extra. På Linux görs sedan AppImage-filen (kräver `squashfs-tools`):
```bash
curl -fsSLO https://github.com/AppImage/type2-runtime/releases/download/continuous/runtime-x86_64
client/linux/packaging/make-appimage.sh dist/ForeverScout-linux-x64-<v>.tar.gz runtime-x86_64 ForeverScout-x86_64.AppImage
```
En AppImage är AppImage-projektets startare följd av en squashfs-avbild med appen; skriptet sätter ihop dem och
skriver ut startarens SHA-256 (anteckna den i släppanteckningarna). Tar.gz:en fungerar också som den är: packa upp,
starta `ForeverScout/ForeverScout`, och `install.sh` lägger appen i programmenyn.

## Släppa
```bash
node scripts/scout-sign.mjs --key <privat.pem> --version <klientversionen i version.json> \
  --linux ForeverScout-x86_64.AppImage --linux-version <v>
```
Skriver `linux`, `linuxFile`, `linuxSha256`, `linuxSig` och `signature5` i `public/scout/version.json`; övriga delar
och signaturer behålls. AppImage-filen är gitignorerad (som exe:n och Mac-filerna) och kopieras separat vid deploy.
Nedladdningssidan visar Linux-kortet först när fälten och `signature5` finns och filens SHA-256 stämmer.

## Tester
`dotnet test client/mac/tests` – `Release118MacTests.cs` (1.0.18, se ovan) och `LinuxTests.cs`: signature5 och linux-fälten (och att Windows och macOS inte
påverkas), autostart-filens citering, och på Linux (`[LinuxFact]`) nyckelfilens rättigheter och sökrötterna.
Provkört i en Debian-container med virtuell skärm: appen startar, ritar onboarding-fönstret och skapar
`~/.config/ForeverScout` med 0700. **Inte provkört** på ett riktigt skrivbord med WoW under Wine.
`ShotEncoderTests.cs` (1.0.17): bildkodarens mått mot Windows-klientens, ingen metadata, TGA, och hela kedjan med en
spelkatalog i ett Wine-prefix (`…/drive_c/Program Files (x86)/World of Warcraft/_forever_/Screenshots`). Kodaren är
**bara körd på Windows** – kör `dotnet test client/mac/tests` i byggcontainern (det gör `build.sh`) så provas den mot
`libSkiaSharp.so`, och prova sedan en riktig bild med `/scout bild` under Wine.

## Krav på användarens dator
x64, glibc-baserad distribution, X11 eller XWayland, fontconfig och ICU (finns på alla vanliga skrivbord). AppImage
behöver `fusermount` (fuse2 eller fuse3); utan det går filen att starta med `--appimage-extract-and-run`.
