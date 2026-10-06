using PathForge.Core.Geometry;
using PathForge.Core.Localization;

namespace PathForge.Core.Text;

/// <summary>
/// Glyph outlines from a CFF or CFF2 table (OpenType fonts with PostScript outlines, usually .otf), written from
/// the CFF, CFF2 and Type 2 charstring specifications: name-keyed and CID-keyed fonts, local and global
/// subroutines, all path operators including flex, and for CFF2 the variation operators (blend, vsindex).
/// Hints are skipped; widths come from the hmtx table (and HVAR).
/// </summary>
internal sealed class CffOutlines
{
    private const int MaxSubrDepth = 10;

    private readonly byte[] _data;
    private readonly bool _cff2;
    private readonly int _maxStack;

    /// <summary>CFF2: variation store of the blend operator and the default vsindex of each font dictionary.</summary>
    private readonly ItemVariationStore? _store;
    private readonly List<int> _defaultVsIndex = new();
    private readonly List<(int Start, int End)> _charStrings;
    private readonly List<(int Start, int End)> _globalSubrs;
    private readonly List<List<(int Start, int End)>> _localSubrs = new();

    /// <summary>Index into <see cref="_localSubrs"/> per glyph (CID fonts); null for name-keyed fonts.</summary>
    private readonly byte[]? _fdSelect;

    /// <summary>CFF2 table at a location of the design space (normalized coordinates, empty = default).</summary>
    public CffOutlines(byte[] data, int offset, int length, double[] coords)
    {
        _data = data;
        _cff2 = true;
        _maxStack = 513;
        var end = offset + length;
        if (length < 5 || data[offset] != 2)
        {
            throw new FormatException(Loc.T("Неподдерживаемая версия таблицы CFF2.", "Unsupported CFF2 table version."));
        }

        var headerSize = data[offset + 2];
        var topLength = U16(offset + 3);
        var top = ReadDict(offset + headerSize, offset + headerSize + topLength);
        ReadIndex(offset + headerSize + topLength, out _globalSubrs);
        if (!top.TryGetValue(17, out var charStringsOffset))
        {
            throw new FormatException(Loc.T("В шрифте CFF2 нет контуров (CharStrings).", "The CFF2 font has no outlines (CharStrings)."));
        }

        ReadIndex(offset + (int)charStringsOffset[0], out _charStrings);
        if (top.TryGetValue(24, out var storeOffset))
        {
            // The store is preceded by its length.
            _store = new ItemVariationStore(new FontData(data), offset + (int)storeOffset[0] + 2, coords);
        }

        if (!top.TryGetValue(1236, out var fdArrayOffset))
        {
            throw new FormatException(Loc.T("В шрифте CFF2 нет FDArray.", "The CFF2 font has no FDArray."));
        }

        ReadIndex(offset + (int)fdArrayOffset[0], out var fontDicts);
        foreach (var (start, stop) in fontDicts)
        {
            var (subrs, vsindex) = ReadPrivate(offset, ReadDict(start, stop));
            _localSubrs.Add(subrs);
            _defaultVsIndex.Add(vsindex);
        }

        if (_localSubrs.Count == 0)
        {
            _localSubrs.Add(new List<(int, int)>());
            _defaultVsIndex.Add(0);
        }

        if (top.TryGetValue(1237, out var fdSelectOffset) && fontDicts.Count > 1)
        {
            _fdSelect = ReadFdSelect(offset + (int)fdSelectOffset[0], _charStrings.Count, end);
        }
    }

