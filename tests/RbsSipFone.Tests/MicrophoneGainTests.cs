using RbsSipFone.Services;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using Xunit;

namespace RbsSipFone.Tests;

public class MicrophoneGainTests
{
    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(6, 1.995)]
    [InlineData(12, 3.981)]
    [InlineData(20, 10.0)]
    [InlineData(-5, 1.0)]
    [InlineData(99, 10.0)]
    public void Factor_converts_and_clamps_db(int db, double expected) =>
        Assert.Equal(expected, MicrophoneGain.Factor(db), 2);

    [Fact]
    public void Quiet_samples_are_amplified_linearly()
    {
        var factor = MicrophoneGain.Factor(12);
        Assert.Equal((short)Math.Round(1000 * factor), MicrophoneGain.Process(1000, factor));
        Assert.Equal((short)Math.Round(-1000 * factor), MicrophoneGain.Process(-1000, factor));
        Assert.Equal(0, MicrophoneGain.Process(0, factor));
    }

    [Fact]
    public void Limiter_never_clips_and_keeps_order()
    {
        var factor = MicrophoneGain.Factor(20);
        short previous = short.MinValue;
        for (int s = short.MinValue; s <= short.MaxValue; s += 7)
        {
            var y = MicrophoneGain.Process((short)s, factor);
            Assert.True(y >= previous, $"nicht monoton bei {s}");
            previous = y;
        }
        Assert.InRange(MicrophoneGain.Process(short.MaxValue, factor), 30000, short.MaxValue);
        Assert.InRange(MicrophoneGain.Process(short.MinValue, factor), short.MinValue, -30000);
    }

    [Fact]
    public void No_gain_leaves_samples_untouched()
    {
        short[] pcm = [100, -2000, 32767, -32768];
        MicrophoneGain.Apply(pcm, MicrophoneGain.Factor(0));
        Assert.Equal(new short[] { 100, -2000, 32767, -32768 }, pcm);
    }

    [Fact]
    public void Encoder_boosts_only_the_microphone_path()
    {
        var inner = new AudioEncoder();
        var pcmu = inner.SupportedFormats.First(f => f.Codec == AudioCodecsEnum.PCMU);
        var factor = 4f;
        var encoder = new GainAudioEncoder(inner, () => factor);

        short[] pcm = Enumerable.Range(0, 160).Select(i => (short)(Math.Sin(i / 5.0) * 2000)).ToArray();
        var original = (short[])pcm.Clone();
        var expected = (short[])pcm.Clone();
        MicrophoneGain.Apply(expected, factor);

        Assert.Equal(inner.EncodeAudio(expected, pcmu), encoder.EncodeAudio(pcm, pcmu));
        Assert.Equal(original, pcm); // Eingangspuffer bleibt unverändert

        var encoded = inner.EncodeAudio(original, pcmu);
        Assert.Equal(inner.DecodeAudio(encoded, pcmu), encoder.DecodeAudio(encoded, pcmu)); // Wiedergabe unverändert

        factor = 1f; // Änderung wirkt sofort
        Assert.Equal(inner.EncodeAudio(original, pcmu), encoder.EncodeAudio(pcm, pcmu));
    }
}
