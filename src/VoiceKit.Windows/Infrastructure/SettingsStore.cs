using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using VoiceKit.Core;

namespace VoiceKit.Windows.Infrastructure;

public sealed class SettingsStore
{
    public string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceKit");
    public string LibraryPath => Path.Combine(Root, "Library");
    private string SettingsPath => Path.Combine(Root, "settings.json");
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public SettingsStore() => Directory.CreateDirectory(LibraryPath);
    public AppSettings Load(out string? warning)
    {
        warning = null;
        if (!File.Exists(SettingsPath)) return new();
        try
        {
            var result = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), _json)
                ?? throw new InvalidDataException("Пустой файл настроек.");
            if (result.SchemaVersion != 1) throw new InvalidDataException("Неизвестная версия настроек.");
            if (result.Library is null || result.Presets is null || result.Hotkeys is null || result.Audio is null)
                throw new InvalidDataException("Неполные настройки.");
            if (result.Library.Any(x => x is null || x.Behavior is null || string.IsNullOrWhiteSpace(x.StoredFile)) ||
                result.Library.Select(x => x.Id).Distinct().Count() != result.Library.Count ||
                result.Presets.Any(x => x is null || x.Effects is null) || result.Hotkeys.Any(x => x is null))
                throw new InvalidDataException("Некорректная библиотека или привязки.");
            foreach (var entry in result.Library) ResolveLibraryFile(entry.StoredFile);
            return result with { Audio = result.Audio.Validated(), BufferMs = Math.Clamp(result.BufferMs, 10, 100) };
        }
        catch (Exception e) when (e is JsonException or IOException or InvalidDataException or ArgumentException)
        {
            string backup = SettingsPath + ".invalid-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Copy(SettingsPath, backup, true);
            warning = $"Настройки не прочитаны: {e.Message}\nИсходник сохранён: {backup}";
            return new();
        }
    }
    public void Save(AppSettings settings)
    {
        string temp = SettingsPath + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, settings, _json);
            stream.Flush(true);
        }
        if (File.Exists(SettingsPath)) File.Replace(temp, SettingsPath, SettingsPath + ".bak");
        else File.Move(temp, SettingsPath);
    }
    public string ResolveLibraryFile(string file)
    {
        if (string.IsNullOrWhiteSpace(file) || file is "." or ".." || file != Path.GetFileName(file) || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || file.Contains('\\') || file.Contains('/'))
            throw new InvalidDataException("Некорректный путь звука в библиотеке.");
        return Path.Combine(LibraryPath, file);
    }
    public string CopyToLibrary(string source, Guid id)
    {
        string filename = id.ToString("N") + Path.GetExtension(source).ToLowerInvariant();
        File.Copy(source, ResolveLibraryFile(filename), false);
        return filename;
    }
}
