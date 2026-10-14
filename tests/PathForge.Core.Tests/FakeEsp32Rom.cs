using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Dlc32Flasher;

namespace PathForge.Core.Tests;

/// <summary>
/// An ESP32 ROM bootloader behind a USB-serial chip with the usual auto-reset circuit, as far as the flasher uses it:
/// SLIP framing, SYNC, registers (chip magic, SPI flash ID, clock calibration), SPI attach and parameters, speed change,
/// erase and write of the flash with checksums, MD5 and the 64-byte reads. Faults can be switched on to test the error paths.
/// </summary>
internal sealed class FakeEsp32Rom : IFlasherPort
{
    private readonly Queue<byte> _toHost = new();
    private readonly List<byte> _frame = new();
    private readonly Dictionary<uint, uint> _registers = new();
    private bool _inFrame;
    private bool _escaped;
    private bool _dtr;
    private bool _rts;
    private bool _inReset;
    private uint _writeOffset;
    private int _blockSize;
    private int _expectedSequence = -1;

    public FakeEsp32Rom(int flashSize)
    {
        Flash = new byte[flashSize];
        Flash.AsSpan().Fill(0xFF);
        var capacityCode = (uint)Math.Log2(flashSize);
        FlashId = (capacityCode << 16) | (0x40u << 8) | 0x20; // XMC-like ID: manufacturer 0x20, type 0x40
        _registers[EspRomLoader.RtcCaliConfig] = 800u << 7; // with the 8 MHz clock code 128: a 40 MHz crystal
        _registers[EspRomLoader.EfuseWord4] = 128;
    }

    public byte[] Flash { get; }

    public uint FlashId { get; set; }

    public uint Magic { get; set; } = 0x00F01D83;

    /// <summary>The chip runs its ROM bootloader (and answers the protocol).</summary>
    public bool InBootloader { get; set; }

    /// <summary>DTR does not reach GPIO0: a reset starts the firmware, which prints its boot log.</summary>
    public bool Gpio0NotWired { get; set; }

    /// <summary>The lines are not wired at all: resets do nothing.</summary>
    public bool LinesNotWired { get; set; }

    /// <summary>The block with this sequence number fails its checksum once.</summary>
    public int ChecksumErrorAtBlock { get; set; } = -1;

    /// <summary>One byte of the flash is changed after the write: the MD5 differs.</summary>
    public bool CorruptAfterWrite { get; set; }

    /// <summary>Called for every block written (to cancel in the middle, for example).</summary>
    public Action<int>? OnBlock { get; set; }

    public int BaudRate { get; set; } = 115200;

    public uint RomBaudAsked { get; private set; }

    public long SizeSet { get; private set; }

    public bool Attached { get; private set; }

    public int ResetsIntoBootloader { get; private set; }

    public int HardResets { get; private set; }

    public int CommandCount { get; private set; }

    public bool Disposed { get; private set; }

    public void SetRegister(uint address, uint value) => _registers[address] = value;

