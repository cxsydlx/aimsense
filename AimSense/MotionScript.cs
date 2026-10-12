using System.Numerics;

namespace AimSense;

public enum MotionProfile { Tactical, Mobile }

public sealed record ActorPose(Vector3 RootPosition, Vector3 Velocity, double Crouch = 0, double GaitPhase = 0, double FacingYaw = 0)
{
    public static readonly Vector3 HeadRadii = new(.10f, .12f, .09f);
    public Vector3 HeadCenter => Joint(0, 1.62 - .50 * Crouch, 0);
    public Vector3 Neck => Joint(0, 1.47 - .50 * Crouch, 0);
    public Vector3 ChestCenter => Joint(0, 1.28 - .43 * Crouch, 0);
    public Vector3 PelvisCenter => Joint(0, .88 - .35 * Crouch, 0);
    public Vector3 LeftShoulder => Joint(-.23, 1.43 - .45 * Crouch, 0);
    public Vector3 RightShoulder => Joint(.23, 1.43 - .45 * Crouch, 0);
    public Vector3 LeftElbow => Joint(-.29, 1.13 - .38 * Crouch, .06 + Stride * .5);
    public Vector3 RightElbow => Joint(.29, 1.13 - .38 * Crouch, .06 - Stride * .5);
    public Vector3 LeftHand => Joint(-.23, .96 - .32 * Crouch, .16 + Stride);
    public Vector3 RightHand => Joint(.23, .96 - .32 * Crouch, .16 - Stride);
    public Vector3 LeftHip => Joint(-.13, .87 - .35 * Crouch, 0);
    public Vector3 RightHip => Joint(.13, .87 - .35 * Crouch, 0);
    public Vector3 LeftKnee => Joint(-.14, .48 - .14 * Crouch, .23 * Crouch - Stride * .5);
    public Vector3 RightKnee => Joint(.14, .48 - .14 * Crouch, .23 * Crouch + Stride * .5);
    public Vector3 LeftFoot => Joint(-.15, .07, .04 - Stride);
    public Vector3 RightFoot => Joint(.15, .07, .04 + Stride);
    private double Stride => Math.Sin(GaitPhase) * Math.Min(.17, new Vector2(Velocity.X, Velocity.Z).Length() * .04);

    private Vector3 Joint(double x, double y, double z) => RootPosition + Vector3.TransformNormal(
        new((float)x, (float)y, (float)z), Matrix4x4.CreateRotationY((float)(FacingYaw * Math.PI / 180)));

    public ActorPose RotateYaw(double degrees)
    {
        var rotation = Matrix4x4.CreateRotationY((float)(degrees * Math.PI / 180));
        return this with { RootPosition = Vector3.Transform(RootPosition, rotation), Velocity = Vector3.TransformNormal(Velocity, rotation), FacingYaw = FacingYaw + degrees };
    }
}

/// <summary>A versioned, deterministic 12 second motion script sampled on a 240 Hz physics clock.</summary>
public sealed class MotionScript
{
    public const string CurrentVersion = "character-motion-2";
    public const int PhysicsHz = 240;
    private const double Duration = 12;
    private readonly ActorPose[] _poses;
    public string Version { get; private set; } = CurrentVersion;
    public string ScriptId => $"{Version}/{Profile.ToString().ToLowerInvariant()}/{Id}";
    public int Id { get; }
    public MotionProfile Profile { get; }
    public double Distance { get; }
    public double SpeedScale { get; }
    public double PathScale { get; }
    public double DurationSeconds => Duration;
    public double HorizontalFov { get; }
    public double Aspect { get; }

    private MotionScript(MotionProfile profile, int id, double distance, double speedScale, double hFov, double aspect, ActorPose[] poses, double pathScale)
    {
        Profile = profile; Id = id; Distance = distance; SpeedScale = speedScale;
        HorizontalFov = hFov; Aspect = aspect; _poses = poses; PathScale = pathScale;
    }

