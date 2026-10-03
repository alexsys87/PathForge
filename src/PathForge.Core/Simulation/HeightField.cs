using PathForge.Core.Geometry;
using PathForge.Core.Localization;

namespace PathForge.Core.Simulation;

/// <summary>
/// Top surface of the stock as a grid of heights (a "Z map"): each cell keeps the lowest Z the tool has
/// reached over it. Burn keeps the strongest laser exposure (0…255) per cell.
/// </summary>
public sealed class HeightField
{
    public HeightField(Vec2 origin, int width, int height, double cellSize, double top, double bottom)
    {
        if (width <= 0 || height <= 0 || cellSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), Loc.T("Пустая область заготовки.", "The stock area is empty."));
        }

        Origin = origin;
        Width = width;
        Height = height;
        CellSize = cellSize;
        Top = top;
        Bottom = bottom;
        Heights = new float[width * height];
        Burn = new byte[width * height];
        Reset();
    }

    /// <summary>Lower-left corner of cell (0, 0).</summary>
    public Vec2 Origin { get; }

    public int Width { get; }

    public int Height { get; }

    public double CellSize { get; }

    public double Top { get; }

    public double Bottom { get; }

    /// <summary>Row-major heights, row 0 at the lowest Y.</summary>
    public float[] Heights { get; }

    public byte[] Burn { get; }

    public double SizeX => Width * CellSize;

    public double SizeY => Height * CellSize;

    public float this[int i, int j] => Heights[j * Width + i];

    public void Reset()
    {
        Array.Fill(Heights, (float)Top);
        Array.Clear(Burn);
    }

    public Vec2 CellCenter(int i, int j) => new(Origin.X + (i + 0.5) * CellSize, Origin.Y + (j + 0.5) * CellSize);

    /// <summary>Height under a point (the surface outside the grid is the untouched top).</summary>
    public double HeightAt(Vec2 p)
    {
        var i = (int)Math.Floor((p.X - Origin.X) / CellSize);
        var j = (int)Math.Floor((p.Y - Origin.Y) / CellSize);
        return i < 0 || j < 0 || i >= Width || j >= Height ? Top : Heights[j * Width + i];
    }

    /// <summary>Volume removed from the stock (mm³).</summary>
    public double RemovedVolume()
    {
        double sum = 0;
        foreach (var h in Heights)
        {
            sum += Top - Math.Max(h, Bottom);
        }

        return sum * CellSize * CellSize;
    }

    /// <summary>
    /// Coarser copy for display with at most <paramref name="maxSide"/> cells along each side. Each display
    /// cell takes the lowest height (so narrow grooves stay visible) and the strongest burn of its block.
    /// </summary>
    public HeightField Downsample(int maxSide)
    {
        var factor = Math.Max(1, (int)Math.Ceiling(Math.Max(Width, Height) / (double)Math.Max(2, maxSide)));
        if (factor == 1)
        {
            return this;
        }

        var width = (Width + factor - 1) / factor;
        var height = (Height + factor - 1) / factor;
        var result = new HeightField(Origin, width, height, CellSize * factor, Top, Bottom);
        for (var j = 0; j < Height; j++)
        {
            var row = j / factor * width;
            for (var i = 0; i < Width; i++)
            {
                var source = j * Width + i;
                var target = row + i / factor;
                if (Heights[source] < result.Heights[target])
                {
                    result.Heights[target] = Heights[source];
                }

                if (Burn[source] > result.Burn[target])
                {
                    result.Burn[target] = Burn[source];
                }
            }
        }

        return result;
    }
}
