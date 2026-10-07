using PathForge.Core.Localization;

namespace PathForge.Core.Import.Pcb;

/// <summary>What a fabrication output file holds, as far as milling is concerned.</summary>
public enum PcbFileKind
{
    /// <summary>Copper layer (Gerber): isolation is milled around it.</summary>
    Copper,

    /// <summary>Board outline (Gerber Edge.Cuts / Profile / keep-out): cut out along it.</summary>
    Outline,

    /// <summary>Excellon drill file.</summary>
    Drill,

    /// <summary>Solder paste layer (Gerber): the windows of a paste stencil cut by laser.</summary>
    Paste,

    /// <summary>Solder mask layer (Gerber): the mask openings over the pads, burned free by laser.</summary>
    SolderMask,

    /// <summary>Silk screen / legend layer (Gerber): markings burned into the mask by laser.</summary>
    Silkscreen,

    /// <summary>A layer that is not used (drawings, courtyard, fabrication notes) or not a fabrication file at all.</summary>
    Skipped,
}

/// <summary>
/// Sorts the files of a PCB fabrication output (KiCad, Altium, EasyEDA, Eagle) so that a whole set can be added
/// in one go: by the X2 file function when the file has one, otherwise by the usual file name extensions.
/// </summary>
public static class PcbFileDetector
{
    /// <summary>Kind of the file and, for skipped files, the reason shown to the user.</summary>
    public static (PcbFileKind Kind, string Reason) Detect(string fileName, string content)
    {
        var head = content.Length > 4096 ? content[..4096] : content;
        var name = Path.GetFileName(fileName);
        var extension = Path.GetExtension(fileName).ToLowerInvariant();

        if (head.Contains("M48", StringComparison.Ordinal) && !head.Contains("%FS", StringComparison.Ordinal))
        {
            return (PcbFileKind.Drill, "");
        }

        if (!head.Contains("%FS", StringComparison.Ordinal) && !head.Contains("%MO", StringComparison.Ordinal))
        {
            return (PcbFileKind.Skipped, Loc.T("не Gerber и не сверловка Excellon", "neither Gerber nor an Excellon drill file"));
        }

        // X2: the file says what it is.
        var function = FileFunction(content);
        if (function is not null)
        {
            if (function.StartsWith("Copper", StringComparison.OrdinalIgnoreCase))
            {
                return (PcbFileKind.Copper, "");
            }

            if (function.StartsWith("Profile", StringComparison.OrdinalIgnoreCase))
            {
                return (PcbFileKind.Outline, "");
            }

            if (function.StartsWith("Paste", StringComparison.OrdinalIgnoreCase))
            {
                return (PcbFileKind.Paste, "");
            }

            if (function.StartsWith("Soldermask", StringComparison.OrdinalIgnoreCase))
            {
                return (PcbFileKind.SolderMask, "");
            }

            if (function.StartsWith("Legend", StringComparison.OrdinalIgnoreCase))
            {
                return (PcbFileKind.Silkscreen, "");
            }

            return (PcbFileKind.Skipped, Loc.T($"слой «{function.Split(',')[0]}» не используется", $"the “{function.Split(',')[0]}” layer is not used"));
        }

        // Classic names: Protel/Altium extensions, KiCad and EasyEDA layer names.
        var lower = name.ToLowerInvariant();
        if (IsOutlineFileName(name))
        {
            return (PcbFileKind.Outline, "");
        }

        if (AuxiliaryLayerKind(name) is { } auxiliary)
        {
            return (auxiliary, "");
        }

        if (lower.Contains("fab") || lower.Contains("courtyard") || lower.Contains("drawing"))
        {
            return (PcbFileKind.Skipped, Loc.T("чертёжный слой не используется", "a drawing layer is not used"));
        }

        var innerLayer = extension.Length == 3 && extension[1] == 'g' && char.IsDigit(extension[2]); // Altium .g1, .g2…
        if (extension is ".gtl" or ".gbl" or ".cmp" or ".sol" || innerLayer ||
            lower.Contains("_cu") || lower.Contains(".cu") || lower.Contains("copper") || lower.Contains("toplayer") || lower.Contains("bottomlayer"))
        {
            return (PcbFileKind.Copper, "");
        }

        return (PcbFileKind.Skipped, Loc.T(
            "не удалось определить слой по имени — добавьте его отдельной командой (медь или контур)",
            "could not tell the layer from the name — add it with its own command (copper or outline)"));
    }

    /// <summary>
    /// Paste, solder mask or silk screen by the usual file (or layer) name: Altium .GTP/.GTS/.GTO and the bottom
    /// ones, KiCad F_Paste / F_Mask / F_Silkscreen, EasyEDA TopPasteMaskLayer / TopSolderMaskLayer / TopSilkLayer.
    /// Null for any other name.
    /// </summary>
    public static PcbFileKind? AuxiliaryLayerKind(string fileName)
    {
        var lower = Path.GetFileName(fileName).ToLowerInvariant();
        var extension = Path.GetExtension(lower);
        // Paste first: EasyEDA calls the paste layer "PasteMask".
        if (extension is ".gtp" or ".gbp" || lower.Contains("paste"))
        {
            return PcbFileKind.Paste;
        }

        if (extension is ".gts" or ".gbs" || lower.Contains("mask"))
        {
            return PcbFileKind.SolderMask;
        }

        if (extension is ".gto" or ".gbo" || lower.Contains("silk") || lower.Contains("legend"))
        {
            return PcbFileKind.Silkscreen;
        }

        return null;
    }

    /// <summary>The file name is the usual one of a board outline (Altium .GKO / .GM1, KiCad Edge_Cuts, EasyEDA BoardOutline…).</summary>
    public static bool IsOutlineFileName(string fileName)
    {
        var lower = Path.GetFileName(fileName).ToLowerInvariant();
        return Path.GetExtension(lower) is ".gko" or ".gm1" or ".gml" or ".gmo" ||
               lower.Contains("edge_cuts") || lower.Contains("edge.cuts") || lower.Contains("boardoutline") ||
               lower.Contains("outline") || lower.Contains("profile");
    }

    /// <summary>Value of the X2 attribute <c>%TF.FileFunction,…*%</c>, or null.</summary>
    private static string? FileFunction(string content)
    {
        const string key = "%TF.FileFunction,";
        var start = content.IndexOf(key, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += key.Length;
        var end = content.IndexOf('*', start);
        return end < 0 ? null : content[start..end];
    }
}