    public static MotionScript Create(MotionProfile profile, int id, double distance, double speedScale, double hFov, double aspect, string version = CurrentVersion)
    {
        if (version is not ("character-motion-1" or CurrentVersion)) throw new ArgumentException("不支持此动作版本。");
        if (id < 0 || id > 7) throw new ArgumentOutOfRangeException(nameof(id), "Each library has eight scripts (0–7).");
        if (!Enum.IsDefined(profile) || !double.IsFinite(distance) || distance < 1 || !double.IsFinite(speedScale) || speedScale < 0 || speedScale > 2 ||
            !double.IsFinite(hFov) || hFov < 20 || hFov > 140 || !double.IsFinite(aspect) || aspect <= 0)
            throw new ArgumentOutOfRangeException(nameof(distance));

        const double dt = 1.0 / PhysicsHz;
        int count = (int)(Duration * PhysicsHz) + 1;
        var raw = new ActorPose[count];
        Vector2 position = Vector2.Zero, velocity = Vector2.Zero;
        double height = 0, verticalSpeed = 0, crouch = 0, gait = 0;
        double speed = (profile == MotionProfile.Tactical ? 3 : 6) * speedScale;
        int mirror = id >= 4 ? -1 : 1, variant = id % 4;
        int previousBeat = -1;
        raw[0] = new(new(0, 0, (float)distance), Vector3.Zero);
        for (int i = 1; i < count; i++)
        {
            double time = (i - 1) * dt;
            int beat = Math.Min(15, (int)(time / .75));
            Command command = CommandAt(profile, version == CurrentVersion && id >= 6 ? id-2 : variant, beat);
            Vector2 direction = new((float)(command.X * mirror), (float)command.Z);
            if (direction.LengthSquared() > 1) direction = Vector2.Normalize(direction);
            Vector2 desired = direction * (float)speed;
            // Braking is stronger than acceleration, including before changing direction.
            bool braking = Vector2.Dot(velocity, desired) < 0 || desired.LengthSquared() < velocity.LengthSquared();
            velocity = Approach(velocity, desired, (float)((braking ? 24 : 18) * dt));
            position += velocity * (float)dt;
            if (profile == MotionProfile.Mobile && command.Jump && beat != previousBeat && height <= 0)
                verticalSpeed = 4.5;
            if (height > 0 || verticalSpeed > 0)
            {
                height += verticalSpeed * dt - .5 * 10 * dt * dt;
                verticalSpeed -= 10 * dt;
                if (height <= 0) { height = 0; verticalSpeed = 0; }
            }
            crouch = Approach(crouch, command.Crouch ? 1 : 0, dt * 4);
            gait += velocity.Length() * dt * 8;
            raw[i] = new(new(position.X, (float)height, (float)distance + position.Y), new(velocity.X, (float)verticalSpeed, velocity.Y), crouch, gait);
            previousBeat = beat;
        }

        // Find one scale for the entire horizontal/depth path. No frame is independently clamped.
        // Jump height remains physical; an unusually narrow vertical FOV can require a farther origin.
        double tanH = Math.Tan(hFov * Math.PI / 360), tanV = tanH / aspect;
        double highestHead = raw.Max(p => Math.Abs(p.HeadCenter.Y - 1.62) + ActorPose.HeadRadii.Y + .03);
        double safeDistance = Math.Max(distance, highestHead / (tanV * .88) + ActorPose.HeadRadii.Z);
        bool Fits(double scale)
        {
            foreach (var pose in raw)
            {
                double z = safeDistance + (pose.RootPosition.Z - distance) * scale - ActorPose.HeadRadii.Z;
                if (z <= .5 || Math.Abs(pose.RootPosition.X * scale) + .34 > z * tanH * .88 ||
                    Math.Abs(pose.HeadCenter.Y - 1.62) + ActorPose.HeadRadii.Y + .03 > z * tanV * .88) return false;
            }
            return true;
        }
        double pathScale = 1;
        if (!Fits(1))
        {
            double low = 0, high = 1;
            for (int iteration = 0; iteration < 36; iteration++)
            {
                double mid = (low + high) / 2;
                if (Fits(mid)) low = mid; else high = mid;
            }
            pathScale = low;
        }
        var bounded = raw.Select(p => p with
        {
            RootPosition = new((float)(p.RootPosition.X * pathScale), p.RootPosition.Y, (float)(safeDistance + (p.RootPosition.Z - distance) * pathScale)),
            Velocity = new((float)(p.Velocity.X * pathScale), p.Velocity.Y, (float)(p.Velocity.Z * pathScale))
        }).ToArray();
        return new(profile, id, safeDistance, speedScale, hFov, aspect, bounded, pathScale) { Version = version };
    }

