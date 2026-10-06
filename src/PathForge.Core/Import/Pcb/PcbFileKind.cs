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

    /// <summary>A layer that is not milled (solder mask, silk screen, paste, drawings) or not a fabrication file at all.</summary>
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

            return (PcbFileKind.Skipped, Loc.T($"слой «{function.Split(',')[0]}» не фрезеруется", $"the “{function.Split(',')[0]}” layer is not milled"));
        }

        // Classic names: Protel/Altium extensions, KiCad and EasyEDA layer names.
        var lower = name.ToLowerInvariant();
        if (extension is ".gko" or ".gm1" or ".gml" or ".gmo" ||
            lower.Contains("edge_cuts") || lower.Contains("edge.cuts") || lower.Contains("boardoutline") ||
            lower.Contains("outline") || lower.Contains("profile"))
        {
            return (PcbFileKind.Outline, "");
        }

        if (extension is ".gts" or ".gbs" or ".gto" or ".gbo" or ".gtp" or ".gbp" ||
            lower.Contains("mask") || lower.Contains("silk") || lower.Contains("paste") || lower.Contains("legend") ||
            lower.Contains("fab") || lower.Contains("courtyard") || lower.Contains("drawing"))
        {
            return (PcbFileKind.Skipped, Loc.T("маска, шелкография или паста не фрезеруются", "mask, silk screen and paste are not milled"));
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
