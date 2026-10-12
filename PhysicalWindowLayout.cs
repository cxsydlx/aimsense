namespace AimSense;
internal readonly record struct LogicalWindowBounds(double Left, double Top, double Width, double Height);
internal static class PhysicalWindowLayout
{
    public static LogicalWindowBounds ToLogical(PhysicalDisplayBounds physical, uint dpi)
    {
        if (dpi == 0 || physical.Width <= 0 || physical.Height <= 0) throw new ArgumentOutOfRangeException(nameof(dpi));
        double scale = dpi / 96d;
        return new(physical.Left / scale, physical.Top / scale, physical.Width / scale, physical.Height / scale);
    }
}