    public ActorPose Sample(double seconds)
    {
        if (!double.IsFinite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        double frame = Math.Clamp(seconds, 0, Duration) * PhysicsHz;
        int index = Math.Min((int)frame, _poses.Length - 2);
        float alpha = (float)(frame - index);
        ActorPose a = _poses[index], b = _poses[index + 1];
        return new(Vector3.Lerp(a.RootPosition, b.RootPosition, alpha), Vector3.Lerp(a.Velocity, b.Velocity, alpha),
            a.Crouch + (b.Crouch - a.Crouch) * alpha, a.GaitPhase + (b.GaitPhase - a.GaitPhase) * alpha);
    }

    private readonly record struct Command(double X, double Z = 0, bool Crouch = false, bool Jump = false);
    // 4/5 mirror 0/1; 6/7 use held-out combinations in version 2.
    private static readonly Command[][] Tactical =
    [
        [new(1), new(0), new(-1), new(-1), new(0), new(1), new(1), new(0), new(-1), new(0), new(1), new(0), new(-1), new(0), new(1), new(-1)],
        [new(1), new(0), new(-1), new(0), new(1), new(0, Crouch:true), new(-1), new(0), new(-1), new(0), new(1), new(0), new(1), new(0,Crouch:true), new(-1), new(0)],
        [new(.7,-.5), new(0), new(-.7,.5), new(-1), new(0), new(1), new(0,.7), new(-1), new(0), new(1), new(0,-.7), new(0,Crouch:true), new(-1), new(0), new(1), new(0)],
        [new(1), new(-1), new(0), new(1,Crouch:true), new(0,Crouch:true), new(-1), new(0,-.7), new(0), new(-1), new(1), new(0), new(-1,Crouch:true), new(0), new(1), new(0,.7), new(0)],
        [new(-.8,-.4),new(0),new(1),new(0,Crouch:true),new(-1,Crouch:true),new(0),new(.7,.5),new(0),new(-1),new(0),new(1),new(1),new(0,-.7),new(0),new(-1),new(0)],
        [new(0,.7),new(1),new(0),new(-1),new(-1,Crouch:true),new(0,Crouch:true),new(1),new(0,-.7),new(-1),new(0),new(.7,.5),new(0),new(-1),new(1),new(0),new(-1)]
    ];
    private static readonly Command[][] Mobile =
    [
        [new(1),new(-1),new(1,Jump:true),new(1),new(-1),new(0),new(-1),new(1),new(-1,Jump:true),new(-1),new(1),new(0),new(1),new(-1),new(1),new(0)],
        [new(.8,-.5),new(-1),new(.8,.5,Jump:true),new(-1),new(1),new(0),new(-.8,-.5),new(1),new(-.8,.5,Jump:true),new(1),new(-1),new(0),new(1),new(-1),new(1),new(0)],
        [new(1,Jump:true),new(1),new(-1),new(0,Crouch:true),new(-1),new(1),new(-1,Jump:true),new(-1),new(1),new(0),new(1),new(-1),new(1,Jump:true),new(1),new(-1),new(0)],
        [new(1),new(-1),new(1),new(0,-.8,Jump:true),new(-1),new(1),new(0,.8),new(-1),new(1),new(-1),new(0,-.8,Jump:true),new(1),new(-1),new(1),new(0,.8),new(0)],
        [new(-1),new(0),new(.8,-.5,Jump:true),new(-1),new(1),new(.8,.5),new(-1),new(0),new(1,Jump:true),new(-1),new(1),new(0,-.8),new(-1),new(1),new(0),new(-1)],
        [new(.8,.5),new(-1,Jump:true),new(1),new(0),new(-1),new(0,-.8),new(1),new(-1),new(1,Jump:true),new(0,.8),new(-1),new(1),new(0),new(-1),new(1),new(0)]
    ];
    private static Command CommandAt(MotionProfile profile, int variant, int beat) => (profile == MotionProfile.Tactical ? Tactical : Mobile)[variant][beat];
    private static Vector2 Approach(Vector2 value, Vector2 target, float step)
    {
        var difference = target - value;
        return difference.LengthSquared() <= step * step ? target : value + Vector2.Normalize(difference) * step;
    }
    private static double Approach(double value, double target, double step) => value + Math.Clamp(target - value, -step, step);
}
