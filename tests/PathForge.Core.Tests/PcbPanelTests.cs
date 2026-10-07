using PathForge.Core.Geometry;
using PathForge.Core.Import.Pcb;

namespace PathForge.Core.Tests;

/// <summary>Step-and-repeat panels and sorting a whole set of fabrication files.</summary>
public class PcbPanelTests
{
    [Fact]
    public void Step_and_repeat_places_the_block_at_every_copy()
    {
        // One 2 × 1 mm pad flashed in a 3 × 2 panel, 10 mm apart in X and 5 mm in Y, then a single pad outside the block.
        const string gerber = """
            %FSLAX46Y46*%
            %MOMM*%
            %ADD10R,2X1*%
            %SRX3Y2I10.0J5.0*%
            D10*
            X0Y0D03*
            %SR*%
            X100000000Y0D03*
            M02*
            """;

        var result = GerberReader.Read(gerber, GerberMode.Copper);

        Assert.Empty(result.Warnings);
        Assert.Equal(7, result.Contours.Count);
        var centres = result.Contours.Select(c => c.GetBounds()).Select(b => (Math.Round((b.MinX + b.MaxX) / 2, 3), Math.Round((b.MinY + b.MaxY) / 2, 3))).OrderBy(p => p).ToList();
        Assert.Equal(new[] { (0.0, 0.0), (0.0, 5.0), (10.0, 0.0), (10.0, 5.0), (20.0, 0.0), (20.0, 5.0), (100.0, 0.0) }, centres);
    }

    [Fact]
    public void Step_and_repeat_copies_board_outlines_and_closes_at_the_end_of_file()
    {
        const string gerber = """
            %FSLAX46Y46*%
            %MOMM*%
            %ADD10C,0.1*%
            %SRX2Y1I12J0*%
            D10*
            X0Y0D02*
            X10000000Y0D01*
            X10000000Y5000000D01*
            X0Y5000000D01*
            X0Y0D01*
            M02*
            """;

        var result = GerberReader.Read(gerber, GerberMode.Outline);

        Assert.Equal(2, result.Contours.Count);
        Assert.All(result.Contours, c => Assert.True(c.IsClosed));
        Assert.Equal(new[] { 0.0, 12.0 }, result.Contours.Select(c => Math.Round(c.GetBounds().MinX, 6)).OrderBy(x => x));
    }

    [Fact]
    public void Clear_polarity_inside_the_block_is_repeated_too()
    {
        const string gerber = """
            %FSLAX46Y46*%
            %MOMM*%
            %ADD10R,4X4*%
            %ADD11C,1*%
            %SRX2Y1I10J0*%
            %LPD*%
            D10*
            X0Y0D03*
            %LPC*%
            D11*
            X0Y0D03*
            %SR*%
            M02*
            """;

        var result = GerberReader.Read(gerber, GerberMode.Copper);

        // Each copy: an outer square and the round hole cut by the clear flash.
        Assert.Equal(4, result.Contours.Count);
        Assert.Equal(2, result.Contours.Count(c => c.GetBounds().Width < 1.5));
    }

    [Theory]
    [InlineData("board-F_Cu.gbr", "%FSLAX46Y46*%\n%MOMM*%\n%TF.FileFunction,Copper,L1,Top*%\n", PcbFileKind.Copper)]
    [InlineData("board-Edge_Cuts.gbr", "%FSLAX46Y46*%\n%MOMM*%\n%TF.FileFunction,Profile,NP*%\n", PcbFileKind.Outline)]
    [InlineData("board-F_Mask.gbr", "%FSLAX46Y46*%\n%MOMM*%\n%TF.FileFunction,Soldermask,Top*%\n", PcbFileKind.SolderMask)]
    [InlineData("board-F_Paste.gbr", "%FSLAX46Y46*%\n%MOMM*%\n%TF.FileFunction,Paste,Top*%\n", PcbFileKind.Paste)]
    [InlineData("board-F_Silkscreen.gbr", "%FSLAX46Y46*%\n%MOMM*%\n%TF.FileFunction,Legend,Top*%\n", PcbFileKind.Silkscreen)]
    [InlineData("board-F_Fab.gbr", "%FSLAX46Y46*%\n%MOMM*%\n%TF.FileFunction,Other,User*%\n", PcbFileKind.Skipped)]
    [InlineData("Gerber_TopPasteMaskLayer.GTP", "%FSLAX24Y24*%\n%MOIN*%\n", PcbFileKind.Paste)]
    [InlineData("Gerber_BottomSolderMaskLayer.GBS", "%FSLAX24Y24*%\n%MOIN*%\n", PcbFileKind.SolderMask)]
    [InlineData("Gerber_TopLayer.GTL", "%FSLAX24Y24*%\n%MOIN*%\n", PcbFileKind.Copper)]
    [InlineData("Gerber_BottomLayer.GBL", "%FSLAX24Y24*%\n%MOIN*%\n", PcbFileKind.Copper)]
    [InlineData("Board.GKO", "%FSLAX24Y24*%\n%MOIN*%\n", PcbFileKind.Outline)]
    [InlineData("Board.GTO", "%FSLAX24Y24*%\n%MOIN*%\n", PcbFileKind.Silkscreen)]
    [InlineData("board-B_Courtyard.gbr", "%FSLAX46Y46*%\n%MOMM*%\n", PcbFileKind.Skipped)]
    [InlineData("board-PTH.drl", "M48\n; DRILL file\nMETRIC\nT1C0.800\n%\n", PcbFileKind.Drill)]
    [InlineData("readme.txt", "Manufacturing notes", PcbFileKind.Skipped)]
    public void Fabrication_files_are_told_apart(string name, string content, PcbFileKind expected)
    {
        var (kind, reason) = PcbFileDetector.Detect(name, content);

        Assert.Equal(expected, kind);
        Assert.Equal(expected == PcbFileKind.Skipped, reason.Length > 0);
    }
}
