using System.Text.Json;
using System.Text.Json.Serialization;

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
                      ?? throw new InvalidDataException("Файл проекта пуст.");
        if (project.FormatVersion > CamProject.CurrentFormatVersion)
        {
            throw new InvalidDataException($"Проект создан более новой версией программы (формат {project.FormatVersion}).");
        }

        return project;
    }

    public static void Save(CamProject project, string path) => File.WriteAllText(path, Serialize(project));

    public static CamProject Load(string path) => Deserialize(File.ReadAllText(path));
}
