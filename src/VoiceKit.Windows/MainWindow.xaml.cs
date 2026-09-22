using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using VoiceKit.Core;
using VoiceKit.Windows.Audio;
using VoiceKit.Windows.Infrastructure;

namespace VoiceKit.Windows;

public partial class MainWindow : Window
{
    private readonly SettingsStore _store = new();
    private AppSettings _settings = new();
    private readonly Dictionary<Guid, AudioClip> _clips = [];
    private AudioClip? _carrier;
    private AudioEngine? _engine;
    private HotkeyService? _hotkeys;
    private TrayIcon? _tray;
    private readonly DispatcherTimer _diagnostics = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private bool _ready, _applying, _exit, _assetsBusy, _initialized;
    private Guid? _mouseHeld;
    private List<HotkeyRow> _hotkeyRows = [];

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(RangeBase.ValueChangedEvent, new RoutedPropertyChangedEventHandler<double>(AudioSliderChanged));
        AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => AudioChanged()));
        AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler((_, _) => AudioChanged()));
        _diagnostics.Tick += (_, _) => UpdateDiagnostics();
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); TrySave(); };
    }
    private void WindowSourceInitialized(object? sender, EventArgs e)
    {
        _tray = new TrayIcon(new WindowInteropHelper(this).Handle, RestoreWindow, Exit);
        _hotkeys = new HotkeyService(Dispatcher, HandleBinding);
    }
    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        StartButton.IsEnabled = false;
        try
        {
            _settings = _store.Load(out var warning);
            RemotePortBox.Text = Math.Clamp(_settings.RemotePort, 1024, 65535).ToString();
            BufferBox.ItemsSource = new[] { 10, 15, 20, 30, 50, 80, 100 };
            BufferBox.SelectedItem = _settings.BufferMs;
            if (BufferBox.SelectedIndex < 0) BufferBox.SelectedItem = 30;
            RefreshDevices();
            PopulateAudio();
            RefreshLibrary();
            RefreshPresets();
            _hotkeyRows = _settings.Hotkeys.Select(b => new HotkeyRow(b.Command, b.Gesture)).ToList();
            HotkeyGrid.ItemsSource = _hotkeyRows;
            try { ConfigureHotkeys(_settings); }
            catch (Exception ex) { ShowError("Хоткеи не активированы", ex); }
            _ready = true;
            SystemEvents.PowerModeChanged += PowerChanged;
            _diagnostics.Start();
            if (warning is not null) MessageBox.Show(this, warning, "Восстановление настроек");
            await LoadAssets();
            if (!_exit) Status.Text = "Готово. Выбери устройства и запусти аудио.";
        }
        catch (Exception ex) { ShowError("Не удалось инициализировать приложение", ex); }
        finally { if (!_exit) StartButton.IsEnabled = _ready; }
    }
    private async Task LoadAssets()
    {
        _assetsBusy = true;
        SetAssetButtons();
        var errors = new List<string>();
        try
        {
            foreach (var item in _settings.Library)
            {
                if (_exit) return;
                try
                {
                    var clip = await Task.Run(() => ClipLoader.Load(_store.ResolveLibraryFile(item.StoredFile), item.Id));
                    CheckBudget(clip, false);
                    _clips[item.Id] = clip;
                }
                catch (Exception ex) { errors.Add(item.Name + ": " + ex.Message); }
            }
            if (_settings.CarrierFile is { } file)
            {
                try { await LoadCarrier(file); }
                catch (Exception ex) { errors.Add("Несущая: " + ex.Message); }
            }
        }
        finally { _assetsBusy = false; SetAssetButtons(); }
        if (errors.Count > 0 && !_exit)
            MessageBox.Show(this, string.Join("\n", errors), "Некоторые файлы не загружены");
    }
    private void SetAssetButtons()
    {
        ImportButton.IsEnabled = CarrierSelectButton.IsEnabled = !_assetsBusy;
    }
    private void CheckBudget(AudioClip clip, bool replacingCarrier)
    {
        long bytes = _clips.Values.Sum(x => (long)x.Samples.Length * sizeof(float));
        if (!replacingCarrier) bytes += (long)(_carrier?.Samples.Length ?? 0) * sizeof(float);
        bytes += (long)clip.Samples.Length * sizeof(float);
        if (bytes > ClipLoader.MaxCacheBytes) throw new InvalidOperationException("Лимит аудиокэша 384 МиБ. Удали ненужные звуки.");
    }
    private void RefreshDevices()
    {
        string? capture = (CaptureDeviceBox.SelectedItem as AudioDevice)?.Id ?? _settings.CaptureId;
        string? output = (OutputDeviceBox.SelectedItem as AudioDevice)?.Id ?? _settings.OutputId;
        string? monitor = (MonitorDeviceBox.SelectedItem as AudioDevice)?.Id ?? _settings.MonitorId;
        var inputs = AudioEngine.Devices(DataFlow.Capture);
        var outputs = AudioEngine.Devices(DataFlow.Render);
        CaptureDeviceBox.ItemsSource = inputs;
        OutputDeviceBox.ItemsSource = outputs;
        MonitorDeviceBox.ItemsSource = new[] { new AudioDevice("", "Не использовать прослушивание") }.Concat(outputs).ToList();
        CaptureDeviceBox.SelectedItem = capture is not null ? inputs.FirstOrDefault(x => x.Id == capture)
            : inputs.FirstOrDefault(x => !x.Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase));
        OutputDeviceBox.SelectedItem = output is not null ? outputs.FirstOrDefault(x => x.Id == output)
            : outputs.FirstOrDefault(x => x.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase));
        MonitorDeviceBox.SelectedItem = ((IEnumerable<AudioDevice>)MonitorDeviceBox.ItemsSource).FirstOrDefault(x => x.Id == monitor);
        if (MonitorDeviceBox.SelectedIndex < 0) MonitorDeviceBox.SelectedIndex = 0;
    }
    private void RefreshDevicesClick(object sender, RoutedEventArgs e)
    {
        try { RefreshDevices(); }
        catch (Exception ex) { ShowError("Список устройств недоступен", ex); }
    }
    private void ReadRouting()
    {
        _settings = _settings with
        {
            CaptureId = (CaptureDeviceBox.SelectedItem as AudioDevice)?.Id,
            OutputId = (OutputDeviceBox.SelectedItem as AudioDevice)?.Id,
            MonitorId = (MonitorDeviceBox.SelectedItem as AudioDevice)?.Id,
            BufferMs = BufferBox.SelectedItem is int ms ? ms : 30
        };
    }
    private void PopulateAudio()
    {
        _applying = true;
        try
        {
            var a = _settings.Audio;
            var s = a.Effects;
            EffectsEnabled.IsChecked = s.Enabled;
            PitchEnabled.IsChecked = s.PitchEnabled; Pitch.Value = s.PitchSemitones;
            WobbleEnabled.IsChecked = s.WobbleEnabled;
            WobbleMin.Value = s.WobbleMinSemitones; WobbleMax.Value = s.WobbleMaxSemitones; WobbleRate.Value = s.WobbleRateHz;
            RobotEnabled.IsChecked = s.RobotEnabled; RobotHz.Value = s.RobotHz; RobotMix.Value = s.RobotMix;
            VocoderEnabled.IsChecked = s.VocoderEnabled; CarrierKindBox.SelectedIndex = (int)s.Carrier;
            CarrierHz.Value = s.CarrierHz; CarrierChord.IsChecked = s.CarrierChord;
            CarrierGain.Value = s.CarrierGain; CarrierLoop.IsChecked = s.CarrierLoop; CarrierRestart.IsChecked = s.CarrierRestart;
            EchoEnabled.IsChecked = s.EchoEnabled; EchoMs.Value = s.EchoMs; EchoFeedback.Value = s.EchoFeedback; EchoMix.Value = s.EchoMix;
            ReverbEnabled.IsChecked = s.ReverbEnabled; ReverbSize.Value = s.ReverbSize; ReverbMix.Value = s.ReverbMix;
            MicGain.Value = a.MicGain; SoundGain.Value = a.SoundGain; OutputGain.Value = a.OutputGain;
            MicMuted.IsChecked = a.MicMuted; MonitorVoice.IsChecked = a.MonitorVoice;
            MonitorSounds.IsChecked = a.MonitorSounds; MonitorDry.IsChecked = a.MonitorDry; MonitorGain.Value = a.MonitorGain;
            CarrierLabel.Text = _settings.CarrierFile ?? "Файл не выбран";
        }
        finally { _applying = false; }
    }
    private void AudioSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _applying) return;
        _applying = true;
        try
        {
            // Moving an endpoint past its neighbour moves that neighbour too; never invert the range.
            if (ReferenceEquals(e.OriginalSource, WobbleMin) && WobbleMin.Value > WobbleMax.Value)
                WobbleMax.Value = WobbleMin.Value;
            else if (ReferenceEquals(e.OriginalSource, WobbleMax) && WobbleMax.Value < WobbleMin.Value)
                WobbleMin.Value = WobbleMax.Value;
        }
        finally { _applying = false; }
        AudioChanged();
    }
    private void AudioChanged()
    {
        if (!_ready || _applying) return;
        var effects = new EffectSettings
        {
            Enabled = EffectsEnabled.IsChecked == true,
            PitchEnabled = PitchEnabled.IsChecked == true, PitchSemitones = Pitch.Value,
            WobbleEnabled = WobbleEnabled.IsChecked == true,
            WobbleMinSemitones = WobbleMin.Value, WobbleMaxSemitones = WobbleMax.Value, WobbleRateHz = WobbleRate.Value,
            RobotEnabled = RobotEnabled.IsChecked == true, RobotHz = RobotHz.Value, RobotMix = RobotMix.Value,
            VocoderEnabled = VocoderEnabled.IsChecked == true, Carrier = (CarrierKind)Math.Max(0, CarrierKindBox.SelectedIndex),
            CarrierHz = CarrierHz.Value, CarrierChord = CarrierChord.IsChecked == true, CarrierGain = CarrierGain.Value,
            CarrierLoop = CarrierLoop.IsChecked == true, CarrierRestart = CarrierRestart.IsChecked == true,
            EchoEnabled = EchoEnabled.IsChecked == true, EchoMs = EchoMs.Value, EchoFeedback = EchoFeedback.Value, EchoMix = EchoMix.Value,
            ReverbEnabled = ReverbEnabled.IsChecked == true, ReverbSize = ReverbSize.Value, ReverbMix = ReverbMix.Value
        };
        var audio = new AudioSettings
        {
            Effects = effects, MicGain = MicGain.Value, SoundGain = SoundGain.Value, OutputGain = OutputGain.Value,
            MicMuted = MicMuted.IsChecked == true, MonitorVoice = MonitorVoice.IsChecked == true,
            MonitorSounds = MonitorSounds.IsChecked == true, MonitorDry = MonitorDry.IsChecked == true, MonitorGain = MonitorGain.Value
        }.Validated();
        _settings = _settings with { Audio = audio };
        _engine?.Graph.Publish(audio);
        MonitorWarning.Foreground = audio.MonitorDry && audio.MonitorVoice ? System.Windows.Media.Brushes.Orange : System.Windows.Media.Brushes.LightSlateGray;
        QueueSave();
    }
    private void CarrierKindChanged(object sender, SelectionChangedEventArgs e) => AudioChanged();
    private void QueueSave() { if (!_ready || _exit) return; _saveTimer.Stop(); _saveTimer.Start(); }
    private void TrySave()
    {
        if (!_ready) return;
        try { ReadRouting(); _store.Save(_settings); }
        catch (Exception ex) { ShowError("Не удалось сохранить настройки", ex); }
    }
    private void SaveClick(object sender, RoutedEventArgs e) => TrySave();
    private void ToggleEngine(object sender, RoutedEventArgs e) => ToggleEngine();
    private void ToggleEngine()
    {
        if (_engine is not null) { StopEngine("Аудиодвижок остановлен."); return; }
        if (!_ready || _assetsBusy) { Status.Text = "Дождись загрузки аудиофайлов."; return; }
        AudioEngine? candidate = null;
        try
        {
            ReadRouting();
            if (_settings.Audio.Effects is { Enabled: true, VocoderEnabled: true, Carrier: CarrierKind.File } && _carrier is null)
                throw new InvalidOperationException("Выбрана файловая несущая, но файл не загружен.");
            var output = OutputDeviceBox.SelectedItem as AudioDevice;
            if (output is not null && !output.Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase) &&
                MessageBox.Show(this, "Выход не похож на VB-CABLE. Если это колонки, возможна обратная связь. Продолжить?", "Проверь маршрутизацию", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            candidate = new AudioEngine();
            var owned = candidate;
            candidate.Faulted += message => Dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(_engine, owned)) StopEngine("Устройство остановлено: " + message + " Переподключи и запусти аудио заново.");
            });
            candidate.Graph.SetCarrier(_carrier);
            candidate.Start(_settings);
            _engine = candidate;
            StartButton.Content = "Остановить аудио";
            Status.Text = "Аудио работает → " + output?.Name;
            QueueSave();
        }
        catch (Exception ex) { candidate?.Dispose(); ShowError("Не удалось запустить аудио", ex); }
    }
    private void StopEngine(string reason)
    {
        var old = _engine;
        _engine = null;
        old?.Dispose();
        _mouseHeld = null;
        StartButton.Content = "Запустить аудио";
        Status.Text = reason;
        MeterLabel.Text = "IN —   OUT —";
    }
    private void PanicClick(object sender, RoutedEventArgs e) => Panic();
    private void Panic()
    {
        _engine?.Graph.SetPanic(true);
        Status.Text = "PANIC: весь выход заглушен. Для сброса останови и заново запусти аудио.";
    }
    private void StopSoundsClick(object sender, RoutedEventArgs e) => _engine?.Graph.Sounds.StopAll();
    private void PowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            // Silence immediately; disposal belongs on UI and may run only after resume.
            _engine?.Graph.SetPanic(true);
            Dispatcher.BeginInvoke(() => StopEngine("Сон Windows: аудио остановлено. После пробуждения запусти заново."));
        }
    }
    private void UpdateDiagnostics()
    {
        if (_engine is null) return;
        var d = _engine.Diagnostics;
        static string Db(float value) => value < .00001f ? "−∞" : (20 * Math.Log10(value)).ToString("F1");
        MeterLabel.Text = $"IN {Db(d.InputPeak),6} dBFS    OUT {Db(d.OutputPeak),6} dBFS" + (_engine.Graph.IsPanicked ? "    [PANIC]" : "");
        DiagnosticsLabel.Text = $"Очередь микрофона:    {d.CaptureQueuedMs:F1} мс\nОчередь мониторинга:  {d.MonitorQueuedMs:F1} мс\n" +
            $"Потеряны сэмплы:      mic {d.CaptureDrops}, monitor {d.MonitorDrops}\nUnderrun / resync:    {d.Underruns} / {d.Resyncs}\n" +
            $"Коррекция часов:      mic {d.CapturePpm:F0}, monitor {d.MonitorPpm:F0} ppm\nDSP / бюджет блока:   {d.ProcessingMs:F2} / {d.BlockMs:F2} мс\n" +
            $"Отклонено команд/голосов: {d.RejectedSounds}\nКэш библиотеки:       {_clips.Values.Sum(x => (long)x.Samples.Length * 4) / 1048576.0:F1} МиБ";
    }
    private static OpenFileDialog AudioDialog() => new()
    {
        Filter = "Звук|*.wav;*.mp3;*.aiff;*.aif;*.wma|Все файлы|*.*", CheckFileExists = true
    };
    private async void ImportSound(object sender, RoutedEventArgs e)
    {
        if (_assetsBusy) return;
        var dialog = AudioDialog();
        if (dialog.ShowDialog(this) != true) return;
        _assetsBusy = true; SetAssetButtons();
        string? stored = null;
        try
        {
            var id = Guid.NewGuid();
            var clip = await Task.Run(() => ClipLoader.Load(dialog.FileName, id));
            if (_exit) return;
            CheckBudget(clip, false);
            stored = await Task.Run(() => _store.CopyToLibrary(dialog.FileName, id));
            if (_exit) return;
            var entry = new SoundEntry { Id = id, Name = Path.GetFileNameWithoutExtension(dialog.FileName), StoredFile = stored };
            _clips[id] = clip;
            _settings = _settings with { Library = [.. _settings.Library, entry] };
            RefreshLibrary(id); QueueSave();
        }
        catch (Exception ex)
        {
            if (stored is not null) File.Delete(_store.ResolveLibraryFile(stored));
            ShowError("Импорт не выполнен", ex);
        }
        finally { _assetsBusy = false; SetAssetButtons(); }
    }
    private async void ChooseCarrier(object sender, RoutedEventArgs e)
    {
        if (_assetsBusy) return;
        var dialog = AudioDialog();
        if (dialog.ShowDialog(this) != true) return;
        _assetsBusy = true; SetAssetButtons();
        try
        {
            var id = Guid.NewGuid();
            var clip = await Task.Run(() => ClipLoader.Load(dialog.FileName, id));
            if (_exit) return;
            CheckBudget(clip, true);
            string stored = await Task.Run(() => _store.CopyToLibrary(dialog.FileName, id));
            if (_exit) return;
            _carrier = clip;
            _settings = _settings with { CarrierFile = stored };
            CarrierLabel.Text = Path.GetFileName(dialog.FileName);
            _engine?.Graph.SetCarrier(clip);
            QueueSave();
        }
        catch (Exception ex) { ShowError("Несущая не загружена", ex); }
        finally { _assetsBusy = false; SetAssetButtons(); }
    }
    private async Task LoadCarrier(string stored)
    {
        var clip = await Task.Run(() => ClipLoader.Load(_store.ResolveLibraryFile(stored), Guid.NewGuid()));
        CheckBudget(clip, true);
        if (_exit) return;
        _carrier = clip;
        _engine?.Graph.SetCarrier(clip);
    }
    private void ClearCarrier(object sender, RoutedEventArgs e)
    {
        if (_assetsBusy) return;
        _carrier = null;
        _settings = _settings with { CarrierFile = null };
        _engine?.Graph.SetCarrier(null);
        CarrierLabel.Text = "Файл не выбран";
        QueueSave();
    }
    private void RefreshLibrary(Guid? select = null)
    {
        SoundList.ItemsSource = _settings.Library;
        SoundList.SelectedItem = _settings.Library.FirstOrDefault(x => x.Id == select);
        if (SoundList.SelectedIndex < 0 && SoundList.Items.Count > 0) SoundList.SelectedIndex = 0;
    }
    private void SoundSelected(object sender, SelectionChangedEventArgs e)
    {
        if (SoundList.SelectedItem is not SoundEntry s) return;
        SoundName.Text = s.Name; SoundHotkey.Text = s.Hotkey;
        SoundTrigger.SelectedIndex = (int)s.Behavior.Trigger; SoundLoop.IsChecked = s.Behavior.Loop;
        SoundRetrigger.SelectedIndex = (int)s.Behavior.Retrigger; ClipGain.Value = s.Behavior.Gain;
    }
    private void ApplySound(object sender, RoutedEventArgs e)
    {
        if (SoundList.SelectedItem is not SoundEntry s) return;
        try
        {
            var updated = s with
            {
                Name = string.IsNullOrWhiteSpace(SoundName.Text) ? s.Name : SoundName.Text.Trim(), Hotkey = SoundHotkey.Text.Trim(),
                Behavior = new((TriggerMode)Math.Max(0, SoundTrigger.SelectedIndex), SoundLoop.IsChecked == true,
                    (RetriggerMode)Math.Max(0, SoundRetrigger.SelectedIndex), (float)ClipGain.Value)
            };
            var settings = _settings with { Library = _settings.Library.Select(x => x.Id == s.Id ? updated : x).ToList() };
            ConfigureHotkeys(settings); _settings = settings;
            RefreshLibrary(s.Id); QueueSave();
        }
        catch (Exception ex) { ShowError("Привязка не сохранена", ex); }
    }
    private void RemoveSound(object sender, RoutedEventArgs e)
    {
        if (_assetsBusy || SoundList.SelectedItem is not SoundEntry s) return;
        if (MessageBox.Show(this, $"Удалить «{s.Name}» из библиотеки?", "Удаление", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        try
        {
            _engine?.Graph.Sounds.StopAll();
            var settings = _settings with { Library = _settings.Library.Where(x => x.Id != s.Id).ToList() };
            ConfigureHotkeys(settings); _settings = settings;
            _clips.Remove(s.Id); RefreshLibrary(); ReadRouting(); _store.Save(_settings);
            if (_settings.CarrierFile != s.StoredFile && !_settings.Presets.Any(x => x.CarrierFile == s.StoredFile))
                File.Delete(_store.ResolveLibraryFile(s.StoredFile));
        }
        catch (Exception ex) { ShowError("Не удалось удалить звук", ex); }
    }
    private void SendSound(Guid id, bool pressed)
    {
        var s = _settings.Library.FirstOrDefault(x => x.Id == id);
        if (s is null || _engine is null) { if (pressed) Status.Text = "Сначала запусти аудио."; return; }
        if (_engine.Graph.IsPanicked) return;
        if (!_clips.TryGetValue(id, out var clip)) { Status.Text = "Файл не загружен. Переимпортируй звук."; return; }
        if (!_engine.Graph.Sounds.Enqueue(new(clip, id, s.Behavior, pressed))) Status.Text = "Очередь команд заполнена.";
    }
    private void PlaySoundClick(object sender, RoutedEventArgs e)
    {
        if (SoundList.SelectedItem is SoundEntry s && s.Behavior.Trigger != TriggerMode.Hold) SendSound(s.Id, true);
    }
    private void PlaySoundDown(object sender, MouseButtonEventArgs e)
    {
        if (SoundList.SelectedItem is not SoundEntry s || s.Behavior.Trigger != TriggerMode.Hold) return;
        _mouseHeld = s.Id; PlayButton.CaptureMouse(); SendSound(s.Id, true); e.Handled = true;
    }
    private void ReleaseMouseSound()
    {
        if (_mouseHeld is not { } id) return;
        _mouseHeld = null; SendSound(id, false); PlayButton.ReleaseMouseCapture();
    }
    private void PlaySoundUp(object sender, MouseButtonEventArgs e)
    {
        if (_mouseHeld is null) return;
        ReleaseMouseSound(); e.Handled = true;
    }
    private void PlaySoundLostCapture(object sender, MouseEventArgs e) => ReleaseMouseSound();
    private void RefreshPresets(Guid? selected = null)
    {
        PresetList.ItemsSource = _settings.Presets;
        PresetList.SelectedItem = _settings.Presets.FirstOrDefault(x => x.Id == selected);
    }
    private void PresetSelected(object sender, SelectionChangedEventArgs e)
    {
        if (PresetList.SelectedItem is EffectPreset p) { PresetName.Text = p.Name; PresetHotkey.Text = p.Hotkey; }
    }
    private void SavePreset(object sender, RoutedEventArgs e) => StorePreset(false);
    private void UpdatePreset(object sender, RoutedEventArgs e) => StorePreset(true);
    private void StorePreset(bool update)
    {
        if (_assetsBusy) return;
        if (update && PresetList.SelectedItem is not EffectPreset) return;
        try
        {
            var preset = new EffectPreset
            {
                Id = update ? ((EffectPreset)PresetList.SelectedItem).Id : Guid.NewGuid(),
                Name = string.IsNullOrWhiteSpace(PresetName.Text) ? "Мой голос" : PresetName.Text.Trim(),
                Hotkey = PresetHotkey.Text.Trim(), Effects = _settings.Audio.Effects, CarrierFile = _settings.CarrierFile
            };
            var settings = _settings with { Presets = update
                ? _settings.Presets.Select(x => x.Id == preset.Id ? preset : x).ToList() : [.. _settings.Presets, preset] };
            ConfigureHotkeys(settings); _settings = settings; RefreshPresets(preset.Id); QueueSave();
        }
        catch (Exception ex) { ShowError("Пресет не сохранён", ex); }
    }
    private async void ApplyPreset(object sender, RoutedEventArgs e)
    {
        if (PresetList.SelectedItem is EffectPreset p) await ApplyPresetById(p.Id);
    }
    private async Task ApplyPresetById(Guid id)
    {
        if (_assetsBusy) return;
        var preset = _settings.Presets.FirstOrDefault(x => x.Id == id);
        if (preset is null) return;
        _assetsBusy = true; SetAssetButtons();
        try
        {
            if (preset.CarrierFile is { } path && (_settings.CarrierFile != path || _carrier is null)) await LoadCarrier(path);
            else if (preset.CarrierFile is null) { _carrier = null; _engine?.Graph.SetCarrier(null); }
            if (_exit) return;
            _settings = _settings with { CarrierFile = preset.CarrierFile, Audio = _settings.Audio with { Effects = preset.Effects.Validated() } };
            PopulateAudio(); _engine?.Graph.Publish(_settings.Audio); QueueSave();
        }
        catch (Exception ex) { ShowError("Пресет не применён", ex); }
        finally { _assetsBusy = false; SetAssetButtons(); }
    }
    private void DeletePreset(object sender, RoutedEventArgs e)
    {
        if (_assetsBusy || PresetList.SelectedItem is not EffectPreset p) return;
        try
        {
            var settings = _settings with { Presets = _settings.Presets.Where(x => x.Id != p.Id).ToList() };
            ConfigureHotkeys(settings); _settings = settings; RefreshPresets(); QueueSave();
        }
        catch (Exception ex) { ShowError("Пресет не удалён", ex); }
    }
    private void ConfigureHotkeys(AppSettings settings)
    {
        var bindings = settings.Hotkeys
            .Concat(settings.Library.Select(x => new HotkeyBinding(VoiceCommand.PlaySound, x.Hotkey, x.Id)))
            .Concat(settings.Presets.Select(x => new HotkeyBinding(VoiceCommand.ApplyPreset, x.Hotkey, x.Id)));
        _hotkeys?.Configure(bindings);
    }
    private void ApplyHotkeys(object sender, RoutedEventArgs e)
    {
        try
        {
            HotkeyGrid.CommitEdit(DataGridEditingUnit.Cell, true); HotkeyGrid.CommitEdit(DataGridEditingUnit.Row, true);
            var settings = _settings with { Hotkeys = _hotkeyRows.Select(x => new HotkeyBinding(x.Command, x.Gesture.Trim())).ToList() };
            ConfigureHotkeys(settings); _settings = settings; QueueSave(); Status.Text = "Хоткеи применены.";
        }
        catch (Exception ex) { ShowError("Хоткеи не применены", ex); }
    }
    private async void HandleBinding(HotkeyBinding binding, bool pressed)
    {
        if (_exit || !_ready) return;
        try
        {
            if (binding.Command == VoiceCommand.PlaySound && binding.Target is { } sound) { SendSound(sound, pressed); return; }
            if (!pressed) return;
            switch (binding.Command)
            {
                case VoiceCommand.ToggleMicrophone: MicMuted.IsChecked = MicMuted.IsChecked != true; break;
                case VoiceCommand.ToggleEffects: EffectsEnabled.IsChecked = EffectsEnabled.IsChecked != true; break;
                case VoiceCommand.ToggleVoiceMonitor: MonitorVoice.IsChecked = MonitorVoice.IsChecked != true; break;
                case VoiceCommand.StopSounds: _engine?.Graph.Sounds.StopAll(); break;
                case VoiceCommand.Panic: Panic(); break;
                case VoiceCommand.ToggleEngine: ToggleEngine(); break;
                case VoiceCommand.ApplyPreset when binding.Target is { } preset: await ApplyPresetById(preset); break;
            }
        }
        catch (Exception ex) { ShowError("Ошибка команды", ex); }
    }
    private void ShowError(string title, Exception ex)
    {
        if (_exit) return;
        Status.Text = title + ": " + ex.Message;
        MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }
    private void RestoreWindow() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void HideToTray(object sender, RoutedEventArgs e)
    {
        if (_tray?.Available == true) Hide();
        else WindowState = WindowState.Minimized;
    }
    private void ExitClick(object sender, RoutedEventArgs e) => Exit();
    private void Exit() { _exit = true; Close(); }
    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownComplete) return;
        if (!_exit && _tray?.Available == true) { e.Cancel = true; Hide(); return; }
        e.Cancel = true;
        if (_shuttingDown) return;
        _shuttingDown = true;
        _exit = true;
        _saveTimer.Stop(); _diagnostics.Stop();
        SystemEvents.PowerModeChanged -= PowerChanged;
        _hotkeys?.Dispose(); _tray?.Dispose();
        StopEngine("Приложение закрыто.");
        // Preserve changes even on explicit exit. Do not show modal errors during shutdown.
        try { if (_ready) { ReadRouting(); _store.Save(_settings); } }
        catch (Exception ex) { Debug.WriteLine(ex); }
        try { await StopRemoteAsync(); }
        catch (Exception ex) { Debug.WriteLine(ex); }
        finally
        {
            _shutdownComplete = true;
            // Even with no active server, leave the current Closing event before calling Close again.
            Dispatcher.BeginInvoke(new Action(Close), DispatcherPriority.Normal);
        }
    }
    private void OpenSettingsFolder(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", _store.Root) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError("Не удалось открыть папку", ex); }
    }
}

public sealed class HotkeyRow(VoiceCommand command, string gesture)
{
    public VoiceCommand Command { get; } = command;
    public string Gesture { get; set; } = gesture;
    public string Label => Command switch
    {
        VoiceCommand.ToggleMicrophone => "Mute микрофона", VoiceCommand.ToggleEffects => "Обработка вкл/выкл",
        VoiceCommand.ToggleVoiceMonitor => "Прослушивание голоса", VoiceCommand.StopSounds => "Остановить саундпад",
        VoiceCommand.ToggleEngine => "Аудиодвижок вкл/выкл", VoiceCommand.Panic => "PANIC: заглушить всё", _ => Command.ToString()
    };
}
