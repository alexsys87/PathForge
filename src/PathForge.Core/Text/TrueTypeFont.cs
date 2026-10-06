using System.Text;
using PathForge.Core.Localization;

namespace PathForge.Core.Text;

/// <summary>Point of a glyph outline in font units; off-curve points are quadratic Bézier control points.</summary>
public readonly record struct GlyphPoint(double X, double Y, bool OnCurve);

/// <summary>
/// Reader for OpenType fonts (.ttf, .ttc, .otf), written from the OpenType specification: character map,
/// metrics, TrueType outlines (including composite glyphs), CFF and CFF2 outlines (see <see cref="CffOutlines"/>),
/// pair kerning (GPOS or the older kern table) and variable fonts (fvar, avar, gvar, HVAR, CFF2 blend: pick a
/// location with <see cref="WithVariation"/>). Hinting is not supported.
/// </summary>
public sealed class TrueTypeFont
{
    private const int MaxCompositeDepth = 8;

    private readonly byte[] _data;
    private readonly int _directoryOffset;
    private readonly Dictionary<string, (int Offset, int Length)> _tables;
    private readonly FontDesignSpace? _designSpace;

    /// <summary>Normalized location in the design space; null at the default location (or for static fonts).</summary>
    private readonly double[]? _coords;
    private readonly GlyphVariations? _gvar;
    private readonly HorizontalVariations? _hvar;
    private readonly int _numGlyphs;
    private readonly bool _longLoca;
    private readonly int _numberOfHMetrics;
    private readonly int _cmapOffset;
    private readonly int _cmapFormat;
    private readonly bool _symbolCmap;
    private readonly CffOutlines? _cff;
    private readonly Dictionary<(int Left, int Right), int> _kerningCache = new();
    private Dictionary<(int Left, int Right), int>? _legacyKerning;
    private List<List<int>>? _kernLookups;

    private TrueTypeFont(byte[] data, int directoryOffset, IReadOnlyDictionary<string, double>? variation = null)
    {
        _data = data;
        _directoryOffset = directoryOffset;
        var version = U32(directoryOffset);
        var isCff = version == Tag("OTTO");
        if (!isCff && version != 0x00010000 && version != Tag("true"))
        {
            throw new FormatException(Loc.T("Файл не является шрифтом OpenType/TrueType.", "The file is not an OpenType/TrueType font."));
        }

        _tables = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        var numTables = U16(directoryOffset + 4);
        for (var i = 0; i < numTables; i++)
        {
            var record = directoryOffset + 12 + i * 16;
            var tag = Encoding.ASCII.GetString(data, record, 4);
            var offset = (int)U32(record + 8);
            var length = (int)U32(record + 12);
            if (offset < 0 || length < 0 || (long)offset + length > data.Length)
            {
                throw new FormatException(Loc.T($"Таблица шрифта «{tag}» выходит за пределы файла.", $"The font table “{tag}” lies outside the file."));
            }

            _tables[tag] = (offset, length);
        }

        foreach (var required in new[] { "head", "maxp", "hhea", "hmtx", "cmap" })
        {
            if (!_tables.ContainsKey(required))
            {
                throw new FormatException(Loc.T($"В шрифте нет таблицы «{required}».", $"The font has no “{required}” table."));
            }
        }

        if (_tables.TryGetValue("fvar", out var fvar))
        {
            _designSpace = new FontDesignSpace(new FontData(data), fvar.Offset,
                _tables.TryGetValue("avar", out var avar) ? avar.Offset : null, ReadName);
            var location = variation ?? new Dictionary<string, double>();
            var coords = _designSpace.Normalize(location);
            Variation = _designSpace.Axes.ToDictionary(a => a.Tag, a => location.TryGetValue(a.Tag, out var v) ? Math.Clamp(v, a.Minimum, a.Maximum) : a.Default);
            if (coords.Any(c => c != 0))
            {
                _coords = coords;
                if (_tables.TryGetValue("gvar", out var gvar))
                {
                    _gvar = new GlyphVariations(new FontData(data), gvar.Offset, coords);
                }

                if (_tables.TryGetValue("HVAR", out var hvar))
                {
                    _hvar = new HorizontalVariations(new FontData(data), hvar.Offset, coords);
                }
            }
        }

        if (isCff)
        {
            if (_tables.TryGetValue("CFF ", out var cff))
            {
                _cff = new CffOutlines(data, cff.Offset, cff.Length);
            }
            else if (_tables.TryGetValue("CFF2", out var cff2))
            {
                // Variable PostScript outlines: the blend operators take the location.
                _cff = new CffOutlines(data, cff2.Offset, cff2.Length, _designSpace is null ? Array.Empty<double>() : _designSpace.Normalize(Variation));
            }
            else
            {
                throw new NotSupportedException(Loc.T("В шрифте нет таблицы контуров CFF.", "The font has no CFF outline table."));
            }
        }
        else if (!_tables.ContainsKey("glyf") || !_tables.ContainsKey("loca"))
        {
            throw new NotSupportedException(Loc.T("В шрифте нет контуров (таблиц glyf/loca или CFF).", "The font has no outlines (glyf/loca or CFF tables)."));
        }

        var head = _tables["head"].Offset;
        UnitsPerEm = U16(head + 18);
        if (UnitsPerEm == 0)
        {
            throw new FormatException(Loc.T("Неверный размер em в шрифте.", "Invalid em size in the font."));
        }

        _longLoca = I16(head + 50) != 0;
        _numGlyphs = U16(_tables["maxp"].Offset + 4);

        var hhea = _tables["hhea"].Offset;
        Ascender = I16(hhea + 4);
        Descender = I16(hhea + 6);
        LineGap = I16(hhea + 8);
        _numberOfHMetrics = Math.Max(1, (int)U16(hhea + 34));

        (_cmapOffset, _cmapFormat, _symbolCmap) = FindCharacterMap();

        CapHeight = ReadCapHeight();
        FamilyName = ReadName(16) ?? ReadName(1) ?? "";
        SubfamilyName = ReadName(17) ?? ReadName(2) ?? "";
    }

