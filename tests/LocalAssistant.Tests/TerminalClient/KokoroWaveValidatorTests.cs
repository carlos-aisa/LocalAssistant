using System.Buffers.Binary;
using LocalAssistant.TerminalClient;

namespace LocalAssistant.Tests.TerminalClient;

public sealed class KokoroWaveValidatorTests
{
    [Fact]
    public void ValidatesTheExpectedKokoroPcmWave() =>
        Assert.True(KokoroWaveValidator.IsValid(CreateWave(), out var duration));

    [Fact]
    public void RejectsNonPcmStereoAndUnsupportedSampleRates()
    {
        Assert.False(KokoroWaveValidator.IsValid(CreateWave(format: 3), out _));
        Assert.False(KokoroWaveValidator.IsValid(CreateWave(channels: 2), out _));
        Assert.False(KokoroWaveValidator.IsValid(CreateWave(sampleRate: 22050), out _));
    }

    [Fact]
    public void RejectsTruncatedAndOverlongWaveData()
    {
        Assert.False(KokoroWaveValidator.IsValid("RIFF"u8.ToArray(), out _));
        Assert.False(KokoroWaveValidator.IsValid(CreateWave(dataLength: 45 * 48000 + 2), out _));
    }

    private static byte[] CreateWave(ushort format = 1, ushort channels = 1, uint sampleRate = 24000, int dataLength = 48)
    {
        var wave = new byte[44 + dataLength];
        "RIFF"u8.CopyTo(wave);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(4, 4), (uint)(wave.Length - 8));
        "WAVEfmt "u8.CopyTo(wave.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(20, 2), format);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(22, 2), channels);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(24, 4), sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(28, 4), sampleRate * channels * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(32, 2), (ushort)(channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(34, 2), 16);
        "data"u8.CopyTo(wave.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(40, 4), (uint)dataLength);
        return wave;
    }
}
