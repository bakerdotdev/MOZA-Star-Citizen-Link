using NAudio.Dsp;

namespace MozaStarCitizen.App.Telemetry.Audio;

/// <summary>
/// Normalized force-feedback signals derived from one analysis window of audio.
/// All values are 0..1 except <see cref="EngineFrequencyHz"/>.
/// </summary>
public readonly record struct AudioFeatures(
    double EngineRumble,
    double EngineFrequencyHz,
    double Atmosphere,
    double Boost,
    double Impact,
    double WeaponFire,
    double Afterburner);

/// <summary>
/// Turns a mono PCM stream into <see cref="AudioFeatures"/> at a fixed cadence.
///
/// Sustained signals (engine rumble, atmosphere) come from band RMS mapped
/// through a dB window. Transient signals (impact, weapon fire) come from
/// onset flux on a <em>spectrally whitened</em> spectrum, so a broadband
/// loudness ramp (engine spool-up / throttle-up) is not mistaken for a
/// transient — only sharp, localized spectral change is. A single
/// mutually-exclusive decision then attributes each onset to impact OR weapon,
/// never both.
///
/// This class is single-threaded by contract: feed it from one capture thread.
/// </summary>
public sealed class AudioTelemetryAnalyzer
{
    private const int FftSize = 2048;       // ~43 ms window at 48 kHz
    private const int Hop = 1024;           // 50% overlap -> ~47 windows/sec
    private const int FftOrder = 11;        // log2(FftSize)

    // dB windows for the sustained bands. Absolute offset depends on the FFT
    // scaling below; the per-user gain knob compensates for mix level.
    private const double EngineFloorDb = -70;
    private const double EngineCeilDb = -30;

    // The 300-2500 Hz "air" band carries BOTH atmospheric wind and the
    // afterburner roar; the two are told apart below.
    private const double AirLoHz = 300;
    private const double AirHiHz = 2500;

    // Afterburner vs atmospheric wind, via two VOLUME-INDEPENDENT measures (the
    // user's absolute level swings ~20 dB between sessions, so absolute dB
    // thresholds are useless):
    //   * Spectral TILT = airDb - engineDb (air-band loudness RELATIVE to the
    //     engine band). Labeled clips: quiet space cruise ~-23 dB, atmospheric
    //     wind ~-17 dB, afterburner roar ~-12 dB. Below TiltFloor = quiet cruise
    //     (no air signal -> silent); ramps to full by TiltCeil.
    //   * Spectral FLATNESS of the air band: the afterburner is a broadband NOISE
    //     roar (flat, ~0.68) while wind is comparatively tonal (~0.25). Above
    //     AirFlatSplit -> afterburner; below -> atmospheric wind. (Cruise can be
    //     incidentally flat, but its tilt is too low to register, so it's safe.)
    private const double TiltFloorDb = -19.0;
    private const double TiltCeilDb = -10.5;
    private const double AirFlatSplit = 0.48;
    private const double SilenceFloorDb = -90; // both bands below this = silence; don't trust the tilt ratio

    // Whitened-onset detection for impact (low thud) and weapon (mid-high crack).
    private const double ImpactLoHz = 40;
    private const double ImpactHiHz = 200;
    private const double WeaponLoHz = 1500;
    private const double WeaponHiHz = 6000;
    private const double WhitenRate = 0.08;      // per-bin spectral-envelope tracking (~260 ms)
    private const double WhitenFloorFrac = 0.004; // denominator floor as a fraction of the loudest bin,
                                                  // so quiet bins aren't amplified into phantom onsets
    private const double OnsetThreshold = 1.40;  // whitened flux per bin to count as an onset
    private const double OnsetScale = 4.0;       // flux above threshold mapped to 0..1 over this span
    private const double ImpactDominance = 1.6;  // impact must clearly out-flux the weapon band (a real thud,
                                                 // not a weapon/afterburner transient that merely has some lows)
    private const double ImpactEnergyFloor = 3e-4; // absolute band RMS gate (silence guard)
    private const double WeaponEnergyFloor = 1e-4;
    private const int RefractoryWindows = 4;     // ~85 ms suppression after an onset

