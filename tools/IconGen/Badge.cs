using SkiaSharp;

namespace IconGen;

internal static class Badge
{
    public static SKBitmap Apply(SKBitmap source)
    {
        var bitmap = source.Copy();
        using var canvas = new SKCanvas(bitmap);
        float size = bitmap.Width;
        var radius = MathF.Max(2.5f, size * 0.17f);
        var cx = size - radius - 0.5f;
        var cy = radius + 0.5f;
        using var ring = new SKPaint { Color = new SKColor(0, 0, 0, 150), IsAntialias = true };
        using var dot = new SKPaint { Color = SKColors.White, IsAntialias = true };
        canvas.DrawCircle(cx, cy, radius + 1f, ring);
        canvas.DrawCircle(cx, cy, radius, dot);
        return bitmap;
    }
}