    public int UnitsPerEm { get; }

    public int Ascender { get; }

    /// <summary>Negative: distance of the lowest descender below the baseline.</summary>
    public int Descender { get; }

    public int LineGap { get; }

    /// <summary>Height of capital letters above the baseline (font units).</summary>
    public int CapHeight { get; }

    public string FamilyName { get; }

    public string SubfamilyName { get; }

    public int GlyphCount => _numGlyphs;

    /// <summary>The font is variable: its shape can be changed along <see cref="Axes"/>.</summary>
    public bool IsVariable => _designSpace is { Axes.Count: > 0 };

    /// <summary>Design axes of a variable font (empty for static fonts).</summary>
    public IReadOnlyList<FontAxis> Axes => _designSpace?.Axes ?? (IReadOnlyList<FontAxis>)Array.Empty<FontAxis>();

    /// <summary>Named styles of a variable font.</summary>
    public IReadOnlyList<FontInstance> Instances => _designSpace?.Instances ?? (IReadOnlyList<FontInstance>)Array.Empty<FontInstance>();

    /// <summary>Location of this font object in the design space (axis tag → user value); empty for static fonts.</summary>
    public IReadOnlyDictionary<string, double> Variation { get; } = new Dictionary<string, double>();

    /// <summary>The same font at another location of its design space (missing axes keep their default).</summary>
    public TrueTypeFont WithVariation(IReadOnlyDictionary<string, double> variation) =>
        IsVariable ? new TrueTypeFont(_data, _directoryOffset, variation) : this;

    public string DisplayName =>
        string.IsNullOrEmpty(SubfamilyName) || SubfamilyName is "Regular" or "Normal" ? FamilyName : $"{FamilyName} {SubfamilyName}";

    public static TrueTypeFont LoadFile(string path, int fontIndex = 0) => Load(File.ReadAllBytes(path), fontIndex);

