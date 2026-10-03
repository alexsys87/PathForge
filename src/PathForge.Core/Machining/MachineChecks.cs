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
                result.Warnings.Add($"Подача инструмента «{tool.Name}» больше максимальной для станка ({machine.MaxFeedRate:0} мм/мин): контроллер её ограничит.");
            }

            if (machine.SpindleMaxS > 0 && tool.Kind != ToolKind.Laser && tool.SpindleRpm > machine.SpindleMaxRpm)
            {
                result.Warnings.Add($"Обороты инструмента «{tool.Name}» ({tool.SpindleRpm:0}) больше максимума шпинделя ({machine.SpindleMaxRpm:0}): будет S{machine.SpindleMaxS:0}.");
            }
        }

        var laserPaths = result.Toolpaths.Where(t => t.Tool.Kind == ToolKind.Laser).ToList();
        if (laserPaths.Count > 0 && !machine.LaserMode)
        {
            result.Warnings.Add("Лазерные операции при профиле фрезера: выберите профиль лазера (в GRBL должно быть $32=1).");
        }

        if (machine.LaserMode && laserPaths.Count < result.Toolpaths.Count)
        {
            result.Warnings.Add("Профиль лазера, а в проекте есть фрезерные операции: отводов по Z не будет.");
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
                result.Warnings.Add($"{toolpath.Operation.Name}: глубина {depthBelowTop:0.##} мм больше толщины заготовки {project.Stock.Thickness:0.##} мм — фреза уйдёт в стол.");
            }
        }
    }

    private static void CheckAxis(string axis, double size, double travel, GenerationResult result)
    {
        if (travel > 0 && size > travel + 1e-6)
        {
            result.Warnings.Add($"Программа занимает по {axis} {size:0.#} мм, а ход станка {travel:0.#} мм.");
        }
    }
}
