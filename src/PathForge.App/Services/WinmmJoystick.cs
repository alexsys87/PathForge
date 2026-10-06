using System.Runtime.InteropServices;
using PathForge.Core.Grbl;

namespace PathForge.App.Services;

/// <summary>
/// Joystick or gamepad read through the Windows multimedia API (winmm): it sees any HID joystick and game
/// controller, Xbox-compatible pads included, without extra drivers or libraries.
/// Left stick (X/Y axes) or the D-pad moves X/Y with a speed proportional to the deflection;
/// buttons 5 and 6 (LB/RB on an Xbox pad) move Z down and up.
/// </summary>
public sealed class WinmmJoystick
{
    /// <summary>Deflection ignored around the centre: worn sticks rarely return exactly to zero.</summary>
    private const double DeadZone = 0.2;

    private const int JoyErrNoError = 0;
    private const uint JoyReturnAll = 0xFF;
    private const uint PovCentered = 0xFFFF;
    private const uint ZDownButton = 1 << 4;
    private const uint ZUpButton = 1 << 5;

    private static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(2);

    private int _id = -1;
    private JoyCaps _caps;
    private DateTime _nextScan = DateTime.MinValue;

    /// <summary>Name of the found joystick, or null.</summary>
    public string? Name { get; private set; }

    /// <summary>Current deflection; zero when no joystick is connected (looked for again every two seconds).</summary>
    public JogVector Read()
    {
        if (_id < 0 && !Scan())
        {
            return default;
        }

        var info = new JoyInfoEx { Size = Marshal.SizeOf<JoyInfoEx>(), Flags = JoyReturnAll };
        if (joyGetPosEx(_id, ref info) != JoyErrNoError)
        {
            // Unplugged.
            _id = -1;
            Name = null;
            return default;
        }

        var x = Axis(info.XPos, _caps.XMin, _caps.XMax);
        // Screen-style axis: pushing the stick away gives small values.
        var y = -Axis(info.YPos, _caps.YMin, _caps.YMax);
        if (info.Pov != PovCentered && info.Pov <= 36000)
        {
            var angle = info.Pov / 100.0 * Math.PI / 180;
            x = Math.Round(Math.Sin(angle), 3);
            y = Math.Round(Math.Cos(angle), 3);
        }

        var z = (info.Buttons & ZUpButton) != 0 ? 1.0 : (info.Buttons & ZDownButton) != 0 ? -1.0 : 0.0;
        return new JogVector(x, y, z);
    }

    private bool Scan()
    {
        if (DateTime.UtcNow < _nextScan)
        {
            return false;
        }

        _nextScan = DateTime.UtcNow + RescanInterval;
        try
        {
            var count = joyGetNumDevs();
            for (var id = 0; id < count; id++)
            {
                var info = new JoyInfoEx { Size = Marshal.SizeOf<JoyInfoEx>(), Flags = JoyReturnAll };
                var caps = new JoyCaps();
                if (joyGetPosEx(id, ref info) == JoyErrNoError &&
                    joyGetDevCapsW(id, ref caps, Marshal.SizeOf<JoyCaps>()) == JoyErrNoError)
                {
                    _id = id;
                    _caps = caps;
                    Name = string.IsNullOrWhiteSpace(caps.Name) ? $"Joystick {id + 1}" : caps.Name.Trim();
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // No multimedia API (stripped-down Windows): no joystick.
        }

        return false;
    }

    /// <summary>Raw axis value → −1…1 with the dead zone removed.</summary>
    private static double Axis(uint value, uint min, uint max)
    {
        if (max <= min)
        {
            return 0;
        }

        var centred = (2.0 * ((double)value - min) / ((double)max - min)) - 1;
        var magnitude = Math.Abs(centred);
        if (magnitude < DeadZone)
        {
            return 0;
        }

        return Math.Round(Math.Sign(centred) * Math.Min(1, (magnitude - DeadZone) / (1 - DeadZone)), 3);
    }

    [DllImport("winmm.dll")]
    private static extern int joyGetNumDevs();

    [DllImport("winmm.dll")]
    private static extern int joyGetPosEx(int id, ref JoyInfoEx info);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int joyGetDevCapsW(nint id, ref JoyCaps caps, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct JoyInfoEx
    {
        public int Size;
        public uint Flags;
        public uint XPos;
        public uint YPos;
        public uint ZPos;
        public uint RPos;
        public uint UPos;
        public uint VPos;
        public uint Buttons;
        public uint ButtonNumber;
        public uint Pov;
        public uint Reserved1;
        public uint Reserved2;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct JoyCaps
    {
        public ushort Mid;
        public ushort Pid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Name;
        public uint XMin;
        public uint XMax;
        public uint YMin;
        public uint YMax;
        public uint ZMin;
        public uint ZMax;
        public uint NumButtons;
        public uint PeriodMin;
        public uint PeriodMax;
        public uint RMin;
        public uint RMax;
        public uint UMin;
        public uint UMax;
        public uint VMin;
        public uint VMax;
        public uint Caps;
        public uint MaxAxes;
        public uint NumAxes;
        public uint MaxButtons;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string RegKey;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string OemVxD;
    }
}
