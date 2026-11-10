using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PcTidy.TestbedGen.Content;

/// <summary>How to encode a picture. Same <see cref="Seed"/> = same scene, so different encodings are near-duplicates.</summary>
internal sealed record PictureSpec(int Seed, int Width = 96, int Height = 64, bool Png = false, int Quality = 90,
    double Scale = 1.0, DateTime? Taken = null, string? Camera = null);

/// <summary>
/// Real, decodable JPEG/PNG bytes made with WIC (via WPF imaging). JPEGs can carry EXIF DateTimeOriginal
/// and camera make/model. Encoding runs on a private STA thread so callers (xUnit, MTA) don't have to be.
/// </summary>
internal static class Images
{
    private const string DateTakenQuery = "/app1/ifd/exif/{ushort=36867}";
    private const string DateDigitizedQuery = "/app1/ifd/exif/{ushort=36868}";
    private const string MakeQuery = "/app1/ifd/{ushort=271}";
    private const string ModelQuery = "/app1/ifd/{ushort=272}";

    public static byte[] Encode(PictureSpec spec) => RunSta(() => EncodeOnSta(spec));

    private static byte[] EncodeOnSta(PictureSpec spec)
    {
        BitmapSource source = Scene(spec.Seed, spec.Width, spec.Height);
        if (spec.Scale != 1.0)
            source = new TransformedBitmap(source, new ScaleTransform(spec.Scale, spec.Scale));

        BitmapEncoder encoder;
        BitmapMetadata? metadata = null;
        if (spec.Png)
        {
            encoder = new PngBitmapEncoder();
        }
        else
        {
            encoder = new JpegBitmapEncoder { QualityLevel = spec.Quality };
            if (spec.Taken is { } taken)
            {
                metadata = new BitmapMetadata("jpg");
                var exifDate = taken.ToString("yyyy:MM:dd HH:mm:ss");
                metadata.SetQuery(MakeQuery, spec.Camera ?? "FakeCam");
                metadata.SetQuery(ModelQuery, "Model T1");
                metadata.SetQuery(DateTakenQuery, exifDate);
                metadata.SetQuery(DateDigitizedQuery, exifDate);
            }
        }
        encoder.Frames.Add(BitmapFrame.Create(source, null, metadata, null));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>Deterministic "photo": a sky-to-ground gradient, a sun and a few blocks, all from the seed.</summary>
    private static BitmapSource Scene(int seed, int width, int height)
    {
        var rng = new Random(seed);
        var pixels = new byte[width * height * 3];
        byte r0 = (byte)rng.Next(256), g0 = (byte)rng.Next(256), b0 = (byte)rng.Next(256);
        byte r1 = (byte)rng.Next(256), g1 = (byte)rng.Next(256), b1 = (byte)rng.Next(256);
        int sunX = rng.Next(width), sunY = rng.Next(height / 2), sunR = rng.Next(4, Math.Max(5, height / 4));
        var blocks = Enumerable.Range(0, 4).Select(_ => (X: rng.Next(width), Y: rng.Next(height / 2, height), W: rng.Next(4, 20),
            H: rng.Next(4, 16), C: ((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256)))).ToArray();

        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            double t = (double)y / height;
            byte r = (byte)(r0 + (r1 - r0) * t), g = (byte)(g0 + (g1 - g0) * t), b = (byte)(b0 + (b1 - b0) * t);
            if ((x - sunX) * (x - sunX) + (y - sunY) * (y - sunY) < sunR * sunR) (r, g, b) = (250, 220, 90);
            foreach (var k in blocks)
                if (x >= k.X && x < k.X + k.W && y >= k.Y && y < k.Y + k.H) (r, g, b) = k.C;
            int i = (y * width + x) * 3;
            pixels[i] = b; pixels[i + 1] = g; pixels[i + 2] = r;
        }
        var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, pixels, width * 3);
        bmp.Freeze();
        return bmp;
    }

    private static T RunSta<T>(Func<T> work)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new InvalidOperationException("Image encoding failed", error);
        return result;
    }
}
