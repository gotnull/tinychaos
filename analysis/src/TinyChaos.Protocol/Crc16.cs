namespace TinyChaos.Protocol;

/// <summary>
/// CRC-16/CCITT-FALSE implementation.
///
/// Polynomial 0x1021, initial value 0xFFFF, no input reflection, no output
/// reflection, no final XOR. Known-answer on UTF-8 bytes of "123456789"
/// is 0x29B1. Identical algorithm to <c>tinychaos.protocol.crc16_ccitt_false</c>
/// in the Python host package and <c>crc16_ccitt_false</c> in the firmware.
/// </summary>
public static class Crc16
{
    private const ushort Polynomial = 0x1021;
    private const ushort InitialValue = 0xFFFF;

    /// <summary>
    /// Compute CRC-16/CCITT-FALSE over <paramref name="data"/>.
    /// </summary>
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = InitialValue;
        foreach (byte b in data)
        {
            crc ^= (ushort)(b << 8);
            for (int i = 0; i < 8; i++)
            {
                if ((crc & 0x8000) != 0)
                {
                    crc = (ushort)((crc << 1) ^ Polynomial);
                }
                else
                {
                    crc = (ushort)(crc << 1);
                }
            }
        }
        return crc;
    }
}
