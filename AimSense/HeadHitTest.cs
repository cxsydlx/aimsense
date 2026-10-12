namespace AimSense;

internal static class HeadHitTest
{
    // Camera ray against the same ellipsoid dimensions used to build the visible head.
    public static bool Contains(double rayX, double rayY, double x, double y, double depth, double size)
    {
        if (size <= 0 || depth <= 0) return false;
        double rx = HeadProjection.HalfWidth * size, ry = HeadProjection.HalfHeight * size, rz = HeadProjection.HalfDepth * size;
        double dx = rayX / rx, dy = rayY / ry, dz = 1 / rz;
        double cx = x / rx, cy = y / ry, cz = depth / rz;
        double a = dx * dx + dy * dy + dz * dz;
        double b = dx * cx + dy * cy + dz * cz;
        double c = cx * cx + cy * cy + cz * cz - 1;
        return b > 0 && b * b - a * c >= 0;
    }
}
