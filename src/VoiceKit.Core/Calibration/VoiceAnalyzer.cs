namespace VoiceKit.Core.Calibration;

public sealed record VoiceAnalysis(double DurationSeconds, double PeakDbfs, double SpeechRmsDbfs,
    double NoiseRmsDbfs, int ClippedSamples, double VoicedSeconds, double VoicedFraction,
    double? MedianF0Hz, double? LowF0Hz, double? HighF0Hz, double Periodicity,
    bool CanSuggestPitch, string[] Warnings);
public sealed record PitchSuggestion(double Semitones, double UnclampedSemitones, bool Limited);

/// <summary>Offline YIN-style periodicity estimator. No model, identity/gender score or formant inference.</summary>
public static class VoiceAnalyzer
{
    private const int Rate = 12000, Window = 768, Hop = 240, MinLag = 24, MaxLag = 200;
    public static VoiceAnalysis Analyze(float[] samples, CancellationToken ct = default)
    {
        if (samples.Length < CalibrationScript.SampleRate * 4 || samples.Length > CalibrationScript.MaxSamples)
            throw new ArgumentException("Нужны минимум 3 секунды фона и 1 секунда речи; максимум 38 секунд.");
        double peak = 0, noiseEnergy = 0, speechEnergy = 0;
        int clipped = 0, noiseCount = CalibrationScript.NoiseSeconds * CalibrationScript.SampleRate, invalid = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
            double value = samples[i];
            if (!double.IsFinite(value)) { invalid++; value = 0; }
            peak = Math.Max(peak, Math.Abs(value));
            if (Math.Abs(value) >= .999) clipped++;
            if (i < noiseCount) noiseEnergy += value * value;
            else speechEnergy += value * value;
        }
        double noiseRms = Math.Sqrt(noiseEnergy / noiseCount);
        double speechRms = Math.Sqrt(speechEnergy / (samples.Length - noiseCount));
        // Cascaded one-pole low-pass (~1.5 kHz each) before decimation. This is only an F0 detector path.
        var down = new double[samples.Length / 4];
        double low1 = 0, low2 = 0, alpha = 1 - Math.Exp(-2 * Math.PI * 1500 / CalibrationScript.SampleRate);
        for (int i = 0; i < samples.Length; i++)
        {
            if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
            double x = float.IsFinite(samples[i]) ? samples[i] : 0;
            low1 += alpha * (x - low1); low2 += alpha * (low1 - low2);
            if ((i & 3) == 3) down[i / 4] = low2;
        }
        var pitches = new List<double>();
        var certainties = new List<double>();
        var difference = new double[MaxLag + 1];
        int frames = 0;
        double gate = Math.Max(.00316, noiseRms * 3.16); // at least -50 dBFS, ~10 dB above measured noise
        for (int start = CalibrationScript.NoiseSeconds * Rate; start + Window <= down.Length; start += Hop)
        {
            ct.ThrowIfCancellationRequested(); frames++;
            double mean = 0, energy = 0;
            for (int i = 0; i < Window; i++) mean += down[start + i];
            mean /= Window;
            for (int i = 0; i < Window; i++) { double d = down[start + i] - mean; energy += d * d; }
            if (Math.Sqrt(energy / Window) < gate) continue;
            double sum = 0;
            difference[0] = 1;
            for (int lag = 1; lag <= MaxLag; lag++)
            {
                double d = 0;
                for (int j = 0; j < Window - MaxLag; j++)
                {
                    double delta = down[start + j] - down[start + j + lag]; d += delta * delta;
                }
                sum += d;
                difference[lag] = sum > 1e-20 ? d * lag / sum : 1;
            }
            int best = -1;
            for (int lag = MinLag; lag <= MaxLag; lag++)
            {
                if (difference[lag] >= .15) continue;
                while (lag < MaxLag && difference[lag + 1] < difference[lag]) lag++;
                best = lag; break; // first periodic valley, not a multiple of the fundamental
            }
            if (best < 0) continue;
            double offset = 0;
            if (best > MinLag && best < MaxLag)
            {
                double denominator = difference[best - 1] - 2 * difference[best] + difference[best + 1];
                if (Math.Abs(denominator) > 1e-12)
                    offset = Math.Clamp(.5 * (difference[best - 1] - difference[best + 1]) / denominator, -1, 1);
            }
            double hz = Rate / (best + offset);
            if (hz < 60 || hz > 500) continue;
            pitches.Add(hz); certainties.Add(1 - difference[best]);
        }
        pitches.Sort(); certainties.Sort();
        double voicedSeconds = pitches.Count * Hop / (double)Rate;
        double? median = pitches.Count == 0 ? null : Percentile(pitches, .5);
        double? low = pitches.Count == 0 ? null : Percentile(pitches, .1);
        double? high = pitches.Count == 0 ? null : Percentile(pitches, .9);
        double confidence = certainties.Count == 0 ? 0 : Percentile(certainties, .5);
        double clippedFraction = (double)clipped / samples.Length;
        bool broad = low.HasValue && high.HasValue && high.Value / low.Value > 2;
        bool usable = voicedSeconds >= 2 && median.HasValue && clippedFraction < .001 && invalid == 0 && !broad;
        var warnings = new List<string>();
        // Allow a small resampler tail loss rather than labelling a complete take as manually interrupted.
        if (samples.Length < CalibrationScript.MaxSamples - CalibrationScript.SampleRate / 10) warnings.Add("Запись остановлена раньше конца сценария; доступна только записанная часть.");
        if (clipped > 0) warnings.Add("Есть сэмплы около цифрового предела. Уменьши усиление микрофона и повтори запись.");
        if (Db(noiseRms) > -40) warnings.Add("Фон заметный. Проверь вентиляторы, расстояние до микрофона и обработку Windows; первые 3 секунды нужно молчать.");
        if (Db(speechRms) < -35) warnings.Add("Речь записана тихо. Попробуй приблизить микрофон; не усиливай шум вслепую.");
        if (voicedSeconds < 2) warnings.Add("Мало уверенно тональных участков. Подбор питча недоступен: повтори гласные и обычную речь.");
        if (broad) warnings.Add("Очень широкий разброс F0: возможны октавные ошибки или неравномерное произношение. Автоподбор отключён.");
        if (invalid > 0) warnings.Add("Некорректные сэмплы в записи. Автоподбор отключён.");
        warnings.Add("F0 — приблизительная оценка 60–500 Гц. Периодичность не является гарантией точности; шёпот/хрип могут не определиться. Форманты не измеряются.");
        return new((double)samples.Length / CalibrationScript.SampleRate, Db(peak), Db(speechRms), Db(noiseRms), clipped,
            voicedSeconds, frames == 0 ? 0 : (double)pitches.Count / frames, median, low, high, confidence, usable, warnings.ToArray());
    }
    public static PitchSuggestion Suggest(VoiceAnalysis analysis, double targetMedianHz)
    {
        if (!analysis.CanSuggestPitch || analysis.MedianF0Hz is not { } median || !double.IsFinite(median) || median <= 0)
            throw new InvalidOperationException("Недостаточно надёжного материала для рекомендации питча.");
        if (!double.IsFinite(targetMedianHz) || targetMedianHz is < 80 or > 350)
            throw new ArgumentOutOfRangeException(nameof(targetMedianHz), "Целевая медиана: 80–350 Гц.");
        double shift = 12 * Math.Log2(targetMedianHz / median);
        return new(Math.Round(Math.Clamp(shift, -12, 12), 1), shift, Math.Abs(shift) > 12);
    }
    public static double Db(double amplitude) => Math.Max(-120, 20 * Math.Log10(Math.Max(1e-6, amplitude)));
    private static double Percentile(List<double> sorted, double p)
    {
        double index = (sorted.Count - 1) * p; int lo = (int)index;
        return sorted[lo] + (sorted[Math.Min(lo + 1, sorted.Count - 1)] - sorted[lo]) * (index - lo);
    }
}
