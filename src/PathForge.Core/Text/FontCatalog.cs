namespace PathForge.Core.Text;

/// <summary>Font available for text: file, index inside a collection and display name.</summary>
public sealed record FontEntry(string Path, int Index, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Finds TrueType fonts in folders and caches loaded fonts.</summary>
public static class FontCatalog
{
    private static readonly Dictionary<(string Path, int Index), (DateTime Stamp, TrueTypeFont Font)> Cache = new();
    private static readonly object CacheLock = new();

    /// <summary>Fonts with TrueType outlines in the given folders, sorted by name. Unreadable files are skipped.</summary>
    public static List<FontEntry> Scan(IEnumerable<string> folders)
    {
        var entries = new List<FontEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder)
                    .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".ttf" or ".ttc")
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                try
                {
                    var data = File.ReadAllBytes(file);
                    for (var index = 0; index < TrueTypeFont.FontCount(data); index++)
                    {
                        var font = TrueTypeFont.Load(data, index);
                        var name = font.DisplayName.Length > 0 ? font.DisplayName : Path.GetFileNameWithoutExtension(file);
                        if (seen.Add(name))
                        {
                            entries.Add(new FontEntry(file, index, name));
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException
                                               or NotSupportedException or IndexOutOfRangeException or ArgumentException)
                {
                    // Not a usable TrueType font.
                }
            }
        }

        entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return entries;
    }

    /// <summary>Loads a font, reusing the parsed font while the file is unchanged.</summary>
    public static TrueTypeFont Load(string path, int index)
    {
        var stamp = File.GetLastWriteTimeUtc(path);
        lock (CacheLock)
        {
            if (Cache.TryGetValue((path, index), out var cached) && cached.Stamp == stamp)
            {
                return cached.Font;
            }
        }

        var font = TrueTypeFont.LoadFile(path, index);
        lock (CacheLock)
        {
            Cache[(path, index)] = (stamp, font);
        }

        return font;
    }
}
