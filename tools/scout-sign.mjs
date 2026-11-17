#!/usr/bin/env node
// Signerar ett Forever Scout-släpp (Windows-klient och/eller addon) med ECDSA P-256 / SHA-256.
//
//   node scripts/scout-sign.mjs --genkey <katalog>
//       Skapar ett nyckelpar: <katalog>/scout-release-private.pem (hemlig, checka ALDRIG in) och
//       <katalog>/scout-release-public.pem. Skriver bara ut den publika nyckeln – klistra in den i
//       client/windows/src/ForeverScout.Core/UpdateKeys.cs.
//
//   node scripts/scout-sign.mjs --key <privat.pem> --version x.y.z [--exe <full/ForeverScout.exe>]
//       [--exe-lite <lite/ForeverScout.exe>] [--lite-version x.y.z]
//       [--setup <ForeverScout-Setup-x.y.z.exe>] [--setup-version x.y.z]
//       [--addon <ForeverScout-addon.zip>] [--addon-version x.y.z] [--out public/scout] [--block 0.9.0,0.9.1]
//       [--mac <ForeverScout-mac.zip>] [--mac-version x.y.z] [--allow-unnotarized] [--dmg <ForeverScout-x.y.z.dmg>]
//       [--linux <ForeverScout-x86_64.AppImage>] [--linux-version x.y.z]
//       [--min-client x.y.z|none] [--notice "text"|none] [--notice-en "text"|none]
//       [--revoke-primary <huvudnyckelns publika .pem>|none] [--client-keys <UpdateKeys.cs>]
//       Kopierar filerna till <out> (ForeverScout.exe, ForeverScout-addon.zip, ForeverScout-mac.zip) och skriver
//       <out>/version.json. En del som inte anges (t.ex. bara --addon) behålls från befintlig version.json med sin
//       gamla filsignatur.
//
//   --mac: zip:en med det FÄRDIGA macOS-paketet – kodsignerat (Developer ID), notariserat och staplat
//       (client/mac/build.sh). Vår signatur läggs alltså ovanpå Apples. Skriptet kontrollerar att zip:en innehåller
//       *.app/Contents/Info.plist, _CodeSignature/CodeResources och den staplade biljetten Contents/CodeResources;
//       --allow-unnotarized hoppar över de två sista (bara för test, släpp aldrig en sådan fil).
//
// Format (måste matcha client/windows/src/ForeverScout.Core/ReleaseVerifier.cs):
//   - Filsignatur (clientSig/addonSig) över UTF-8 "ForeverScout-file/v1\n<client|addon>\n<version>\n<sha256 hex>".
//   - signature över kanonisk JSON av exakt SIGNED_FIELDS i bokstavsordning (saknat = null), JSON.stringify.
//     Den ÄNDRAS INTE i format: klienter 1.0.0–1.0.2 verifierar den och uppdaterar till full-exe:n (clientFile).
//   - signature2 (från 1.0.3) över SIGNED_FIELDS_2 = SIGNED_FIELDS + lite, liteFile, liteSha256, liteSig.
//     Nya klienter verifierar signature2 och väljer fil efter variant (full → clientFile, lite → liteFile).
//   - Lite-filens filsignatur: kind "lite". Installationsprogrammet (setup, setupFile, setupSha256, setupSig) ingår
//     också i signature2 (kind "setup") – det används inte för uppdateringar, bara för nedladdningssidan.
//   - signature5 (Linux-klienten och nedladdningssidan) över SIGNED_FIELDS_5 = SIGNED_FIELDS_4 + linux, linuxFile,
//     linuxSha256, linuxSig: AppImage med den fristående Linux-appen (client/linux), filsignatur kind "linux".
//     Skrivs alltid (saknat = null). Windows- och macOS-klienterna läser den inte; signature–signature4 är oförändrade.
//   - signature4 (bara nedladdningssidan) över SIGNED_FIELDS_4 = SIGNED_FIELDS_3 + dmg, dmgFile, dmgSha256, dmgSig:
//     DMG:n med samma notariserade app (client/mac/build.sh --dmg), filsignatur kind "dmg", version = --mac-version.
//     Inga klienter läser den – Mac-appen uppdaterar sig med zip:en (signature3).
//   - signature3 (macOS-klienten) över SIGNED_FIELDS_3 = SIGNED_FIELDS_2 + mac, macFile, macSha256, macSig. Skrivs
//     alltid (saknat = null). Windows-klienterna läser den inte; signature och signature2 är oförändrade.
//     Mac-filens filsignatur: kind "mac".
//   - signatureAll (klienter från 1.0.18, alla plattformar) över de fält som räknas upp i det signerade fältet
//     "signed" ("a,b,c" i bokstavsordning, "signed" självt ingår). Listan bestäms HÄR, inte i klienten: ett nytt
//     fält kräver varken nytt klientsläpp eller en sjätte signatur. Lägg nya fält i EXTRA_FIELDS. Klienten litar
//     bara på fält i listan, och har den en gång sett signatureAll kräver den signaturen därefter.
//       minClient  – lägsta klientversion som får bidra (--min-client, "none" tar bort). Servern svarar äldre
//                    klienter 426 client-too-old; klienter från 1.0.18 säger det själva.
//       notice / noticeEn – kort besked som klienten visar en gång (--notice / --notice-en, "none" tar bort).
//       primaryRevoked – huvudnyckelns fingeravtryck (--revoke-primary <publik.pem>, "none" tar bort). Klienter från
//                    1.0.18 godtar två nycklar: huvudnyckeln och reservnyckeln (UpdateKeys.cs). Har huvudnyckeln
//                    läckt signeras ALLT om med reservnyckeln (--key <reservnyckeln> och alla filer) tillsammans
//                    med --revoke-primary: klienterna godtar därefter bara reservnyckeln, för alltid. Det går bara
//                    åt det hållet – skriptet vägrar om --key är samma nyckel som den som återkallas.
//                    Skriptet jämför med nycklarna som klienten bär (UpdateKeys.cs, eller --client-keys): den
//                    återkallade ska vara klientens huvudnyckel och --key klientens reservnyckel, annars vore
//                    återkallelsen en som ingen klient godtar. "none" tar bara bort fältet ur versionsinfon –
//                    klienter som redan sett återkallelsen har den kvar för alltid. EFTER en återkallelse:
//                    signera med reservnyckeln tills alla klienter uppdaterat; nästa klientsläpp får en NY
//                    huvudnyckel men SAMMA reservnyckel (äldre klienter litar då bara på reservnyckeln).
//     Fälten behålls från befintlig version.json tills de ändras eller tas bort.
//   - Signaturer: base64 av IEEE P1363 (r||s, 64 byte).
//   blockedAddons ingår inte i signaturen (servern kan lägga till via SCOUT_BLOCKED_ADDONS).
// Servern ska skicka de signerade fälten och "signature" oförändrade i GET /api/bidrag/version.

