using System.Windows;
using VoiceKit.Core;
using VoiceKit.Windows.Audio;
using VoiceKit.Windows.Calibration;

namespace VoiceKit.Windows;

public partial class MainWindow
{
    private volatile CalibrationWindow? _calibrationWindow;
    private void OpenCalibration(object sender, RoutedEventArgs e)
    {
        if (!_ready || _exit || _assetsBusy || _calibrationWindow is not null) return;
        try
        {
            ReadRouting();
            if (CaptureDeviceBox.SelectedItem is not AudioDevice input || string.IsNullOrEmpty(input.Id))
                throw new InvalidOperationException("Сначала выбери физический микрофон на вкладке «Маршруты».");
            if (MonitorDeviceBox.SelectedItem is not AudioDevice monitor || string.IsNullOrEmpty(monitor.Id))
                throw new InvalidOperationException("Сначала выбери наушники для прослушивания на вкладке «Маршруты».");
            if (input.Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase) ||
                monitor.Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase) || monitor.Id == _settings.OutputId)
                throw new InvalidOperationException("Лаборатории нужны физический микрофон и отдельные наушники, не VB-CABLE и не выход передачи.");
            if (_engine is not null && MessageBox.Show(this,
                "На время лаборатории передача микрофона/саундпада остановится. После закрытия запусти аудио вручную. Продолжить?",
                "Изолированное прослушивание", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
            StopEngine("Лаборатория открыта: передача остановлена, хоткеи (кроме PANIC) приостановлены.");
            var lab = new CalibrationWindow(input, monitor, _settings.OutputId, _settings.Audio.Effects, _carrier, _settings.CarrierFile, _store.Root) { Owner = this };
            _calibrationWindow = lab;
            try { lab.ShowDialog(); }
            finally { _calibrationWindow = null; }
            if (_exit) return;
            if (lab.SelectedEffects is { } effects)
            {
                var preset = new EffectPreset { Name = lab.SelectedName, Effects = effects, CarrierFile = lab.SelectedCarrierFile };
                _settings = _settings with { Audio = _settings.Audio with { Effects = effects }, Presets = [.. _settings.Presets, preset] };
                PopulateAudio(); RefreshPresets(preset.Id); QueueSave();
                Status.Text = "Пресет из лаборатории применён. Запусти аудио вручную для живой проверки. Маршруты и уровни не изменялись.";
            }
            else Status.Text = "Лаборатория закрыта без применения. Запусти аудио вручную.";
        }
        catch (Exception ex) { ShowError("Лаборатория недоступна", ex); }
    }
}
