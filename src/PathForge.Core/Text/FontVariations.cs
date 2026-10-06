namespace PathForge.Core.Text;

/// <summary>Design axis of a variable font (weight, width, slant…), in the font's own user units.</summary>
public sealed record FontAxis(string Tag, string Name, double Minimum, double Default, double Maximum);

/// <summary>Named style of a variable font ("Bold", "Condensed Light"…): a point in the design space.</summary>
public sealed record FontInstance(string Name, IReadOnlyDictionary<string, double> Coordinates);

/// <summary>Big-endian reading helpers shared by the variation tables.</summary>
internal readonly struct FontData
{
    public FontData(byte[] bytes)
    {
        Bytes = bytes;
    }

    public byte[] Bytes { get; }

    public int U8(int offset) => Bytes[offset];

    public int U16(int offset) => (Bytes[offset] << 8) | Bytes[offset + 1];

    public short I16(int offset) => (short)U16(offset);

    public uint U32(int offset) => (uint)((Bytes[offset] << 24) | (Bytes[offset + 1] << 16) | (Bytes[offset + 2] << 8) | Bytes[offset + 3]);

    public int I32(int offset) => (int)U32(offset);

    public double F2Dot14(int offset) => I16(offset) / 16384.0;

    public double Fixed(int offset) => I32(offset) / 65536.0;

    public string Tag(int offset) => System.Text.Encoding.ASCII.GetString(Bytes, offset, 4);
}

/// <summary>
/// The design space of a variable font: axes and named instances from 'fvar', and the conversion of user
/// coordinates to normalized ones (−1…0…1) including the 'avar' segment maps.
/// </summary>
internal sealed class FontDesignSpace
{
    private readonly List<List<(double From, double To)>> _avar = new();

    public FontDesignSpace(FontData data, int fvar, int? avar, Func<int, string?> name)
    {
        var axesOffset = fvar + data.U16(fvar + 4);
        var axisCount = data.U16(fvar + 8);
        var axisSize = data.U16(fvar + 10);
        var instanceCount = data.U16(fvar + 12);
        var instanceSize = data.U16(fvar + 14);
        for (var i = 0; i < axisCount; i++)
        {
            var record = axesOffset + i * axisSize;
            var tag = data.Tag(record);
            Axes.Add(new FontAxis(tag, name(data.U16(record + 18)) ?? tag,
                data.Fixed(record + 4), data.Fixed(record + 8), data.Fixed(record + 12)));
        }

        var instances = axesOffset + axisCount * axisSize;
        for (var i = 0; i < instanceCount; i++)
        {
            var record = instances + i * instanceSize;
            var coordinates = new Dictionary<string, double>(StringComparer.Ordinal);
            for (var a = 0; a < axisCount; a++)
            {
                coordinates[Axes[a].Tag] = Math.Round(data.Fixed(record + 4 + a * 4), 3);
            }

            Instances.Add(new FontInstance(name(data.U16(record)) ?? $"#{i + 1}", coordinates));
        }

        if (avar is int map && data.U16(map + 6) == axisCount)
        {
            var p = map + 8;
            for (var a = 0; a < axisCount; a++)
            {
                var count = data.U16(p);
                var segments = new List<(double, double)>(count);
                for (var k = 0; k < count; k++)
                {
                    segments.Add((data.F2Dot14(p + 2 + k * 4), data.F2Dot14(p + 4 + k * 4)));
                }

                _avar.Add(segments);
                p += 2 + count * 4;
            }
        }
    }

    public List<FontAxis> Axes { get; } = new();

    public List<FontInstance> Instances { get; } = new();

    /// <summary>Normalized coordinates (one per axis) of a user location; axes not given stay at their default.</summary>
    public double[] Normalize(IReadOnlyDictionary<string, double> user)
    {
        var result = new double[Axes.Count];
        for (var a = 0; a < Axes.Count; a++)
        {
            var axis = Axes[a];
            if (!user.TryGetValue(axis.Tag, out var value))
            {
                continue;
            }

            value = Math.Clamp(value, axis.Minimum, axis.Maximum);
            var normalized = value < axis.Default
                ? (axis.Default > axis.Minimum ? (value - axis.Default) / (axis.Default - axis.Minimum) : 0)
                : (axis.Maximum > axis.Default ? (value - axis.Default) / (axis.Maximum - axis.Default) : 0);
            if (a < _avar.Count)
            {
                normalized = MapSegments(_avar[a], normalized);
            }

            // Normalized values are stored as F2Dot14 by the fonts: round the same way.
            result[a] = Math.Round(normalized * 16384) / 16384;
        }

        return result;
    }