    /// <summary>Loads font number <paramref name="fontIndex"/> of a .ttf (always 0) or .ttc collection.</summary>
    public static TrueTypeFont Load(byte[] data, int fontIndex = 0)
    {
        if (data.Length < 12)
        {
            throw new FormatException(Loc.T("Файл шрифта слишком короткий.", "The font file is too short."));
        }

        var count = FontCount(data);
        if (fontIndex < 0 || fontIndex >= count)
        {
            throw new ArgumentOutOfRangeException(nameof(fontIndex), Loc.T($"В файле {count} шрифт(ов).", $"The file contains {count} font(s)."));
        }

        var directory = IsCollection(data) ? (int)ReadU32(data, 12 + fontIndex * 4) : 0;
        return new TrueTypeFont(data, directory);
    }

    /// <summary>Number of fonts in the file (more than one for a .ttc collection).</summary>
    public static int FontCount(byte[] data) => IsCollection(data) ? (int)Math.Min(ReadU32(data, 8), 1024) : 1;

    private static bool IsCollection(byte[] data) => data.Length >= 12 && ReadU32(data, 0) == Tag("ttcf");

    /// <summary>Glyph index for a Unicode code point; 0 (the "missing" glyph) when the font has no such character.</summary>
    public int GlyphIndex(int codePoint)
    {
        var glyph = LookupGlyph(codePoint);
        if (glyph == 0 && _symbolCmap && codePoint < 0x100)
        {
            // Symbol fonts map their characters to the private range U+F000…U+F0FF.
            glyph = LookupGlyph(0xF000 + codePoint);
        }

        return glyph < _numGlyphs ? glyph : 0;
    }

    /// <summary>Horizontal advance of a glyph (font units).</summary>
    public int AdvanceWidth(int glyph)
    {
        var hmtx = _tables["hmtx"];
        var index = Math.Min(glyph, _numberOfHMetrics - 1);
        var offset = hmtx.Offset + index * 4;
        var advance = offset + 2 <= hmtx.Offset + hmtx.Length ? U16(offset) : 0;
        if (_hvar is not null)
        {
            advance += (int)Math.Round(_hvar.AdvanceDelta(glyph));
        }
        else if (_gvar is not null && glyph >= 0 && glyph < _numGlyphs)
        {
            // Without HVAR the advance follows the second phantom point minus the first.
            var count = OutlinePointCount(glyph);
            var (dx, _) = _gvar.Deltas(glyph, count + 4);
            advance += (int)Math.Round(dx[count + 1] - dx[count]);
        }

        return Math.Max(0, advance);
    }

    /// <summary>Points of a glyph in gvar terms: outline points of a simple glyph, one per component of a composite.</summary>
    private int OutlinePointCount(int glyph)
    {
        var (offset, length) = GlyphLocation(glyph);
        if (length < 10)
        {
            return 0;
        }

        var contours = I16(offset);
        if (contours >= 0)
        {
            return contours == 0 ? 0 : U16(offset + 10 + (contours - 1) * 2) + 1;
        }

        return ReadComponents(offset + 10).Count;
    }

    /// <summary>The font has PostScript (CFF) outlines instead of TrueType ones.</summary>
    public bool HasCffOutlines => _cff is not null;

    /// <summary>Closed outline paths of a glyph in font units (Y up). Empty for blank glyphs such as space.</summary>
    public List<GlyphPath> GetPaths(int glyph)
    {
        if (_cff is not null)
        {
            return _cff.GetPaths(glyph);
        }

        return GetOutline(glyph)
            .Select(c => GlyphPath.FromQuadraticPoints(c.Select(p => (new Geometry.Vec2(p.X, p.Y), p.OnCurve)).ToList()))
            .ToList();
    }

    /// <summary>
    /// Horizontal kerning between two glyphs (font units, usually negative): from the GPOS 'kern' feature
    /// (pairs and classes), otherwise from the older kern table.
    /// </summary>
    public int Kerning(int left, int right)
    {
        if (left == 0 || right == 0)
        {
            return 0;
        }

        lock (_kerningCache)
        {
            if (_kerningCache.TryGetValue((left, right), out var cached))
            {
                return cached;
            }

            int value;
            try
            {
                value = GposKerning(left, right) ?? LegacyKerning(left, right);
            }
            catch (IndexOutOfRangeException)
            {
                // Damaged table: no kerning rather than no text.
                value = 0;
            }

            _kerningCache[(left, right)] = value;
            return value;
        }
    }