    // A throttle-up / engine spool is a SUSTAINED broadband rise; a real impact
    // or shot is a brief spike. Suppress onsets while the engine envelope has
    // been climbing for several consecutive windows (a ramp), which a 1-2 window
    // transient never trips.
    private const double EngineRampDelta = 0.015;
    private const int EngineRampGateWindows = 3;

    private readonly int _sampleRate;
    private readonly Complex[] _fft = new Complex[FftSize];
    private readonly double[] _window = new double[FftSize];
    private readonly float[] _magnitude = new float[(FftSize / 2) + 1];
    private readonly float[] _whitened = new float[(FftSize / 2) + 1];
    private readonly float[] _prevWhitened = new float[(FftSize / 2) + 1];
    private readonly float[] _specAvg = new float[(FftSize / 2) + 1];

    private float[] _buffer = new float[FftSize * 4];
    private int _count;
    private bool _hasPrev;
    private int _refractory;

    private double _engineEnv;
    private double _prevEngineEnv;
    private int _engineRampWindows;
    private double _afterburnerEnv;
    private double _airEnv;

    public AudioTelemetryAnalyzer(int sampleRate)
    {
        _sampleRate = sampleRate <= 0 ? 48000 : sampleRate;
        for (var i = 0; i < FftSize; i++)
        {
            _window[i] = FastFourierTransform.HannWindow(i, FftSize);
        }
    }

    /// <summary>Windows/sec emitted, for diagnostics.</summary>
    public double FramesPerSecond => (double)_sampleRate / Hop;

    public double LastEngineDb { get; private set; } = double.NegativeInfinity;

    public double LastAirDb { get; private set; } = double.NegativeInfinity;

    public double LastAirFlatness { get; private set; }

    /// <summary>Whitened onset flux in the impact band for the last window.</summary>
    public double LastImpactRatio { get; private set; }

    /// <summary>Whitened onset flux in the weapon band for the last window.</summary>
    public double LastWeaponRatio { get; private set; }

    /// <summary>
    /// Appends mono samples and invokes <paramref name="emit"/> once per
    /// completed analysis window.
    /// </summary>
    public void Add(ReadOnlySpan<float> mono, Action<AudioFeatures> emit)
    {
        if (mono.IsEmpty)
        {
            return;
        }

        if (_count + mono.Length > _buffer.Length)
        {
            var capacity = _buffer.Length;
            while (capacity < _count + mono.Length)
            {
                capacity *= 2;
            }

            Array.Resize(ref _buffer, capacity);
        }

        mono.CopyTo(_buffer.AsSpan(_count));
        _count += mono.Length;

        while (_count >= FftSize)
        {
            emit(Analyze(_buffer.AsSpan(0, FftSize)));

            var remaining = _count - Hop;
            Array.Copy(_buffer, Hop, _buffer, 0, remaining);
            _count = remaining;
        }
    }

