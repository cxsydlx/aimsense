using System.Numerics;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace AimSense;

/// <summary>Reusable meshes; an update changes transforms only. Physics Z is reflected for WPF.</summary>
public sealed class CharacterScene3D
{
    private readonly PerspectiveCamera _camera;
    private readonly Model3DGroup _actor = new();
    private readonly ModelVisual3D _actorVisual;
    private readonly BodyPart _head, _chest, _pelvis;
    private readonly BodyPart[] _limbs;
    private readonly Material _headMaterial = Material(Color.FromRgb(242, 139, 110));
    private readonly Material _bodyMaterial = Material(Color.FromRgb(90, 122, 155));
    private readonly Material _previewMaterial = Material(Color.FromArgb(125, 120, 208, 255));
    private bool _preview;
    private bool _visible = true;
    private ActorPose? _lastPose;
    private double _yaw = double.NaN, _pitch = double.NaN, _fov = double.NaN;

    public CharacterScene3D(Viewport3D viewport)
    {
        RenderOptions.SetEdgeMode(viewport, EdgeMode.Aliased);
        _camera = new PerspectiveCamera
        {
            Position = new Point3D(0, 1.62, 0), LookDirection = new Vector3D(0, 0, -1),
            UpDirection = new Vector3D(0, 1, 0), FieldOfView = 90, NearPlaneDistance = .08, FarPlaneDistance = 160
        };
        viewport.Camera = _camera;
        var lighting = new Model3DGroup();
        lighting.Children.Add(new AmbientLight(Color.FromRgb(120, 131, 145)));
        lighting.Children.Add(new DirectionalLight(Color.FromRgb(255, 237, 222), new Vector3D(.3, -.8, -.5)));
        lighting.Freeze();
        viewport.Children.Add(new ModelVisual3D { Content = lighting });
        viewport.Children.Add(new ModelVisual3D { Content = BuildGround() });

        var mesh = BuildUnitSphere();
        _head = new(mesh, _headMaterial); _chest = new(mesh, _bodyMaterial); _pelvis = new(mesh, _bodyMaterial);
        _limbs = Enumerable.Range(0, 8).Select(_ => new BodyPart(mesh, _bodyMaterial)).ToArray();
        _actor.Children.Add(_head.Model); _actor.Children.Add(_chest.Model); _actor.Children.Add(_pelvis.Model);
        foreach (var limb in _limbs) _actor.Children.Add(limb.Model);
        _actorVisual = new ModelVisual3D { Content = _actor };
        viewport.Children.Add(_actorVisual);
    }

    public void SetCamera(double yaw, double pitch, double hFov)
    {
        if (yaw == _yaw && pitch == _pitch && hFov == _fov) return;
        _yaw = yaw; _pitch = pitch; _fov = hFov;
        double y = yaw * Math.PI / 180, p = Math.Clamp(pitch, -85, 85) * Math.PI / 180;
        _camera.LookDirection = new Vector3D(Math.Sin(y) * Math.Cos(p), Math.Sin(p), -Math.Cos(y) * Math.Cos(p));
        _camera.UpDirection = new Vector3D(-Math.Sin(y) * Math.Sin(p), Math.Cos(p), Math.Cos(y) * Math.Sin(p));
        if (_camera.FieldOfView != Math.Clamp(hFov, 60, 140)) _camera.FieldOfView = Math.Clamp(hFov, 60, 140);
    }

    public void SetActor(ActorPose pose, bool preview)
    {
        if (!_visible) { _actorVisual.Content = _actor; _visible = true; }
        if (_preview != preview)
        {
            _head.SetMaterial(preview ? _previewMaterial : _headMaterial);
            _chest.SetMaterial(preview ? _previewMaterial : _bodyMaterial);
            _pelvis.SetMaterial(preview ? _previewMaterial : _bodyMaterial);
            foreach (var limb in _limbs) limb.SetMaterial(preview ? _previewMaterial : _bodyMaterial);
            _preview = preview;
        }
        if (_lastPose == pose) return;
        _lastPose = pose;
        _head.SetEllipsoid(pose.HeadCenter, ActorPose.HeadRadii, pose.FacingYaw);
        _chest.SetEllipsoid(pose.ChestCenter, new(.245f, .28f, .13f), pose.FacingYaw);
        _pelvis.SetEllipsoid(pose.PelvisCenter, new(.18f, .15f, .12f), pose.FacingYaw);
        _limbs[0].SetSegment(pose.LeftShoulder, pose.LeftElbow, .065);
        _limbs[1].SetSegment(pose.LeftElbow, pose.LeftHand, .052);
        _limbs[2].SetSegment(pose.RightShoulder, pose.RightElbow, .065);
        _limbs[3].SetSegment(pose.RightElbow, pose.RightHand, .052);
        _limbs[4].SetSegment(pose.LeftHip, pose.LeftKnee, .082);
        _limbs[5].SetSegment(pose.LeftKnee, pose.LeftFoot, .062);
        _limbs[6].SetSegment(pose.RightHip, pose.RightKnee, .082);
        _limbs[7].SetSegment(pose.RightKnee, pose.RightFoot, .062);
    }