    /// <summary>TrueType contours (quadratic points) of a glyph; empty for CFF fonts, use <see cref="GetPaths"/>.</summary>
    public List<List<GlyphPoint>> GetOutline(int glyph)
    {
        var contours = new List<List<GlyphPoint>>();
        AppendOutline(glyph, contours, 0);
        return contours;
    }

    private void AppendOutline(int glyph, List<List<GlyphPoint>> contours, int depth)
    {
        if (_cff is not null || glyph < 0 || glyph >= _numGlyphs || depth > MaxCompositeDepth)
        {
            return;
        }

        var (offset, length) = GlyphLocation(glyph);
        if (length < 10)
        {
            return;
        }

        var numberOfContours = I16(offset);
        if (numberOfContours >= 0)
        {
            ReadSimpleGlyph(glyph, offset, numberOfContours, contours);
        }
        else
        {
            ReadCompositeGlyph(glyph, offset + 10, contours, depth);
        }
    }

    private (int Offset, int Length) GlyphLocation(int glyph)
    {
        var loca = _tables["loca"];
        var glyf = _tables["glyf"];
        int start, end;
        if (_longLoca)
        {
            start = (int)U32(loca.Offset + glyph * 4);
            end = (int)U32(loca.Offset + glyph * 4 + 4);
        }
        else
        {
            start = U16(loca.Offset + glyph * 2) * 2;
            end = U16(loca.Offset + glyph * 2 + 2) * 2;
        }

        if (start < 0 || end <= start || end > glyf.Length)
        {
            return (0, 0);
        }

        return (glyf.Offset + start, end - start);
    }

    private void ReadSimpleGlyph(int glyph, int offset, int numberOfContours, List<List<GlyphPoint>> contours)
    {
        var p = offset + 10;
        var endPoints = new int[numberOfContours];
        for (var i = 0; i < numberOfContours; i++)
        {
            endPoints[i] = U16(p + i * 2);
        }

        p += numberOfContours * 2;
        if (numberOfContours == 0)
        {
            return;
        }

        var pointCount = endPoints[^1] + 1;
        var instructionLength = U16(p);
        p += 2 + instructionLength;

        var flags = new byte[pointCount];
        for (var i = 0; i < pointCount;)
        {
            var flag = _data[p++];
            flags[i++] = flag;
            if ((flag & 0x08) != 0)
            {
                var repeat = _data[p++];
                for (var r = 0; r < repeat && i < pointCount; r++)
                {
                    flags[i++] = flag;
                }
            }
        }

        var xs = new int[pointCount];
        var ys = new int[pointCount];
        p = ReadCoordinates(p, flags, xs, shortBit: 0x02, sameBit: 0x10);
        ReadCoordinates(p, flags, ys, shortBit: 0x04, sameBit: 0x20);
        var (dx, dy) = _gvar is null
            ? (new double[pointCount], new double[pointCount])
            : _gvar.Deltas(glyph, pointCount + 4, xs, ys, endPoints);

        var start = 0;
        foreach (var end in endPoints)
        {
            if (end < start || end >= pointCount)
            {
                break;
            }

            var contour = new List<GlyphPoint>(end - start + 1);
            for (var i = start; i <= end; i++)
            {
                contour.Add(new GlyphPoint(xs[i] + dx[i], ys[i] + dy[i], (flags[i] & 0x01) != 0));
            }

            if (contour.Count >= 2)
            {
                contours.Add(contour);
            }

            start = end + 1;
        }
    }

    /// <summary>Delta-encoded coordinates: short values are unsigned bytes with the sign in a flag bit.</summary>
    private int ReadCoordinates(int p, byte[] flags, int[] values, int shortBit, int sameBit)
    {
        var value = 0;
        for (var i = 0; i < flags.Length; i++)
        {
            var flag = flags[i];
            if ((flag & shortBit) != 0)
            {
                var delta = _data[p++];
                value += (flag & sameBit) != 0 ? delta : -delta;
            }
            else if ((flag & sameBit) == 0)
            {
                value += I16(p);
                p += 2;
            }

            values[i] = value;
        }

        return p;
    }

    private const int ArgsAreXyValues = 0x0002;

