namespace TinyTracker.Core.Layout;

// Large text (spec §4.8): two parts stay side by side while they fit; otherwise the second goes under the first. A first part
// that wraps, such as a label beside its control, needs only its share of the width, which grows with Windows' text size.
public static class SideBySide
{
    public static bool Fits(double available, double first, double second, double spacing, double firstShare = 0, double textScale = 1) =>
        (firstShare > 0 ? Math.Min(first, available * firstShare * textScale) : first) + spacing + second <= available + 0.5;
}
