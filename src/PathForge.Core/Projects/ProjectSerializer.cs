using System.Text.Json;
using System.Text.Json.Serialization;
using PathForge.Core.Localization;

namespace PathForge.Core.Projects;

/// <summary>Reads and writes projects as JSON (*.pfproj).</summary>
public static class ProjectSerializer
{
    public const string FileExtension = ".pfproj";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(CamProject project) => JsonSerializer.Serialize(project, Options);

    public static CamProject Deserialize(string json)
    {
        var project = JsonSerializer.Deserialize<CamProject>(json, Options)
                      ?? throw new InvalidDataException(Loc.T("Файл проекта пуст.", "The project file is empty."));
        if (project.FormatVersion > CamProject.CurrentFormatVersion)
        {
            throw new InvalidDataException(Loc.T($"Проект создан более новой версией программы (формат {project.FormatVersion}).", $"The project was created by a newer version of the program (format {project.FormatVersion})."));
        }

        return project;
    }

    /// <summary>Independent copy of an operation (with a new id), e.g. to base a rest machining pass on it.</summary>
    public static Machining.Operation CloneOperation(Machining.Operation operation)
    {
        var copy = JsonSerializer.Deserialize<Machining.Operation>(JsonSerializer.Serialize(operation, Options), Options)!;
        copy.Id = Guid.NewGuid().ToString("N");
        return copy;
    }

    public static void Save(CamProject project, string path) => File.WriteAllText(path, Serialize(project));

    public static CamProject Load(string path) => Deserialize(File.ReadAllText(path));
}