    /// <summary>One component of a composite glyph: glyph, placement arguments and 2×2 transform.</summary>
    private readonly record struct Component(int Flags, int Glyph, int Arg1, int Arg2, double A, double B, double C, double D);

    private List<Component> ReadComponents(int p)
    {
        const int ArgsAreWords = 0x0001;
        const int HaveScale = 0x0008;
        const int MoreComponents = 0x0020;
        const int HaveXyScale = 0x0040;
        const int HaveTwoByTwo = 0x0080;

        var components = new List<Component>();
        int flags;
        do
        {
            flags = U16(p);
            var glyph = U16(p + 2);
            p += 4;

            int arg1, arg2;
            if ((flags & ArgsAreWords) != 0)
            {
                arg1 = (flags & ArgsAreXyValues) != 0 ? I16(p) : U16(p);
                arg2 = (flags & ArgsAreXyValues) != 0 ? I16(p + 2) : U16(p + 2);
                p += 4;
            }
            else
            {
                arg1 = (flags & ArgsAreXyValues) != 0 ? (sbyte)_data[p] : _data[p];
                arg2 = (flags & ArgsAreXyValues) != 0 ? (sbyte)_data[p + 1] : _data[p + 1];
                p += 2;
            }

            double a = 1, b = 0, c = 0, d = 1;
            if ((flags & HaveScale) != 0)
            {
                a = d = F2Dot14(p);
                p += 2;
            }
            else if ((flags & HaveXyScale) != 0)
            {
                a = F2Dot14(p);
                d = F2Dot14(p + 2);
                p += 4;
            }
            else if ((flags & HaveTwoByTwo) != 0)
            {
                a = F2Dot14(p);
                b = F2Dot14(p + 2);
                c = F2Dot14(p + 4);
                d = F2Dot14(p + 6);
                p += 8;
            }

            components.Add(new Component(flags, glyph, arg1, arg2, a, b, c, d));
        }
        while ((flags & MoreComponents) != 0);

        return components;
    }

    private void ReadCompositeGlyph(int glyph, int p, List<List<GlyphPoint>> contours, int depth)
    {
        const int ScaledComponentOffset = 0x0800;

        var components = ReadComponents(p);
        // In a variable font each component offset is a "point" that moves with the location.
        var (deltaX, deltaY) = _gvar is null
            ? (new double[components.Count], new double[components.Count])
            : _gvar.Deltas(glyph, components.Count + 4);

        for (var index = 0; index < components.Count; index++)
        {
            var (flags, child, arg1, arg2, a, b, c, d) = components[index];
            var component = new List<List<GlyphPoint>>();
            AppendOutline(child, component, depth + 1);

            GlyphPoint Map(GlyphPoint q) => q with { X = a * q.X + c * q.Y, Y = b * q.X + d * q.Y };

            double dx, dy;
            if ((flags & ArgsAreXyValues) != 0)
            {
                dx = arg1 + deltaX[index];
                dy = arg2 + deltaY[index];
                if ((flags & ScaledComponentOffset) != 0)
                {
                    (dx, dy) = (a * dx + c * dy, b * dx + d * dy);
                }
            }
            else
            {
                // Point matching: point arg2 of the component lands on point arg1 of the glyph built so far.
                var parent = contours.SelectMany(x => x).ToList();
                var points = component.SelectMany(x => x).Select(Map).ToList();
                if (arg1 < parent.Count && arg2 < points.Count)
                {
                    dx = parent[arg1].X - points[arg2].X;
                    dy = parent[arg1].Y - points[arg2].Y;
                }
                else
                {
                    dx = dy = 0;
                }
            }

            // A mirroring transform reverses the winding; restore it so that the non-zero fill stays correct.
            var mirrored = a * d - b * c < 0;
            foreach (var contour in component)
            {
                var mapped = contour.Select(q =>
                {
                    var m = Map(q);
                    return m with { X = m.X + dx, Y = m.Y + dy };
                }).ToList();
                if (mirrored)
                {
                    mapped.Reverse();
                }

                contours.Add(mapped);
            }
        }
    }

