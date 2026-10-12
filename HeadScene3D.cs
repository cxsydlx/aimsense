using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace AimSense;

// A reusable 3D head mesh. Animation changes only its transform, never the mesh.
internal sealed class HeadScene3D
{
    private readonly ScaleTransform3D _scale = new();
    private readonly TranslateTransform3D _translation = new();

    public HeadScene3D(Viewport3D viewport)
    {
        viewport.Camera = new PerspectiveCamera
        {
            Position = new Point3D(0, 0, 0),
            LookDirection = new Vector3D(0, 0, -1),
            UpDirection = new Vector3D(0, 1, 0),
            FieldOfView = 90,
            NearPlaneDistance = .1,
            FarPlaneDistance = 100
        };
        var lights = new Model3DGroup();
        lights.Children.Add(new AmbientLight(Color.FromRgb(75, 87, 105)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(255, 230, 212), new Vector3D(.3, -.5, -.9)));
        viewport.Children.Add(new ModelVisual3D { Content = lights });

        var transforms = new Transform3DGroup();
        transforms.Children.Add(_scale);
        transforms.Children.Add(_translation);
        var material = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(239, 127, 108)));
        var head = new GeometryModel3D(BuildHead(), material) { BackMaterial = material, Transform = transforms };
        viewport.Children.Add(new ModelVisual3D { Content = head });
    }

    public void SetPose(double x, double y, double depth, double size)
    {
        _scale.ScaleX = size;
        _scale.ScaleY = size;
        _scale.ScaleZ = size;
        _translation.OffsetX = x;
        _translation.OffsetY = y;
        _translation.OffsetZ = -depth;
    }

    private static MeshGeometry3D BuildHead()
    {
        const int slices = 20, stacks = 12;
        var mesh = new MeshGeometry3D();
        for (int row = 0; row <= stacks; row++)
        {
            double latitude = Math.PI * row / stacks;
            for (int col = 0; col <= slices; col++)
            {
                double longitude = 2 * Math.PI * col / slices;
                double x = HeadProjection.HalfWidth * Math.Sin(latitude) * Math.Cos(longitude);
                double y = HeadProjection.HalfHeight * Math.Cos(latitude);
                double z = HeadProjection.HalfDepth * Math.Sin(latitude) * Math.Sin(longitude);
                mesh.Positions.Add(new Point3D(x, y, z));
                mesh.Normals.Add(new Vector3D(x / .25, y / .3844, z / .1849));
            }
        }
        for (int row = 0; row < stacks; row++)
        for (int col = 0; col < slices; col++)
        {
            int a = row * (slices + 1) + col, b = a + slices + 1;
            mesh.TriangleIndices.Add(a); mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(a + 1);
            mesh.TriangleIndices.Add(a + 1); mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(b + 1);
        }
        mesh.Freeze();
        return mesh;
    }
}
