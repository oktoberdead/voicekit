# Сторонние компоненты

- **NAudio 2.2.1** — Mark Heath и участники проекта, MIT License. Аудиоустройства, sample providers, WDL resampling файлов, декодирование. Исходники и лицензия: https://github.com/naudio/NAudio/tree/v2.2.1
- **xUnit.net**, **Microsoft.NET.Test.Sdk**, **xunit.runner.visualstudio** — зависимости только тестового проекта; их лицензии поставляются NuGet-пакетами.
- **Playwright** — Microsoft, Apache-2.0; dev dependency только браузерных тестов. Не поставляется с Windows-приложением.
- **.NET / WPF / ASP.NET Core** — Microsoft и участники .NET; при self-contained publish runtime и его notices распространяются вместе с приложением.
- **VB-CABLE** — внешний драйвер VB-Audio, не входит в репозиторий и дистрибутив VoiceKit. Его условия использования независимы от приложения.

DSP в `VoiceKit.Core` реализован в этом репозитории. Нейросетевые модели, аудиозаписи и код/пресеты Voicemod не включены. Используй файлы, на которые у тебя есть право.