    private static double MapSegments(List<(double From, double To)> segments, double value)
    {
        if (segments.Count < 2)
        {
            return value;
        }

        for (var k = 1; k < segments.Count; k++)
        {
            var (x0, y0) = segments[k - 1];
            var (x1, y1) = segments[k];
            if (value <= x1)
            {
                return x1 - x0 < 1e-12 ? y0 : y0 + (value - x0) / (x1 - x0) * (y1 - y0);
            }
        }

        return segments[^1].To;
    }
}

/// <summary>
/// Scalar of a variation region at a location: how much of its deltas apply (OpenType "Algorithm for
/// interpolation of instance values").
/// </summary>
internal static class VariationRegion
{
    public static double Scalar(double[] coords, Func<int, (double Start, double Peak, double End)> axis)
    {
        var scalar = 1.0;
        for (var a = 0; a < coords.Length; a++)
        {
            var (start, peak, end) = axis(a);
            if (start > peak || peak > end || (start < 0 && end > 0 && peak != 0) || peak == 0)
            {
                continue;
            }

            var coord = coords[a];
            if (coord < start || coord > end)
            {
                return 0;
            }

            if (coord == peak)
            {
                continue;
            }

            scalar *= coord < peak ? (coord - start) / (peak - start) : (end - coord) / (end - peak);
        }

        return scalar;
    }
}

/// <summary>Item variation store (used by HVAR and CFF2): delta sets per item over shared regions.</summary>
internal sealed class ItemVariationStore
{
    private readonly FontData _data;
    private readonly double[] _regionScalars;
    private readonly List<int> _subtables = new();

    public ItemVariationStore(FontData data, int offset, double[] coords)
    {
        _data = data;
        var regions = offset + (int)data.U32(offset + 2);
        var count = data.U16(offset + 6);
        for (var i = 0; i < count; i++)
        {
            _subtables.Add(offset + (int)data.U32(offset + 8 + i * 4));
        }

        var axisCount = data.U16(regions);
        var regionCount = data.U16(regions + 2);
        _regionScalars = new double[regionCount];
        for (var r = 0; r < regionCount; r++)
        {
            var record = regions + 4 + r * axisCount * 6;
            _regionScalars[r] = coords.Length < axisCount
                ? 0
                : VariationRegion.Scalar(coords.Take(axisCount).ToArray(), a => (data.F2Dot14(record + a * 6), data.F2Dot14(record + a * 6 + 2), data.F2Dot14(record + a * 6 + 4)));
        }
    }

    /// <summary>Scalars of the regions used by subtable <paramref name="outer"/>, in their order there (CFF2 blend).</summary>
    public double[] SubtableScalars(int outer)
    {
        if (outer < 0 || outer >= _subtables.Count)
        {
            return Array.Empty<double>();
        }

        var subtable = _subtables[outer];
        var regionCount = _data.U16(subtable + 4);
        var scalars = new double[regionCount];
        for (var k = 0; k < regionCount; k++)
        {
            var region = _data.U16(subtable + 6 + k * 2);
            scalars[k] = region < _regionScalars.Length ? _regionScalars[region] : 0;
        }

        return scalars;
    }

    /// <summary>Interpolated delta of one item.</summary>
    public double Delta(int outer, int inner)
    {
        if (outer < 0 || outer >= _subtables.Count)
        {
            return 0;
        }

        var subtable = _subtables[outer];
        var itemCount = _data.U16(subtable);
        var wordField = _data.U16(subtable + 2);
        var regionCount = _data.U16(subtable + 4);
        if (inner < 0 || inner >= itemCount)
        {
            return 0;
        }

        var longWords = (wordField & 0x8000) != 0;
        var wordCount = wordField & 0x7FFF;
        var (big, small) = longWords ? (4, 2) : (2, 1);
        var rowSize = wordCount * big + (regionCount - wordCount) * small;
        var p = subtable + 6 + regionCount * 2 + inner * rowSize;
        var total = 0.0;
        for (var k = 0; k < regionCount; k++)
        {
            int delta;
            if (k < wordCount)
            {
                delta = longWords ? _data.I32(p) : _data.I16(p);
                p += big;
            }
            else
            {
                delta = longWords ? _data.I16(p) : (sbyte)_data.Bytes[p];
                p += small;
            }

            if (delta != 0)
            {
                var region = _data.U16(subtable + 6 + k * 2);
                total += delta * (region < _regionScalars.Length ? _regionScalars[region] : 0);
            }
        }

        return total;
    }
}

/// <summary>HVAR: advance width deltas of a variable font.</summary>
internal sealed class HorizontalVariations
{
    private readonly FontData _data;
    private readonly ItemVariationStore _store;
    private readonly int _map;