    public void HideActor()
    {
        if (!_visible) return;
        _actorVisual.Content = null;
        _visible = false;
    }

    private sealed class BodyPart
    {
        public GeometryModel3D Model { get; }
        private readonly ScaleTransform3D _scale = new();
        private readonly QuaternionRotation3D _rotation = new();
        private readonly TranslateTransform3D _translation = new();
        public BodyPart(MeshGeometry3D mesh, Material material)
        {
            var transforms = new Transform3DGroup();
            transforms.Children.Add(_scale); transforms.Children.Add(new RotateTransform3D(_rotation)); transforms.Children.Add(_translation);
            Model = new GeometryModel3D(mesh, material) { Transform = transforms };
        }
        public void SetMaterial(Material material) { Model.Material = material; }
        public void SetEllipsoid(Vector3 position, Vector3 radii, double yaw)
        {
            _scale.ScaleX = radii.X; _scale.ScaleY = radii.Y; _scale.ScaleZ = radii.Z;
            _rotation.Quaternion = new System.Windows.Media.Media3D.Quaternion(new Vector3D(0, 1, 0), -yaw);
            Translate(position);
        }
        public void SetSegment(Vector3 a, Vector3 b, double radius)
        {
            Vector3 direction = b - a;
            double length = direction.Length();
            _scale.ScaleX = radius; _scale.ScaleY = length / 2 + radius * .25; _scale.ScaleZ = radius;
            Vector3D axis = new(direction.X, direction.Y, -direction.Z);
            if (length > .0001)
            {
                axis.Normalize();
                Vector3D cross = Vector3D.CrossProduct(new(0, 1, 0), axis);
                double angle = Math.Acos(Math.Clamp(axis.Y, -1, 1)) * 180 / Math.PI;
                _rotation.Quaternion = cross.LengthSquared < 1e-10
                    ? new System.Windows.Media.Media3D.Quaternion(new Vector3D(1, 0, 0), axis.Y < 0 ? 180 : 0)
                    : new System.Windows.Media.Media3D.Quaternion(cross, angle);
            }
            Translate((a + b) / 2);
        }
        private void Translate(Vector3 position) { _translation.OffsetX = position.X; _translation.OffsetY = position.Y; _translation.OffsetZ = -position.Z; }
    }

    private static Material Material(Color color)
    {
        var material = new DiffuseMaterial(new SolidColorBrush(color)); material.Freeze(); return material;
    }

    private static MeshGeometry3D BuildUnitSphere()
    {
        const int slices = 24, stacks = 16;
        var mesh = new MeshGeometry3D();
        for (int row = 0; row <= stacks; row++)
        for (int col = 0; col <= slices; col++)
        {
            double latitude = Math.PI * row / stacks, longitude = 2 * Math.PI * col / slices;
            double x = Math.Sin(latitude) * Math.Cos(longitude), y = Math.Cos(latitude), z = Math.Sin(latitude) * Math.Sin(longitude);
            mesh.Positions.Add(new Point3D(x, y, z)); mesh.Normals.Add(new Vector3D(x, y, z));
        }
        for (int row = 0; row < stacks; row++)
        for (int col = 0; col < slices; col++)
        {
            int a = row * (slices + 1) + col, b = a + slices + 1;
            mesh.TriangleIndices.Add(a); mesh.TriangleIndices.Add(a + 1); mesh.TriangleIndices.Add(b);
            mesh.TriangleIndices.Add(a + 1); mesh.TriangleIndices.Add(b + 1); mesh.TriangleIndices.Add(b);
        }
        mesh.Freeze(); return mesh;
    }

    private static Model3D BuildGround()
    {
        var ground = new Model3DGroup();
        var plane = new MeshGeometry3D();
        Quad(plane, -80, -80, 80, 80, -.02);
        plane.Freeze();
        ground.Children.Add(new GeometryModel3D(plane, Material(Color.FromRgb(25, 34, 45))));
        var grid = new MeshGeometry3D();
        for (int i = -40; i <= 40; i++)
        {
            double width = i % 5 == 0 ? .018 : .006;
            Quad(grid, i - width, -40, i + width, 40, -.015);
            Quad(grid, -40, i - width, 40, i + width, -.015);
        }
        grid.Freeze();
        var gridMaterial = Material(Color.FromRgb(49, 64, 79));
        ground.Children.Add(new GeometryModel3D(grid, gridMaterial));
        ground.Freeze(); return ground;
    }

    private static void Quad(MeshGeometry3D mesh, double x1, double z1, double x2, double z2, double y)
    {
        int start = mesh.Positions.Count;
        mesh.Positions.Add(new(x1, y, z1)); mesh.Positions.Add(new(x1, y, z2)); mesh.Positions.Add(new(x2, y, z2)); mesh.Positions.Add(new(x2, y, z1));
        for (int i = 0; i < 4; i++) mesh.Normals.Add(new(0, 1, 0));
        mesh.TriangleIndices.Add(start); mesh.TriangleIndices.Add(start + 1); mesh.TriangleIndices.Add(start + 2);
        mesh.TriangleIndices.Add(start); mesh.TriangleIndices.Add(start + 2); mesh.TriangleIndices.Add(start + 3);
    }
}
