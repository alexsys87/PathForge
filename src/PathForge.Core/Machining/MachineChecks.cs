using PathForge.Core.Localization;
using PathForge.Core.Projects;

namespace PathForge.Core.Machining;

/// <summary>Warnings about programs the machine cannot run as intended.</summary>
internal static class MachineChecks
{
    public static void Apply(CamProject project, GenerationResult result)
    {
        var machine = project.Machine;
        foreach (var tool in result.Toolpaths.Select(t => t.Tool).DistinctBy(t => t.Id))
        {
            if (machine.MaxFeedRate > 0 && Math.Max(tool.FeedRate, tool.PlungeRate) > machine.MaxFeedRate)
            {
                result.Warnings.Add(Loc.T($"Подача инструмента «{tool.Name}» больше максимальной для станка ({machine.MaxFeedRate:0} мм/мин): контроллер её ограничит.", $"The feed of the tool “{tool.Name}” exceeds the machine maximum ({machine.MaxFeedRate:0} mm/min): the controller will limit it."));
            }

            if (machine.SpindleMaxS > 0 && tool.Kind != ToolKind.Laser && tool.SpindleRpm > machine.SpindleMaxRpm)
            {
                result.Warnings.Add(Loc.T($"Обороты инструмента «{tool.Name}» ({tool.SpindleRpm:0}) больше максимума шпинделя ({machine.SpindleMaxRpm:0}): будет S{machine.SpindleMaxS:0}.", $"The speed of the tool “{tool.Name}” ({tool.SpindleRpm:0}) exceeds the spindle maximum ({machine.SpindleMaxRpm:0}): S{machine.SpindleMaxS:0} will be used."));
            }
        }

        var laserPaths = result.Toolpaths.Where(t => t.Tool.Kind == ToolKind.Laser).ToList();
        if (laserPaths.Count > 0 && !machine.LaserMode)
        {
            result.Warnings.Add(Loc.T("Лазерные операции при профиле фрезера: выберите профиль лазера (в GRBL должно быть $32=1).", "Laser operations with a milling profile: choose a laser profile (GRBL needs $32=1)."));
        }

        if (machine.LaserMode && laserPaths.Count < result.Toolpaths.Count)
        {
            result.Warnings.Add(Loc.T("Профиль лазера, а в проекте есть фрезерные операции: отводов по Z не будет.", "Laser profile, but the project has milling operations: there will be no Z retracts."));
        }

        var moves = result.Toolpaths.SelectMany(t => t.Moves).Select(m => m.Target).ToList();
        if (moves.Count > 0)
        {
            CheckAxis("X", moves.Max(p => p.X) - moves.Min(p => p.X), machine.WorkAreaX, result);
            CheckAxis("Y", moves.Max(p => p.Y) - moves.Min(p => p.Y), machine.WorkAreaY, result);
            CheckAxis("Z", moves.Max(p => p.Z) - moves.Min(p => p.Z), machine.WorkAreaZ, result);
        }

        foreach (var toolpath in result.Toolpaths)
        {
            var depthBelowTop = -toolpath.Operation.BottomZ;
            if (depthBelowTop > project.Stock.Thickness + 0.5)
            {
                result.Warnings.Add(Loc.T($"{toolpath.Operation.Name}: глубина {depthBelowTop:0.##} мм больше толщины заготовки {project.Stock.Thickness:0.##} мм — фреза уйдёт в стол.", $"{toolpath.Operation.Name}: the depth {depthBelowTop:0.##} mm exceeds the stock thickness {project.Stock.Thickness:0.##} mm — the tool will cut into the table."));
            }
        }
    }

    private static void CheckAxis(string axis, double size, double travel, GenerationResult result)
    {
        if (travel > 0 && size > travel + 1e-6)
        {
            result.Warnings.Add(Loc.T($"Программа занимает по {axis} {size:0.#} мм, а ход станка {travel:0.#} мм.", $"The program spans {size:0.#} mm in {axis}, but the machine travel is {travel:0.#} mm."));
        }
    }
}
