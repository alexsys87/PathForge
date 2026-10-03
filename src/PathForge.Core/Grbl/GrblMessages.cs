using System.Globalization;
using PathForge.Core.Localization;

namespace PathForge.Core.Grbl;

/// <summary>Explanations of GRBL 1.1 error and alarm numbers for the operator.</summary>
public static class GrblMessages
{
    private static readonly Dictionary<int, (string Ru, string En)> Errors = new()
    {
        [1] = ("в строке есть слово без буквы", "a word in the line has no letter"),
        [2] = ("неверное или пропущенное число", "invalid or missing number"),
        [3] = ("неизвестная команда $", "unknown $ command"),
        [4] = ("отрицательное значение там, где нужно положительное", "negative value where a positive one is expected"),
        [5] = ("поиск дома ($H) выключен в настройках ($22)", "homing ($H) is disabled in the settings ($22)"),
        [6] = ("слишком короткий импульс шага (меньше 3 мкс)", "step pulse too short (less than 3 µs)"),
        [7] = ("ошибка чтения EEPROM, настройки сброшены по умолчанию", "EEPROM read failed, settings restored to defaults"),
        [8] = ("команда $ доступна только в состоянии Idle", "the $ command is only available in the Idle state"),
        [9] = ("G-code заблокирован: станок в аварии (Alarm) или в режиме перемещения кнопками", "G-code locked: the machine is in alarm or jogging"),
        [10] = ("мягкие пределы нельзя включить без поиска дома", "soft limits cannot be enabled without homing"),
        [11] = ("строка длиннее допустимой (80 символов)", "line longer than allowed (80 characters)"),
        [12] = ("значение настройки превышает максимальную частоту шагов", "setting value exceeds the maximum step rate"),
        [13] = ("открыта дверь безопасности", "safety door is open"),
        [14] = ("строка не помещается в EEPROM", "line does not fit into EEPROM"),
        [15] = ("перемещение выходит за рабочее поле станка", "jog target is outside the machine travel"),
        [16] = ("неверная команда перемещения $J", "invalid $J jog command"),
        [17] = ("режим лазера требует ШИМ-выхода", "laser mode requires a PWM output"),
        [20] = ("неподдерживаемая команда G-code", "unsupported G-code command"),
        [21] = ("в строке две команды одной модальной группы", "two commands of the same modal group in one line"),
        [22] = ("не задана подача F", "feed rate F is not set"),
        [23] = ("команда требует целого значения", "the command requires an integer value"),
        [24] = ("в строке две команды, которым нужны оси", "two commands in the line need axis words"),
        [25] = ("слово G-code повторяется в строке", "a G-code word is repeated in the line"),
        [26] = ("команде нужны координаты X/Y/Z, а их нет", "the command needs X/Y/Z coordinates, but there are none"),
        [27] = ("номер строки N вне диапазона", "line number N out of range"),
        [28] = ("команде не хватает слова P или L", "the command is missing a P or L word"),
        [29] = ("поддерживаются только системы координат G54–G59", "only the G54–G59 coordinate systems are supported"),
        [30] = ("G53 работает только с G0 или G1", "G53 works only with G0 or G1"),
        [31] = ("лишние координаты при отменённом движении (G80)", "extra axis words with motion cancelled (G80)"),
        [32] = ("у дуги G2/G3 нет координат в выбранной плоскости", "G2/G3 arc has no coordinates in the selected plane"),
        [33] = ("недопустимая цель движения (дуга невозможна или щуп уже в цели)", "invalid motion target (impossible arc or probe already at target)"),
        [34] = ("ошибка расчёта дуги, заданной радиусом", "radius-format arc calculation error"),
        [35] = ("у дуги G2/G3 нет смещения центра I/J/K", "G2/G3 arc has no I/J/K centre offset"),
        [36] = ("в строке остались неиспользованные слова", "unused words left in the line"),
        [37] = ("G43.1 применяется только к своей оси", "G43.1 applies only to its own axis"),
        [38] = ("номер инструмента слишком большой", "tool number too large"),
    };

    private static readonly Dictionary<int, (string Ru, string En)> Alarms = new()
    {
        [1] = ("сработал концевик — положение потеряно, нужен поиск дома или повторная установка нуля", "hard limit triggered — position lost, home the machine or set zero again"),
        [2] = ("цель движения за пределами рабочего поля (мягкие пределы); положение сохранено", "motion target beyond the work area (soft limits); position kept"),
        [3] = ("сброс во время движения — положение могло сбиться, проверьте ноль", "reset while in motion — position may be lost, check zero"),
        [4] = ("щуп уже замкнут перед началом измерения", "probe already triggered before the cycle started"),
        [5] = ("щуп не коснулся пластины на заданном ходе", "probe did not touch the plate within the travel"),
        [6] = ("поиск дома прерван сбросом", "homing aborted by reset"),
        [7] = ("поиск дома прерван: открыта дверь", "homing aborted: door opened"),
        [8] = ("поиск дома: концевик не отпустился при отъезде", "homing: limit switch did not release on pull-off"),
        [9] = ("поиск дома: концевик не найден", "homing: limit switch not found"),
        [10] = ("поиск дома: не найден второй концевик оси", "homing: second limit switch of the axis not found"),
    };

    public static string Error(int code) =>
        Errors.TryGetValue(code, out var text)
            ? Loc.T($"Ошибка {code}: {text.Ru}", $"Error {code}: {text.En}")
            : Loc.T($"Ошибка {code}", $"Error {code}");

    public static string Alarm(int code) =>
        Alarms.TryGetValue(code, out var text)
            ? Loc.T($"Авария {code}: {text.Ru}", $"Alarm {code}: {text.En}")
            : Loc.T($"Авария {code}", $"Alarm {code}");

    /// <summary>Operator text for an "error:N" or "ALARM:N" response, otherwise the line itself.</summary>
    public static string Describe(string response)
    {
        if (TryCode(response, "error:", out var error))
        {
            return Error(error);
        }

        return TryCode(response, "ALARM:", out var alarm) ? Alarm(alarm) : response;
    }

    internal static bool TryCode(string line, string prefix, out int code)
    {
        code = 0;
        return line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
               int.TryParse(line.AsSpan(prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out code);
    }

    public static string StateName(GrblState state) => state switch
    {
        GrblState.Idle => Loc.T("Готов", "Idle"),
        GrblState.Run => Loc.T("Работа", "Running"),
        GrblState.Hold => Loc.T("Пауза", "Paused"),
        GrblState.Jog => Loc.T("Перемещение", "Jogging"),
        GrblState.Alarm => Loc.T("Авария (нужно разблокировать)", "Alarm (unlock needed)"),
        GrblState.Door => Loc.T("Дверь открыта", "Door open"),
        GrblState.Check => Loc.T("Проверка программы", "Check mode"),
        GrblState.Home => Loc.T("Поиск дома", "Homing"),
        GrblState.Sleep => Loc.T("Сон", "Sleep"),
        _ => Loc.T("Нет данных", "No data"),
    };
}
