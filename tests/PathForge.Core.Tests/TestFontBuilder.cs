using System.Text;

namespace PathForge.Core.Tests;

/// <summary>
/// Builds a tiny TrueType font in memory (written from the OpenType table layouts) so that the font reader
/// can be tested without any real font files:
/// ' ' (glyph 1, blank), 'H' (glyph 2, rectangle 600×700), 'O' (glyph 3, off-curve circle with a square hole),
/// 'A' (glyph 4, composite of two 'H's, the second scaled to one half).
/// </summary>
internal static class TestFontBuilder
{
    public const int UnitsPerEm = 1000;
    public const int CapHeight = 700;
    public const string Family = "Test Sans";

    public static byte[] Build()
    {
        var glyphs = new List<byte[]>
        {
            Array.Empty<byte>(),
            Array.Empty<byte>(),
            SimpleGlyph(new[] { new[] { (0, 0, true), (600, 0, true), (600, 700, true), (0, 700, true) } }),
            SimpleGlyph(new[]
            {
                // Only off-curve points: every on-curve point is implied half-way between them.
                new[] { (700, 700, false), (100, 700, false), (100, 100, false), (700, 100, false) },
                // Hole, opposite direction.
                new[] { (300, 300, true), (300, 500, true), (500, 500, true), (500, 300, true) },
            }),
            CompositeGlyph(),
        };
        var advances = new[] { 500, 500, 700, 800, 1000 };

        var glyf = new MemoryStream();
        var loca = new BigEndian();
        foreach (var glyph in glyphs)
        {
            loca.U16((ushort)(glyf.Length / 2));
            glyf.Write(glyph);
            if (glyf.Length % 2 != 0)
            {
                glyf.WriteByte(0);
            }
        }

        loca.U16((ushort)(glyf.Length / 2));

        var head = new BigEndian();
        head.U32(0x00010000).U32(0x00010000).U32(0).U32(0x5F0F3CF5).U16(0).U16(UnitsPerEm);
        head.Zeros(16).I16(0).I16(-200).I16(1000).I16(1000).U16(0).U16(8).I16(2).I16(0).I16(0);

        var hhea = new BigEndian();
        hhea.U32(0x00010000).I16(900).I16(-200).I16(100).U16(1000).I16(0).I16(0).I16(1000)
            .I16(1).I16(0).I16(0).Zeros(8).I16(0).U16((ushort)advances.Length);

        var maxp = new BigEndian();
        maxp.U32(0x00005000).U16((ushort)glyphs.Count);

        var hmtx = new BigEndian();
        foreach (var advance in advances)
        {
            hmtx.U16((ushort)advance).I16(0);
        }

        var os2 = new BigEndian();
        os2.U16(2).Zeros(86).I16(CapHeight).Zeros(6);

        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["OS/2"] = os2.ToArray(),
            ["cmap"] = Cmap(),
            ["glyf"] = glyf.ToArray(),
            ["head"] = head.ToArray(),
            ["hhea"] = hhea.ToArray(),
            ["hmtx"] = hmtx.ToArray(),
            ["loca"] = loca.ToArray(),
            ["maxp"] = maxp.ToArray(),
            ["name"] = Name(),
        };

        var file = new BigEndian();
        file.U32(0x00010000).U16((ushort)tables.Count).U16(0).U16(0).U16(0);
        var offset = 12 + tables.Count * 16;
        var body = new MemoryStream();
        foreach (var (tag, data) in tables)
        {
            file.Bytes(Encoding.ASCII.GetBytes(tag)).U32(0).U32((uint)(offset + body.Length)).U32((uint)data.Length);
            body.Write(data);
            while (body.Length % 4 != 0)
            {
                body.WriteByte(0);
            }
        }

