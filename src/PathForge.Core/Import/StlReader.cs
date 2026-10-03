using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using PathForge.Core.Localization;

namespace PathForge.Core.Import;

/// <summary>Triangle mesh (from STL). Stored compactly as little-endian floats, 9 per triangle.</summary>
public sealed class StlMesh
{
    private float[]? _vertices;

    /// <summary>Raw vertex data (serialized as base64): x1 y1 z1 x2 y2 z2 x3 y3 z3 per triangle.</summary>
    public byte[] Data { get; set; } = Array.Empty<byte>();

    public string SourceName { get; set; } = "";

    [JsonIgnore]
    public int TriangleCount => Data.Length / 36;

    [JsonIgnore]
    public float[] Vertices
    {
        get
        {
            if (_vertices is null || _vertices.Length * 4 != Data.Length)
            {
                _vertices = new float[Data.Length / 4];
                Buffer.BlockCopy(Data, 0, _vertices, 0, _vertices.Length * 4);
            }

            return _vertices;
        }
    }

    public static StlMesh FromVertices(float[] vertices, string sourceName)
    {
        var data = new byte[vertices.Length * 4];
        Buffer.BlockCopy(vertices, 0, data, 0, data.Length);
        return new StlMesh { Data = data, SourceName = sourceName };
    }

    /// <summary>(minX, minY, minZ, maxX, maxY, maxZ)</summary>
    public (double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ) Bounds()
    {
        var v = Vertices;
        if (v.Length == 0)
        {
            return (0, 0, 0, 0, 0, 0);
        }

        double minX = v[0], minY = v[1], minZ = v[2], maxX = v[0], maxY = v[1], maxZ = v[2];
        for (var i = 0; i < v.Length; i += 3)
        {
            minX = Math.Min(minX, v[i]);
            maxX = Math.Max(maxX, v[i]);
            minY = Math.Min(minY, v[i + 1]);
            maxY = Math.Max(maxY, v[i + 1]);
            minZ = Math.Min(minZ, v[i + 2]);
            maxZ = Math.Max(maxZ, v[i + 2]);
        }

        return (minX, minY, minZ, maxX, maxY, maxZ);
    }
}

/// <summary>Reader for binary and ASCII STL files.</summary>
public static class StlReader
{
    public static StlMesh ReadFile(string path) => Read(File.ReadAllBytes(path), Path.GetFileName(path));

    public static StlMesh Read(byte[] bytes, string sourceName = "")
    {
        if (IsBinary(bytes))
        {
            var count = BitConverter.ToInt32(bytes, 80);
            var vertices = new float[count * 9];
            for (var t = 0; t < count; t++)
            {
                // 12 bytes normal, then 3 vertices of 3 floats, then 2 bytes attributes.
                var offset = 84 + t * 50 + 12;
                for (var k = 0; k < 9; k++)
                {
                    vertices[t * 9 + k] = BitConverter.ToSingle(bytes, offset + k * 4);
                }
            }

            return StlMesh.FromVertices(vertices, sourceName);
        }

        return ReadAscii(Encoding.ASCII.GetString(bytes), sourceName);
    }

    /// <summary>Binary files have a 80 byte header and a triangle count that matches the file size.</summary>
    private static bool IsBinary(byte[] bytes)
    {
        if (bytes.Length < 84)
        {
            return false;
        }

        var count = BitConverter.ToUInt32(bytes, 80);
        return 84L + count * 50L == bytes.Length;
    }

    private static StlMesh ReadAscii(string text, string sourceName)
    {
        var vertices = new List<float>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("vertex", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4)
            {
                throw new FormatException(Loc.T($"Неверная строка STL: {line}", $"Invalid STL line: {line}"));
            }

            for (var k = 1; k <= 3; k++)
            {
                vertices.Add(float.Parse(parts[k], NumberStyles.Float, CultureInfo.InvariantCulture));
            }
        }

        if (vertices.Count == 0 || vertices.Count % 9 != 0)
        {
            throw new FormatException(Loc.T("В файле STL не найдено треугольников.", "No triangles found in the STL file."));
        }

        return StlMesh.FromVertices(vertices.ToArray(), sourceName);
    }
}
