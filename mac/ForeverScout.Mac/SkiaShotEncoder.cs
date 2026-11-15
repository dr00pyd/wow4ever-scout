using System.Runtime.InteropServices;
using ForeverScout.Core;
using SkiaSharp;

namespace ForeverScout.Mac;

/// <summary>
/// Utsnittet ur en skärmbild och måtten på den förminskade bilden – ren funktion, exakt samma räkning som
/// Windows-klientens <c>GdiShotEncoder.Encode</c> (testas mot samma mått: 3840×2160 → 1600×1103 osv.).
/// </summary>
/// <param name="X">Utsnittets vänsterkant i originalet.</param>
/// <param name="Y">Utsnittets överkant i originalet.</param>
/// <param name="Width">Utsnittets bredd i originalet.</param>
/// <param name="Height">Utsnittets höjd i originalet.</param>
/// <param name="OutWidth">Den färdiga bildens bredd (aldrig större än utsnittet).</param>
/// <param name="OutHeight">Den färdiga bildens höjd.</param>
internal readonly record struct ShotCrop(int X, int Y, int Width, int Height, int OutWidth, int OutHeight)
{
    public static ShotCrop For(int srcWidth, int srcHeight, int maxWidth)
    {
        // Bara skärmens mitt sparas (62 % av höjden, bredd 1,45 × den höjden) – hela skärmen gjorde NPC:n till en
        // liten figur i en bred bild. Spelaren har målet ungefär i mitten när bilden tas.
        int ch = Math.Max(1, (int)Math.Round(srcHeight * 0.62));
        int cw = Math.Min(srcWidth, Math.Max(1, (int)Math.Round(ch * 1.45)));
        // Utsnittet ligger lite ovanför mitten (30 % av marginalen ovanför) – figurer står oftast ovanför skärmens
        // mitt, och långa NPC:er fick annars huvudet i bildens överkant.
        int sx = (srcWidth - cw) / 2, sy = (int)Math.Round((srcHeight - ch) * 0.30);
        int w = Math.Min(cw, Math.Max(1, maxWidth)); // aldrig uppåt
        int h = Math.Max(1, (int)Math.Round(ch * (double)w / cw));
        return new ShotCrop(sx, sy, cw, ch, w, h);
    }
}

/// <summary>
/// 1.0.17: förminskar en skärmbild med Skia (SkiaSharp) – biblioteket som Avalonia redan ritar hela gränssnittet med
/// på både macOS och Linux. Inget nytt paket och ingen större app: SkiaSharp.dll och libSkiaSharp ligger redan i
/// paketet. Samma utsnitt och mått som Windows-klientens GdiShotEncoder (<see cref="ShotCrop"/>). Bilden ritas om i
/// en ny bitmapp och sparas som JPEG, så inget ur originalets metadata följer med. Spelets TGA-format (inställningen
/// screenshotFormat) kan Skia inte läsa; det avkodas här (okomprimerat och RLE, 24/32 bitar).
/// </summary>
internal sealed class SkiaShotEncoder : IShotEncoder
{
    /// <summary>Större bilder än så avkodas inte (en 8K-skärm är 7680×4320 = 33 miljoner punkter).</summary>
    private const long MaxPixels = 80_000_000;

