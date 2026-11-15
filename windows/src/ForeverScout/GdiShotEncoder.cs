using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ForeverScout.Core;

namespace ForeverScout;

/// <summary>
/// 1.0.15: förminskar en skärmbild med Windows egen bildkod (GDI+ genom System.Drawing, som WinForms redan använder –
/// inget nytt beroende och ingen större exe). Bilden ritas om i en ny bitmapp och sparas som JPEG, så inget ur
/// originalets metadata följer med. Spelets TGA-format (inställningen screenshotFormat) kan GDI+ inte läsa; det
/// avkodas här (okomprimerat och RLE, 24/32 bitar).
/// </summary>
internal sealed class GdiShotEncoder : IShotEncoder
{
    public byte[]? Encode(byte[] original, int maxWidth, int quality)
    {
        try
        {
            using var ms = new MemoryStream(original, writable: false); // måste leva lika länge som bitmappen
            using var src = Decode(ms, original);
            if (src is null || src.Width <= 0 || src.Height <= 0) return null;
            // 1.0.16: bara skärmens mitt sparas (62 % av höjden, bredd 1,45 × den höjden) – hela skärmen gjorde
            // NPC:n till en liten figur i en bred bild. Spelaren har målet ungefär i mitten när bilden tas.
            int ch = Math.Max(1, (int)Math.Round(src.Height * 0.62));
            int cw = Math.Min(src.Width, Math.Max(1, (int)Math.Round(ch * 1.45)));
            // 1.0.17: utsnittet ligger lite ovanför mitten (30 % av marginalen ovanför) – figurer står oftast ovanför
            // skärmens mitt, och långa NPC:er fick annars huvudet i bildens överkant.
            int sx = (src.Width - cw) / 2, sy = (int)Math.Round((src.Height - ch) * 0.30);
            int w = Math.Min(cw, Math.Max(1, maxWidth)); // aldrig uppåt
            int h = Math.Max(1, (int)Math.Round(ch * (double)w / cw));
            using var dst = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(dst))
            using (var attr = new ImageAttributes())
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                attr.SetWrapMode(WrapMode.TileFlipXY); // ingen ljus kant där bilden tar slut
                g.DrawImage(src, new Rectangle(0, 0, w, h), sx, sy, cw, ch, GraphicsUnit.Pixel, attr);
            }
            var codec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
            if (codec is null) return null;
            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(quality, 1, 100));
            using var jpeg = new MemoryStream();
            dst.Save(jpeg, codec, ep);
            return jpeg.ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or OutOfMemoryException or InvalidOperationException or NotSupportedException)
        {
            return null; // inte en bild (eller för stor för GDI+): hoppas över
        }
    }

    private static Bitmap? Decode(MemoryStream ms, byte[] original)
    {
        try { return new Bitmap(ms); }
        catch (ArgumentException) { return DecodeTga(original); }
    }

    /// <summary>
    /// Truevision TGA som spelet skriver den: bildtyp 2 (okomprimerad) eller 10 (RLE), 24 eller 32 bitar, ingen
    /// färgkarta. Alfakanalen används inte (en skärmbild är alltid täckande). Null om filen inte är en sådan.
    /// </summary>
    internal static Bitmap? DecodeTga(byte[] d)
    {
        if (d.Length < 18) return null;
        int type = d[2], w = d[12] | d[13] << 8, h = d[14] | d[15] << 8, bpp = d[16];
        if (d[1] != 0 || type is not (2 or 10) || bpp is not (24 or 32) || w is <= 0 or > 16384 || h is <= 0 or > 16384) return null;
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
        var bmp = new Bitmap(w, h, bpp == 24 ? PixelFormat.Format24bppRgb : PixelFormat.Format32bppRgb); // BGR(X) – samma byteordning som TGA
        var bits = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, bmp.PixelFormat);
        try
        {
            for (int y = 0; y < h; y++)
                Marshal.Copy(px, pos + (topDown ? y : h - 1 - y) * row, bits.Scan0 + y * bits.Stride, row);
        }
        finally { bmp.UnlockBits(bits); }
        return bmp;
    }
}