import { createHash, createPrivateKey, createPublicKey, generateKeyPairSync, sign, verify } from "node:crypto";
import { copyFileSync, existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const SIGNED_FIELDS = ["addon", "addonFile", "addonSha256", "addonSig", "client", "clientFile", "clientSig", "issued", "sha256", "v"];
const LITE_FIELDS = ["lite", "liteFile", "liteSha256", "liteSig"];
const SETUP_FIELDS = ["setup", "setupFile", "setupSha256", "setupSig"];
const SIGNED_FIELDS_2 = [...SIGNED_FIELDS, ...LITE_FIELDS, ...SETUP_FIELDS].sort();
const MAC_FIELDS = ["mac", "macFile", "macSha256", "macSig"];
const SIGNED_FIELDS_3 = [...SIGNED_FIELDS_2, ...MAC_FIELDS].sort();
const DMG_FIELDS = ["dmg", "dmgFile", "dmgSha256", "dmgSig"];
const SIGNED_FIELDS_4 = [...SIGNED_FIELDS_3, ...DMG_FIELDS].sort();
const LINUX_FIELDS = ["linux", "linuxFile", "linuxSha256", "linuxSig"];
const SIGNED_FIELDS_5 = [...SIGNED_FIELDS_4, ...LINUX_FIELDS].sort();
/** Fält som bara signatureAll täcker. Nya fält läggs här – klienten behöver inte ändras för att verifiera dem. */
const EXTRA_FIELDS = ["minClient", "notice", "noticeEn", "primaryRevoked"];

/** Nyckelns fingeravtryck: SHA-256 (hex) över den publika nyckeln i DER-form – samma som ReleaseVerifier.Fingerprint. */
export const fingerprint = (key) => {
  // En publik KeyObject används som den är; en privat nyckel eller en PEM-text görs om till sin publika nyckel.
  const pub = typeof key === "object" && key !== null && key.type === "public" ? key : createPublicKey(key);
  return createHash("sha256").update(pub.export({ type: "spki", format: "der" })).digest("hex");
};
const FIELD_NAME = /^[A-Za-z][A-Za-z0-9]{0,40}$/;
const MAX_NOTICE = 300;

/** Fälten som signatureAll ska täcka för manifestet m: alla gamla fält (saknat = null), "signed" och de extra som finns. */
export function signedList(m) {
  const names = [...new Set([...SIGNED_FIELDS_5, "signed", ...EXTRA_FIELDS.filter((k) => m[k] !== undefined && m[k] !== null)])].sort();
  for (const n of names) if (!FIELD_NAME.test(n) || n.startsWith("signature")) throw new Error(`ogiltigt fältnamn i signed: ${n}`);
  return names;
}

/** Kanonisk JSON för signatureAll: fälten i m.signed ("a,b,c"), i den ordningen. */
export const canonicalAll = (m) => canonical(m, String(m.signed).split(","));
const VERSION = /^\d{1,4}(\.\d{1,6}){0,3}([-+][0-9A-Za-z.]{1,20})?$/;
const CLIENT_FILE = "ForeverScout.exe";
const LITE_FILE = "ForeverScout-lite.exe";
const SETUP_FILE = "ForeverScout-Setup.exe";
const ADDON_FILE = "ForeverScout-addon.zip";
const MAC_FILE = "ForeverScout-mac.zip";
const DMG_FILE = "ForeverScout-mac.dmg";
const LINUX_FILE = "ForeverScout-x86_64.AppImage";

/** Beta-kanalens filnamn: "ForeverScout-beta.exe", "ForeverScout-addon-beta.zip", "ForeverScout-beta-x86_64.AppImage". */
export function betaName(n) {
  if (n.endsWith(".AppImage")) return n.replace("-x86_64", "-beta-x86_64");
  const dot = n.lastIndexOf(".");
  return dot < 0 ? `${n}-beta` : `${n.slice(0, dot)}-beta${n.slice(dot)}`;
}

const FLAGS = new Set(["genkey", "key", "version", "exe", "exe-lite", "lite-version", "setup", "setup-version", "addon", "addon-version", "out", "block",
  "mac", "mac-version", "allow-unnotarized", "dmg", "linux", "linux-version", "channel", "min-client", "store-version", "notice", "notice-en", "revoke-primary", "client-keys"]);

function args(argv) {
  const out = {};
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (!a.startsWith("--")) fail(`Okänt argument: ${a}`);
    const k = a.slice(2);
    // En felstavad flagga (--minclient) fick förut släppet att signeras utan det man bad om.
    if (!FLAGS.has(k)) fail(`Okänd flagga: ${a}`);
    const v = argv[i + 1] && !argv[i + 1].startsWith("--") ? argv[++i] : true;
    out[k] = v;
  }
  return out;
}