        file.Bytes(body.ToArray());
        return file.ToArray();
    }

    /// <summary>A .ttc collection holding the test font twice.</summary>
    public static byte[] BuildCollection()
    {
        var font = Build();
        var file = new BigEndian();
        file.Bytes(Encoding.ASCII.GetBytes("ttcf")).U32(0x00010000).U32(2);
        var header = 12 + 8;
        file.U32((uint)header).U32((uint)header);

        // Table offsets inside the collection are relative to the file start: shift the copy.
        var shifted = (byte[])font.Clone();
        var numTables = (shifted[4] << 8) | shifted[5];
        for (var i = 0; i < numTables; i++)
        {
            var at = 12 + i * 16 + 8;
            var value = (uint)((shifted[at] << 24) | (shifted[at + 1] << 16) | (shifted[at + 2] << 8) | shifted[at + 3]) + (uint)header;
            shifted[at] = (byte)(value >> 24);
            shifted[at + 1] = (byte)(value >> 16);
            shifted[at + 2] = (byte)(value >> 8);
            shifted[at + 3] = (byte)value;
        }

        file.Bytes(shifted);
        return file.ToArray();
    }

    private static byte[] SimpleGlyph((int X, int Y, bool On)[][] contours)
    {
        var points = contours.SelectMany(c => c).ToList();
        var g = new BigEndian();
        g.I16((short)contours.Length)
            .I16((short)points.Min(p => p.X)).I16((short)points.Min(p => p.Y))
            .I16((short)points.Max(p => p.X)).I16((short)points.Max(p => p.Y));
        var end = -1;
        foreach (var contour in contours)
        {
            end += contour.Length;
            g.U16((ushort)end);
        }

        g.U16(0);

        // Choose the most compact encoding for each delta, as font tools do.
        var flags = new List<byte>();
        var xs = new BigEndian();
        var ys = new BigEndian();
        int px = 0, py = 0;
        foreach (var (x, y, on) in points)
        {
            var flag = (byte)(on ? 0x01 : 0);
            flag |= Encode(x - px, xs, 0x02, 0x10);
            flag |= Encode(y - py, ys, 0x04, 0x20);
            flags.Add(flag);
            px = x;
            py = y;
        }

        // Repeat flag for runs of equal flags.
        for (var i = 0; i < flags.Count;)
        {
            var run = 1;
            while (i + run < flags.Count && flags[i + run] == flags[i] && run < 255)
            {
                run++;
            }

            if (run > 1)
            {
                g.U8((byte)(flags[i] | 0x08)).U8((byte)(run - 1));
            }
            else
            {
                g.U8(flags[i]);
            }

            i += run;
        }

        g.Bytes(xs.ToArray()).Bytes(ys.ToArray());
        return g.ToArray();
    }

    private static byte Encode(int delta, BigEndian output, byte shortBit, byte sameBit)
    {
        if (delta == 0)
        {
            return sameBit;
        }

        if (Math.Abs(delta) < 256)
        {
            output.U8((byte)Math.Abs(delta));
            return (byte)(shortBit | (delta > 0 ? sameBit : 0));
        }

        output.I16((short)delta);
        return 0;
    }

    private static byte[] CompositeGlyph()
    {
        var g = new BigEndian();
        g.I16(-1).I16(0).I16(0).I16(900).I16(1050);
        // ARG_1_AND_2_ARE_WORDS | ARGS_ARE_XY_VALUES | MORE_COMPONENTS
        g.U16(0x0001 | 0x0002 | 0x0020).U16(2).I16(100).I16(0);
        // ARGS_ARE_XY_VALUES | WE_HAVE_A_SCALE (byte arguments)
        g.U16(0x0002 | 0x0008).U16(2).U8(0).U8(100).I16(0x2000);
        return g.ToArray();
    }

    /// <summary>cmap with one (3,1) format 4 subtable; 'O' goes through the glyph id array.</summary>
    private static byte[] Cmap()
    {
        var segments = new (ushort Start, ushort End, short Delta, bool UseArray, ushort Glyph)[]
        {
            (0x20, 0x20, (short)(1 - 0x20), false, 0),
            (0x41, 0x41, (short)(4 - 0x41), false, 0),
            (0x48, 0x48, (short)(2 - 0x48), false, 0),
            (0x4F, 0x4F, 0, true, 3),
            (0xFFFF, 0xFFFF, 1, false, 0),
        };
        var segCount = segments.Length;
        var sub = new BigEndian();
        var arrayEntries = segments.Count(s => s.UseArray);
        var length = 16 + segCount * 8 + arrayEntries * 2;
        sub.U16(4).U16((ushort)length).U16(0).U16((ushort)(segCount * 2)).U16(0).U16(0).U16(0);
        foreach (var s in segments)
        {
            sub.U16(s.End);
        }

        sub.U16(0);
        foreach (var s in segments)
        {
            sub.U16(s.Start);
        }

        foreach (var s in segments)
        {
            sub.I16(s.Delta);
        }

        var arrayIndex = 0;
        for (var i = 0; i < segCount; i++)
        {
            if (segments[i].UseArray)
            {
                // Bytes from this idRangeOffset entry to the glyph id array entry.
                sub.U16((ushort)(2 * (segCount - i) + 2 * arrayIndex));
                arrayIndex++;
            }
            else
            {
                sub.U16(0);
            }
        }

        foreach (var s in segments.Where(s => s.UseArray))
        {
            sub.U16(s.Glyph);
        }

        var cmap = new BigEndian();
        cmap.U16(0).U16(1).U16(3).U16(1).U32(12).Bytes(sub.ToArray());
        return cmap.ToArray();
    }

    private static byte[] Name()
    {
        var family = Encoding.BigEndianUnicode.GetBytes(Family);
        var style = Encoding.BigEndianUnicode.GetBytes("Regular");
        var name = new BigEndian();
        name.U16(0).U16(2).U16(6 + 2 * 12);
        name.U16(3).U16(1).U16(0x0409).U16(1).U16((ushort)family.Length).U16(0);
        name.U16(3).U16(1).U16(0x0409).U16(2).U16((ushort)style.Length).U16((ushort)family.Length);
        name.Bytes(family).Bytes(style);
        return name.ToArray();
    }

    private sealed class BigEndian
    {
        private readonly MemoryStream _stream = new();

        public BigEndian U8(byte value)
        {
            _stream.WriteByte(value);
            return this;
        }

        public BigEndian U16(int value)
        {
            _stream.WriteByte((byte)(value >> 8));
            _stream.WriteByte((byte)value);
            return this;
        }

        public BigEndian I16(int value) => U16((ushort)(short)value);

        public BigEndian U32(uint value)
        {
            U16((int)(value >> 16));
            return U16((int)(value & 0xFFFF));
        }

        public BigEndian Zeros(int count) => Bytes(new byte[count]);

        public BigEndian Bytes(byte[] data)
        {
            _stream.Write(data);
            return this;
        }

        public byte[] ToArray() => _stream.ToArray();
    }
}
