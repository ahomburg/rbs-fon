using SIPSorceryMedia.Abstractions;

namespace RbsSipFone.Services;

/// <summary>
/// Software-Verstärkung für das Mikrofon (0 bis +20 dB) mit weichem Begrenzer: Leise Signale werden linear verstärkt,
/// laute ab ca. 70 % Vollaussteuerung sanft abgeflacht, damit die Stimme nicht hart übersteuert.
/// </summary>
public static class MicrophoneGain
{
    public const int MaxDb = 20;

    /// <summary>Ab diesem Pegel (Anteil der Vollaussteuerung) setzt der Begrenzer ein.</summary>
    public const float Knee = 0.7f;

    private const float FullScale = 32767f;
    private const float KneeLevel = Knee * FullScale;
    private const float Headroom = FullScale - KneeLevel;

    /// <summary>dB-Wert → linearer Faktor (z. B. +6 dB ≈ 2,0×).</summary>
    public static float Factor(int db) => (float)Math.Pow(10, Math.Clamp(db, 0, MaxDb) / 20.0);

    public static string Describe(int db) =>
        db <= 0 ? "Aus (0 dB)" : $"+{db} dB ({Factor(db).ToString("0.0", System.Globalization.CultureInfo.GetCultureInfo("de-DE"))}×)";

    public static short Process(short sample, float factor)
    {
        var y = sample * factor;
        var magnitude = MathF.Abs(y);
        if (magnitude > KneeLevel)
        {
            magnitude = KneeLevel + Headroom * MathF.Tanh((magnitude - KneeLevel) / Headroom);
            y = MathF.CopySign(magnitude, y);
        }
        return (short)Math.Clamp(MathF.Round(y), short.MinValue, short.MaxValue);
    }

    public static void Apply(Span<short> pcm, float factor)
    {
        if (factor <= 1.0001f) return;
        for (var i = 0; i < pcm.Length; i++) pcm[i] = Process(pcm[i], factor);
    }
}

/// <summary>
/// Verstärkt die Mikrofon-Samples, bevor sie kodiert werden. Die Wiedergabe (Dekodieren) bleibt unverändert.
/// Der Faktor wird bei jedem Audioblock neu gelesen, Änderungen in den Einstellungen wirken also sofort.
/// </summary>
public sealed class GainAudioEncoder : IAudioEncoder
{
    private readonly IAudioEncoder _inner;
    private readonly Func<float> _factor;

    public GainAudioEncoder(IAudioEncoder inner, Func<float> factor)
    {
        _inner = inner;
        _factor = factor;
    }

    public List<AudioFormat> SupportedFormats => _inner.SupportedFormats;

    public byte[] EncodeAudio(short[] pcm, AudioFormat format)
    {
        var factor = _factor();
        if (factor > 1.0001f)
        {
            var boosted = (short[])pcm.Clone();
            MicrophoneGain.Apply(boosted, factor);
            pcm = boosted;
        }
        return _inner.EncodeAudio(pcm, format);
    }

    public short[] DecodeAudio(byte[] encodedSample, AudioFormat format) => _inner.DecodeAudio(encodedSample, format);
}