function fail(msg) {
  console.error(`scout-sign: ${msg}`);
  process.exit(1);
}

export function canonical(obj, fields = SIGNED_FIELDS) {
  const o = {};
  for (const k of fields) o[k] = obj[k] === undefined ? null : obj[k];
  return JSON.stringify(o);
}

export const canonical2 = (obj) => canonical(obj, SIGNED_FIELDS_2);
export const canonical3 = (obj) => canonical(obj, SIGNED_FIELDS_3);
export const canonical4 = (obj) => canonical(obj, SIGNED_FIELDS_4);
export const canonical5 = (obj) => canonical(obj, SIGNED_FIELDS_5);
export { SIGNED_FIELDS, SIGNED_FIELDS_2, SIGNED_FIELDS_3, SIGNED_FIELDS_4, SIGNED_FIELDS_5, EXTRA_FIELDS };

/** Namnen på posterna i en zip (centralkatalogen; zip64 stöds inte – macOS-zip:en är långt under 4 GB). */
export function zipEntryNames(buf) {
  const min = Math.max(0, buf.length - 65557);
  let eocd = -1;
  for (let i = buf.length - 22; i >= min; i--) if (buf.readUInt32LE(i) === 0x06054b50) { eocd = i; break; }
  if (eocd < 0) throw new Error("hittar inte zip:ens slutpost");
  const count = buf.readUInt16LE(eocd + 10), size = buf.readUInt32LE(eocd + 12), offset = buf.readUInt32LE(eocd + 16);
  if (count === 0xffff || size === 0xffffffff || offset === 0xffffffff) throw new Error("zip64 stöds inte");
  const names = [];
  let p = offset;
  for (let k = 0; k < count; k++) {
    if (buf.readUInt32LE(p) !== 0x02014b50) throw new Error("trasig centralkatalog");
    const n = buf.readUInt16LE(p + 28), x = buf.readUInt16LE(p + 30), c = buf.readUInt16LE(p + 32);
    names.push(buf.subarray(p + 46, p + 46 + n).toString("utf8"));
    p += 46 + n + x + c;
  }
  return names;
}