    public HorizontalVariations(FontData data, int offset, double[] coords)
    {
        _data = data;
        _store = new ItemVariationStore(data, offset + (int)data.U32(offset + 4), coords);
        var map = (int)data.U32(offset + 8);
        _map = map == 0 ? 0 : offset + map;
    }

    public double AdvanceDelta(int glyph)
    {
        if (_map == 0)
        {
            return _store.Delta(0, glyph);
        }

        // DeltaSetIndexMap: the last entry is used for glyphs past the end.
        var format = _data.U8(_map);
        var entryFormat = _data.U8(_map + 1);
        var count = format == 0 ? _data.U16(_map + 2) : (int)_data.U32(_map + 2);
        var start = _map + (format == 0 ? 4 : 6);
        if (count == 0)
        {
            return 0;
        }

        var entrySize = ((entryFormat & 0x30) >> 4) + 1;
        var innerBits = (entryFormat & 0x0F) + 1;
        var p = start + Math.Min(glyph, count - 1) * entrySize;
        var value = 0;
        for (var i = 0; i < entrySize; i++)
        {
            value = (value << 8) | _data.Bytes[p + i];
        }

        return _store.Delta(value >> innerBits, value & ((1 << innerBits) - 1));
    }
}

/// <summary>
/// gvar: per-glyph point deltas of a variable TrueType font (tuple variations with shared or private point
/// numbers, packed deltas, intermediate regions), with the interpolation of untouched points (IUP).
/// </summary>
internal sealed class GlyphVariations
{
    private readonly FontData _data;
    private readonly double[] _coords;
    private readonly int _axisCount;
    private readonly int _sharedTuples;
    private readonly int _glyphCount;
    private readonly bool _longOffsets;
    private readonly int _dataArray;
    private readonly int _offsets;

    public GlyphVariations(FontData data, int offset, double[] coords)
    {
        _data = data;
        _coords = coords;
        _axisCount = data.U16(offset + 4);
        _sharedTuples = offset + (int)data.U32(offset + 8);
        _glyphCount = data.U16(offset + 12);
        _longOffsets = (data.U16(offset + 14) & 1) != 0;
        _dataArray = offset + (int)data.U32(offset + 16);
        _offsets = offset + 20;
    }

    /// <summary>
    /// Deltas for the <paramref name="pointCount"/> points of a glyph (outline points, then the four phantom points).
    /// For simple glyphs pass the original coordinates and contour ends so that untouched points are interpolated;
    /// for composites (one point per component) leave them null: untouched points do not move.
    /// </summary>
    public (double[] X, double[] Y) Deltas(int glyph, int pointCount, int[]? xs = null, int[]? ys = null, int[]? contourEnds = null)
    {
        var dx = new double[pointCount];
        var dy = new double[pointCount];
        if (glyph < 0 || glyph >= _glyphCount || _coords.Length < _axisCount)
        {
            return (dx, dy);
        }

        int Offset(int g) => _longOffsets ? (int)_data.U32(_offsets + g * 4) : _data.U16(_offsets + g * 2) * 2;

        var start = _dataArray + Offset(glyph);
        var end = _dataArray + Offset(glyph + 1);
        if (end <= start)
        {
            return (dx, dy);
        }

        var tupleCount = _data.U16(start);
        var serialized = start + _data.U16(start + 2);
        var header = start + 4;
        List<int>? shared = null;
        if ((tupleCount & 0x8000) != 0)
        {
            shared = ReadPoints(ref serialized);
        }

        for (var t = 0; t < (tupleCount & 0x0FFF); t++)
        {
            var size = _data.U16(header);
            var index = _data.U16(header + 2);
            header += 4;
            var peak = new double[_axisCount];
            if ((index & 0x8000) != 0)
            {
                for (var a = 0; a < _axisCount; a++)
                {
                    peak[a] = _data.F2Dot14(header + a * 2);
                }

                header += _axisCount * 2;
            }
            else
            {
                var tuple = _sharedTuples + (index & 0x0FFF) * _axisCount * 2;
                for (var a = 0; a < _axisCount; a++)
                {
                    peak[a] = _data.F2Dot14(tuple + a * 2);
                }
            }

            var startTuple = new double[_axisCount];
            var endTuple = new double[_axisCount];
            if ((index & 0x4000) != 0)
            {
                for (var a = 0; a < _axisCount; a++)
                {
                    startTuple[a] = _data.F2Dot14(header + a * 2);
                    endTuple[a] = _data.F2Dot14(header + _axisCount * 2 + a * 2);
                }

                header += _axisCount * 4;
            }
            else
            {
                for (var a = 0; a < _axisCount; a++)
                {
                    startTuple[a] = Math.Min(peak[a], 0);
                    endTuple[a] = Math.Max(peak[a], 0);
                }
            }

            var p = serialized;
            serialized += size;
            var scalar = VariationRegion.Scalar(_coords, a => (startTuple[a], peak[a], endTuple[a]));
            if (scalar == 0)
            {
                continue;
            }

            var points = (index & 0x2000) != 0 ? ReadPoints(ref p) : shared;
            var count = points?.Count ?? pointCount;
            var xDeltas = ReadDeltas(ref p, count);
            var yDeltas = ReadDeltas(ref p, count);
            if (points is null)
            {
                for (var i = 0; i < pointCount && i < count; i++)
                {
                    dx[i] += xDeltas[i] * scalar;
                    dy[i] += yDeltas[i] * scalar;
                }

                continue;
            }

            var touched = new bool[pointCount];
            var tx = new double[pointCount];
            var ty = new double[pointCount];
            for (var i = 0; i < points.Count; i++)
            {
                var point = points[i];
                if (point < pointCount)
                {
                    touched[point] = true;
                    tx[point] += xDeltas[i];
                    ty[point] += yDeltas[i];
                }
            }

            if (xs is not null && ys is not null && contourEnds is not null)
            {
                Interpolate(xs, ys, contourEnds, touched, tx, ty);
            }

            for (var i = 0; i < pointCount; i++)
            {
                dx[i] += tx[i] * scalar;
                dy[i] += ty[i] * scalar;
            }
        }

        return (dx, dy);
    }