    public void SetLines(bool dtr, bool rts)
    {
        if (LinesNotWired)
        {
            return;
        }

        if (rts && !_dtr)
        {
            _inReset = true; // EN low
        }
        else if (_inReset && !rts)
        {
            // EN released: GPIO0 low (DTR) starts the bootloader, otherwise the firmware.
            _inReset = false;
            var bootloader = dtr && !Gpio0NotWired;
            if (bootloader)
            {
                InBootloader = true;
                ResetsIntoBootloader++;
                Send(Encoding.ASCII.GetBytes("rst:0x1 (POWERON_RESET),boot:0x3 (DOWNLOAD_BOOT(UART0/UART1/SDIO_REI_REO_V2))\r\nwaiting for download\r\n"));
            }
            else
            {
                InBootloader = false;
                HardResets++;
                Send(Encoding.ASCII.GetBytes("rst:0x1 (POWERON_RESET),boot:0x13 (SPI_FAST_FLASH_BOOT)\r\n"));
            }
        }

        _dtr = dtr;
        _rts = rts;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            if (!_inFrame)
            {
                if (b == 0xC0)
                {
                    _inFrame = true;
                    _frame.Clear();
                }

                continue;
            }

            if (_escaped)
            {
                _escaped = false;
                _frame.Add(b == 0xDC ? (byte)0xC0 : b == 0xDD ? (byte)0xDB : b);
            }
            else if (b == 0xDB)
            {
                _escaped = true;
            }
            else if (b == 0xC0)
            {
                if (_frame.Count > 0)
                {
                    _inFrame = false;
                    Handle(_frame.ToArray());
                }
            }
            else
            {
                _frame.Add(b);
            }
        }
    }

    public int Read(Span<byte> buffer, TimeSpan timeout)
    {
        if (_toHost.Count == 0)
        {
            Thread.Sleep(1);
            return 0;
        }

        var count = 0;
        while (count < buffer.Length && _toHost.Count > 0)
        {
            buffer[count++] = _toHost.Dequeue();
        }

        return count;
    }

    public void DiscardInput() => _toHost.Clear();

    public void Dispose() => Disposed = true;

    private void Handle(byte[] packet)
    {
        if (!InBootloader || packet.Length < 8 || packet[0] != 0)
        {
            return;
        }

        CommandCount++;
        var op = packet[1];
        var size = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2));
        var checksum = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4));
        var data = packet.AsSpan(8, size).ToArray();
        uint U(int at) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));

        switch (op)
        {
            case EspRomLoader.CmdSync:
                for (var i = 0; i < 8; i++)
                {
                    Respond(op, 0x20120707);
                }

                break;
            case EspRomLoader.CmdReadReg:
                var address = U(0);
                Respond(op, address == EspRomLoader.ChipMagicAddress ? Magic : _registers.GetValueOrDefault(address));
                break;
            case EspRomLoader.CmdWriteReg:
                _registers[U(0)] = U(4);
                if (U(0) == EspRomLoader.SpiCmd && (U(4) & EspRomLoader.SpiCmdUsr) != 0)
                {
                    // The SPI controller runs the user command at once: RDID puts the JEDEC ID into W0.
                    var command = _registers.GetValueOrDefault(EspRomLoader.SpiUsr2) & 0xFF;
                    _registers[EspRomLoader.SpiW0] = command == 0x9F ? FlashId : 0;
                    _registers[EspRomLoader.SpiCmd] = 0;
                }

                Respond(op);
                break;
            case EspRomLoader.CmdSpiAttach:
                Attached = size == 8;
                Respond(op, status: Attached ? (byte)0 : (byte)1, error: 0x01);
                break;
            case EspRomLoader.CmdSpiSetParams:
                SizeSet = U(4);
                Respond(op);
                break;
            case EspRomLoader.CmdChangeBaud:
                RomBaudAsked = U(0);
                Respond(op);
                break;
            case EspRomLoader.CmdFlashBegin:
                if (!Attached)
                {
                    Respond(op, status: 1, error: 0x06);
                    break;
                }

                var eraseSize = (int)U(0);
                _blockSize = (int)U(8);
                _writeOffset = U(12);
                var eraseEnd = Math.Min(Flash.Length, (int)_writeOffset + ((eraseSize + 0xFFF) / 0x1000 * 0x1000));
                Flash.AsSpan((int)_writeOffset, eraseEnd - (int)_writeOffset).Fill(0xFF);
                _expectedSequence = 0;
                Respond(op);
                break;
            case EspRomLoader.CmdFlashData:
                var length = (int)U(0);
                var sequence = (int)U(4);
                var block = data.AsSpan(16, length);
                if (sequence == ChecksumErrorAtBlock)
                {
                    ChecksumErrorAtBlock = -1;
                    Respond(op, status: 1, error: 0x07);
                    break;
                }

                if (EspRomLoader.Checksum(block) != checksum || sequence != _expectedSequence)
                {
                    Respond(op, status: 1, error: sequence != _expectedSequence ? (byte)0x01 : (byte)0x07);
                    break;
                }

                var at = (int)_writeOffset + (sequence * _blockSize);
                var fit = Math.Min(length, Flash.Length - at);
                for (var i = 0; i < fit; i++)
                {
                    Flash[at + i] &= block[i]; // NOR flash: writing only clears bits
                }

                _expectedSequence++;
                OnBlock?.Invoke(sequence);
                if (CorruptAfterWrite)
                {
                    Flash[at] ^= 0x01;
                    CorruptAfterWrite = false;
                }

                Respond(op);
                break;
            case EspRomLoader.CmdSpiFlashMd5:
                var digest = MD5.HashData(Flash.AsSpan((int)U(0), (int)U(4)));
                Respond(op, payload: Encoding.ASCII.GetBytes(Convert.ToHexString(digest).ToLowerInvariant()));
                break;
            case EspRomLoader.CmdReadFlashSlow:
                var block64 = new byte[64];
                Flash.AsSpan((int)U(0), (int)U(4)).CopyTo(block64);
                Respond(op, payload: block64);
                break;
            default:
                // Unknown command: the ROM answers with status 1 and error 0x05 under another code.
                Respond(0xFF, status: 1, error: 0x05);
                break;
        }
    }

    private void Respond(byte op, uint value = 0, byte[]? payload = null, byte status = 0, byte error = 0)
    {
        payload ??= Array.Empty<byte>();
        var body = new byte[8 + payload.Length + 4];
        body[0] = 0x01;
        body[1] = op;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), (ushort)(payload.Length + 4));
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), value);
        payload.CopyTo(body, 8);
        body[8 + payload.Length] = status;
        body[9 + payload.Length] = error;

        var frame = new List<byte> { 0xC0 };
        foreach (var b in body)
        {
            if (b == 0xC0)
            {
                frame.Add(0xDB);
                frame.Add(0xDC);
            }
            else if (b == 0xDB)
            {
                frame.Add(0xDB);
                frame.Add(0xDD);
            }
            else
            {
                frame.Add(b);
            }
        }

        frame.Add(0xC0);
        Send(frame.ToArray());
    }

    private void Send(byte[] bytes)
    {
        foreach (var b in bytes)
        {
            _toHost.Enqueue(b);
        }
    }
}
