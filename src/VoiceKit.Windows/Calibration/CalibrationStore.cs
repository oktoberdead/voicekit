using System.IO;
using System.Text.Json;
using NAudio.Wave;
using VoiceKit.Core;
using VoiceKit.Core.Calibration;

namespace VoiceKit.Windows.Calibration;

public sealed class CalibrationStore(string root)
{
    public string Root { get; } = Path.Combine(root, "Calibration");
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    public string Save(CalibrationProfile profile, float[] raw, CancellationToken ct)
    {
        if (raw.Length is < CalibrationScript.SampleRate * 4 or > CalibrationScript.MaxSamples)
            throw new ArgumentException("Недопустимая длина образца.");
        if (raw.Any(x => !float.IsFinite(x) || Math.Abs(x) > 8)) throw new ArgumentException("Некорректные сэмплы.");
        Directory.CreateDirectory(Root);
        // Always create a new profile; never overwrite the user's previous comparison.
        var id = Guid.NewGuid();
        string pending = Path.Combine(Root, ".pending-" + id.ToString("N"));
        string final = Path.Combine(Root, id.ToString("N"));
        Directory.CreateDirectory(pending);
        try
        {
            using (var writer = new WaveFileWriter(Path.Combine(pending, "sample.wav"), WaveFormat.CreateIeeeFloatWaveFormat(CalibrationScript.SampleRate, 1)))
            {
                for (int i = 0; i < raw.Length; i += 8192)
                {
                    ct.ThrowIfCancellationRequested(); writer.WriteSamples(raw, i, Math.Min(8192, raw.Length - i));
                }
                writer.Flush();
            }
            ct.ThrowIfCancellationRequested();
            File.WriteAllText(Path.Combine(pending, "profile.json"), JsonSerializer.Serialize(profile with { Id = id, SchemaVersion = 2 }, _json));
            ct.ThrowIfCancellationRequested();
            Directory.Move(pending, final); // same-volume, expose only a complete pair
            return Path.Combine(final, "profile.json");
        }
        catch { if (Directory.Exists(pending)) Directory.Delete(pending, true); throw; }
    }
    public (CalibrationProfile Profile, float[] Raw) Load(string jsonPath, CancellationToken ct)
    {
        if (new FileInfo(jsonPath).Length > 1_048_576) throw new InvalidDataException("Файл профиля слишком большой.");
        var profile = JsonSerializer.Deserialize<CalibrationProfile>(File.ReadAllText(jsonPath), _json)
            ?? throw new InvalidDataException("Пустой профиль.");
        if (profile.SchemaVersion is not (1 or 2) || profile.ScriptVersion != CalibrationScript.Version || profile.Reference is null || profile.Candidate is null)
            throw new InvalidDataException("Неподдерживаемая версия или повреждённый профиль.");
        // No file path from JSON is used. A profile can only reference the adjacent sample.wav.
        string wavPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(jsonPath))!, "sample.wav");
        if (new FileInfo(wavPath).Length > CalibrationScript.MaxSamples * 4L + 65536)
            throw new InvalidDataException("Образец больше разрешённых 38 секунд.");
        using var reader = new WaveFileReader(wavPath);
        var format = reader.WaveFormat.AsStandardWaveFormat();
        if (format.SampleRate != CalibrationScript.SampleRate || format.Channels != 1 || format.Encoding != WaveFormatEncoding.IeeeFloat || format.BitsPerSample != 32)
            throw new InvalidDataException("Ожидается образец VoiceKit: mono float32, 48 кГц.");
        long frames = reader.Length / 4;
        if (frames is < CalibrationScript.SampleRate * 4 or > CalibrationScript.MaxSamples)
            throw new InvalidDataException("Некорректная длина образца.");
        var raw = new float[(int)frames]; var bytes = new byte[8192]; int offset = 0;
        while (offset < raw.Length)
        {
            ct.ThrowIfCancellationRequested();
            int n = reader.Read(bytes, 0, Math.Min(bytes.Length, (raw.Length - offset) * 4));
            if (n == 0 || n % 4 != 0) throw new InvalidDataException("Образец обрезан.");
            Buffer.BlockCopy(bytes, 0, raw, offset * 4, n); offset += n / 4;
        }
        if (raw.Any(x => !float.IsFinite(x) || Math.Abs(x) > 8)) throw new InvalidDataException("Некорректные сэмплы в образце.");
        return (profile with
        {
            Name = string.IsNullOrWhiteSpace(profile.Name) ? "Мой голос" : profile.Name[..Math.Min(80, profile.Name.Length)],
            CaptureDeviceName = profile.CaptureDeviceName ?? "Неизвестное устройство",
            Reference = profile.Reference.Validated(), Candidate = profile.Candidate.Validated(),
            TargetMedianHz = double.IsFinite(profile.TargetMedianHz) ? Math.Clamp(profile.TargetMedianHz, 80, 350) : 180
        }, raw);
    }
}