    public CffOutlines(byte[] data, int offset, int length)
    {
        _data = data;
        _maxStack = 48;
        var end = offset + length;
        if (length < 4 || data[offset] != 1)
        {
            throw new FormatException(Loc.T("Неподдерживаемая версия таблицы CFF.", "Unsupported CFF table version."));
        }

        var position = offset + data[offset + 2]; // header size
        position = ReadIndex(position, out _); // Name INDEX
        position = ReadIndex(position, out var topDicts);
        position = ReadIndex(position, out _); // String INDEX
        ReadIndex(position, out _globalSubrs);
        if (topDicts.Count == 0)
        {
            throw new FormatException(Loc.T("В таблице CFF нет шрифта.", "The CFF table contains no font."));
        }

        var top = ReadDict(topDicts[0].Start, topDicts[0].End);
        if (top.TryGetValue(1206, out var charstringType) && charstringType[0] != 2)
        {
            throw new NotSupportedException(Loc.T("Поддерживаются только контуры Type 2 (CFF).", "Only Type 2 (CFF) outlines are supported."));
        }

        if (!top.TryGetValue(17, out var charStringsOffset))
        {
            throw new FormatException(Loc.T("В шрифте CFF нет контуров (CharStrings).", "The CFF font has no outlines (CharStrings)."));
        }

        ReadIndex(offset + (int)charStringsOffset[0], out _charStrings);

        if (top.ContainsKey(1230))
        {
            // CID-keyed: every glyph belongs to a font dictionary with its own private data.
            if (!top.TryGetValue(1236, out var fdArrayOffset) || !top.TryGetValue(1237, out var fdSelectOffset))
            {
                throw new FormatException(Loc.T("В CID-шрифте нет FDArray/FDSelect.", "The CID font has no FDArray/FDSelect."));
            }

            ReadIndex(offset + (int)fdArrayOffset[0], out var fontDicts);
            foreach (var (start, stop) in fontDicts)
            {
                _localSubrs.Add(ReadPrivateSubrs(offset, ReadDict(start, stop)));
            }

            _fdSelect = ReadFdSelect(offset + (int)fdSelectOffset[0], _charStrings.Count, end);
        }
        else
        {
            _localSubrs.Add(ReadPrivateSubrs(offset, top));
        }
    }

    public int GlyphCount => _charStrings.Count;

    public List<GlyphPath> GetPaths(int glyph)
    {
        var paths = new List<GlyphPath>();
        if (glyph < 0 || glyph >= _charStrings.Count)
        {
            return paths;
        }

        var fd = _fdSelect is null ? 0 : Math.Min(_fdSelect[glyph], _localSubrs.Count - 1);
        var machine = new Interpreter(this, _localSubrs[fd], paths, fd < _defaultVsIndex.Count ? _defaultVsIndex[fd] : 0);
        var (start, stop) = _charStrings[glyph];
        machine.Run(start, stop, 0);
        machine.ClosePath();
        return paths;
    }

    private List<(int Start, int End)> ReadPrivateSubrs(int cffOffset, Dictionary<int, List<double>> dict) =>
        ReadPrivate(cffOffset, dict).Subrs;

    /// <summary>Local subroutines and (CFF2) the default variation data index of a private dictionary.</summary>
    private (List<(int Start, int End)> Subrs, int VsIndex) ReadPrivate(int cffOffset, Dictionary<int, List<double>> dict)
    {
        var subrs = new List<(int, int)>();
        if (!dict.TryGetValue(18, out var privateEntry) || privateEntry.Count < 2)
        {
            return (subrs, 0);
        }

        var size = (int)privateEntry[0];
        var privateStart = cffOffset + (int)privateEntry[1];
        var privateDict = ReadDict(privateStart, privateStart + size);
        if (privateDict.TryGetValue(19, out var subrsOffset))
        {
            ReadIndex(privateStart + (int)subrsOffset[0], out subrs);
        }

        var vsindex = privateDict.TryGetValue(22, out var vs) && vs.Count > 0 ? (int)vs[0] : 0;
        return (subrs, vsindex);
    }

    private byte[] ReadFdSelect(int position, int glyphCount, int end)
    {
        var result = new byte[glyphCount];
        var format = _data[position];
        if (format == 0)
        {
            Array.Copy(_data, position + 1, result, 0, Math.Min(glyphCount, end - position - 1));
        }
        else if (format == 4)
        {
            // CFF2: 32-bit glyph ranges, 16-bit font dictionary numbers.
            var ranges = (int)U32(position + 1);
            for (var r = 0; r < ranges; r++)
            {
                var record = position + 5 + r * 6;
                var first = (int)U32(record);
                var fd = U16(record + 4);
                var next = (int)U32(record + 6);
                for (var g = first; g < next && g < glyphCount; g++)
                {
                    result[g] = (byte)Math.Min(fd, 255);
                }
            }
        }
        else if (format == 3)
        {
            var ranges = U16(position + 1);
            for (var r = 0; r < ranges; r++)
            {
                var record = position + 3 + r * 3;
                var first = U16(record);
                var fd = _data[record + 2];
                var next = U16(record + 3); // next range's first glyph, or the sentinel
                for (var g = first; g < next && g < glyphCount; g++)
                {
                    result[g] = fd;
                }
            }
        }
        else
        {
            throw new NotSupportedException(Loc.T($"Формат FDSelect {format} не поддерживается.", $"FDSelect format {format} is not supported."));
        }

        return result;
    }

