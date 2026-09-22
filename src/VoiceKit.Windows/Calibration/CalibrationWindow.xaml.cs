using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using VoiceKit.Core;
using VoiceKit.Core.Calibration;
using VoiceKit.Windows.Audio;

namespace VoiceKit.Windows.Calibration;

public partial class CalibrationWindow : Window
{
    private readonly AudioDevice _input, _headphones;
    private readonly string? _forbiddenOutput, _availableCarrierFile;
    private readonly AudioClip? _availableCarrier;
    private AudioClip? _carrier;
    private string? _carrierFile;
    private readonly CalibrationStore _store;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _work;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private CalibrationCapture? _capture;
    private CalibrationPlayer? _player;
    private EffectSettings _reference, _candidateBase;
    private float[]? _raw;
    private VoiceAnalysis? _analysis;
    private ComparisonAudio? _comparison;
    private CalibrationExploration? _exploration;
    private bool _ready, _busy, _closed, _applying;
    private string _recordDeviceName;
    private long _recordStarted;
    public EffectSettings? SelectedEffects { get; private set; }
    public string SelectedName { get; private set; } = "Мой голос";
    public string? SelectedCarrierFile { get; private set; }

    public CalibrationWindow(AudioDevice input, AudioDevice headphones, string? transmissionId,
        EffectSettings reference, AudioClip? carrier, string? carrierFile, string settingsRoot)
    {
        InitializeComponent();
        _input = input; _headphones = headphones; _forbiddenOutput = transmissionId;
        _recordDeviceName = input.Name;
        _reference = reference.Validated(); _candidateBase = _reference;
        _availableCarrier = _carrier = carrier; _availableCarrierFile = _carrierFile = carrierFile;
        _store = new(settingsRoot);
        ExcerptBox.ItemsSource = CalibrationScript.Excerpts; ExcerptBox.SelectedIndex = 0;
        DeviceLabel.Text = $"Запись: {input.Name}\nПрослушивание: {headphones.Name}";
        PopulateCandidate(_reference with { Enabled = true, PitchEnabled = true });
        ReferenceLabel.Text = Describe("A", _reference);
        _ready = true; _timer.Tick += Tick; _timer.Start(); UpdateButtons();
    }
    private void UpdateButtons()
    {
        if (_closed) return;
        bool idle = !_busy && _capture is null;
        RecordButton.IsEnabled = LoadButton.IsEnabled = idle;
        StopRecordButton.IsEnabled = _capture is not null;
        SuggestButton.IsEnabled = AutoExploreButton.IsEnabled = idle && _analysis?.CanSuggestPitch == true;
        RenderButton.IsEnabled = ExploreButton.IsEnabled = SaveButton.IsEnabled = idle && _raw is not null;
        DryButton.IsEnabled = AButton.IsEnabled = BButton.IsEnabled = ApplyButton.IsEnabled = idle && _comparison is not null;
        ExplorationBox.IsEnabled = idle && _exploration is not null;
        CandidateFormant.IsEnabled = CandidateFormantShift.IsEnabled = idle;
        CandidateShift.IsEnabled = CandidatePitch.IsEnabled = CandidateEnabled.IsEnabled = PitchOnly.IsEnabled = NeutralReference.IsEnabled = idle;
        ExcerptBox.IsEnabled = MatchLevels.IsEnabled = TargetHz.IsEnabled = PreferredBox.IsEnabled = ProfileName.IsEnabled = idle;
    }
    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy; if (status is not null) LabStatus.Text = status; UpdateButtons();
    }
    private CancellationToken BeginWork()
    {
        _work?.Dispose(); _work = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); return _work.Token;
    }
    private void InvalidateComparison()
    {
        StopPreview(); _comparison = null; ClearExploration();
        PreviewInfo.Text = "Параметры изменены. Нажми «Подготовить A/B» перед прослушиванием/применением.";
        UpdateButtons();
    }
    private void ClearExploration()
    {
        _exploration = null; ExplorationBox.ItemsSource = null;
    }
    private void PopulateCandidate(EffectSettings effects, bool resetIsolation = true)
    {
        bool wasApplying = _applying;
        _applying = true;
        try
        {
            _candidateBase = effects;
            CandidateEnabled.IsChecked = effects.Enabled; CandidatePitch.IsChecked = effects.PitchEnabled;
            CandidateShift.Value = effects.PitchSemitones;
            if (resetIsolation) PitchOnly.IsChecked = false;
            CandidateFormant.IsChecked = effects.FormantEnabled; CandidateFormantShift.Value = effects.FormantSemitones;
        }
        finally { _applying = wasApplying; }
    }
    private EffectSettings Reference() => NeutralReference.IsChecked == true ? new EffectSettings() : _reference;
    private EffectSettings Candidate() => (PitchOnly.IsChecked == true ? new EffectSettings() : _candidateBase) with
    {
        Enabled = CandidateEnabled.IsChecked == true,
        PitchEnabled = CandidatePitch.IsChecked == true,
        PitchSemitones = CandidateShift.Value,
        FormantEnabled = CandidateFormant.IsChecked == true,
        FormantSemitones = CandidateFormantShift.Value
    };
    private void CandidateChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready || _applying) return;
        ReferenceLabel.Text = Describe("A", Reference());
        InvalidateComparison();
    }
    private void CandidateSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (_ready && !_applying) InvalidateComparison(); }
    private void ExcerptChanged(object sender, SelectionChangedEventArgs e) { if (_ready && !_applying) InvalidateComparison(); }
    private void PreviewVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => _player?.Playback.SetVolume((float)e.NewValue);
    private void StartRecording(object sender, RoutedEventArgs e)
    {
        if (_busy || _capture is not null) return;
        if (_raw is not null && MessageBox.Show(this, "Новый дубль заменит образец в памяти. Сохранённые профили останутся. Продолжить?", "Новая запись", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        try
        {
            StopPreview(); _comparison = null; ClearExploration();
            var capture = new CalibrationCapture();
            _capture = capture; _recordStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            try { capture.Start(_input.Id); } catch { _capture = null; capture.Dispose(); throw; }
            _raw = null; _analysis = null; _recordDeviceName = _input.Name;
            AnalysisLabel.Text = "Идёт запись…"; AnalysisWarnings.Text = ""; SavedPathLabel.Text = "";
            PromptLabel.Text = CalibrationScript.Stages[0].Prompt;
            LabStatus.Text = "Микрофон записывает. В VB-CABLE ничего не передаётся."; UpdateButtons();
        }
        catch (Exception ex) { Error("Запись не началась", ex); }
        finally { UpdateButtons(); }
    }
    private async void StopRecording(object sender, RoutedEventArgs e) => await FinishRecording();
    private async Task FinishRecording()
    {
        var capture = _capture; if (capture is null || _busy) return;
        _capture = null; SetBusy(true, "Останавливаем запись и анализируем…");
        var ct = BeginWork();
        try
        {
            capture.Dispose();
            var result = await Task.Run(() =>
            {
                var raw = capture.To48Khz(ct);
                return (Raw: raw, Analysis: VoiceAnalyzer.Analyze(raw, ct));
            }, ct);
            if (_closed || ct.IsCancellationRequested) return;
            _raw = result.Raw; _analysis = result.Analysis;
            SelectAvailableExcerpt(); ShowAnalysis();
            StageLabel.Text = $"Записано {_analysis.DurationSeconds:F1} с. Можно готовить сравнение.";
            LabStatus.Text = "Образец только в памяти. Для сохранения нажми «Сохранить профиль + WAV».";
        }
        catch (OperationCanceledException) { if (!_closed) LabStatus.Text = "Анализ отменён."; }
        catch (Exception ex) { Error("Не удалось обработать запись", ex); }
        finally { if (!_closed) SetBusy(false); }
    }
    private void SelectAvailableExcerpt()
    {
        if (_raw is not null && _raw.Length < 13 * CalibrationScript.SampleRate) ExcerptBox.SelectedIndex = 3;
    }
    private async void Tick(object? sender, EventArgs e)
    {
        var capture = _capture;
        if (capture is not null)
        {
            if (System.Diagnostics.Stopwatch.GetElapsedTime(_recordStarted).TotalSeconds > CalibrationScript.DurationSeconds + 12)
            {
                EmergencyStop("За 50 секунд не получен полный дубль. Проверь устройство и запиши заново."); return;
            }
            if (capture.Error is { } error)
            {
                EmergencyStop("Устройство записи недоступно: " + error); return;
            }
            double seconds = capture.Seconds;
            var stage = CalibrationScript.StageAt(seconds);
            StageLabel.Text = $"{stage.Name} · осталось {Math.Max(0, stage.EndSeconds - seconds):F1} с";
            PromptLabel.Text = stage.Prompt; RecordProgress.Value = seconds;
            RecordLevel.Text = $"{seconds:F1} / 38.0 с · пик {VoiceAnalyzer.Db(capture.Peak):F1} dBFS";
            if (capture.Complete) await FinishRecording();
        }
        if (_player is { } player)
        {
            PlaybackProgress.Value = (double)player.Playback.Position / player.Playback.Frames;
            PlaybackLabel.Text = $"{player.Playback.Position / 48000.0:F1} / {player.Playback.Frames / 48000.0:F1} с · переключай A/B на той же позиции";
        }
    }
    private void ShowAnalysis()
    {
        if (_analysis is not { } a) return;
        static string Hz(double? x) => x.HasValue ? $"{x:F1} Гц" : "не определено";
        AnalysisLabel.Text = $"Запись: {a.DurationSeconds:F1} с | пик {a.PeakDbfs:F1} dBFS | у предела: {a.ClippedSamples} сэмплов\n" +
            $"Речь RMS: {a.SpeechRmsDbfs:F1} dBFS | фон RMS: {a.NoiseRmsDbfs:F1} dBFS\n" +
            $"Медиана F0: {Hz(a.MedianF0Hz)} | P10–P90: {Hz(a.LowF0Hz)} — {Hz(a.HighF0Hz)}\n" +
            $"Тональные кадры: {a.VoicedFraction:P0} (~{a.VoicedSeconds:F1} с) | периодичность: {a.Periodicity:P0}";
        AnalysisWarnings.Text = string.Join("\n", a.Warnings.Select(x => "• " + x));
    }
    private double ReadTarget()
    {
        string value = TargetHz.Text.Trim().Replace(',', '.');
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double target) || !double.IsFinite(target) || target is < 80 or > 350)
            throw new ArgumentException("Целевая медиана должна быть 80–350 Гц.");
        return target;
    }
    private void SuggestPitch(object sender, RoutedEventArgs e)
    {
        if (_analysis is null || _busy) return;
        try
        {
            var suggestion = VoiceAnalyzer.Suggest(_analysis, ReadTarget());
            _applying = true;
            try { CandidateEnabled.IsChecked = CandidatePitch.IsChecked = true; CandidateShift.Value = suggestion.Semitones; }
            finally { _applying = false; }
            InvalidateComparison();
            LabStatus.Text = $"Ориентир для B: {suggestion.Semitones:+0.0;-0.0;0.0} st. Сравни на слух." +
                (suggestion.Limited ? " Расчётный сдвиг выходит за ±12 st и ограничен." : " Это не автотюн/монотонность.");
        }
        catch (Exception ex) { Error("Нет рекомендации", ex); }
    }
    private async void PrepareComparison(object sender, RoutedEventArgs e)
    {
        if (_busy || _raw is null || ExcerptBox.SelectedItem is not CalibrationSection excerpt) return;
        StopPreview(); _comparison = null; ClearExploration();
        var raw = _raw; var a = Reference(); var b = Candidate().Validated(); var carrier = _carrier;
        bool match = MatchLevels.IsChecked == true;
        SetBusy(true, "Готовим одинаковые фрагменты A/B…"); var ct = BeginWork();
        try
        {
            var comparison = await Task.Run(() => CalibrationRenderer.Render(raw, a, b, carrier, excerpt, match, ct), ct);
            if (_closed || ct.IsCancellationRequested) return;
            _comparison = comparison;
            PreviewInfo.Text = $"{excerpt.Name}, {comparison.Frames / 48000.0:F1} с. " + Describe("B", b) +
                $"\nУровни прослушивания: оригинал ×{comparison.GainDry:F2}, A ×{comparison.GainA:F2}, B ×{comparison.GainB:F2}. RMS-сопоставление приблизительное, с ограничением пиков.";
            LabStatus.Text = "Готово. Начни с небольшой громкости и переключай A/B.";
        }
        catch (OperationCanceledException) { if (!_closed) LabStatus.Text = "Подготовка отменена."; }
        catch (Exception ex) { Error("Не удалось подготовить A/B", ex); }
        finally { if (!_closed) SetBusy(false); }
    }
    private async void StartGuidedExploration(object sender, RoutedEventArgs e)
    {
        if (_busy || _capture is not null || _analysis?.CanSuggestPitch != true) return;
        try
        {
            var suggestion = VoiceAnalyzer.Suggest(_analysis, ReadTarget());
            if (suggestion.Limited && MessageBox.Show(this,
                "Для выбранной цели нужен сдвиг за пределами ±12 st. Подбор будет ограничен этим диапазоном. Продолжить?",
                "Цель вне диапазона", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            _applying = true;
            try
            {
                PopulateCandidate(new() { PitchEnabled = true, PitchSemitones = suggestion.Semitones,
                    FormantEnabled = true, FormantSemitones = 1.5 });
                NeutralReference.IsChecked = true; PitchOnly.IsChecked = true; PreferredBox.SelectedIndex = 1;
                ReferenceLabel.Text = Describe("A", Reference());
            }
            finally { _applying = false; }
            InvalidateComparison();
            await BuildExploration();
        }
        catch (Exception ex) { Error("Подбор недоступен", ex); }
    }
    private async void PrepareExploration(object sender, RoutedEventArgs e) => await BuildExploration();
    private async Task BuildExploration()
    {
        if (_busy || _raw is null || ExcerptBox.SelectedItem is not CalibrationSection excerpt) return;
        StopPreview(); _comparison = null; ClearExploration();
        var raw = _raw; var reference = Reference(); var center = Candidate().Validated(); var carrier = _carrier;
        bool match = MatchLevels.IsChecked == true;
        SetBusy(true, "Готовим сочетания высоты и тембра. PANIC отменяет расчёт…"); var ct = BeginWork();
        var progress = new Progress<int>(percent =>
        {
            if (!_closed && _work?.Token == ct && !ct.IsCancellationRequested && _busy) LabStatus.Text = $"Сочетания высоты и тембра: {percent}% · PANIC — отмена";
        });
        try
        {
            var result = await Task.Run(() => CalibrationExplorer.Render(raw, reference, center, carrier, excerpt, match, ct, progress), ct);
            if (_closed || ct.IsCancellationRequested) return;
            _exploration = result; _comparison = result.Audio;
            _applying = true;
            try { ExplorationBox.ItemsSource = result.Choices; ExplorationBox.SelectedIndex = 0; }
            finally { _applying = false; }
            SelectExploration();
            BButton.BringIntoView();
            LabStatus.Text = "Варианты готовы. Слушай B и выбирай сочетания из списка — на той же позиции.";
        }
        catch (OperationCanceledException) { if (!_closed) LabStatus.Text = "Перебор отменён."; }
        catch (Exception ex) { Error("Не удалось подготовить варианты", ex); }
        finally { if (!_closed) SetBusy(false); }
    }
    private void ExplorationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && !_applying) SelectExploration();
    }
    private void SelectExploration()
    {
        if (_exploration is null || ExplorationBox.SelectedIndex < 0) return;
        int index = ExplorationBox.SelectedIndex;
        var choice = _exploration.Choices[index];
        PopulateCandidate(choice.Effects, resetIsolation: false);
        PreferredBox.SelectedIndex = 1;
        PreviewInfo.Text = $"B: {choice} · громкость предпрослушивания ×{choice.PreviewGain:F2}. " +
            "Ручное изменение слайдера сбрасывает набор; можно подготовить новый вокруг понравившегося B. Сохраняется выбранный вариант, не весь набор.";
        if (_player is not null)
        {
            _player.Playback.Select(2 + index);
            LabStatus.Text = $"Слушаем B: {choice}. Только наушники.";
        }
    }
    private void PlayDry(object sender, RoutedEventArgs e) => Play(0);
    private void PlayA(object sender, RoutedEventArgs e) => Play(1);
    private void PlayB(object sender, RoutedEventArgs e) => Play(_exploration is null ? 2 : 2 + Math.Max(0, ExplorationBox.SelectedIndex));
    private void Play(int variant)
    {
        if (_busy || _capture is not null || _comparison is null) return;
        try
        {
            if (_player is not null) { _player.Playback.Select(variant); }
            else
            {
                var player = new CalibrationPlayer(_comparison, variant);
                player.Stopped += error => Dispatcher.BeginInvoke(() =>
                {
                    if (_closed || !ReferenceEquals(_player, player)) return;
                    StopPreview();
                    LabStatus.Text = error is null ? "Фрагмент закончился. Повторное нажатие начнёт его заново." : "Ошибка прослушивания: " + error;
                });
                _player = player; // publish before starting: suspend can mute even during device initialization
                player.Start(_headphones.Id, _forbiddenOutput, (float)PreviewVolume.Value);
            }
            LabStatus.Text = "Слушаем: " + (variant == 0 ? "оригинал" : variant == 1 ? "A" : "B") + ". Только наушники.";
        }
        catch (Exception ex) { StopPreview(); Error("Прослушивание недоступно", ex); }
    }
    private void StopPreview()
    {
        var player = _player; _player = null;
        try { player?.Dispose(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }
    private void StopPreviewClick(object sender, RoutedEventArgs e) { StopPreview(); PlaybackProgress.Value = 0; }
    private void PanicClick(object sender, RoutedEventArgs e) => EmergencyStop("PANIC: запись, подготовка и прослушивание остановлены. Новое воспроизведение — только по кнопке.");
    // No WPF/COM calls here: may be invoked by the Windows power-event thread.
    public void SilenceImmediately()
    {
        Volatile.Read(ref _player)?.Playback.Mute();
        Volatile.Read(ref _capture)?.Abort();
    }
    public void EmergencyStop(string message)
    {
        SilenceImmediately();
        _work?.Cancel(); StopPreview();
        var capture = _capture; _capture = null;
        try { capture?.Dispose(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        // An interrupted active recording is discarded, not analyzed/saved behind the user's back.
        if (!_closed) { LabStatus.Text = message; StageLabel.Text = "Остановлено"; UpdateButtons(); }
    }
    private async void SaveProfile(object sender, RoutedEventArgs e)
    {
        if (_busy || _capture is not null || _raw is null) return;
        try
        {
            var profile = new CalibrationProfile
            {
                Name = NameText(), CaptureDeviceName = _recordDeviceName, Reference = Reference(), Candidate = Candidate().Validated(),
                Analysis = _analysis, TargetMedianHz = ReadTarget(), MatchLevels = MatchLevels.IsChecked == true,
                Preferred = PreferredBox.SelectedIndex == 0 ? "A" : "B", CarrierFile = _carrierFile
            };
            if (MessageBox.Show(this, "Сохранить исходный голос (WAV), анализ и A/B-настройки в локальную папку Calibration? Запись не загружается в сеть.", "Сохранение голосового образца", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            StopPreview(); var raw = _raw; SetBusy(true, "Сохраняем локальный профиль…"); var ct = BeginWork();
            string path = await Task.Run(() => _store.Save(profile, raw, ct), ct);
            if (_closed) return;
            SavedPathLabel.Text = path; LabStatus.Text = "Профиль сохранён. Исходные настройки живого голоса не изменены.";
        }
        catch (OperationCanceledException) { if (!_closed) LabStatus.Text = "Сохранение отменено."; }
        catch (Exception ex) { Error("Профиль не сохранён", ex); }
        finally { if (!_closed) SetBusy(false); }
    }
    private async void LoadProfile(object sender, RoutedEventArgs e)
    {
        if (_busy || _capture is not null) return;
        var dialog = new OpenFileDialog { Filter = "Профиль VoiceKit|profile.json", CheckFileExists = true, InitialDirectory = Directory.Exists(_store.Root) ? _store.Root : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) };
        if (dialog.ShowDialog(this) != true) return;
        if (_raw is not null && MessageBox.Show(this, "Заменить образец в памяти выбранным профилем? Несохранённый дубль будет потерян.", "Открытие профиля", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        StopPreview(); SetBusy(true, "Открываем образец и заново проверяем анализ…"); var ct = BeginWork();
        try
        {
            var loaded = await Task.Run(() =>
            {
                var data = _store.Load(dialog.FileName, ct);
                return (data.Profile, data.Raw, Analysis: VoiceAnalyzer.Analyze(data.Raw, ct));
            }, ct);
            if (_closed || ct.IsCancellationRequested) return;
            _raw = loaded.Raw; _analysis = loaded.Analysis; _reference = loaded.Profile.Reference;
            _carrierFile = loaded.Profile.CarrierFile; _carrier = _carrierFile == _availableCarrierFile ? _availableCarrier : null;
            _recordDeviceName = loaded.Profile.CaptureDeviceName; ProfileName.Text = loaded.Profile.Name;
            PopulateCandidate(loaded.Profile.Candidate); ReferenceLabel.Text = Describe("A", _reference);
            _applying = true;
            try
            {
                NeutralReference.IsChecked = false;
                MatchLevels.IsChecked = loaded.Profile.MatchLevels; PreferredBox.SelectedIndex = loaded.Profile.Preferred == "A" ? 0 : 1;
                TargetHz.Text = loaded.Profile.TargetMedianHz.ToString("F1", CultureInfo.InvariantCulture);
            }
            finally { _applying = false; }
            SelectAvailableExcerpt(); ShowAnalysis(); InvalidateComparison();
            StageLabel.Text = $"Профиль загружен: {_analysis.DurationSeconds:F1} с";
            SavedPathLabel.Text = dialog.FileName;
            LabStatus.Text = "Анализ пересчитан из WAV. Подготовь сравнение заново.";
        }
        catch (OperationCanceledException) { if (!_closed) LabStatus.Text = "Загрузка отменена."; }
        catch (Exception ex) { Error("Не удалось открыть профиль", ex); }
        finally { if (!_closed) SetBusy(false); }
    }
    private void OpenProfilesFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_store.Root);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", _store.Root) { UseShellExecute = true });
        }
        catch (Exception ex) { Error("Не удалось открыть папку", ex); }
    }
    private string NameText() => string.IsNullOrWhiteSpace(ProfileName.Text) ? "Мой голос" : ProfileName.Text.Trim();
    private void ApplyResult(object sender, RoutedEventArgs e)
    {
        if (_busy || _comparison is null || _capture is not null) return;
        SelectedEffects = PreferredBox.SelectedIndex == 0 ? Reference() : Candidate().Validated();
        SelectedName = NameText();
        SelectedCarrierFile = SelectedEffects.Enabled && SelectedEffects.Carrier == CarrierKind.File && SelectedEffects.VocoderEnabled ? _carrierFile : null;
        Close();
    }
    private static string Describe(string label, EffectSettings s)
    {
        if (!s.Enabled) return label + ": общий bypass";
        var effects = new List<string>();
        if (s.PitchEnabled) effects.Add($"питч {s.PitchSemitones:+0.0;-0.0;0.0} st");
        if (s.FormantEnabled) effects.Add($"тембр {s.FormantSemitones:+0.0;-0.0;0.0} st (раздельно)");
        if (s.WobbleEnabled) effects.Add("воббл"); if (s.RobotEnabled) effects.Add("робот");
        if (s.VocoderEnabled) effects.Add("вокодер"); if (s.EchoEnabled) effects.Add("эхо"); if (s.ReverbEnabled) effects.Add("реверберация");
        return label + ": " + (effects.Count == 0 ? "без эффектов" : string.Join(", ", effects));
    }
    private void Error(string title, Exception ex)
    {
        if (_closed) return;
        LabStatus.Text = title + ": " + ex.Message;
        MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (_closed) return;
        _closed = true; _timer.Stop(); _lifetime.Cancel(); EmergencyStop("Закрыто");
        // No auto-save of voice samples, no auto-restart of the live microphone path.
    }
}
