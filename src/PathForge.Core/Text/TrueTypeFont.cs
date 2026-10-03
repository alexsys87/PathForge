using System.Text;

namespace PathForge.Core.Text;

/// <summary>Point of a glyph outline in font units; off-curve points are quadratic Bézier control points.</summary>
public readonly record struct GlyphPoint(double X, double Y, bool OnCurve);

/// <summary>
/// Minimal reader for fonts with TrueType outlines (.ttf, .ttc), written from the OpenType specification:
/// character map, metrics and quadratic glyph outlines including composite glyphs. Hinting, kerning
/// and CFF outlines (.otf with 'OTTO') are not supported.
/// </summary>
public sealed class TrueTypeFont
{
    private const int MaxCompositeDepth = 8;

    private readonly byte[] _data;
    private readonly Dictionary<string, (int Offset, int Length)> _tables;
    private readonly int _numGlyphs;
    private readonly bool _longLoca;
    private readonly int _numberOfHMetrics;
    private readonly int _cmapOffset;
    private readonly int _cmapFormat;
    private readonly bool _symbolCmap;

    private TrueTypeFont(byte[] data, int directoryOffset)
    {
        _data = data;
        var version = U32(directoryOffset);
        if (version == Tag("OTTO"))
        {
            throw new NotSupportedException("Шрифт с контурами CFF (OpenType .otf) не поддерживается — выберите шрифт TrueType (.ttf).");
        }

        if (version != 0x00010000 && version != Tag("true"))
        {
            throw new FormatException("Файл не является шрифтом TrueType.");
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
                throw new FormatException($"Таблица шрифта «{tag}» выходит за пределы файла.");
            }

            _tables[tag] = (offset, length);
        }

        foreach (var required in new[] { "head", "maxp", "hhea", "hmtx", "cmap" })
        {
            if (!_tables.ContainsKey(required))
            {
                throw new FormatException($"В шрифте нет таблицы «{required}».");
            }
        }

        if (!_tables.ContainsKey("glyf") || !_tables.ContainsKey("loca"))
        {
            throw new NotSupportedException("В шрифте нет контуров TrueType (таблиц glyf/loca).");
        }

        var head = _tables["head"].Offset;
        UnitsPerEm = U16(head + 18);
        if (UnitsPerEm == 0)
        {
            throw new FormatException("Неверный размер em в шрифте.");
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

    public string DisplayName =>
        string.IsNullOrEmpty(SubfamilyName) || SubfamilyName is "Regular" or "Normal" ? FamilyName : $"{FamilyName} {SubfamilyName}";

    public static TrueTypeFont LoadFile(string path, int fontIndex = 0) => Load(File.ReadAllBytes(path), fontIndex);

    /// <summary>Loads font number <paramref name="fontIndex"/> of a .ttf (always 0) or .ttc collection.</summary>
    public static TrueTypeFont Load(byte[] data, int fontIndex = 0)
    {
        if (data.Length < 12)
        {
            throw new FormatException("Файл шрифта слишком короткий.");
        }

        var count = FontCount(data);
        if (fontIndex < 0 || fontIndex >= count)
        {
            throw new ArgumentOutOfRangeException(nameof(fontIndex), $"В файле {count} шрифт(ов).");
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
        return offset + 2 <= hmtx.Offset + hmtx.Length ? U16(offset) : 0;
    }

    /// <summary>Closed outline contours of a glyph in font units (Y up). Empty for blank glyphs such as space.</summary>
    public List<List<GlyphPoint>> GetOutline(int glyph)
    {
        var contours = new List<List<GlyphPoint>>();
        AppendOutline(glyph, contours, 0);
        return contours;
    }

    private void AppendOutline(int glyph, List<List<GlyphPoint>> contours, int depth)
    {
        if (glyph < 0 || glyph >= _numGlyphs || depth > MaxCompositeDepth)
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
            ReadSimpleGlyph(offset, numberOfContours, contours);
        }
        else
        {
            ReadCompositeGlyph(offset + 10, contours, depth);
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

    private void ReadSimpleGlyph(int offset, int numberOfContours, List<List<GlyphPoint>> contours)
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
                contour.Add(new GlyphPoint(xs[i], ys[i], (flags[i] & 0x01) != 0));
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

    private void ReadCompositeGlyph(int p, List<List<GlyphPoint>> contours, int depth)
    {
        const int ArgsAreWords = 0x0001;
        const int ArgsAreXyValues = 0x0002;
        const int HaveScale = 0x0008;
        const int MoreComponents = 0x0020;
        const int HaveXyScale = 0x0040;
        const int HaveTwoByTwo = 0x0080;
        const int ScaledComponentOffset = 0x0800;

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

            var component = new List<List<GlyphPoint>>();
            AppendOutline(glyph, component, depth + 1);

            GlyphPoint Map(GlyphPoint q) => q with { X = a * q.X + c * q.Y, Y = b * q.X + d * q.Y };

            double dx, dy;
            if ((flags & ArgsAreXyValues) != 0)
            {
                dx = arg1;
                dy = arg2;
                if ((flags & ScaledComponentOffset) != 0)
                {
                    (dx, dy) = (a * dx + c * dy, b * dx + d * dy);
                }
            }
            else
            {
                // Point matching: point arg2 of the component lands on point arg1 of the glyph built so far.
                var parent = contours.SelectMany(x => x).ToList();
                var child = component.SelectMany(x => x).Select(Map).ToList();
                if (arg1 < parent.Count && arg2 < child.Count)
                {
                    dx = parent[arg1].X - child[arg2].X;
                    dy = parent[arg1].Y - child[arg2].Y;
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
        while ((flags & MoreComponents) != 0);
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
            throw new NotSupportedException("В шрифте нет таблицы символов Юникода.");
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
        if (h != 0)
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
