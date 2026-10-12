using System.Numerics;

namespace AimSense;

public readonly record struct ProjectedTarget(double X, double Y, double Depth, bool OnScreen);

/// <summary>World coordinates: X right, Y up, Z forward. Angles are in degrees.</summary>
public sealed class FpsCamera
{
    private double _yaw;
    private double _pitch;
    public double Yaw { get => _yaw; set => _yaw = double.IsFinite(value) ? value : _yaw; }
    public double Pitch { get => _pitch; set => _pitch = double.IsFinite(value) ? Math.Clamp(value, -85, 85) : _pitch; }
    public Vector3 Position { get; set; } = new(0, 1.62f, 0);
    public Vector3 Forward
    {
        get
        {
            double yaw = Yaw * Math.PI / 180, pitch = Pitch * Math.PI / 180;
            return Vector3.Normalize(new((float)(Math.Sin(yaw) * Math.Cos(pitch)), (float)Math.Sin(pitch), (float)(Math.Cos(yaw) * Math.Cos(pitch))));
        }
    }

    public void ApplyCounts(double dx, double dy, double dpi, double cm360)
    {
        if (!double.IsFinite(dx) || !double.IsFinite(dy) || !double.IsFinite(dpi) || !double.IsFinite(cm360) || dpi <= 0 || cm360 <= 0)
            throw new ArgumentOutOfRangeException(nameof(dpi), "Counts must be finite; DPI and cm/360 must be positive.");
        double degreesPerCount = 360 * 2.54 / (dpi * cm360);
        Yaw += dx * degreesPerCount;
        Pitch -= dy * degreesPerCount;
    }

    public (double Yaw, double Pitch) TargetAngles(ActorPose pose)
    {
        Vector3 target = pose.HeadCenter - Position;
        return (Math.Atan2(target.X, target.Z) * 180 / Math.PI,
            Math.Atan2(target.Y, Math.Sqrt(target.X * target.X + target.Z * target.Z)) * 180 / Math.PI);
    }

    public double AngleError(ActorPose pose)
    {
        Vector3 target = pose.HeadCenter - Position;
        if (target.LengthSquared() < 1e-12) return 0;
        // atan2 keeps precision for small offsets where an acos dot product rounds to one.
        var forward = Forward;
        return Math.Atan2(Vector3.Cross(forward, target).Length(), Vector3.Dot(forward, target)) * 180 / Math.PI;
    }

    public bool ContainsHead(ActorPose pose)
    {
        var inverse = Matrix4x4.CreateRotationY((float)(-pose.FacingYaw * Math.PI / 180));
        Vector3 origin = Vector3.TransformNormal(Position - pose.HeadCenter, inverse) / ActorPose.HeadRadii;
        Vector3 ray = Vector3.TransformNormal(Forward, inverse) / ActorPose.HeadRadii;
        double a = (double)ray.X * ray.X + (double)ray.Y * ray.Y + (double)ray.Z * ray.Z;
        if (a <= 0) return false;
        double t = -((double)origin.X * ray.X + (double)origin.Y * ray.Y + (double)origin.Z * ray.Z) / a;
        // The closest point avoids cancellation in the quadratic discriminant at long distances.
        t = Math.Max(0, t);
        double x = origin.X + t * ray.X, y = origin.Y + t * ray.Y, z = origin.Z + t * ray.Z;
        return x * x + y * y + z * z <= 1;
    }

    public ProjectedTarget Project(Vector3 world, double hFov, double aspect)
    {
        if (!double.IsFinite(hFov) || hFov <= 0 || hFov >= 180 || !double.IsFinite(aspect) || aspect <= 0)
            throw new ArgumentOutOfRangeException(nameof(hFov));
        double yaw = Yaw * Math.PI / 180;
        Vector3 right = new((float)Math.Cos(yaw), 0, (float)-Math.Sin(yaw));
        Vector3 up = Vector3.Normalize(Vector3.Cross(Forward, right));
        Vector3 delta = world - Position;
        double depth = Vector3.Dot(delta, Forward), tanHalf = Math.Tan(hFov * Math.PI / 360);
        // A signed small depth preserves useful offscreen direction without NaN/Infinity.
        double divisor = Math.Abs(depth) < .0001 ? .0001 : depth;
        double x = .5 + Vector3.Dot(delta, right) / (2 * divisor * tanHalf);
        double y = .5 - Vector3.Dot(delta, up) * aspect / (2 * divisor * tanHalf);
        return new(x, y, depth, depth > .1 && x >= 0 && x <= 1 && y >= 0 && y <= 1);
    }
}
