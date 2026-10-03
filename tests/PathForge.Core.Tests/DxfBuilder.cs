using System.Globalization;
using System.Text;

namespace PathForge.Core.Tests;

/// <summary>Builds small ASCII DXF documents for tests.</summary>
internal sealed class DxfBuilder
{
    private readonly StringBuilder _header = new();
    private readonly StringBuilder _blocks = new();
    private readonly StringBuilder _entities = new();
    private StringBuilder _target;

    public DxfBuilder()
    {
        _target = _entities;
    }

    public DxfBuilder Units(int insUnits)
    {
        Pair(_header, 9, "$INSUNITS");
        Pair(_header, 70, insUnits);
        return this;
    }

    public DxfBuilder BeginBlock(string name, double baseX = 0, double baseY = 0)
    {
        _target = _blocks;
        Pair(_target, 0, "BLOCK");
        Pair(_target, 8, "0");
        Pair(_target, 2, name);
        Pair(_target, 10, baseX);
        Pair(_target, 20, baseY);
        return this;
    }

    public DxfBuilder EndBlock()
    {
        Pair(_target, 0, "ENDBLK");
        _target = _entities;
        return this;
    }

    public DxfBuilder Line(double x1, double y1, double x2, double y2, string layer = "0")
    {
        Pair(_target, 0, "LINE");
        Pair(_target, 8, layer);
        Pair(_target, 10, x1);
        Pair(_target, 20, y1);
        Pair(_target, 11, x2);
        Pair(_target, 21, y2);
        return this;
    }

    public DxfBuilder Circle(double x, double y, double r)
    {
        Pair(_target, 0, "CIRCLE");
        Pair(_target, 8, "0");
        Pair(_target, 10, x);
        Pair(_target, 20, y);
        Pair(_target, 40, r);
        return this;
    }

    public DxfBuilder Arc(double x, double y, double r, double startDeg, double endDeg)
    {
        Pair(_target, 0, "ARC");
        Pair(_target, 8, "0");
        Pair(_target, 10, x);
        Pair(_target, 20, y);
        Pair(_target, 40, r);
        Pair(_target, 50, startDeg);
        Pair(_target, 51, endDeg);
        return this;
    }

    public DxfBuilder LwPolyline(bool closed, params (double X, double Y, double Bulge)[] vertices)
    {
        Pair(_target, 0, "LWPOLYLINE");
        Pair(_target, 8, "0");
        Pair(_target, 90, vertices.Length);
        Pair(_target, 70, closed ? 1 : 0);
        foreach (var v in vertices)
        {
            Pair(_target, 10, v.X);
            Pair(_target, 20, v.Y);
            if (v.Bulge != 0)
            {
                Pair(_target, 42, v.Bulge);
            }
        }

        return this;
    }

    public DxfBuilder Insert(string name, double x, double y, double scale = 1, double rotationDeg = 0)
    {
        Pair(_target, 0, "INSERT");
        Pair(_target, 8, "0");
        Pair(_target, 2, name);
        Pair(_target, 10, x);
        Pair(_target, 20, y);
        Pair(_target, 41, scale);
        Pair(_target, 42, scale);
        Pair(_target, 50, rotationDeg);
        return this;
    }

    public DxfBuilder Raw(params (int Code, object Value)[] pairs)
    {
        foreach (var (code, value) in pairs)
        {
            Pair(_target, code, value);
        }

        return this;
    }

    public string Build()
    {
        var sb = new StringBuilder();
        Pair(sb, 0, "SECTION");
        Pair(sb, 2, "HEADER");
        sb.Append(_header);
        Pair(sb, 0, "ENDSEC");
        Pair(sb, 0, "SECTION");
        Pair(sb, 2, "BLOCKS");
        sb.Append(_blocks);
        Pair(sb, 0, "ENDSEC");
        Pair(sb, 0, "SECTION");
        Pair(sb, 2, "ENTITIES");
        sb.Append(_entities);
        Pair(sb, 0, "ENDSEC");
        Pair(sb, 0, "EOF");
        return sb.ToString();
    }

    private static void Pair(StringBuilder sb, int code, object value) =>
        sb.Append(code.ToString(CultureInfo.InvariantCulture)).Append("\r\n")
          .Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append("\r\n");
}