    /// <summary>Reads an INDEX structure (16-bit count in CFF, 32-bit in CFF2); returns the position after it.</summary>
    private int ReadIndex(int position, out List<(int Start, int End)> items)
    {
        items = new List<(int, int)>();
        var countSize = _cff2 ? 4 : 2;
        var count = _cff2 ? (int)U32(position) : U16(position);
        if (count == 0)
        {
            return position + countSize;
        }

        var offSize = _data[position + countSize];
        var offsets = position + countSize + 1;
        var dataStart = offsets + (count + 1) * offSize - 1;
        for (var i = 0; i < count; i++)
        {
            items.Add((dataStart + Offset(offsets + i * offSize, offSize), dataStart + Offset(offsets + (i + 1) * offSize, offSize)));
        }

        return dataStart + Offset(offsets + count * offSize, offSize);
    }

    private int Offset(int position, int size)
    {
        var value = 0;
        for (var i = 0; i < size; i++)
        {
            value = (value << 8) | _data[position + i];
        }

        return value;
    }

    /// <summary>DICT data: operator (escaped ones as 1200 + second byte) to its operands.</summary>
    private Dictionary<int, List<double>> ReadDict(int position, int end)
    {
        var result = new Dictionary<int, List<double>>();
        var operands = new List<double>();
        while (position < end)
        {
            int b0 = _data[position++];
            if (b0 <= 27)
            {
                // 22 vsindex and 23 blend are CFF2 operators; the other values up to 27 are reserved.
                var op = b0 == 12 ? 1200 + _data[position++] : b0;
                result[op] = operands;
                operands = new List<double>();
            }
            else if (b0 == 28)
            {
                operands.Add((short)U16(position));
                position += 2;
            }
            else if (b0 == 29)
            {
                operands.Add((_data[position] << 24) | (_data[position + 1] << 16) | (_data[position + 2] << 8) | _data[position + 3]);
                position += 4;
            }
            else if (b0 == 30)
            {
                position = ReadReal(position, out var real);
                operands.Add(real);
            }
            else if (b0 >= 32 && b0 <= 246)
            {
                operands.Add(b0 - 139);
            }
            else if (b0 >= 247 && b0 <= 250)
            {
                operands.Add((b0 - 247) * 256 + _data[position++] + 108);
            }
            else if (b0 >= 251 && b0 <= 254)
            {
                operands.Add(-(b0 - 251) * 256 - _data[position++] - 108);
            }
        }

        return result;
    }

    /// <summary>Real number in nibbles: digits, '.', 'E', 'E-', '-', end.</summary>
    private int ReadReal(int position, out double value)
    {
        var text = new System.Text.StringBuilder();
        while (true)
        {
            var b = _data[position++];
            foreach (var nibble in new[] { b >> 4, b & 15 })
            {
                switch (nibble)
                {
                    case < 10:
                        text.Append((char)('0' + nibble));
                        break;
                    case 10:
                        text.Append('.');
                        break;
                    case 11:
                        text.Append('E');
                        break;
                    case 12:
                        text.Append("E-");
                        break;
                    case 14:
                        text.Append('-');
                        break;
                    case 15:
                        double.TryParse(text.ToString(), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out value);
                        return position;
                }
            }
        }
    }

    private int U16(int position) => (_data[position] << 8) | _data[position + 1];

    private uint U32(int position) => (uint)((_data[position] << 24) | (_data[position + 1] << 16) | (_data[position + 2] << 8) | _data[position + 3]);

