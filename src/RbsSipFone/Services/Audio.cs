using NAudio.Wave;

namespace RbsSipFone.Services;

public static class AudioDevices
{
    public const string DefaultDevice = "Windows-Standard";

    public static List<string> Microphones()
    {
        var list = new List<string> { DefaultDevice };
        for (var i = 0; i < WaveIn.DeviceCount; i++) list.Add(WaveIn.GetCapabilities(i).ProductName);
        return list;
    }

    public static List<string> Speakers()
    {
        var list = new List<string> { DefaultDevice };
        for (var i = 0; i < WaveOut.DeviceCount; i++) list.Add(WaveOut.GetCapabilities(i).ProductName);
        return list;
    }

    /// <summary>Gerätename → NAudio-Index; -1 = Windows-Standardgerät.</summary>
    public static int MicrophoneIndex(string? name)
    {
        if (string.IsNullOrEmpty(name) || name == DefaultDevice) return -1;
        for (var i = 0; i < WaveIn.DeviceCount; i++)
            if (WaveIn.GetCapabilities(i).ProductName == name) return i;
        return -1;
    }

    public static int SpeakerIndex(string? name)
    {
        if (string.IsNullOrEmpty(name) || name == DefaultDevice) return -1;
        for (var i = 0; i < WaveOut.DeviceCount; i++)
            if (WaveOut.GetCapabilities(i).ProductName == name) return i;
        return -1;
    }
}

public enum ToneKind { Ring, Ringback, CallWaiting, Dtmf }

/// <summary>Erzeugt Klingelton, Freiton, Anklopfton und DTMF-Rückmeldung lokal (ohne Audiodateien).</summary>
public sealed class TonePlayer : IDisposable
{
    private readonly object _gate = new();
    private WaveOut? _loop;
    public ToneKind? Current { get; private set; }

    public void Play(ToneKind kind, int deviceIndex)
    {
        lock (_gate)
        {
            if (Current == kind && _loop != null) return;
            StopInternal();
            try
            {
                _loop = new WaveOut { DeviceNumber = deviceIndex };
                _loop.Init(new ToneSource(kind));
                _loop.Play();
                Current = kind;
            }
            catch (Exception ex)
            {
                SipLog.Write($"Ton konnte nicht abgespielt werden: {ex.Message}");
                StopInternal();
            }
        }
    }

    public void PlayDtmf(char key, int deviceIndex)
    {
        try
        {
            var output = new WaveOut { DeviceNumber = deviceIndex };
            output.Init(new ToneSource(ToneKind.Dtmf, key));
            output.PlaybackStopped += (_, _) => output.Dispose();
            output.Play();
        }
        catch (Exception ex)
        {
            SipLog.Write($"DTMF-Ton konnte nicht abgespielt werden: {ex.Message}");
        }
    }

    public void Stop()
    {
        lock (_gate) StopInternal();
    }

    private void StopInternal()
    {
        Current = null;
        if (_loop == null) return;
        try { _loop.Stop(); } catch { }
        _loop.Dispose();
        _loop = null;
    }

    public void Dispose() => Stop();

    private sealed class ToneSource : ISampleProvider
    {
        private const int Rate = 16000;
        private readonly ToneKind _kind;
        private readonly double _f1, _f2;
        private long _n;
        private double _p1, _p2;

        public ToneSource(ToneKind kind, char dtmfKey = '0')
        {
            _kind = kind;
            (_f1, _f2) = kind == ToneKind.Dtmf ? DtmfFrequencies(dtmfKey) : (0, 0);
        }

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1);

        public int Read(Span<float> buffer)
        {
            for (var i = 0; i < buffer.Length; i++)
            {
                var t = (double)_n / Rate;
                if (_kind == ToneKind.Dtmf && t >= 0.14) return i;
                buffer[i] = (float)Sample(t);
                _n++;
            }
            return buffer.Length;
        }

        private double Sample(double t)
        {
            switch (_kind)
            {
                case ToneKind.Ring:
                {
                    // Zwei kurze Doppeltriller pro 3 s, Wechsel 1050/1350 Hz alle 40 ms.
                    var c = t % 3.0;
                    var on = (c < 0.4) ? Envelope(c, 0.4) : (c >= 0.6 && c < 1.0) ? Envelope(c - 0.6, 0.4) : 0;
                    var f = ((int)(t / 0.04) % 2 == 0) ? 1050 : 1350;
                    return 0.22 * on * Osc(ref _p1, f);
                }
                case ToneKind.Ringback:
                {
                    // Deutscher Freiton: 425 Hz, 1 s Ton / 4 s Pause.
                    var c = t % 5.0;
                    return 0.15 * (c < 1.0 ? Envelope(c, 1.0) : 0) * Osc(ref _p1, 425);
                }
                case ToneKind.CallWaiting:
                {
                    var c = t % 5.0;
                    var on = c < 0.2 ? Envelope(c, 0.2) : (c >= 0.4 && c < 0.6) ? Envelope(c - 0.4, 0.2) : 0;
                    return 0.12 * on * Osc(ref _p1, 425);
                }
                default:
                    return 0.12 * Envelope(t, 0.14) * (Osc(ref _p1, _f1) + Osc(ref _p2, _f2));
            }
        }

        private static double Osc(ref double phase, double f)
        {
            phase += 2 * Math.PI * f / Rate;
            if (phase > 2 * Math.PI) phase -= 2 * Math.PI;
            return Math.Sin(phase);
        }

        /// <summary>Weiche Ein-/Ausblendung (8 ms), damit es nicht knackt.</summary>
        private static double Envelope(double t, double length)
        {
            const double fade = 0.008;
            if (t < fade) return t / fade;
            if (t > length - fade) return Math.Max(0, (length - t) / fade);
            return 1;
        }

        private static (double, double) DtmfFrequencies(char key) => key switch
        {
            '1' => (697, 1209), '2' => (697, 1336), '3' => (697, 1477),
            '4' => (770, 1209), '5' => (770, 1336), '6' => (770, 1477),
            '7' => (852, 1209), '8' => (852, 1336), '9' => (852, 1477),
            '*' => (941, 1209), '0' => (941, 1336), '#' => (941, 1477),
            _ => (941, 1336)
        };
    }
}
