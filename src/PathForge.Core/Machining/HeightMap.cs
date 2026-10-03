using PathForge.Core.Import;

namespace PathForge.Core.Machining;

/// <summary>Regular grid of surface heights (≤ 0, relative to the relief top). Cell (0, 0) is the lower-left one.</summary>
internal sealed class HeightMap
{
    public HeightMap(int columns, int rows, double cellSize, double originX, double originY)
    {
        Columns = columns;
        Rows = rows;
        CellSize = cellSize;
        OriginX = originX;
        OriginY = originY;
        Z = new float[columns * rows];
    }

    public int Columns { get; }

    public int Rows { get; }

    public double CellSize { get; }

    /// <summary>Drawing coordinates of the lower-left corner of the grid.</summary>
    public double OriginX { get; }

    public double OriginY { get; }

    public float[] Z { get; }

    public float this[int column, int row]
    {
        get => Z[row * Columns + column];
        set => Z[row * Columns + column] = value;
    }

    public double CellX(int column) => OriginX + (column + 0.5) * CellSize;

    public double CellY(int row) => OriginY + (row + 0.5) * CellSize;

    public static HeightMap FromImage(ReliefOperation operation, int columns, int rows)
    {
        var map = new HeightMap(columns, rows, operation.Resolution, operation.X, operation.Y);
        var image = operation.Image;
        for (var r = 0; r < rows; r++)
        {
            // Image row 0 is the top; map row 0 is the bottom.
            var imageY = (rows - r - 0.5) * image.Height / rows;
            for (var c = 0; c < columns; c++)
            {
                var brightness = image.Sample((c + 0.5) * image.Width / columns, imageY);
                var height = operation.Invert ? 1 - brightness : brightness;
                map[c, r] = (float)(-operation.Depth * (1 - height));
            }
        }

        return map;
    }

    /// <summary>Top surface of the mesh, scaled to the relief width and depth; uncovered cells are at the bottom.</summary>
    public static HeightMap FromMesh(ReliefOperation operation, int columns, int rows)
    {
        var map = new HeightMap(columns, rows, operation.Resolution, operation.X, operation.Y);
        Array.Fill(map.Z, (float)-operation.Depth);
        var mesh = operation.Mesh;
        var (minX, minY, minZ, maxX, _, maxZ) = mesh.Bounds();
        var scale = (maxX - minX) < 1e-12 ? 1 : operation.WidthMm / (maxX - minX);
        var zRange = Math.Max(1e-12, maxZ - minZ);
        var v = mesh.Vertices;

        // Vertices in grid units: column, row, height.
        (double C, double R, double H) Vertex(int index) => (
            (v[index] - minX) * scale / map.CellSize - 0.5,
            (v[index + 1] - minY) * scale / map.CellSize - 0.5,
            ((v[index + 2] - minZ) / zRange - 1) * operation.Depth);

        for (var t = 0; t < v.Length; t += 9)
        {
            var a = Vertex(t);
            var b = Vertex(t + 3);
            var c = Vertex(t + 6);
            var area = (b.C - a.C) * (c.R - a.R) - (c.C - a.C) * (b.R - a.R);
            if (Math.Abs(area) < 1e-12)
            {
                continue; // seen edge-on from above
            }

            var c0 = Math.Max(0, (int)Math.Floor(Math.Min(a.C, Math.Min(b.C, c.C))));
            var c1 = Math.Min(columns - 1, (int)Math.Ceiling(Math.Max(a.C, Math.Max(b.C, c.C))));
            var r0 = Math.Max(0, (int)Math.Floor(Math.Min(a.R, Math.Min(b.R, c.R))));
            var r1 = Math.Min(rows - 1, (int)Math.Ceiling(Math.Max(a.R, Math.Max(b.R, c.R))));
            for (var row = r0; row <= r1; row++)
            {
                for (var col = c0; col <= c1; col++)
                {
                    // Barycentric coordinates of the cell centre.
                    var w0 = ((b.C - col) * (c.R - row) - (c.C - col) * (b.R - row)) / area;
                    var w1 = ((c.C - col) * (a.R - row) - (a.C - col) * (c.R - row)) / area;
                    var w2 = 1 - w0 - w1;
                    if (w0 < -1e-9 || w1 < -1e-9 || w2 < -1e-9)
                    {
                        continue;
                    }

                    var h = (float)(w0 * a.H + w1 * b.H + w2 * c.H);
                    if (h > map[col, row])
                    {
                        map[col, row] = h;
                    }
                }
            }
        }

        return map;
    }

    /// <summary>
    /// "Drop cutter": the lowest tool-tip height at each cell such that the tool does not cut below the surface.
    /// <paramref name="profile"/> gives how much higher the cutting edge is than the tip at distance d from the axis.
    /// </summary>
    public HeightMap DropCutter(double radius, Func<double, double> profile)
    {
        var result = new HeightMap(Columns, Rows, CellSize, OriginX, OriginY);
        var reach = (int)Math.Ceiling(radius / CellSize);
        var kernel = new List<(int Dc, int Dr, float Lift)>();
        for (var dr = -reach; dr <= reach; dr++)
        {
            for (var dc = -reach; dc <= reach; dc++)
            {
                var d = Math.Sqrt(dc * dc + dr * dr) * CellSize;
                if (d <= radius + 1e-9)
                {
                    kernel.Add((dc, dr, (float)profile(Math.Min(d, radius))));
                }
            }
        }

        for (var r = 0; r < Rows; r++)
        {
            for (var c = 0; c < Columns; c++)
            {
                var best = float.NegativeInfinity;
                foreach (var (dc, dr, lift) in kernel)
                {
                    var cc = c + dc;
                    var rr = r + dr;
                    if (cc < 0 || rr < 0 || cc >= Columns || rr >= Rows)
                    {
                        continue;
                    }

                    var z = Z[rr * Columns + cc] - lift;
                    if (z > best)
                    {
                        best = z;
                    }
                }

                result[c, r] = Math.Min(0, best);
            }
        }

        return result;
    }
}