    /// <summary>Packed point numbers; null means all points of the glyph.</summary>
    private List<int>? ReadPoints(ref int p)
    {
        int count = _data.U8(p++);
        if (count == 0)
        {
            return null;
        }

        if ((count & 0x80) != 0)
        {
            count = ((count & 0x7F) << 8) | _data.U8(p++);
        }

        var points = new List<int>(count);
        var last = 0;
        while (points.Count < count)
        {
            var control = _data.U8(p++);
            var run = (control & 0x7F) + 1;
            var words = (control & 0x80) != 0;
            for (var i = 0; i < run && points.Count < count; i++)
            {
                last += words ? _data.U16(p) : _data.U8(p);
                p += words ? 2 : 1;
                points.Add(last);
            }
        }

        return points;
    }

    private int[] ReadDeltas(ref int p, int count)
    {
        var deltas = new int[count];
        var read = 0;
        while (read < count)
        {
            var control = _data.U8(p++);
            var run = (control & 0x3F) + 1;
            for (var i = 0; i < run && read < count; i++)
            {
                if ((control & 0x80) != 0)
                {
                    deltas[read++] = 0;
                }
                else if ((control & 0x40) != 0)
                {
                    deltas[read++] = _data.I16(p);
                    p += 2;
                }
                else
                {
                    deltas[read++] = (sbyte)_data.Bytes[p++];
                }
            }
        }

        return deltas;
    }

    /// <summary>Interpolation of untouched points between the touched ones of each contour, per axis.</summary>
    private static void Interpolate(int[] xs, int[] ys, int[] contourEnds, bool[] touched, double[] dx, double[] dy)
    {
        var start = 0;
        foreach (var end in contourEnds)
        {
            if (end >= touched.Length || end < start)
            {
                break;
            }

            var touchedPoints = Enumerable.Range(start, end - start + 1).Where(i => touched[i]).ToList();
            if (touchedPoints.Count == 1)
            {
                for (var i = start; i <= end; i++)
                {
                    dx[i] = dx[touchedPoints[0]];
                    dy[i] = dy[touchedPoints[0]];
                }
            }
            else if (touchedPoints.Count > 1)
            {
                for (var k = 0; k < touchedPoints.Count; k++)
                {
                    var a = touchedPoints[k];
                    var b = touchedPoints[(k + 1) % touchedPoints.Count];
                    // Untouched points after a, up to b, going round the contour.
                    for (var i = Next(a, start, end); i != b; i = Next(i, start, end))
                    {
                        dx[i] = Infer(xs[i], xs[a], xs[b], dx[a], dx[b]);
                        dy[i] = Infer(ys[i], ys[a], ys[b], dy[a], dy[b]);
                    }
                }
            }

            start = end + 1;
        }
    }

    private static int Next(int i, int start, int end) => i == end ? start : i + 1;

    private static double Infer(int c, int c1, int c2, double d1, double d2)
    {
        if (c1 == c2)
        {
            return d1 == d2 ? d1 : 0;
        }

        if (c1 > c2)
        {
            (c1, c2, d1, d2) = (c2, c1, d2, d1);
        }

        if (c <= c1)
        {
            return d1;
        }

        if (c >= c2)
        {
            return d2;
        }

        return d1 + (c - c1) * (d2 - d1) / (c2 - c1);
    }
}
