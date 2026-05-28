using System.Globalization;
using TinyChaos.Protocol;

namespace TinyChaos.Host.Export;

/// <summary>
/// Write decoded samples to a CSV file with the canonical column schema
/// shared with the Python reference exporter:
///
///   host_time, packet_seq, stm32_time_us, sample_index, channel_index,
///   adc_value, validation_label
///
/// Channel index is derived from sample_index modulo the channel count
/// (channels are interleaved in the firmware's DMA output).
/// </summary>
public sealed class CsvExporter : IDisposable
{
    public static readonly string[] Columns =
    {
        "host_time",
        "packet_seq",
        "stm32_time_us",
        "sample_index",
        "channel_index",
        "adc_value",
        "validation_label",
    };

    private readonly StreamWriter _writer;
    private readonly int _channelCount;
    private readonly string _validationLabel;

    public CsvExporter(string path, int channelCount = 2, string validationLabel = "")
    {
        if (channelCount < 1) throw new ArgumentOutOfRangeException(nameof(channelCount));
        _channelCount = channelCount;
        _validationLabel = validationLabel;
        _writer = new StreamWriter(path);
        _writer.WriteLine(string.Join(",", Columns));
    }

    public void WritePacket(Packet packet, double hostTime)
    {
        var seq = packet.Header.Seq;
        var timeUs = packet.Header.TimeUs;
        var cc = _channelCount;
        for (int idx = 0; idx < packet.Samples.Length; idx++)
        {
            _writer.Write(hostTime.ToString("F6", CultureInfo.InvariantCulture));
            _writer.Write(',');
            _writer.Write(seq);
            _writer.Write(',');
            _writer.Write(timeUs);
            _writer.Write(',');
            _writer.Write(idx);
            _writer.Write(',');
            _writer.Write(idx % cc);
            _writer.Write(',');
            _writer.Write(packet.Samples[idx]);
            _writer.Write(',');
            _writer.WriteLine(_validationLabel);
        }
    }

    public void Dispose()
    {
        _writer.Flush();
        _writer.Dispose();
    }
}