    private static int Bias(int count) => count < 1240 ? 107 : count < 33900 ? 1131 : 32768;

    /// <summary>Type 2 charstring interpreter producing glyph paths.</summary>
    private sealed class Interpreter
    {
        private readonly CffOutlines _font;
        private readonly List<(int Start, int End)> _localSubrs;
        private readonly List<GlyphPath> _paths;
        private readonly List<double> _stack = new();
        private GlyphPath? _path;
        private double _x;
        private double _y;
        private int _stems;
        private bool _widthDone;
        private bool _ended;
        private int _vsIndex;
        private double[]? _scalars;

        public Interpreter(CffOutlines font, List<(int Start, int End)> localSubrs, List<GlyphPath> paths, int vsIndex = 0)
        {
            _font = font;
            _localSubrs = localSubrs;
            _paths = paths;
            _vsIndex = vsIndex;
            // CFF2 charstrings carry no width.
            _widthDone = font._cff2;
        }

        public void ClosePath()
        {
            if (_path is { Segments.Count: > 0 })
            {
                if (!_path.End.IsNear(_path.Start, 1e-9))
                {
                    _path.LineTo(_path.Start);
                }

                _paths.Add(_path);
            }

            _path = null;
        }

        public void Run(int position, int end, int depth)
        {
            var data = _font._data;
            while (position < end && !_ended)
            {
                int b0 = data[position++];
                if (b0 >= 32 || b0 == 28)
                {
                    position = Number(data, position, b0);
                    continue;
                }

                switch (b0)
                {
                    case 1: // hstem
                    case 3: // vstem
                    case 18: // hstemhm
                    case 23: // vstemhm
                        Width(_stack.Count % 2 == 1);
                        _stems += _stack.Count / 2;
                        _stack.Clear();
                        break;
                    case 19: // hintmask
                    case 20: // cntrmask
                        // Arguments here are an implicit vstem.
                        Width(_stack.Count % 2 == 1);
                        _stems += _stack.Count / 2;
                        _stack.Clear();
                        position += (_stems + 7) / 8;
                        break;
                    case 21: // rmoveto
                        Width(_stack.Count > 2);
                        MoveTo(Arg(0), Arg(1));
                        break;
                    case 22: // hmoveto
                        Width(_stack.Count > 1);
                        MoveTo(Arg(0), 0);
                        break;
                    case 4: // vmoveto
                        Width(_stack.Count > 1);
                        MoveTo(0, Arg(0));
                        break;
                    case 5: // rlineto
                        for (var i = 0; i + 1 < _stack.Count; i += 2)
                        {
                            LineTo(_stack[i], _stack[i + 1]);
                        }

                        _stack.Clear();
                        break;
                    case 6: // hlineto
                    case 7: // vlineto
                        var horizontal = b0 == 6;
                        foreach (var d in _stack)
                        {
                            LineTo(horizontal ? d : 0, horizontal ? 0 : d);
                            horizontal = !horizontal;
                        }

                        _stack.Clear();
                        break;
                    case 8: // rrcurveto
                        for (var i = 0; i + 5 < _stack.Count; i += 6)
                        {
                            CurveTo(_stack[i], _stack[i + 1], _stack[i + 2], _stack[i + 3], _stack[i + 4], _stack[i + 5]);
                        }

                        _stack.Clear();
                        break;
                    case 24: // rcurveline
                    {
                        var i = 0;
                        for (; i + 7 < _stack.Count; i += 6)
                        {
                            CurveTo(_stack[i], _stack[i + 1], _stack[i + 2], _stack[i + 3], _stack[i + 4], _stack[i + 5]);
                        }

                        if (i + 1 < _stack.Count)
                        {
                            LineTo(_stack[i], _stack[i + 1]);
                        }

                        _stack.Clear();
                        break;
                    }

                    case 25: // rlinecurve
                    {
                        var i = 0;
                        for (; i + 7 < _stack.Count; i += 2)
                        {
                            LineTo(_stack[i], _stack[i + 1]);
                        }

                        if (i + 5 < _stack.Count)
                        {
                            CurveTo(_stack[i], _stack[i + 1], _stack[i + 2], _stack[i + 3], _stack[i + 4], _stack[i + 5]);
                        }

                        _stack.Clear();
                        break;
                    }

                    case 26: // vvcurveto
                    {
                        var i = 0;
                        var dx1 = 0.0;
                        if (_stack.Count % 4 == 1)
                        {
                            dx1 = _stack[0];
                            i = 1;
                        }

                        for (; i + 3 < _stack.Count; i += 4)
                        {
                            CurveTo(dx1, _stack[i], _stack[i + 1], _stack[i + 2], 0, _stack[i + 3]);
                            dx1 = 0;
                        }

                        _stack.Clear();
                        break;
                    }

                    case 27: // hhcurveto
                    {
                        var i = 0;
                        var dy1 = 0.0;
                        if (_stack.Count % 4 == 1)
                        {
                            dy1 = _stack[0];
                            i = 1;
                        }

                        for (; i + 3 < _stack.Count; i += 4)
                        {
                            CurveTo(_stack[i], dy1, _stack[i + 1], _stack[i + 2], _stack[i + 3], 0);
                            dy1 = 0;
                        }

                        _stack.Clear();
                        break;
                    }

                    case 30: // vhcurveto
                    case 31: // hvcurveto
                    {
                        var horizontalStart = b0 == 31;
                        var i = 0;
                        while (i + 3 < _stack.Count)
                        {
                            // The very last curve may carry a fifth argument for its final other-axis delta.
                            var last = _stack.Count - i == 5 ? _stack[i + 4] : 0;
                            if (horizontalStart)
                            {
                                CurveTo(_stack[i], 0, _stack[i + 1], _stack[i + 2], last, _stack[i + 3]);
                            }
                            else
                            {
                                CurveTo(0, _stack[i], _stack[i + 1], _stack[i + 2], _stack[i + 3], last);
                            }

                            horizontalStart = !horizontalStart;
                            i += 4;
                        }

                        _stack.Clear();
                        break;
                    }

                    case 10: // callsubr
                    case 29: // callgsubr
                    {
                        if (_stack.Count == 0 || depth >= MaxSubrDepth)
                        {
                            _stack.Clear();
                            break;
                        }

                        var subrs = b0 == 10 ? _localSubrs : _font._globalSubrs;
                        var index = (int)_stack[^1] + Bias(subrs.Count);
                        _stack.RemoveAt(_stack.Count - 1);
                        if (index >= 0 && index < subrs.Count)
                        {
                            Run(subrs[index].Start, subrs[index].End, depth + 1);
                        }

                        break;
                    }

                    case 11: // return
                        return;
                    case 15 when _font._cff2: // vsindex
                        if (_stack.Count > 0)
                        {
                            _vsIndex = (int)_stack[^1];
                            _scalars = null;
                        }

                        _stack.Clear();
                        break;
                    case 16 when _font._cff2: // blend
                        Blend();
                        break;
                    case 14: // endchar
                        Width(_stack.Count == 1 || _stack.Count == 5);
                        ClosePath();
                        _ended = true;
                        return;
                    case 12:
                        Escape(data[position++]);
                        break;
                    default:
                        // Reserved operators: drop their arguments.
                        _stack.Clear();
                        break;
                }
            }
        }