    private (int Offset, int Format, bool Symbol) FindCharacterMap()
    {
        var cmap = _tables["cmap"].Offset;
        var count = U16(cmap + 2);
        int best = -1, bestFormat = 0, bestRank = int.MaxValue;
        var symbol = false;
        for (var i = 0; i < count; i++)
        {
            var record = cmap + 4 + i * 8;
            var platform = U16(record);
            var encoding = U16(record + 2);
            var offset = cmap + (int)U32(record + 4);
            if (offset + 4 > _data.Length)
            {
                continue;
            }

            var format = U16(offset);
            var rank = (platform, encoding, format) switch
            {
                (3, 10, 12) => 0,
                (0, _, 12) => 1,
                (3, 1, 4) => 2,
                (0, _, 4) => 3,
                (3, 0, 4) => 4,
                _ => int.MaxValue,
            };

            if (rank < bestRank)
            {
                bestRank = rank;
                best = offset;
                bestFormat = format;
                symbol = platform == 3 && encoding == 0;
            }
        }

        if (best < 0)
        {
            throw new NotSupportedException(Loc.T("В шрифте нет таблицы символов Юникода.", "The font has no Unicode character map."));
        }

        return (best, bestFormat, symbol);
    }

    private int LookupGlyph(int codePoint)
    {
        var t = _cmapOffset;
        if (_cmapFormat == 12)
        {
            var groups = (int)U32(t + 12);
            int lo = 0, hi = groups - 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                var group = t + 16 + mid * 12;
                var startChar = U32(group);
                var endChar = U32(group + 4);
                if (codePoint < startChar)
                {
                    hi = mid - 1;
                }
                else if (codePoint > endChar)
                {
                    lo = mid + 1;
                }
                else
                {
                    return (int)(U32(group + 8) + (codePoint - startChar));
                }
            }

            return 0;
        }

        // Format 4: segments of 16-bit codes.
        if (codePoint > 0xFFFF)
        {
            return 0;
        }

        var segCount = U16(t + 6) / 2;
        var endCodes = t + 14;
        var startCodes = endCodes + segCount * 2 + 2;
        var idDeltas = startCodes + segCount * 2;
        var idRangeOffsets = idDeltas + segCount * 2;
        for (var i = 0; i < segCount; i++)
        {
            if (U16(endCodes + i * 2) < codePoint)
            {
                continue;
            }

            var startCode = U16(startCodes + i * 2);
            if (startCode > codePoint)
            {
                return 0;
            }

            var delta = I16(idDeltas + i * 2);
            var rangeOffsetPosition = idRangeOffsets + i * 2;
            var rangeOffset = U16(rangeOffsetPosition);
            if (rangeOffset == 0)
            {
                return (codePoint + delta) & 0xFFFF;
            }

            var address = rangeOffsetPosition + rangeOffset + 2 * (codePoint - startCode);
            if (address + 2 > _data.Length)
            {
                return 0;
            }

            var glyph = U16(address);
            return glyph == 0 ? 0 : (glyph + delta) & 0xFFFF;
        }