/** Kontrollerar att zip:en är ett macOS-paket som är kodsignerat och (om inte allowUnnotarized) staplat. */
export function checkMacZip(buf, allowUnnotarized = false) {
  if (buf[0] !== 0x50 || buf[1] !== 0x4b) return "--mac ska vara en zip (ditto -c -k --keepParent Forever\\ Scout.app)";
  let names;
  try { names = zipEntryNames(buf); } catch (e) { return `--mac: ${e.message}`; }
  const apps = new Set(names.map((n) => n.match(/^([^/]+\.app)\//)?.[1]).filter(Boolean));
  if (apps.size !== 1) return "--mac ska innehålla exakt ett .app-paket i roten";
  const [app] = apps;
  const has = (rel) => names.includes(`${app}/${rel}`);
  if (!has("Contents/Info.plist")) return `--mac: ${app}/Contents/Info.plist saknas`;
  if (!has("Contents/MacOS/ForeverScout")) return `--mac: ${app}/Contents/MacOS/ForeverScout saknas`;
  if (allowUnnotarized) return null;
  if (!has("Contents/_CodeSignature/CodeResources")) return "--mac: paketet är inte kodsignerat (_CodeSignature saknas) – kör build.sh med signering";
  if (!has("Contents/CodeResources")) return "--mac: ingen staplad notariseringsbiljett (Contents/CodeResources) – kör xcrun stapler staple före zip";
  return null;
}

export function fileMessage(kind, version, sha256) {
  return Buffer.from(`ForeverScout-file/v1\n${kind}\n${version}\n${sha256.toLowerCase()}`, "utf8");
}

const signB64 = (key, data) => sign("sha256", data, { key, dsaEncoding: "ieee-p1363" }).toString("base64");
const verifyB64 = (key, data, sig) => verify("sha256", data, { key, dsaEncoding: "ieee-p1363" }, Buffer.from(sig, "base64"));
const sha256 = (buf) => createHash("sha256").update(buf).digest("hex");

function genkey(dir) {
  mkdirSync(dir, { recursive: true });
  const priv = join(dir, "scout-release-private.pem");
  if (existsSync(priv)) fail(`${priv} finns redan – skriver inte över en nyckel.`);
  const { publicKey, privateKey } = generateKeyPairSync("ec", { namedCurve: "prime256v1" });
  const pubPem = publicKey.export({ type: "spki", format: "pem" });
  writeFileSync(priv, privateKey.export({ type: "pkcs8", format: "pem" }), { mode: 0o600, flag: "wx" });
  writeFileSync(join(dir, "scout-release-public.pem"), pubPem, { flag: "wx" });
  process.stdout.write(pubPem);
}

function release(a) {
  if (typeof a.key !== "string") fail("--key <privat.pem> krävs");
  if (typeof a.version !== "string" || !VERSION.test(a.version)) fail("--version x.y.z krävs");
  const extras = a["min-client"] !== undefined || a.notice !== undefined || a["notice-en"] !== undefined || a["revoke-primary"] !== undefined;
  if (!a.exe && !a["exe-lite"] && !a.setup && !a.addon && !a.mac && !a.dmg && !a.linux && !extras) fail("ange --exe, --exe-lite, --setup, --addon, --mac, --dmg, --linux, --min-client, --notice och/eller --revoke-primary");
  const linuxVersion = typeof a["linux-version"] === "string" ? a["linux-version"] : a.version;
  if (!VERSION.test(linuxVersion)) fail("ogiltig --linux-version");
  const macVersion = typeof a["mac-version"] === "string" ? a["mac-version"] : a.version;
  if (!VERSION.test(macVersion)) fail("ogiltig --mac-version");
  const setupVersion = typeof a["setup-version"] === "string" ? a["setup-version"] : a.version;
  if (!VERSION.test(setupVersion)) fail("ogiltig --setup-version");
  const liteVersion = typeof a["lite-version"] === "string" ? a["lite-version"] : a.version;
  if (!VERSION.test(liteVersion)) fail("ogiltig --lite-version");
  const addonVersion = typeof a["addon-version"] === "string" ? a["addon-version"] : a.version;
  if (!VERSION.test(addonVersion)) fail("ogiltig --addon-version");

  const privateKey = createPrivateKey(readFileSync(a.key));
  if (privateKey.asymmetricKeyDetails?.namedCurve !== "prime256v1") fail("nyckeln är inte ECDSA P-256");
  const publicKey = createPublicKey(privateKey);

  const out = resolve(typeof a.out === "string" ? a.out : join(ROOT, "public", "scout"));
  mkdirSync(out, { recursive: true });
  // --channel beta: egen signerad version-beta.json och egna filnamn (ForeverScout-beta.exe …) bredvid de vanliga –
  // klienter med "Betaversioner" påslaget hämtar den (GET /api/bidrag/version?kanal=beta). Finns ingen beta-fil än
  // börjar den som en kopia av den vanliga, så att delar som inte anges (t.ex. Mac) pekar på de vanliga filerna.
  if (a.channel !== undefined && a.channel !== "beta") fail("--channel kan bara vara beta");
  const beta = a.channel === "beta";
  const fileName = (n) => (beta ? betaName(n) : n);
  const manifestPath = join(out, beta ? "version-beta.json" : "version.json");
  const stablePath = join(out, "version.json");
  const prev = existsSync(manifestPath)
    ? JSON.parse(readFileSync(manifestPath, "utf8"))
    : beta && existsSync(stablePath)
      ? JSON.parse(readFileSync(stablePath, "utf8"))
      : {};

  const m = { v: 1 };
  for (const k of SIGNED_FIELDS_5) if (k !== "v" && k !== "issued" && prev[k] !== undefined) m[k] = prev[k];
  // Lägsta version och besked ligger kvar tills de ändras eller tas bort ("none").
  for (const k of EXTRA_FIELDS) if (typeof prev[k] === "string" && prev[k]) m[k] = prev[k];
  const extra = (flag, key, check) => {
    const v = a[flag];
    if (v === undefined) return;
    if (v === true) fail(`--${flag} kräver ett värde (eller none)`);
    if (v === "none") { delete m[key]; return; }
    const problem = check(v);
    if (problem) fail(`--${flag}: ${problem}`);
    m[key] = v;
  };
  // Ett besked är en enda rad: inga styrtecken (klienten ersätter dem annars med blanksteg) och högst MAX_NOTICE tecken.
  const notice = (v) => (/[\u0000-\u001f\u007f-\u009f\u2028\u2029]/.test(v) ? "inga radbrytningar eller styrtecken" : v.trim() !== v || !v ? "inga blanksteg först eller sist" : v.length > MAX_NOTICE ? `högst ${MAX_NOTICE} tecken` : null);
  // Servern läser lägsta version ur version.json, och beskedet ska vara samma för alla: i beta-kanalen följer de
  // alltid den vanliga filen och kan inte sättas för sig.
  if (beta) {
    if (extras) fail("--min-client, --notice och --revoke-primary sätts på den vanliga kanalen (utan --channel beta); beta-filen följer den");
    const stable = existsSync(stablePath) ? JSON.parse(readFileSync(stablePath, "utf8")) : {};
    for (const k of EXTRA_FIELDS) {
      if (typeof stable[k] === "string" && stable[k]) m[k] = stable[k];
      else delete m[k];
    }
  }
  extra("min-client", "minClient", (v) => (VERSION.test(v) && /^\d+\.\d+\.\d+$/.test(v) ? null : "ska vara x.y.z"));
  extra("notice", "notice", notice);
  extra("notice-en", "noticeEn", notice);
  // Återkallelse av huvudnyckeln: anges som den publika nyckelfilen, sparas som fingeravtryck. Bara en ANNAN nyckel
  // (reservnyckeln) kan signera den – annars vore det nyckeln som återkallar sig själv, vilket klienten inte godtar.
  if (typeof a["revoke-primary"] === "string" && a["revoke-primary"] !== "none") {
    if (!existsSync(a["revoke-primary"])) fail(`hittar inte --revoke-primary ${a["revoke-primary"]}`);
    // Bara en publik ECDSA P-256-nyckel: något annat (RSA, en privat nyckel) skulle ge ett fingeravtryck som ingen
    // klient någonsin godtar – och man skulle tro att huvudnyckeln var återkallad.
    let revoked;
    try { revoked = createPublicKey(readFileSync(a["revoke-primary"])); } catch { fail("--revoke-primary ska vara huvudnyckelns publika .pem"); }
    if (!/-----BEGIN PUBLIC KEY-----/.test(readFileSync(a["revoke-primary"], "utf8")) || revoked.asymmetricKeyType !== "ec" || revoked.asymmetricKeyDetails?.namedCurve !== "prime256v1")
      fail("--revoke-primary ska vara huvudnyckelns publika .pem (ECDSA P-256)");
    // Mot nycklarna som klienten bär: första blocket i UpdateKeys.cs är huvudnyckeln, andra reservnyckeln.
    const keysPath = typeof a["client-keys"] === "string" ? a["client-keys"] : join(ROOT, "client", "windows", "src", "ForeverScout.Core", "UpdateKeys.cs");
    if (!existsSync(keysPath)) fail(`hittar inte klientens nycklar (${keysPath}) – ange --client-keys <UpdateKeys.cs>`);
    const blocks = readFileSync(keysPath, "utf8").match(/-----BEGIN PUBLIC KEY-----[A-Za-z0-9+/=\s]+-----END PUBLIC KEY-----/g) ?? [];
    if (blocks.length < 2) fail(`${keysPath} bär inte två nycklar (huvudnyckel och reservnyckel)`);
    const [clientPrimary, clientBackup] = blocks.map((b) => fingerprint(b));
    if (fingerprint(revoked) !== clientPrimary) fail("--revoke-primary är inte klientens huvudnyckel (UpdateKeys.cs) – ingen klient skulle godta återkallelsen");
    if (fingerprint(publicKey) === clientPrimary) fail("--key är samma nyckel som den som återkallas – signera med reservnyckeln");
    if (fingerprint(publicKey) !== clientBackup) fail("--key är inte klientens reservnyckel (UpdateKeys.cs) – bara reservnyckeln kan återkalla huvudnyckeln");
    a["revoke-primary"] = fingerprint(revoked);
  }
  extra("revoke-primary", "primaryRevoked", (v) => (/^[0-9a-f]{64}$/.test(v) ? null : "ogiltigt fingeravtryck"));
  if (m.primaryRevoked && m.primaryRevoked === fingerprint(publicKey)) fail("--key är samma nyckel som den som återkallas – signera med reservnyckeln");

  if (a.exe) {
    if (typeof a.exe !== "string" || !existsSync(a.exe)) fail(`hittar inte --exe ${a.exe}`);
    const buf = readFileSync(a.exe);
    if (buf[0] !== 0x4d || buf[1] !== 0x5a) fail("--exe är inte en Windows-exe (MZ)");
    const h = sha256(buf);
    copyFileSync(a.exe, join(out, fileName(CLIENT_FILE)));
    Object.assign(m, { client: a.version, clientFile: fileName(CLIENT_FILE), sha256: h, clientSig: signB64(privateKey, fileMessage("client", a.version, h)) });
  }
  if (a["exe-lite"]) {
    const p = a["exe-lite"];
    if (typeof p !== "string" || !existsSync(p)) fail(`hittar inte --exe-lite ${p}`);
    const buf = readFileSync(p);
    if (buf[0] !== 0x4d || buf[1] !== 0x5a) fail("--exe-lite är inte en Windows-exe (MZ)");
    if (a.exe && sha256(buf) === sha256(readFileSync(a.exe))) fail("--exe och --exe-lite är samma fil");
    const h = sha256(buf);
    copyFileSync(p, join(out, fileName(LITE_FILE)));
    Object.assign(m, { lite: liteVersion, liteFile: fileName(LITE_FILE), liteSha256: h, liteSig: signB64(privateKey, fileMessage("lite", liteVersion, h)) });
  }
  if (a.setup) {
    if (typeof a.setup !== "string" || !existsSync(a.setup)) fail(`hittar inte --setup ${a.setup}`);
    const buf = readFileSync(a.setup);
    if (buf[0] !== 0x4d || buf[1] !== 0x5a) fail("--setup är inte en Windows-exe (MZ)");
    const h = sha256(buf);
    copyFileSync(a.setup, join(out, fileName(SETUP_FILE)));
    Object.assign(m, { setup: setupVersion, setupFile: fileName(SETUP_FILE), setupSha256: h, setupSig: signB64(privateKey, fileMessage("setup", setupVersion, h)) });
  }
  if (a.addon) {
    if (typeof a.addon !== "string" || !existsSync(a.addon)) fail(`hittar inte --addon ${a.addon}`);
    const buf = readFileSync(a.addon);
    if (buf[0] !== 0x50 || buf[1] !== 0x4b) fail("--addon ska vara en zip (mappen ForeverScout/ med .toc/.lua)");
    const h = sha256(buf);
    copyFileSync(a.addon, join(out, fileName(ADDON_FILE)));
    Object.assign(m, { addon: addonVersion, addonFile: fileName(ADDON_FILE), addonSha256: h, addonSig: signB64(privateKey, fileMessage("addon", addonVersion, h)) });
  }
  if (a.mac) {
    if (typeof a.mac !== "string" || !existsSync(a.mac)) fail(`hittar inte --mac ${a.mac}`);
    const buf = readFileSync(a.mac);
    const problem = checkMacZip(buf, a["allow-unnotarized"] === true);
    if (problem) fail(problem);
    const h = sha256(buf);
    copyFileSync(a.mac, join(out, fileName(MAC_FILE)));
    Object.assign(m, { mac: macVersion, macFile: fileName(MAC_FILE), macSha256: h, macSig: signB64(privateKey, fileMessage("mac", macVersion, h)) });
  }
  if (a.dmg) {
    if (typeof a.dmg !== "string" || !existsSync(a.dmg)) fail(`hittar inte --dmg ${a.dmg}`);
    const buf = readFileSync(a.dmg);
    // UDIF-avbilder slutar med ett 512 byte långt "koly"-block.
    if (buf.length < 4096 || buf.subarray(buf.length - 512, buf.length - 508).toString("latin1") !== "koly") fail("--dmg är inte en macOS-diskavbild (koly saknas)");
    const h = sha256(buf);
    copyFileSync(a.dmg, join(out, fileName(DMG_FILE)));
    Object.assign(m, { dmg: macVersion, dmgFile: fileName(DMG_FILE), dmgSha256: h, dmgSig: signB64(privateKey, fileMessage("dmg", macVersion, h)) });
  }
  if (a.linux) {
    if (typeof a.linux !== "string" || !existsSync(a.linux)) fail(`hittar inte --linux ${a.linux}`);
    const buf = readFileSync(a.linux);
    // En AppImage är en ELF-fil (startaren) följd av en squashfs-avbild ("hsqs") med appen.
    if (buf.length < 1024 * 1024 || buf.readUInt32BE(0) !== 0x7f454c46 || buf.indexOf(Buffer.from("hsqs")) < 0) fail("--linux ska vara en AppImage (client/linux/packaging/make-appimage.sh)");
    const h = sha256(buf);
    copyFileSync(a.linux, join(out, fileName(LINUX_FILE)));
    Object.assign(m, { linux: linuxVersion, linuxFile: fileName(LINUX_FILE), linuxSha256: h, linuxSig: signB64(privateKey, fileMessage("linux", linuxVersion, h)) });
  }
  // Behållna delar måste fortfarande verifiera mot samma nyckel.
  for (const [kind, ver, sh, sig] of [["client", m.client, m.sha256, m.clientSig], ["lite", m.lite, m.liteSha256, m.liteSig], ["setup", m.setup, m.setupSha256, m.setupSig], ["addon", m.addon, m.addonSha256, m.addonSig], ["mac", m.mac, m.macSha256, m.macSig], ["dmg", m.dmg, m.dmgSha256, m.dmgSig], ["linux", m.linux, m.linuxSha256, m.linuxSig]]) {
    if (ver && !verifyB64(publicKey, fileMessage(kind, ver, sh), sig)) fail(`befintlig ${kind}-signatur stämmer inte med nyckeln – signera om med --${kind === "client" ? "exe" : kind === "lite" ? "exe-lite" : kind === "setup" ? "setup" : kind === "mac" ? "mac" : kind === "dmg" ? "dmg" : kind === "linux" ? "linux" : "addon"}`);
  }
  // En lägsta version som en plattform inte kan uppdatera sig till stänger ute alla på den plattformen – de får
  // "uppdatera" utan att det finns något att uppdatera till. Microsoft Store syns inte i versionsinfon: den som sätter
  // en lägsta version måste ange Store-versionen (--store-version, som den står i Partner Center), och den får inte
  // vara lägre – Store-spelare kan bara uppdatera via Store (2026-11-17).
  if (m.minClient) {
    const num = (v) => v.split(/[-+]/)[0].split(".").map(Number).concat(0, 0).slice(0, 3);
    const less = (x, y) => { const [p, q] = [num(x), num(y)]; for (let i = 0; i < 3; i++) if (p[i] !== q[i]) return p[i] < q[i]; return false; };
    if (typeof a["min-client"] === "string" && a["min-client"] !== "none") {
      const store = a["store-version"];
      if (typeof store !== "string" || !/^\d+\.\d+\.\d+$/.test(store)) fail("--min-client kräver --store-version x.y.z: versionen som finns i Microsoft Store just nu (Partner Center)");
      if (less(store, m.minClient)) fail(`lägsta klientversion ${m.minClient} är högre än Store-versionen ${store} – Store-spelarna skulle stängas ute. Vänta tills Store har den nya versionen`);
    }
    for (const [name, ver] of [["klienten (--exe)", m.client], ["lite (--exe-lite)", m.lite], ["Mac (--mac)", m.mac], ["Linux (--linux)", m.linux]])
      if (ver && less(ver, m.minClient)) fail(`lägsta klientversion ${m.minClient} är högre än ${name} ${ver} i ${beta ? "beta-filen" : "versionsinfon"} – släpp den plattformen först, eller sänk med --min-client`);
  }
  m.issued = new Date().toISOString();
  // Klienten godtar aldrig en äldre versionsinfo än den den redan sett (AcceptIssued) – en klocka som gått bakåt
  // skulle annars ge en fil som ingen klient tar emot.
  if (typeof prev.issued === "string" && prev.issued >= m.issued) fail(`datorns klocka (${m.issued}) är före förra signeringen (${prev.issued}) – rätta klockan`);

  const blocked = new Set(Array.isArray(prev.blockedAddons) ? prev.blockedAddons : []);
  if (typeof a.block === "string") for (const b of a.block.split(",").map((s) => s.trim()).filter(Boolean)) {
    if (!VERSION.test(b)) fail(`ogiltig version i --block: ${b}`);
    blocked.add(b);
  }

  // signature: oförändrat format (gamla fält) – för klienter 1.0.0–1.0.2. signature2: gamla + lite-fält.
  const signature = signB64(privateKey, Buffer.from(canonical(m), "utf8"));
  if (!verifyB64(publicKey, Buffer.from(canonical(m), "utf8"), signature)) fail("självkontrollen av signaturen misslyckades");
  const signature2 = signB64(privateKey, Buffer.from(canonical2(m), "utf8"));
  if (!verifyB64(publicKey, Buffer.from(canonical2(m), "utf8"), signature2)) fail("självkontrollen av signature2 misslyckades");
  // signature3: alla fält inklusive mac-fälten – bara macOS-klienten läser den.
  const signature3 = signB64(privateKey, Buffer.from(canonical3(m), "utf8"));
  if (!verifyB64(publicKey, Buffer.from(canonical3(m), "utf8"), signature3)) fail("självkontrollen av signature3 misslyckades");
  // signature4: dessutom DMG-fälten – bara nedladdningssidan.
  const signature4 = signB64(privateKey, Buffer.from(canonical4(m), "utf8"));
  if (!verifyB64(publicKey, Buffer.from(canonical4(m), "utf8"), signature4)) fail("självkontrollen av signature4 misslyckades");
  // signature5: dessutom linux-fälten – Linux-klienten och nedladdningssidan.
  const signature5 = signB64(privateKey, Buffer.from(canonical5(m), "utf8"));
  if (!verifyB64(publicKey, Buffer.from(canonical5(m), "utf8"), signature5)) fail("självkontrollen av signature5 misslyckades");
  // signatureAll: alla fält i listan "signed" – klienter från 1.0.18 på alla plattformar. Listan signeras med.
  m.signed = signedList(m).join(",");
  const signatureAll = signB64(privateKey, Buffer.from(canonicalAll(m), "utf8"));
  if (!verifyB64(publicKey, Buffer.from(canonicalAll(m), "utf8"), signatureAll)) fail("självkontrollen av signatureAll misslyckades");
  const manifest = { ...m, blockedAddons: [...blocked], signature, signature2, signature3, signature4, signature5, signatureAll };
  writeFileSync(manifestPath, JSON.stringify(manifest, null, 2) + "\n");
  console.log(`Skrev ${manifestPath}`);
  if (a.exe) console.log(`  klient ${m.client}  sha256 ${m.sha256}`);
  if (a["exe-lite"]) console.log(`  lite   ${m.lite}  sha256 ${m.liteSha256}`);
  if (a.setup) console.log(`  setup  ${m.setup}  sha256 ${m.setupSha256}`);
  if (a.addon) console.log(`  addon  ${m.addon}  sha256 ${m.addonSha256}`);
  if (a.mac) console.log(`  mac    ${m.mac}  sha256 ${m.macSha256}`);
  if (a.dmg) console.log(`  dmg    ${m.dmg}  sha256 ${m.dmgSha256}`);
  if (a.linux) console.log(`  linux  ${m.linux}  sha256 ${m.linuxSha256}`);
  // Alltid utskrivet: de här fälten ligger kvar mellan släppen och ska inte kunna glömmas.
  console.log(`  lägsta klientversion: ${m.minClient ?? "ingen"}`);
  console.log(`  besked: ${m.notice ?? "inget"}${m.noticeEn ? ` / ${m.noticeEn}` : ""}`);
  console.log(`  signerad med nyckeln ${fingerprint(publicKey)}`);
  if (m.primaryRevoked) console.log(`  ÅTERKALLAR huvudnyckeln ${m.primaryRevoked} – klienterna godtar därefter bara reservnyckeln. Fortsätt signera med reservnyckeln tills alla klienter uppdaterat.`);
  // Beta-filen bär en kopia av lägsta version och besked: säg till när den inte längre stämmer med den vanliga.
  const betaPath = join(out, "version-beta.json");
  if (!beta && existsSync(betaPath)) {
    const b = JSON.parse(readFileSync(betaPath, "utf8"));
    // En plattform som ligger EFTER i beta-filen: klienter med "Betaversioner" påslaget får då aldrig den vanliga
    // kanalens nyare version (beta-filen pekade länge på Mac 1.0.12 medan den vanliga hade 1.0.17).
    const num = (v) => String(v).split(/[-+]/)[0].split(".").map(Number).concat(0, 0).slice(0, 3);
    const older = (x, y) => { const [u, w] = [num(x), num(y)]; for (let i = 0; i < 3; i++) if (u[i] !== w[i]) return u[i] < w[i]; return false; };
    for (const [name, k, flag] of [["Windows", "client", "--exe"], ["lite", "lite", "--exe-lite"], ["Mac", "mac", "--mac/--dmg"], ["Linux", "linux", "--linux"], ["addonen", "addon", "--addon"]])
      if (m[k] && b[k] && older(b[k], m[k])) console.log(`  OBS: beta-kanalen ligger efter för ${name} (${b[k]} mot ${m[k]}) – signera om beta-kanalen med ${flag} så att beta-klienterna får den.`);
    if ((b.primaryRevoked ?? null) !== (m.primaryRevoked ?? null)) console.log("  VIKTIGT: version-beta.json känner inte till återkallelsen – klienter på beta-kanalen litar fortfarande på huvudnyckeln. Signera om beta-kanalen med reservnyckeln NU (alla filer).");
    if (EXTRA_FIELDS.some((k) => (b[k] ?? null) !== (m[k] ?? null))) console.log("  OBS: version-beta.json har en annan lägsta version eller ett annat besked – signera om beta-kanalen så att den följer med.");
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const a = args(process.argv.slice(2));
  if (a.genkey) {
    if (typeof a.genkey !== "string") fail("--genkey <katalog>");
    genkey(a.genkey);
  } else release(a);
}