    public byte[]? Encode(byte[] original, int maxWidth, int quality)
    {
        try
        {
            using var src = Decode(original);
            if (src is null || src.Width <= 0 || src.Height <= 0) return null;
            var c = ShotCrop.For(src.Width, src.Height, maxWidth);
            // Täckande målbild utan färgprofil: JPEG-filen får bara bilddata (JFIF-huvudet), ingen metadata.
            using var dst = new SKBitmap(new SKImageInfo(c.OutWidth, c.OutHeight, SKColorType.Rgba8888, SKAlphaType.Opaque));
            src.SetImmutable(); // bilden nedan delar punkterna med bitmappen i stället för att kopiera dem
            using (var image = SKImage.FromBitmap(src))
            using (var canvas = new SKCanvas(dst))
            {
                canvas.Clear(SKColors.Black);
                // Liten förminskning: bikubiskt (Mitchell). Mer än en halvering: bilinjärt med mipmappar, annars
                // hoppar filtret över punkter och bilden blir grynig. Ingen förminskning: punkt för punkt.
                var sampling = c.Width > c.OutWidth * 2 ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)
                    : c.Width == c.OutWidth ? new SKSamplingOptions(SKFilterMode.Nearest)
                    : new SKSamplingOptions(SKCubicResampler.Mitchell);
                canvas.DrawImage(image, SKRect.Create(c.X, c.Y, c.Width, c.Height), SKRect.Create(0, 0, c.OutWidth, c.OutHeight), sampling);
                canvas.Flush();
            }
            using var jpeg = dst.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(quality, 1, 100));
            return jpeg?.ToArray();
        }
        catch (Exception)
        {
            return null; // inte en bild (eller för stor för minnet): hoppas över
        }
    }

    /// <summary>JPEG eller PNG genom Skia, annars spelets TGA. Null om filen inte är någon av dem.</summary>
    private static SKBitmap? Decode(byte[] original)
    {
        if (original.Length < 18) return null;
        using (var data = SKData.CreateCopy(original))
        using (var codec = SKCodec.Create(data))
        {
            if (codec is not null)
            {
                if (codec.EncodedFormat is not (SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png)) return null;
                var info = codec.Info;
                if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > MaxPixels) return null;
                return SKBitmap.Decode(codec);
            }
        }
        return DecodeTga(original);
    }

    /// <summary>
    /// Truevision TGA som spelet skriver den: bildtyp 2 (okomprimerad) eller 10 (RLE), 24 eller 32 bitar, ingen
    /// färgkarta. Alfakanalen används inte (en skärmbild är alltid täckande). Null om filen inte är en sådan.
    /// Samma regler som <c>GdiShotEncoder.DecodeTga</c> i Windows-klienten.
    /// </summary>
    internal static SKBitmap? DecodeTga(byte[] d)
    {
        if (d.Length < 18) return null;
        int type = d[2], w = d[12] | d[13] << 8, h = d[14] | d[15] << 8, bpp = d[16];
        if (d[1] != 0 || type is not (2 or 10) || bpp is not (24 or 32) || w is <= 0 or > 16384 || h is <= 0 or > 16384) return null;
        if ((long)w * h > MaxPixels) return null;
        int bytesPp = bpp / 8, row = w * bytesPp;
        long need = (long)row * h;
        if (need > int.MaxValue) return null;
        int pos = 18 + d[0];
        byte[] px;
        if (type == 2)
        {
            if (d.Length - pos < need) return null;
            px = d;
        }
        else
        {
            px = new byte[need];
            int o = 0;
            while (o < need)
            {
                if (pos >= d.Length) return null;
                int head = d[pos++], count = (head & 0x7F) + 1;
                if ((head & 0x80) != 0)
                {
                    if (pos + bytesPp > d.Length) return null;
                    for (int i = 0; i < count && o < need; i++, o += bytesPp) Buffer.BlockCopy(d, pos, px, o, bytesPp);
                    pos += bytesPp;
                }
                else
                {
                    int len = count * bytesPp;
                    if (pos + len > d.Length) return null;
                    int take = (int)Math.Min(len, need - o);
                    Buffer.BlockCopy(d, pos, px, o, take);
                    o += take;
                    pos += len;
                }
            }
            pos = 0;
        }
        bool topDown = (d[17] & 0x20) != 0;
        // BGRA – samma byteordning som TGA. Skia har inget 24-bitarsformat, så varje rad fylls ut med täckande alfa.
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
        try
        {
            var line = new byte[w * 4];
            IntPtr pixels = bmp.GetPixels();
            int stride = bmp.RowBytes;
            for (int y = 0; y < h; y++)
            {
                int s = pos + (topDown ? y : h - 1 - y) * row;
                for (int x = 0, t = 0; x < w; x++, s += bytesPp, t += 4)
                {
                    line[t] = px[s];
                    line[t + 1] = px[s + 1];
                    line[t + 2] = px[s + 2];
                    line[t + 3] = 255;
                }
                Marshal.Copy(line, 0, pixels + y * stride, line.Length);
            }
            return bmp;
        }
        catch
        {
            bmp.Dispose();
            throw;
        }
    }
}