        return 0;
    }

    private int ReadCapHeight()
    {
        if (_tables.TryGetValue("OS/2", out var os2) && os2.Length >= 90 && U16(os2.Offset) >= 2)
        {
            var capHeight = I16(os2.Offset + 88);
            if (capHeight > 0)
            {
                return capHeight;
            }
        }

        // Older fonts: measure the letter H.
        var h = GlyphIndex('H');
        if (h != 0 && _cff is not null)
        {
            var top = _cff.GetPaths(h).SelectMany(p => p.Segments.Select(s => s.End.Y).Append(p.Start.Y)).DefaultIfEmpty(0).Max();
            if (top > 0)
            {
                return (int)Math.Round(top);
            }
        }
        else if (h != 0)
        {
            var (offset, length) = GlyphLocation(h);
            if (length >= 10)
            {
                var yMax = I16(offset + 8);
                if (yMax > 0)
                {
                    return yMax;
                }
            }
        }

        return (int)Math.Round(UnitsPerEm * 0.7);
    }

    // ---- Kerning ------------------------------------------------------------------------------

    /// <summary>Sum of the pair adjustments of the GPOS 'kern' lookups; null when the font has no such lookups.</summary>
    private int? GposKerning(int left, int right)
    {
        _kernLookups ??= FindKernLookups();
        if (_kernLookups.Count == 0)
        {
            return null;
        }

        var total = 0;
        foreach (var subtables in _kernLookups)
        {
            // Within one lookup the first subtable that covers the pair applies.
            foreach (var subtable in subtables)
            {
                if (PairAdjustment(subtable, left, right) is { } value)
                {
                    total += value;
                    break;
                }
            }
        }

        return total;
    }

    /// <summary>PairPos subtable offsets of every lookup used by a 'kern' feature (of any script).</summary>
    private List<List<int>> FindKernLookups()
    {
        var result = new List<List<int>>();
        if (!_tables.TryGetValue("GPOS", out var gpos) || gpos.Length < 10)
        {
            return result;
        }

        var featureList = gpos.Offset + U16(gpos.Offset + 6);
        var lookupList = gpos.Offset + U16(gpos.Offset + 8);
        var lookups = new SortedSet<int>();
        var featureCount = U16(featureList);
        for (var i = 0; i < featureCount; i++)
        {
            var record = featureList + 2 + i * 6;
            if (Encoding.ASCII.GetString(_data, record, 4) != "kern")
            {
                continue;
            }

            var feature = featureList + U16(record + 4);
            var count = U16(feature + 2);
            for (var k = 0; k < count; k++)
            {
                lookups.Add(U16(feature + 4 + k * 2));
            }
        }

        var lookupCount = U16(lookupList);
        foreach (var index in lookups.Where(l => l < lookupCount))
        {
            var lookup = lookupList + U16(lookupList + 2 + index * 2);
            var type = U16(lookup);
            var subtableCount = U16(lookup + 4);
            var subtables = new List<int>();
            for (var k = 0; k < subtableCount; k++)
            {
                var subtable = lookup + U16(lookup + 6 + k * 2);
                var subtableType = type;
                if (type == 9)
                {
                    // Extension: the real subtable is further away (32-bit offset).
                    subtableType = U16(subtable + 2);
                    subtable += (int)U32(subtable + 4);
                }

                if (subtableType == 2)
                {
                    subtables.Add(subtable);
                }
            }

            if (subtables.Count > 0)
            {
                result.Add(subtables);
            }
        }

        return result;
    }

    /// <summary>XAdvance of the first glyph from a PairPos subtable, or null when the pair is not covered.</summary>
    private int? PairAdjustment(int subtable, int left, int right)
    {
        var format = U16(subtable);
        var coverage = CoverageIndex(subtable + U16(subtable + 2), left);
        if (coverage < 0)
        {
            return null;
        }

        var valueFormat1 = U16(subtable + 4);
        var valueFormat2 = U16(subtable + 6);
        var size1 = 2 * BitCount(valueFormat1);
        var size2 = 2 * BitCount(valueFormat2);
        int XAdvance(int record) => (valueFormat1 & 0x0004) == 0 ? 0 : I16(record + 2 * BitCount(valueFormat1 & 0x0003));

        if (format == 1)
        {
            var pairSetCount = U16(subtable + 8);
            if (coverage >= pairSetCount)
            {
                return null;
            }

            var pairSet = subtable + U16(subtable + 10 + coverage * 2);
            var count = U16(pairSet);
            var recordSize = 2 + size1 + size2;
            int lo = 0, hi = count - 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                var record = pairSet + 2 + mid * recordSize;
                var second = U16(record);
                if (second == right)
                {
                    return XAdvance(record + 2);
                }

                if (second < right)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            return null;
        }

        if (format == 2)
        {
            var class1 = ClassOf(subtable + U16(subtable + 8), left);
            var class2 = ClassOf(subtable + U16(subtable + 10), right);
            var class1Count = U16(subtable + 12);
            var class2Count = U16(subtable + 14);
            if (class1 >= class1Count || class2 >= class2Count)
            {
                return null;
            }

            var record = subtable + 16 + (class1 * class2Count + class2) * (size1 + size2);
            return XAdvance(record);
        }

        return null;
    }

    private int CoverageIndex(int coverage, int glyph)
    {
        var format = U16(coverage);
        var count = U16(coverage + 2);
        if (format == 1)
        {
            int lo = 0, hi = count - 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                var value = U16(coverage + 4 + mid * 2);
                if (value == glyph)
                {
                    return mid;
                }

                if (value < glyph)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            return -1;
        }

        if (format == 2)
        {
            for (var i = 0; i < count; i++)
            {
                var range = coverage + 4 + i * 6;
                var start = U16(range);
                if (glyph >= start && glyph <= U16(range + 2))
                {
                    return U16(range + 4) + glyph - start;
                }
            }
        }

        return -1;
    }

    private int ClassOf(int classDef, int glyph)
    {
        var format = U16(classDef);
        if (format == 1)
        {
            var start = U16(classDef + 2);
            var count = U16(classDef + 4);
            return glyph >= start && glyph < start + count ? U16(classDef + 6 + (glyph - start) * 2) : 0;
        }

        if (format == 2)
        {
            var count = U16(classDef + 2);
            for (var i = 0; i < count; i++)
            {
                var range = classDef + 4 + i * 6;
                if (glyph >= U16(range) && glyph <= U16(range + 2))
                {
                    return U16(range + 4);
                }
            }
        }

        return 0;
    }

    private static int BitCount(int value) => System.Numerics.BitOperations.PopCount((uint)value);

    /// <summary>The 'kern' table of older TrueType fonts (version 0, format 0 horizontal pairs).</summary>
    private int LegacyKerning(int left, int right)
    {
        if (_legacyKerning is null)
        {
            _legacyKerning = new Dictionary<(int, int), int>();
            if (_tables.TryGetValue("kern", out var kern) && kern.Length >= 4 && U16(kern.Offset) == 0)
            {
                var count = U16(kern.Offset + 2);
                var position = kern.Offset + 4;
                for (var t = 0; t < count && position + 6 <= kern.Offset + kern.Length; t++)
                {
                    var length = U16(position + 2);
                    var coverage = U16(position + 4);
                    var horizontal = (coverage & 0x1) != 0;
                    var minimumOrCrossStream = (coverage & 0x6) != 0;
                    if (coverage >> 8 == 0 && horizontal && !minimumOrCrossStream)
                    {
                        var pairs = U16(position + 6);
                        for (var k = 0; k < pairs; k++)
                        {
                            var record = position + 14 + k * 6;
                            if (record + 6 > kern.Offset + kern.Length)
                            {
                                break;
                            }

                            var key = (U16(record), U16(record + 2));
                            _legacyKerning[key] = _legacyKerning.GetValueOrDefault(key) + I16(record + 4);
                        }
                    }

                    position += Math.Max(6, (int)length);
                }
            }
        }

        return _legacyKerning.GetValueOrDefault((left, right));
    }

    private string? ReadName(int nameId)
    {
        if (!_tables.TryGetValue("name", out var name) || name.Length < 6)
        {
            return null;
        }

        var count = U16(name.Offset + 2);
        var storage = name.Offset + U16(name.Offset + 4);
        string? fallback = null;
        for (var i = 0; i < count; i++)
        {
            var record = name.Offset + 6 + i * 12;
            if (record + 12 > name.Offset + name.Length || U16(record + 6) != nameId)
            {
                continue;
            }

            var platform = U16(record);
            var language = U16(record + 4);
            var length = U16(record + 8);
            var offset = storage + U16(record + 10);
            if (offset + length > _data.Length)
            {
                continue;
            }

            if (platform is 0 or 3)
            {
                var text = Encoding.BigEndianUnicode.GetString(_data, offset, length);
                if (platform == 3 && language == 0x0409)
                {
                    return text;
                }

                fallback ??= text;
            }
            else if (platform == 1)
            {
                fallback ??= Encoding.Latin1.GetString(_data, offset, length);
            }
        }

        return fallback;
    }

    private double F2Dot14(int offset) => I16(offset) / 16384.0;

    private ushort U16(int offset) => (ushort)((_data[offset] << 8) | _data[offset + 1]);

    private short I16(int offset) => (short)U16(offset);

    private uint U32(int offset) => ReadU32(_data, offset);

    private static uint ReadU32(byte[] data, int offset) =>
        (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);

    private static uint Tag(string tag) => (uint)((tag[0] << 24) | (tag[1] << 16) | (tag[2] << 8) | tag[3]);
}
