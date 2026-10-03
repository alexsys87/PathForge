using System.Globalization;

namespace PathForge.Core.Grbl;

/// <summary>Explanations of GRBL 1.1 error and alarm numbers for the operator.</summary>
public static class GrblMessages
{
    private static readonly Dictionary<int, string> Errors = new()
    {
        [1] = "в строке есть слово без буквы",
        [2] = "неверное или пропущенное число",
        [3] = "неизвестная команда $",
        [4] = "отрицательное значение там, где нужно положительное",
        [5] = "поиск дома ($H) выключен в настройках ($22)",
        [6] = "слишком короткий импульс шага (меньше 3 мкс)",
        [7] = "ошибка чтения EEPROM, настройки сброшены по умолчанию",
        [8] = "команда $ доступна только в состоянии Idle",
        [9] = "G-code заблокирован: станок в аварии (Alarm) или в режиме перемещения кнопками",
        [10] = "мягкие пределы нельзя включить без поиска дома",
        [11] = "строка длиннее допустимой (80 символов)",
        [12] = "значение настройки превышает максимальную частоту шагов",
        [13] = "открыта дверь безопасности",
        [14] = "строка не помещается в EEPROM",
        [15] = "перемещение выходит за рабочее поле станка",
        [16] = "неверная команда перемещения $J",
        [17] = "режим лазера требует ШИМ-выхода",
        [20] = "неподдерживаемая команда G-code",
        [21] = "в строке две команды одной модальной группы",
        [22] = "не задана подача F",
        [23] = "команда требует целого значения",
        [24] = "в строке две команды, которым нужны оси",
        [25] = "слово G-code повторяется в строке",
        [26] = "команде нужны координаты X/Y/Z, а их нет",
        [27] = "номер строки N вне диапазона",
        [28] = "команде не хватает слова P или L",
        [29] = "поддерживаются только системы координат G54–G59",
        [30] = "G53 работает только с G0 или G1",
        [31] = "лишние координаты при отменённом движении (G80)",
        [32] = "у дуги G2/G3 нет координат в выбранной плоскости",
        [33] = "недопустимая цель движения (дуга невозможна или щуп уже в цели)",
        [34] = "ошибка расчёта дуги, заданной радиусом",
        [35] = "у дуги G2/G3 нет смещения центра I/J/K",
        [36] = "в строке остались неиспользованные слова",
        [37] = "G43.1 применяется только к своей оси",
        [38] = "номер инструмента слишком большой",
    };

    private static readonly Dictionary<int, string> Alarms = new()
    {
        [1] = "сработал концевик — положение потеряно, нужен поиск дома или повторная установка нуля",
        [2] = "цель движения за пределами рабочего поля (мягкие пределы); положение сохранено",
        [3] = "сброс во время движения — положение могло сбиться, проверьте ноль",
        [4] = "щуп уже замкнут перед началом измерения",
        [5] = "щуп не коснулся пластины на заданном ходе",
        [6] = "поиск дома прерван сбросом",
        [7] = "поиск дома прерван: открыта дверь",
        [8] = "поиск дома: концевик не отпустился при отъезде",
        [9] = "поиск дома: концевик не найден",
        [10] = "поиск дома: не найден второй концевик оси",
    };

    public static string Error(int code) =>
        Errors.TryGetValue(code, out var text) ? $"Ошибка {code}: {text}" : $"Ошибка {code}";

    public static string Alarm(int code) =>
        Alarms.TryGetValue(code, out var text) ? $"Авария {code}: {text}" : $"Авария {code}";

    /// <summary>Russian text for an "error:N" or "ALARM:N" response, otherwise the line itself.</summary>
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
        GrblState.Idle => "Готов",
        GrblState.Run => "Работа",
        GrblState.Hold => "Пауза",
        GrblState.Jog => "Перемещение",
        GrblState.Alarm => "Авария (нужно разблокировать)",
        GrblState.Door => "Дверь открыта",
        GrblState.Check => "Проверка программы",
        GrblState.Home => "Поиск дома",
        GrblState.Sleep => "Сон",
        _ => "Нет данных",
    };
}
