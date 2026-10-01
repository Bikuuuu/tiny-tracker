using SkiaSharp;

namespace IconGen;

// The working state: a small arc turning where the badge dot sits.
internal static class Spinner
{
    public const int Frames = 8;

    public static SKBitmap Apply(SKBitmap source, int frame)
    {
        var bitmap = source.Copy();
        using var canvas = new SKCanvas(bitmap);
        float size = bitmap.Width;
        var radius = MathF.Max(3f, size * 0.2f);
        var cx = size - radius - 0.5f;
        var cy = radius + 0.5f;
        var stroke = MathF.Max(1.25f, size * 0.08f);
        using var ring = new SKPaint { Color = new SKColor(0, 0, 0, 150), IsAntialias = true };
        using var arc = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = stroke, StrokeCap = SKStrokeCap.Round };
        canvas.DrawCircle(cx, cy, radius + 1f, ring);
        var inset = radius - stroke / 2;
        canvas.DrawArc(new SKRect(cx - inset, cy - inset, cx + inset, cy + inset), frame * 360f / Frames - 90, 270, false, arc);
        return bitmap;
    }
}
