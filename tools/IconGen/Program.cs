using IconGen;
using SkiaSharp;
using Svg.Skia;

var root = FindRepoRoot();
var iconDir = Path.Combine(root, "assets", "icon");
var outDir = Path.Combine(iconDir, "generated");
var installerDir = Path.Combine(root, "assets", "installer");
int[] appSizes = [16, 20, 24, 32, 40, 48, 64, 256];
int[] traySizes = [16, 20, 24, 32, 40, 48];
// Inno Setup's picture sizes at 100% to 250% scaling; Setup picks the one that fits.
(int Percent, int Width, int Height)[] largeSizes = [(100, 202, 386), (125, 269, 515), (150, 336, 643), (175, 403, 772), (200, 430, 824), (225, 498, 953), (250, 534, 1022)];
(int Percent, int Size)[] smallSizes = [(100, 58), (125, 77), (150, 97), (175, 116), (200, 124), (225, 143), (250, 159)];

Directory.CreateDirectory(outDir);
IcoWriter.Write(Path.Combine(outDir, "app.ico"), appSizes.Select(s => (s, Png(Render(s)))).ToList());
IcoWriter.Write(Path.Combine(outDir, "tray.ico"), traySizes.Select(s => (s, Png(Render(s)))).ToList());
IcoWriter.Write(Path.Combine(outDir, "tray-badge.ico"), traySizes.Select(s => (s, Png(Badge.Apply(Render(s))))).ToList());
for (var frame = 0; frame < Spinner.Frames; frame++)
    IcoWriter.Write(Path.Combine(outDir, $"tray-work-{frame}.ico"), traySizes.Select(s => (s, Png(Spinner.Apply(Render(s), frame)))).ToList());
File.WriteAllBytes(Path.Combine(outDir, "app-64.png"), Png(Render(64)));
Console.WriteLine($"Icons written to {outDir}");
Directory.CreateDirectory(installerDir);
foreach (var (percent, width, height) in largeSizes) File.WriteAllBytes(Path.Combine(installerDir, $"wizard-large-{percent}.png"), Png(Centered(width, height)));
foreach (var (percent, size) in smallSizes) File.WriteAllBytes(Path.Combine(installerDir, $"wizard-small-{percent}.png"), Png(Render(size)));
Console.WriteLine($"Installer pictures written to {installerDir}");

SKBitmap Render(int size) => Draw(size, size, 0, 0, size);

// The hamster across most of the width, a little above the middle; the clear rest suits light and dark pages.
SKBitmap Centered(int width, int height)
{
    var size = width * 3 / 4;
    return Draw(width, height, (width - size) / 2, height * 2 / 5 - size / 2, size);
}

// The hamster at size px, placed at (x, y) on a clear picture.
SKBitmap Draw(int width, int height, int x, int y, int size)
{
    var file = Path.Combine(iconDir, size <= 24 ? "hamster-small.svg" : "hamster.svg");
    using var svg = new SKSvg();
    var picture = svg.Load(file) ?? throw new InvalidDataException($"Cannot load {file}");
    var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
    using var canvas = new SKCanvas(bitmap);
    canvas.Clear(SKColors.Transparent);
    canvas.Translate(x, y);
    canvas.Scale(size / picture.CullRect.Width, size / picture.CullRect.Height);
    canvas.DrawPicture(picture);
    canvas.Flush();
    return bitmap;
}

static byte[] Png(SKBitmap bitmap)
{
    using var image = SKImage.FromBitmap(bitmap);
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TinyTracker.slnx"))) dir = dir.Parent;
    return dir?.FullName ?? throw new DirectoryNotFoundException("Run from inside the repository.");
}
