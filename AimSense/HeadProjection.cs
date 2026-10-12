namespace AimSense;

internal static class HeadProjection
{
    public const double HalfWidth = .5;
    public const double HalfHeight = .62;
    public const double HalfDepth = .43;
    public const double Padding = 12;

    // Reserve room for the closest face of the complete mesh, not only its center.
    public static double CenterLimit(double focal, double viewportHalfSize, double depth, double size, double modelHalfSize)
    {
        double nearestDepth = depth - HalfDepth * size;
        return Math.Max(0, (viewportHalfSize - Padding) * nearestDepth / focal - modelHalfSize * size);
    }
}
