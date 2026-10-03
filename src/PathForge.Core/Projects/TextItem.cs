namespace PathForge.Core.Projects;

public enum TextAlignment
{
    Left,
    Center,
    Right,
}

/// <summary>
/// Text written with a TrueType font. Its outlines are stored as ordinary contours (marked with
/// <see cref="Geometry.Contour.TextId"/>) and rebuilt whenever the text settings change.
/// </summary>
public sealed class TextItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Text { get; set; } = "Текст";

    /// <summary>Full path of the .ttf / .ttc file.</summary>
    public string FontPath { get; set; } = "";

    /// <summary>Font number inside a .ttc collection.</summary>
    public int FontIndex { get; set; }

    /// <summary>Font name for display (the file may be missing on another computer).</summary>
    public string FontName { get; set; } = "";

    /// <summary>Height of capital letters (mm).</summary>
    public double HeightMm { get; set; } = 10;

    /// <summary>Start of the baseline of the first line (left end, centre or right end depending on the alignment).</summary>
    public double X { get; set; }

    public double Y { get; set; }

    public TextAlignment Alignment { get; set; } = TextAlignment.Left;

    /// <summary>Extra space between letters (mm, may be negative).</summary>
    public double LetterSpacing { get; set; }

    /// <summary>Distance between lines as a multiple of the font's line height.</summary>
    public double LineSpacing { get; set; } = 1;

    /// <summary>Mirrored left-right around X (e.g. for the bottom side of a board).</summary>
    public bool Mirrored { get; set; }

    public string Layer { get; set; } = "Текст";
}
