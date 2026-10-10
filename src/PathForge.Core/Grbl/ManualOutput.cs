using System.Globalization;
using PathForge.Core.Localization;
using PathForge.Core.Machining;

namespace PathForge.Core.Grbl;

/// <summary>
/// The spindle or the laser switched by hand on the control panel, outside any program.
/// <para>
/// A spindle takes <c>M3 S…</c> and <c>M5</c>. A laser needs more: in GRBL laser mode ($32=1) the parser passes the
/// power on only while the modal motion is G1, G2 or G3 (gcode.c, "laser mode"; Grbl_Esp32 and FluidNC do the same).
/// A plain <c>M3 S…</c> after a reset, when the modal motion is G0, switches the output on with zero power: the
/// status shows A:S but there is no beam. <c>G1</c> without axis words moves nothing and makes the power count; it
/// needs a feed rate (error:22 otherwise). Switching off with <c>G0 M5</c> also puts the modal motion back to G0,
/// so that a line typed later without a G word cannot move the machine with the beam on.
/// </para>
/// </summary>
public static class ManualOutput
{
    /// <summary>Feed rate written with the laser-on command: G1 needs one although nothing moves.</summary>
    private const string LaserFeed = "100";

    /// <summary>The command that switches the spindle or the beam off.</summary>
    public static string Off(bool laser) => laser ? "G0 M5" : "M5";

    /// <summary>
    /// The command that switches the output on. For a laser <paramref name="value"/> is the power in percent of the
    /// maximum (0 &lt; value ≤ 100), written as the S word of the machine (S1000 when no range is configured).
    /// For a spindle it is the speed in rpm, scaled to the S word exactly as the program's M3 is.
    /// </summary>
    /// <exception cref="InvalidOperationException">The value is out of range.</exception>
    public static string On(bool laser, double value, MachineSettings machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        if (laser)
        {
            if (!double.IsFinite(value) || value <= 0 || value > 100)
            {
                throw new InvalidOperationException(Loc.T("Мощность лазера — от 0 до 100 %.", "Laser power must be from 0 to 100 %."));
            }

            var maxS = machine.SpindleMaxS > 0 ? machine.SpindleMaxS : 1000;
            // At least S1: S0 would switch the beam off while the output is meant to be on.
            var power = Math.Max(1, Math.Round(value / 100 * maxS));
            return $"G1 F{LaserFeed} M3 S" + power.ToString("0", CultureInfo.InvariantCulture);
        }

        if (!double.IsFinite(value) || value <= 0)
        {
            throw new InvalidOperationException(Loc.T("Обороты шпинделя должны быть больше нуля.", "Spindle speed must be greater than zero."));
        }

        var word = Math.Max(1, machine.SpindleWord(value));
        return "M3 S" + word.ToString("0", CultureInfo.InvariantCulture);
    }
}