    private AudioFeatures Analyze(ReadOnlySpan<float> samples)
    {
        for (var i = 0; i < FftSize; i++)
        {
            _fft[i].X = (float)(samples[i] * _window[i]);
            _fft[i].Y = 0f;
        }

        // NAudio's forward FFT already scales the result by 1/N, so the
        // magnitude here is amplitude-normalized (a full-scale tone reads ~0.5
        // at its bin). No further division by FftSize.
        FastFourierTransform.FFT(true, FftOrder, _fft);

        for (var k = 0; k < _magnitude.Length; k++)
        {
            var re = _fft[k].X;
            var im = _fft[k].Y;
            _magnitude[k] = (float)Math.Sqrt((re * re) + (im * im));
        }

        // --- Engine rumble (sustained low-frequency band) ---
        var engineDb = ToDb(BandRms(30, 160));
        LastEngineDb = engineDb;
        var engineRaw = MapDb(engineDb, EngineFloorDb, EngineCeilDb);
        _engineEnv = engineRaw > _engineEnv
            ? Lerp(_engineEnv, engineRaw, 0.40)
            : Lerp(_engineEnv, engineRaw, 0.08);

        // Track a sustained engine ramp (throttle-up / spool) to gate onsets.
        _engineRampWindows = (_engineEnv - _prevEngineEnv) > EngineRampDelta ? _engineRampWindows + 1 : 0;
        _prevEngineEnv = _engineEnv;

        // --- Air band: afterburner roar vs atmospheric wind ---
        var airDb = ToDb(BandRms(AirLoHz, AirHiHz));
        LastAirDb = airDb;
        var flatness = BandFlatness(AirLoHz, AirHiHz);
        LastAirFlatness = flatness;

        // Volume-independent "air present" measure: how loud the air band is
        // relative to the engine band. Quiet space cruise sits below TiltFloor
        // (air far under engine) and yields nothing; wind and afterburner rise
        // above it. Guarded so true silence doesn't produce a garbage ratio.
        var audioPresent = engineDb > SilenceFloorDb || airDb > SilenceFloorDb;
        var airPresence = audioPresent
            ? Clamp01((airDb - engineDb - TiltFloorDb) / (TiltCeilDb - TiltFloorDb))
            : 0.0;

        // Flatness routes the air signal: flat broadband roar -> afterburner,
        // tonal -> atmospheric wind. Mutually exclusive per window.
        var isRoar = flatness > AirFlatSplit;
        var afterburnerTarget = isRoar ? airPresence : 0.0;
        var atmosphereTarget = isRoar ? 0.0 : airPresence;

        // Afterburner: fast attack / fairly fast release so it tracks the throttle
        // (no lag, no lingering tail). Atmosphere: a touch smoother — a sustained
        // texture, not a surge.
        _afterburnerEnv = Lerp(_afterburnerEnv, afterburnerTarget, afterburnerTarget > _afterburnerEnv ? 0.30 : 0.18);
        _airEnv = Lerp(_airEnv, atmosphereTarget, atmosphereTarget > _airEnv ? 0.20 : 0.08);

        var (impact, weapon) = DetectOnsets();

        var engineHz = _engineEnv > 0.04 ? EngineFrequency() : 0.0;

        return new AudioFeatures(
            EngineRumble: _engineEnv,
            EngineFrequencyHz: engineHz,
            Atmosphere: _airEnv,
            // Boost (a discrete kick) isn't inferred from audio.
            Boost: 0.0,
            Impact: impact,
            WeaponFire: weapon,
            Afterburner: _afterburnerEnv);
    }

    /// <summary>
    /// Whiten the spectrum (divide each bin by its slowly-tracked envelope),
    /// measure positive onset flux in the impact and weapon bands, and fire at
    /// most one of them. Whitening makes a gradual broadband rise (engine
    /// spool) produce little flux while a sharp transient still spikes.
    /// </summary>
    private (double Impact, double Weapon) DetectOnsets()
    {
        if (!_hasPrev)
        {
            // Seed the envelope and the previous-whitened buffer on the first
            // window so onsets aren't fabricated from a cold start.
            for (var k = 0; k < _magnitude.Length; k++)
            {
                _specAvg[k] = _magnitude[k];
                _whitened[k] = 1f;
                _prevWhitened[k] = 1f;
            }

            _hasPrev = true;
            return (0.0, 0.0);
        }

        // Floor the whitening denominator at a fraction of the loudest bin so
        // near-silent bins aren't divided into huge phantom onsets.
        var maxAvg = 1e-9f;
        for (var k = 0; k < _specAvg.Length; k++)
        {
            if (_specAvg[k] > maxAvg)
            {
                maxAvg = _specAvg[k];
            }
        }

        var floor = (float)WhitenFloorFrac * maxAvg;
        for (var k = 0; k < _magnitude.Length; k++)
        {
            _whitened[k] = _magnitude[k] / Math.Max(_specAvg[k], floor);
            _specAvg[k] = (float)Lerp(_specAvg[k], _magnitude[k], WhitenRate);
        }

        var impactFlux = WhitenedFlux(ImpactLoHz, ImpactHiHz);
        var weaponFlux = WhitenedFlux(WeaponLoHz, WeaponHiHz);
        LastImpactRatio = impactFlux;
        LastWeaponRatio = weaponFlux;

        var impact = 0.0;
        var weapon = 0.0;

        // Gate out onsets during a sustained engine ramp (throttle-up/spool).
        var ramping = _engineRampWindows >= EngineRampGateWindows;

        if (_refractory <= 0 && !ramping)
        {
            var impactHit = impactFlux > OnsetThreshold && BandRms(ImpactLoHz, ImpactHiHz) > ImpactEnergyFloor;
            var weaponHit = weaponFlux > OnsetThreshold && BandRms(WeaponLoHz, WeaponHiHz) > WeaponEnergyFloor;

            if (impactHit && weaponHit && impactFlux >= weaponFlux * ImpactDominance)
            {
                // A real collision is a BROADBAND thud: both the low and weapon
                // bands cross the onset threshold, with the low band dominant.
                // An engine/afterburner whoosh is low-ONLY (weapon band stays
                // below threshold) so it's excluded here; a weapon is high-
                // dominant so it falls through to the weapon branch.
                impact = Clamp01((impactFlux - OnsetThreshold) / OnsetScale);
                _refractory = RefractoryWindows;
            }
            else if (weaponHit)
            {
                weapon = Clamp01((weaponFlux - OnsetThreshold) / OnsetScale);
                _refractory = RefractoryWindows;
            }
        }
        else if (_refractory > 0)
        {
            _refractory--;
        }

        Array.Copy(_whitened, _prevWhitened, _whitened.Length);
        return (impact, weapon);
    }