        private void Escape(int op)
        {
            var s = _stack;
            switch (op)
            {
                case 35 when s.Count >= 12: // flex
                    CurveTo(s[0], s[1], s[2], s[3], s[4], s[5]);
                    CurveTo(s[6], s[7], s[8], s[9], s[10], s[11]);
                    break;
                case 34 when s.Count >= 7: // hflex
                {
                    var startY = _y;
                    CurveTo(s[0], 0, s[1], s[2], s[3], 0);
                    CurveTo(s[4], 0, s[5], startY - _y, s[6], 0);
                    break;
                }

                case 36 when s.Count >= 9: // hflex1
                {
                    var startY = _y;
                    CurveTo(s[0], s[1], s[2], s[3], s[4], 0);
                    var y = _y;
                    CurveTo(s[5], 0, s[6], s[7], s[8], startY - (y + s[7]));
                    break;
                }

                case 37 when s.Count >= 11: // flex1
                {
                    var startX = _x;
                    var startY = _y;
                    var dx = s[0] + s[2] + s[4] + s[6] + s[8];
                    var dy = s[1] + s[3] + s[5] + s[7] + s[9];
                    CurveTo(s[0], s[1], s[2], s[3], s[4], s[5]);
                    var c3x = _x + s[6];
                    var c3y = _y + s[7];
                    var c4x = c3x + s[8];
                    var c4y = c3y + s[9];
                    double ex, ey;
                    if (Math.Abs(dx) > Math.Abs(dy))
                    {
                        ex = c4x + s[10];
                        ey = startY;
                    }
                    else
                    {
                        ex = startX;
                        ey = c4y + s[10];
                    }

                    _path?.CubicTo(new Vec2(c3x, c3y), new Vec2(c4x, c4y), new Vec2(ex, ey));
                    _x = ex;
                    _y = ey;
                    break;
                }
            }

            // Arithmetic and storage operators are deprecated and not used by fonts in practice.
            _stack.Clear();
        }

