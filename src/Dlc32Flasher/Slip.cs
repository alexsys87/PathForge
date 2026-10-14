using System.Text;

namespace Dlc32Flasher;

/// <summary>
/// SLIP framing of the ESP32 bootloader protocol: every packet is enclosed in 0xC0, and inside it 0xC0 is sent as 0xDB 0xDC
/// and 0xDB as 0xDB 0xDD.
/// </summary>
internal static class Slip
{
    public const byte End = 0xC0;
    public const byte Escape = 0xDB;
    public const byte EscapedEnd = 0xDC;
    public const byte EscapedEscape = 0xDD;

    public static byte[] Encode(ReadOnlySpan<byte> packet)
    {
        var frame = new List<byte>(packet.Length + 8) { End };
        foreach (var b in packet)
        {
            switch (b)
            {
                case End:
                    frame.Add(Escape);
                    frame.Add(EscapedEnd);
                    break;
                case Escape:
                    frame.Add(Escape);
                    frame.Add(EscapedEscape);
                    break;
                default:
                    frame.Add(b);
                    break;
            }
        }

        frame.Add(End);
        return frame.ToArray();
    }
}

/// <summary>
/// Reads SLIP frames from the port. Bytes outside of frames (the boot log the chip prints after a reset) are kept as
/// text: they tell whether the chip started the bootloader or its firmware.
/// </summary>
internal sealed class SlipReader
{
    private readonly IFlasherPort _port;
    private readonly byte[] _chunk = new byte[4096];
    private readonly List<byte> _frame = new();
    private readonly StringBuilder _text = new();
    private int _chunkStart;
    private int _chunkEnd;
    private bool _inFrame;
    private bool _escaped;

    public SlipReader(IFlasherPort port)
    {
        _port = port;
    }

    /// <summary>Text received outside of frames since the last <see cref="Reset"/> (printable characters only).</summary>
    public string Text => _text.ToString();

    /// <summary>Forgets everything half-read: the next frame starts from the next 0xC0.</summary>
    public void Reset()
    {
        _chunkStart = _chunkEnd = 0;
        _frame.Clear();
        _text.Clear();
        _inFrame = false;
        _escaped = false;
    }

    /// <summary>The next whole frame (without the delimiters and with the escapes undone), or null when none came in time.</summary>
    public byte[]? ReadFrame(TimeSpan timeout, CancellationToken token)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            while (_chunkStart < _chunkEnd)
            {
                var b = _chunk[_chunkStart++];
                if (!_inFrame)
                {
                    if (b == Slip.End)
                    {
                        _inFrame = true;
                        _frame.Clear();
                    }
                    else if (b is (byte)'\n' or (byte)'\r' or (byte)'\t' || (b >= 0x20 && b < 0x7F))
                    {
                        if (_text.Length < 4096)
                        {
                            _text.Append((char)b);
                        }
                    }

                    continue;
                }

                if (_escaped)
                {
                    _escaped = false;
                    _frame.Add(b switch
                    {
                        Slip.EscapedEnd => Slip.End,
                        Slip.EscapedEscape => Slip.Escape,
                        _ => b,
                    });
                    continue;
                }

                if (b == Slip.Escape)
                {
                    _escaped = true;
                }
                else if (b == Slip.End)
                {
                    if (_frame.Count == 0)
                    {
                        // Two delimiters in a row: the second one opens the frame.
                        continue;
                    }

                    _inFrame = false;
                    return _frame.ToArray();
                }
                else
                {
                    _frame.Add(b);
                }
            }

            token.ThrowIfCancellationRequested();
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero)
            {
                return null;
            }

            // Short waits, so that a cancellation is noticed quickly.
            var count = _port.Read(_chunk, left < TimeSpan.FromMilliseconds(100) ? left : TimeSpan.FromMilliseconds(100));
            _chunkStart = 0;
            _chunkEnd = count;
        }
    }
}