    private double EngineFrequency()
    {
        var lo = Bin(30);
        var hi = Bin(160);
        double weighted = 0;
        double total = 0;
        for (var k = lo; k <= hi; k++)
        {
            var hz = (double)k * _sampleRate / FftSize;
            weighted += hz * _magnitude[k];
            total += _magnitude[k];
        }

        if (total <= 0)
        {
            return 0;
        }

        var centroid = weighted / total;
        // Fold the audio-domain centroid (30-160 Hz) into a tactile range.
        return Math.Clamp(centroid * 0.30, 12, 55);
    }

    private float BandRms(double loHz, double hiHz)
    {
        var lo = Bin(loHz);
        var hi = Bin(hiHz);
        double sumSq = 0;
        for (var k = lo; k <= hi; k++)
        {
            sumSq += (double)_magnitude[k] * _magnitude[k];
        }

        var bins = hi - lo + 1;
        return bins <= 0 ? 0f : (float)Math.Sqrt(sumSq / bins);
    }

    /// <summary>
    /// Spectral flatness (geometric mean / arithmetic mean) over a band: ~1 for
    /// flat noise (air rush), near 0 for tonal/peaky content (engine, weapons).
    /// </summary>
    private double BandFlatness(double loHz, double hiHz)
    {
        var lo = Bin(loHz);
        var hi = Bin(hiHz);
        double logSum = 0;
        double sum = 0;
        var n = 0;
        for (var k = lo; k <= hi; k++)
        {
            var m = _magnitude[k] + 1e-9;
            logSum += Math.Log(m);
            sum += m;
            n++;
        }

        if (n == 0 || sum <= 0)
        {
            return 0;
        }

        var geo = Math.Exp(logSum / n);
        var arith = sum / n;
        return Clamp01(geo / arith);
    }

    private double WhitenedFlux(double loHz, double hiHz)
    {
        var lo = Bin(loHz);
        var hi = Bin(hiHz);
        double flux = 0;
        for (var k = lo; k <= hi; k++)
        {
            var diff = _whitened[k] - _prevWhitened[k];
            if (diff > 0)
            {
                flux += diff;
            }
        }

        var bins = hi - lo + 1;
        return bins <= 0 ? 0 : flux / bins;
    }

    private int Bin(double hz)
    {
        var bin = (int)Math.Round(hz * FftSize / _sampleRate);
        return Math.Clamp(bin, 1, FftSize / 2);
    }

    private static double ToDb(double linear) => 20.0 * Math.Log10(linear + 1e-9);

    private static double MapDb(double db, double floorDb, double ceilDb) =>
        Clamp01((db - floorDb) / (ceilDb - floorDb));

    private static double Lerp(double current, double target, double amount) =>
        current + ((target - current) * amount);

    private static double Clamp01(double value) =>
        Math.Clamp(double.IsFinite(value) ? value : 0, 0, 1);
}