        /// <summary>
        /// CFF2 blend: n default values followed by n × k deltas (k regions of the current variation data) and n;
        /// leaves the n values interpolated to the font's location on the stack.
        /// </summary>
        private void Blend()
        {
            if (_stack.Count == 0)
            {
                return;
            }

            var n = (int)_stack[^1];
            _stack.RemoveAt(_stack.Count - 1);
            _scalars ??= _font._store?.SubtableScalars(_vsIndex) ?? Array.Empty<double>();
            var k = _scalars.Length;
            var first = _stack.Count - n * (k + 1);
            if (n < 0 || first < 0)
            {
                _stack.Clear();
                return;
            }

            for (var i = 0; i < n; i++)
            {
                var value = _stack[first + i];
                for (var j = 0; j < k; j++)
                {
                    value += _stack[first + n + i * k + j] * _scalars[j];
                }

                _stack[first + i] = value;
            }

            _stack.RemoveRange(first + n, n * k);
        }

        private int Number(byte[] data, int position, int b0)
        {
            double value;
            if (b0 == 28)
            {
                value = (short)((data[position] << 8) | data[position + 1]);
                position += 2;
            }
            else if (b0 <= 246)
            {
                value = b0 - 139;
            }
            else if (b0 <= 250)
            {
                value = (b0 - 247) * 256 + data[position++] + 108;
            }
            else if (b0 <= 254)
            {
                value = -(b0 - 251) * 256 - data[position++] - 108;
            }
            else
            {
                // 16.16 fixed point.
                value = ((data[position] << 24) | (data[position + 1] << 16) | (data[position + 2] << 8) | data[position + 3]) / 65536.0;
                position += 4;
            }

            if (_stack.Count < _font._maxStack)
            {
                _stack.Add(value);
            }

            return position;
        }

        /// <summary>The first stack-clearing operator may carry the glyph width as an extra first argument.</summary>
        private void Width(bool present)
        {
            if (!_widthDone)
            {
                _widthDone = true;
                if (present && _stack.Count > 0)
                {
                    _stack.RemoveAt(0);
                }
            }
        }

        private double Arg(int index) => index < _stack.Count ? _stack[index] : 0;

        private void MoveTo(double dx, double dy)
        {
            ClosePath();
            _x += dx;
            _y += dy;
            _path = new GlyphPath(new Vec2(_x, _y));
            _stack.Clear();
        }

        private void LineTo(double dx, double dy)
        {
            _path ??= new GlyphPath(new Vec2(_x, _y));
            _x += dx;
            _y += dy;
            _path.LineTo(new Vec2(_x, _y));
        }

        private void CurveTo(double dx1, double dy1, double dx2, double dy2, double dx3, double dy3)
        {
            _path ??= new GlyphPath(new Vec2(_x, _y));
            var c1 = new Vec2(_x + dx1, _y + dy1);
            var c2 = new Vec2(c1.X + dx2, c1.Y + dy2);
            var end = new Vec2(c2.X + dx3, c2.Y + dy3);
            _path.CubicTo(c1, c2, end);
            _x = end.X;
            _y = end.Y;
        }
    }
}
