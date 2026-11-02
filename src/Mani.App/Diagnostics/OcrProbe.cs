using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;

namespace Mani_App.Diagnostics;

internal sealed record OcrProbeResult(string Status, string Language, string[] InstalledLanguages,
    string Frame, double Scale, string Text);

internal static class OcrProbe
{
    internal static async Task<OcrProbeResult> RecognizeAsync(string path, string language, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var available = OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToArray();
        var engine = OcrEngine.TryCreateFromLanguage(new Language(language));
        if (engine is null) return new("blocked-missing-language", language, available, path, 1, "");
        var file = await StorageFile.GetFileFromPathAsync(path).AsTask(cancellation);
        using var stream = await file.OpenReadAsync().AsTask(cancellation);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellation);
        double scale = Math.Min(1, (double)OcrEngine.MaxImageDimension / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var transform = new BitmapTransform
        {
            ScaledWidth = Math.Max(1, (uint)(decoder.PixelWidth * scale)),
            ScaledHeight = Math.Max(1, (uint)(decoder.PixelHeight * scale))
        };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(cancellation);
        var result = await engine.RecognizeAsync(bitmap).AsTask(cancellation);
        cancellation.ThrowIfCancellationRequested();
        return new("completed", language, available, path, scale, result.Text);
    }
}
