using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace MCL;

internal static class CharacterPreviewModel
{
    private record struct Atlas(int X, int Y);

    public static Model3D Create(BitmapSource? skin, BitmapSource? cape, bool slim)
    {
        var model = new Model3DGroup();
        var modern = skin?.PixelHeight == 64;
        var armWidth = slim ? 3 : 4;
        var skinTone = Color.FromRgb(204, 158, 171);
        var top = Color.FromRgb(175, 109, 150);
        var trousers = Color.FromRgb(98, 82, 121);

        AddBox(model, new(-4, 8, -4), 8, 8, 8, skin, new(0, 0), new(32, 0), skinTone);
        AddBox(model, new(-4, -4, -2), 8, 12, 4, skin, new(16, 16), modern ? new(16, 32) : null, top);
        AddBox(model, new(-4 - armWidth, -4, -2), armWidth, 12, 4, skin, new(40, 16), modern ? new(40, 32) : null, skinTone);
        AddBox(model, new(4, -4, -2), armWidth, 12, 4, skin, modern ? new(32, 48) : new(40, 16),
            modern ? new(48, 48) : null, skinTone, mirror: !modern);
        AddBox(model, new(-4, -16, -2), 4, 12, 4, skin, new(0, 16), modern ? new(0, 32) : null, trousers);
        AddBox(model, new(0, -16, -2), 4, 12, 4, skin, modern ? new(16, 48) : new(0, 16),
            modern ? new(0, 48) : null, trousers, mirror: !modern);

        if (cape is not null)
        {
            var cloak = new Model3DGroup
            {
                Transform = new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), 12), new Point3D(0, 8, -2.25))
            };
            // The cape atlas lays out its outer face first, unlike a skin viewed from the front.
            AddBox(cloak, new(-5, -8, -3.25), 10, 16, 1, cape, new(0, 0), null, Colors.Transparent, reverse: true);
            model.Children.Add(cloak);
        }

        model.Freeze();
        return model;
    }

    private static void AddBox(Model3DGroup model, Point3D origin, int width, int height, int depth,
        BitmapSource? texture, Atlas atlas, Atlas? overlay, Color fallback, bool mirror = false, bool reverse = false)
    {
        var (x0, y0, z0) = (origin.X, origin.Y, origin.Z);
        var (x1, y1, z1) = (x0 + width, y0 + height, z0 + depth);
        var front = depth;
        var back = width + 2 * depth;
        var left = 0;
        var right = width + depth;
        if (reverse) (front, back, left, right) = (back, front, right, left);
        if (mirror) (left, right) = (right, left);

        AddFace(new(x0, y1, z1), new(x1, y1, z1), new(x1, y0, z1), new(x0, y0, z1), front, depth, width, height);
        AddFace(new(x1, y1, z0), new(x0, y1, z0), new(x0, y0, z0), new(x1, y0, z0), back, depth, width, height);
        AddFace(new(x0, y1, z0), new(x0, y1, z1), new(x0, y0, z1), new(x0, y0, z0), left, depth, depth, height);
        AddFace(new(x1, y1, z1), new(x1, y1, z0), new(x1, y0, z0), new(x1, y0, z1), right, depth, depth, height);
        AddFace(new(x0, y1, z0), new(x1, y1, z0), new(x1, y1, z1), new(x0, y1, z1), depth, 0, width, depth, flipVertical: reverse);
        AddFace(new(x0, y0, z1), new(x1, y0, z1), new(x1, y0, z0), new(x0, y0, z0), depth + width, 0, width, depth, flipVertical: !reverse);

        void AddFace(Point3D topLeft, Point3D topRight, Point3D bottomRight, Point3D bottomLeft,
            int u, int v, int w, int h, bool flipVertical = false)
        {
            var mesh = new MeshGeometry3D
            {
                Positions = new Point3DCollection([topLeft, topRight, bottomRight, bottomLeft]),
                TriangleIndices = new Int32Collection([0, 2, 1, 0, 3, 2]),
                TextureCoordinates = new PointCollection(
                    [Uv(0, 0), Uv(1, 0), Uv(1, 1), Uv(0, 1)])
            };
            Brush brush = texture is null ? new SolidColorBrush(fallback)
                : new ImageBrush(CreateFaceTexture(texture, atlas, overlay, fallback, u, v, w, h));
            model.Children.Add(new GeometryModel3D(mesh, new DiffuseMaterial(brush)));

            Point Uv(int s, int t) => new(mirror ? 1 - s : s, flipVertical ? 1 - t : t);
        }
    }

    private static BitmapSource CreateFaceTexture(BitmapSource texture, Atlas atlas, Atlas? overlay, Color fallback,
        int u, int v, int width, int height)
    {
        // Upscale before mapping to the mesh so the 3D sampler preserves the pixel art.
        const int scale = 8;
        var visual = new DrawingVisual();
        var bounds = new Rect(0, 0, width, height);
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(fallback), null, bounds);
            drawing.DrawImage(new CroppedBitmap(texture, new Int32Rect(atlas.X + u, atlas.Y + v, width, height)), bounds);
            if (overlay is { } layer)
                drawing.DrawImage(new CroppedBitmap(texture, new Int32Rect(layer.X + u, layer.Y + v, width, height)), bounds);
        }
        var face = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        face.Render(visual);
        var source = new int[width * height];
        face.CopyPixels(source, width * sizeof(int), 0);
        var scaledWidth = width * scale;
        var scaledHeight = height * scale;
        var pixels = new int[scaledWidth * scaledHeight];
        for (var y = 0; y < scaledHeight; y++)
        for (var x = 0; x < scaledWidth; x++)
            pixels[y * scaledWidth + x] = source[y / scale * width + x / scale];

        var bitmap = BitmapSource.Create(scaledWidth, scaledHeight, 96, 96,
            PixelFormats.Pbgra32, null, pixels, scaledWidth * sizeof(int));
        bitmap.Freeze();
        return bitmap;
    }
}
