using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using StrikeEQ.Audio;
using StrikeEQ.Dsp;
using StrikeEQ.Models;
using StrikeEQ.Services;

namespace StrikeEQ;

public partial class MainWindow : Window
{
    private readonly DspChain _dspChain = new();
    private readonly TacticalAudioGenerator _soundLab = new();
    private readonly WasapiAudioEngine _audioEngine;
    private readonly EqualizerApoService _apoService = new();
    private readonly NativeApoSharedMemory _nativeApoShm = new();
    private readonly GitHubUpdateService _updateService = new();
    private readonly GameDetectorService _gameDetector;
    private readonly GlobalHotkeyService _hotkeyService = new();
    private TrayIconService? _trayService;
    private readonly DispatcherTimer _uiTimer;

    private AppSettings _settings;
    private readonly List<EqProfile> _profiles = new();
    private EqProfile _activeProfile = null!;
    private string? _profileBeforeAutoSwitch;

    private readonly Slider[] _faderSliders = new Slider[10];
    private readonly TextBlock[] _faderGainLabels = new TextBlock[10];
    private readonly TextBlock[] _faderFreqLabels = new TextBlock[10];
    private readonly Border[] _faderCards = new Border[10];

    private bool _suppressUiEvents;
    private bool _isRealExit;

    public MainWindow()
    {
        _suppressUiEvents = true;
        InitializeComponent();

        _settings = PresetLibrary.LoadSettings();
        _apoService.ConfigDirectory = _settings.ApoConfigDirectory;
        _updateService.RepositorySlug = _settings.GitHubRepo;
        TxtAppVersion.Text = $"ATUALIZAÇÕES VIA GITHUB (v{GitHubUpdateService.CurrentVersion})";
        _audioEngine = new WasapiAudioEngine(_dspChain, _soundLab);

        _gameDetector = new GameDetectorService(() => _profiles);
        _gameDetector.Enabled = _settings.AutoGameDetect;
        _gameDetector.GameDetected += OnGameDetected;
        _gameDetector.GameClosed += OnGameClosed;

        BuildFaderBank();
        LoadProfiles();
        RefreshAudioDevices();
        UpdateHeadsetActivationUi();

        EqCanvas.BandChanged += OnCanvasBandChanged;
        EqCanvas.SelectedBandChanged += HighlightSelectedFader;

        _suppressUiEvents = true;
        ChkAutoGameDetect.IsChecked = _settings.AutoGameDetect;
        ChkAutoGameDetectProfiles.IsChecked = _settings.AutoGameDetect;
        ChkMinimizeTray.IsChecked = _settings.MinimizeToTray;
        _suppressUiEvents = false;

        Loaded += OnWindowLoaded;
        Closing += OnWindowClosing;

        _uiTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(22)
        };
        _uiTimer.Tick += OnUiTimerTick;
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        _trayService = new TrayIconService();
        _trayService.RestoreRequested += () => Dispatcher.BeginInvoke(RestoreFromTray);
        _trayService.ToggleBypassRequested += () => Dispatcher.BeginInvoke(ToggleBypass);
        _trayService.ToggleScoutModeRequested += () => Dispatcher.BeginInvoke(ToggleScoutMode);
        _trayService.ProfileSelected += id => Dispatcher.BeginInvoke(() => SelectProfileById(id));
        _trayService.ExitRequested += () => Dispatcher.BeginInvoke(ExitApplication);
        UpdateTrayMenu();

        _hotkeyService.Attach(this);
        _hotkeyService.HotkeyPressed += OnGlobalHotkeyPressed;

        _gameDetector.Start();
        StartEngineForMode(AudioEngineMode.ApoSyncAndMonitor);
        _uiTimer.Start();
    }

    // ==================== NAVIGATION ====================

    private void OnNavChanged(object sender, RoutedEventArgs e)
    {
        if (PageEqualizer is null || sender is not RadioButton rb || rb.Tag is not string page) return;

        PageEqualizer.Visibility = page == "Equalizer" ? Visibility.Visible : Visibility.Collapsed;
        PageProfiles.Visibility = page == "Profiles" ? Visibility.Visible : Visibility.Collapsed;
        PageSoundTest.Visibility = page == "SoundTest" ? Visibility.Visible : Visibility.Collapsed;
        PageAudioOutput.Visibility = page == "AudioOutput" ? Visibility.Visible : Visibility.Collapsed;

        if (page == "AudioOutput")
            UpdateHeadsetActivationUi();
    }

    private void OnSidebarAudioCardClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        NavAudioOutput.IsChecked = true;
    }

    private void OnGoToEqualizerClick(object sender, RoutedEventArgs e)
    {
        NavEqualizer.IsChecked = true;
    }

    // ==================== FADER BANK & PROFILES ====================

    private void BuildFaderBank()
    {
        FaderBankGrid.Children.Clear();
        for (int i = 0; i < 10; i++)
        {
            int idx = i;
            bool isFootstepBand = i is 6 or 7;

            var card = new Border
            {
                Background = isFootstepBand
                    ? new LinearGradientBrush(Color.FromRgb(34, 14, 20), Color.FromRgb(18, 10, 14), 90.0)
                    : new LinearGradientBrush(Color.FromRgb(22, 15, 19), Color.FromRgb(14, 10, 12), 90.0),
                BorderBrush = new SolidColorBrush(isFootstepBand ? Color.FromRgb(120, 30, 48) : Color.FromRgb(42, 23, 30)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Margin = new Thickness(i == 0 ? 0 : 4, 0, i == 9 ? 0 : 4, 0),
                Padding = new Thickness(4, 9, 4, 8)
            };
            card.MouseLeftButtonDown += (_, _) =>
            {
                EqCanvas.SelectedBandIndex = idx;
                HighlightSelectedFader(idx);
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var gainTxt = new TextBlock
            {
                Text = "0.0",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(isFootstepBand ? Color.FromRgb(255, 115, 85) : Color.FromRgb(255, 58, 86)),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 6)
            };
            Grid.SetRow(gainTxt, 0);

            var slider = new Slider
            {
                Style = (Style)FindResource("EqVerticalSlider"),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            slider.ValueChanged += (_, args) => OnFaderSliderChanged(idx, args.NewValue);
            slider.MouseDoubleClick += (_, _) => slider.Value = 0.0;
            Grid.SetRow(slider, 1);

            var freqTxt = new TextBlock
            {
                Text = "1k",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(235, 224, 228)),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0)
            };
            Grid.SetRow(freqTxt, 2);

            var tagTxt = new TextBlock
            {
                Text = i switch
                {
                    0 or 1 => "GRAVE",
                    6 or 7 => "PASSOS",
                    _ => ""
                },
                FontSize = 8.5,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(isFootstepBand ? Color.FromRgb(255, 82, 102) : Color.FromRgb(135, 105, 115)),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0)
            };
            Grid.SetRow(tagTxt, 3);

            grid.Children.Add(gainTxt);
            grid.Children.Add(slider);
            grid.Children.Add(freqTxt);
            grid.Children.Add(tagTxt);
            card.Child = grid;

            _faderCards[i] = card;
            _faderSliders[i] = slider;
            _faderGainLabels[i] = gainTxt;
            _faderFreqLabels[i] = freqTxt;
            FaderBankGrid.Children.Add(card);
        }
    }

    private void LoadProfiles()
    {
        _profiles.Clear();
        _profiles.AddRange(PresetLibrary.CreateBuiltInProfiles());
        if (_settings.CustomProfiles is { Count: > 0 })
        {
            foreach (var custom in _settings.CustomProfiles)
            {
                custom.IsBuiltIn = false;
                _profiles.Add(custom);
            }
        }

        RefreshProfileLists();
        SelectProfileById(_settings.ActiveProfileId);
    }

    private void RefreshProfileLists()
    {
        _suppressUiEvents = true;
        LstProfiles.ItemsSource = null;
        LstProfiles.ItemsSource = _profiles;
        CmbQuickProfile.ItemsSource = null;
        CmbQuickProfile.ItemsSource = _profiles;
        _suppressUiEvents = false;
    }

    private void SelectProfileById(string profileId)
    {
        var found = _profiles.FirstOrDefault(p => p.Id == profileId) ?? _profiles.First();
        _activeProfile = found;
        _settings.ActiveProfileId = found.Id;

        _suppressUiEvents = true;
        LstProfiles.SelectedItem = found;
        CmbQuickProfile.SelectedItem = found;
        _suppressUiEvents = false;

        ApplyActiveProfileToEngineAndUi();
    }

    private void ApplyActiveProfileToEngineAndUi()
    {
        AutoCalculatePreampHeadroom();
        _dspChain.ApplyProfile(_activeProfile);
        EqCanvas.Bind(_activeProfile, _dspChain);

        _suppressUiEvents = true;
        TxtProfileName.Text = _activeProfile.Name;
        TxtProfileDesc.Text = string.IsNullOrWhiteSpace(_activeProfile.Description)
            ? "Perfil personalizado de equalização gamer."
            : _activeProfile.Description;
        BtnDeleteProfile.IsEnabled = !_activeProfile.IsBuiltIn;

        for (int i = 0; i < Math.Min(10, _activeProfile.Bands.Count); i++)
        {
            var b = _activeProfile.Bands[i];
            _faderSliders[i].Value = b.GainDb;
            _faderGainLabels[i].Text = FormatShortDb(b.GainDb);
            _faderFreqLabels[i].Text = FormatShortFreq(b.Frequency);
        }

        var c = _activeProfile.Compressor;
        BtnScoutMode.IsChecked = c.Enabled;
        BtnScoutMode.Content = c.Enabled ? "● LIGADO" : "DESLIGADO";
        double intensity = CompressorToIntensity(c);
        SldFootstepIntensity.Value = intensity;
        TxtIntensityLabel.Text = FormatIntensityText(intensity);
        _suppressUiEvents = false;

        HighlightSelectedFader(EqCanvas.SelectedBandIndex);
        SyncAudioEngineParameters();
        UpdateTrayMenu();
        EqCanvas.InvalidateVisual();
    }

    private void AutoCalculatePreampHeadroom()
    {
        if (_activeProfile is null) return;
        double maxBoost = _activeProfile.Bands.Where(b => b.Enabled).Select(b => b.GainDb).DefaultIfEmpty(0).Max();
        _activeProfile.PreampDb = maxBoost > 1.0 ? Math.Round(-maxBoost * 0.55, 1) : 0.0;
    }

    private static double CompressorToIntensity(CompressorSettings c)
    {
        double t = (-14.0 - c.ThresholdDb) / 16.0;
        return Math.Round(Math.Clamp(t * 100.0, 5.0, 100.0));
    }

    private static void ApplyIntensityToCompressor(CompressorSettings c, double intensityPercent)
    {
        double t = Math.Clamp(intensityPercent / 100.0, 0.0, 1.0);
        c.ThresholdDb = Math.Round(-14.0 - t * 16.0, 1);
        c.Ratio = Math.Round(2.2 + t * 6.3, 1);
        c.AttackMs = Math.Round(4.0 - t * 2.8, 1);
        c.ReleaseMs = Math.Round(130.0 - t * 55.0, 0);
        c.MakeupGainDb = Math.Round(1.8 + t * 7.2, 1);
        c.SoftKneeDb = 6.0;
        c.LimiterEnabled = true;
        c.LimiterCeilingDb = -0.5;
    }

    private static string FormatIntensityText(double pct) => pct switch
    {
        < 35 => "Suave",
        < 72 => "Médio",
        _ => "Máximo"
    };

    private void HighlightSelectedFader(int selectedIdx)
    {
        for (int i = 0; i < _faderCards.Length; i++)
        {
            if (_faderCards[i] is null) continue;
            bool isSel = i == selectedIdx;
            bool isFootstep = i is 6 or 7;
            _faderCards[i].BorderBrush = new SolidColorBrush(isSel
                ? Color.FromRgb(255, 36, 66)
                : isFootstep ? Color.FromRgb(120, 30, 48) : Color.FromRgb(42, 23, 30));
        }
    }

    // ==================== AUDIO DEVICES & ACTIVATION ====================

    private void RefreshAudioDevices()
    {
        var renders = _audioEngine.GetRenderDevices();

        _suppressUiEvents = true;
        CmbRenderDevices.ItemsSource = renders;

        var selRen = renders.FirstOrDefault(d => d.Id == _settings.RenderDeviceId)
                     ?? renders.FirstOrDefault(d => d.IsDefault)
                     ?? renders.FirstOrDefault();

        CmbRenderDevices.SelectedItem = selRen;
        _settings.RenderDeviceId = selRen?.Id;
        _suppressUiEvents = false;
    }

    private void StartEngineForMode(AudioEngineMode mode)
    {
        _settings.EngineMode = mode;

        _suppressUiEvents = true;
        BtnToggleSoundLab.Content = mode == AudioEngineMode.SoundTestLab ? "■ PARAR TESTE" : "▶ OUVIR TESTE NO FONE";
        _suppressUiEvents = false;

        _audioEngine.Start(mode, _settings.CaptureDeviceId, _settings.RenderDeviceId, _settings.BufferMs);
        SyncAudioEngineParameters();
    }

    private void SyncAudioEngineParameters()
    {
        if (_activeProfile is not null)
        {
            _nativeApoShm.WriteProfile(_activeProfile, _dspChain.Bypass);
            if (_settings.AutoSyncApo && _apoService.IsInstalled)
                _apoService.SyncProfile(_activeProfile, _dspChain.Bypass, out _);
        }

        UpdateHeadsetActivationUi();
    }

    private void UpdateHeadsetActivationUi()
    {
        bool installedOnDevice = NativeApoInstallerService.IsInstalledOnEndpoint(_settings.RenderDeviceId);
        var tel = _nativeApoShm.ReadTelemetry();
        string headsetName = (CmbRenderDevices.SelectedItem as AudioDeviceInfo)?.Name ?? "Nenhum fone selecionado";
        TxtSidebarHeadsetName.Text = headsetName;

        if (installedOnDevice)
        {
            BtnActivateHeadset.Content = "DESATIVAR NO FONE";
            SidebarStatusDot.Fill = new SolidColorBrush(Color.FromRgb(255, 42, 74));
            TxtSidebarAudioState.Text = tel.IsProcessingLive ? "Ativo ao vivo" : "Ativo no fone";
            SidebarAudioStatusCard.BorderBrush = new SolidColorBrush(Color.FromRgb(140, 28, 48));

            AudioStatusBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(255, 42, 74));
            TxtAudioPageStatusTitle.Text = tel.IsProcessingLive
                ? "● Ativo e equalizando seu fone agora!"
                : "● Ativado neste fone (aguardando som)";
            TxtAudioPageStatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(255, 92, 112));
            TxtAudioPageStatusDesc.Text = _dspChain.Bypass
                ? "O equalizador está desligado no momento (pressione Ctrl+Alt+B ou clique em 'Ligado' no menu)."
                : "Todo áudio de jogos e do Windows neste fone já passa pelo StrikeEQ sem atraso.";
        }
        else
        {
            BtnActivateHeadset.Content = "ATIVAR NO FONE";
            SidebarStatusDot.Fill = new SolidColorBrush(Color.FromRgb(122, 101, 108));
            TxtSidebarAudioState.Text = "Fone não ativado";
            SidebarAudioStatusCard.BorderBrush = new SolidColorBrush(Color.FromRgb(51, 25, 34));

            AudioStatusBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(56, 26, 36));
            TxtAudioPageStatusTitle.Text = "Ainda não ativado neste fone";
            TxtAudioPageStatusTitle.Foreground = Brushes.White;
            TxtAudioPageStatusDesc.Text = "Clique em 'ATIVAR NO FONE' para aplicar o equalizador diretamente no fone selecionado, sem atraso e sem programas extras.";
        }
    }

    private void OnUiTimerTick(object? sender, EventArgs e)
    {
        if (PageEqualizer.Visibility == Visibility.Visible)
            EqCanvas.RefreshFrame();

        float gr = _dspChain.GainReductionDb;
        if (_audioEngine.ActiveMode == AudioEngineMode.ApoSyncAndMonitor)
        {
            var tel = _nativeApoShm.ReadTelemetry();
            if (tel.IsProcessingLive)
            {
                gr = tel.GainReductionDb;
                if (TxtSidebarAudioState.Text != "Ativo ao vivo")
                    UpdateHeadsetActivationUi();
            }
        }

        double maxGrW = Math.Max(30, ((FrameworkElement)MeterGainReduction.Parent).ActualWidth - 2);
        MeterGainReduction.Width = Math.Clamp(gr / 18.0, 0, 1) * maxGrW;
    }

    // ==================== EVENT HANDLERS ====================

    private void OnBypassClick(object sender, RoutedEventArgs e) => SetBypass(BtnMasterPower.IsChecked == true);

    private void OnQuickBypassButtonClick(object sender, RoutedEventArgs e) => ToggleBypass();

    private void ToggleBypass() => SetBypass(!_dspChain.Bypass);

    private void SetBypass(bool bypassed)
    {
        _dspChain.Bypass = bypassed;
        EqCanvas.Bypassed = bypassed;
        _suppressUiEvents = true;
        BtnMasterPower.IsChecked = bypassed;
        BtnTestQuickBypass.Content = bypassed ? "Equalizador: DESLIGADO (clique p/ ligar)" : "Equalizador: LIGADO (clique p/ comparar)";
        _suppressUiEvents = false;
        SyncAudioEngineParameters();
        UpdateTrayMenu();
    }

    private void OnProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || LstProfiles.SelectedItem is not EqProfile selected) return;
        SelectProfileById(selected.Id);
    }

    private void OnQuickProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || CmbQuickProfile.SelectedItem is not EqProfile selected) return;
        SelectProfileById(selected.Id);
    }

    private void OnCanvasBandChanged(EqBand band)
    {
        AutoCalculatePreampHeadroom();
        _dspChain.UpdatePreamp(_activeProfile.PreampDb);
        _dspChain.UpdateBand(band);

        _suppressUiEvents = true;
        if (band.Index >= 0 && band.Index < _faderSliders.Length)
        {
            _faderSliders[band.Index].Value = band.GainDb;
            _faderGainLabels[band.Index].Text = FormatShortDb(band.GainDb);
            _faderFreqLabels[band.Index].Text = FormatShortFreq(band.Frequency);
        }
        _suppressUiEvents = false;

        SyncAudioEngineParameters();
    }

    private void OnFaderSliderChanged(int index, double newValue)
    {
        if (_suppressUiEvents || _activeProfile is null || index >= _activeProfile.Bands.Count) return;

        var band = _activeProfile.Bands[index];
        band.GainDb = Math.Round(newValue, 1);
        _faderGainLabels[index].Text = FormatShortDb(band.GainDb);

        AutoCalculatePreampHeadroom();
        _dspChain.UpdatePreamp(_activeProfile.PreampDb);
        _dspChain.UpdateBand(band);
        EqCanvas.SelectedBandIndex = index;
        HighlightSelectedFader(index);
        SyncAudioEngineParameters();
        EqCanvas.InvalidateVisual();
    }

    private void OnQuickBoostFootstepsClick(object sender, RoutedEventArgs e)
    {
        if (_activeProfile is null || _activeProfile.Bands.Count < 10) return;
        _activeProfile.Bands[6].GainDb = Math.Round(Math.Clamp(_activeProfile.Bands[6].GainDb + 2.0, -15, 15), 1);
        _activeProfile.Bands[7].GainDb = Math.Round(Math.Clamp(_activeProfile.Bands[7].GainDb + 2.5, -15, 15), 1);
        _activeProfile.Bands[8].GainDb = Math.Round(Math.Clamp(_activeProfile.Bands[8].GainDb + 1.0, -15, 15), 1);
        ApplyActiveProfileToEngineAndUi();
    }

    private void OnQuickCutBassClick(object sender, RoutedEventArgs e)
    {
        if (_activeProfile is null || _activeProfile.Bands.Count < 10) return;
        _activeProfile.Bands[0].GainDb = Math.Round(Math.Clamp(_activeProfile.Bands[0].GainDb - 2.5, -15, 15), 1);
        _activeProfile.Bands[1].GainDb = Math.Round(Math.Clamp(_activeProfile.Bands[1].GainDb - 2.0, -15, 15), 1);
        _activeProfile.Bands[2].GainDb = Math.Round(Math.Clamp(_activeProfile.Bands[2].GainDb - 1.5, -15, 15), 1);
        ApplyActiveProfileToEngineAndUi();
    }

    private void OnScoutModeToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents || _activeProfile is null) return;
        bool on = BtnScoutMode.IsChecked == true;
        _activeProfile.Compressor.Enabled = on;
        BtnScoutMode.Content = on ? "● LIGADO" : "DESLIGADO";
        _dspChain.UpdateCompressor(_activeProfile.Compressor);
        SyncAudioEngineParameters();
        UpdateTrayMenu();
    }

    private void ToggleScoutMode()
    {
        bool next = !_activeProfile.Compressor.Enabled;
        _activeProfile.Compressor.Enabled = next;
        _suppressUiEvents = true;
        BtnScoutMode.IsChecked = next;
        BtnScoutMode.Content = next ? "● LIGADO" : "DESLIGADO";
        _suppressUiEvents = false;
        _dspChain.UpdateCompressor(_activeProfile.Compressor);
        SyncAudioEngineParameters();
        UpdateTrayMenu();
    }

    private void OnFootstepIntensityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressUiEvents || _activeProfile is null) return;
        double val = SldFootstepIntensity.Value;
        TxtIntensityLabel.Text = FormatIntensityText(val);
        ApplyIntensityToCompressor(_activeProfile.Compressor, val);
        _dspChain.UpdateCompressor(_activeProfile.Compressor);
        SyncAudioEngineParameters();
    }

    private void OnCloneProfileClick(object sender, RoutedEventArgs e)
    {
        int num = _profiles.Count(p => !p.IsBuiltIn) + 1;
        var clone = _activeProfile.Clone($"Meu Perfil {num}", isBuiltIn: false);
        clone.Category = "Personalizado";
        clone.Description = "Perfil personalizado salvo por você.";
        _profiles.Add(clone);

        RefreshProfileLists();
        SelectProfileById(clone.Id);
        PersistSettings();
    }

    private void OnDeleteProfileClick(object sender, RoutedEventArgs e)
    {
        if (_activeProfile.IsBuiltIn) return;
        _profiles.Remove(_activeProfile);

        RefreshProfileLists();
        SelectProfileById("cs2-precision");
        PersistSettings();
    }

    private void OnResetFlatClick(object sender, RoutedEventArgs e)
    {
        _activeProfile.PreampDb = 0.0;
        for (int i = 0; i < _activeProfile.Bands.Count; i++)
        {
            _activeProfile.Bands[i].Frequency = PresetLibrary.StandardFrequencies[i];
            _activeProfile.Bands[i].GainDb = 0.0;
            _activeProfile.Bands[i].Enabled = true;
        }
        ApplyActiveProfileToEngineAndUi();
    }

    private void OnSaveProfilesClick(object sender, RoutedEventArgs e)
    {
        PersistSettings();
        System.Windows.MessageBox.Show("Perfis e configurações salvos com sucesso!", "StrikeEQ", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnActivateHeadsetClick(object sender, RoutedEventArgs e)
    {
        string? renderId = _settings.RenderDeviceId;
        if (string.IsNullOrWhiteSpace(renderId))
        {
            System.Windows.MessageBox.Show("Selecione primeiro o seu fone de ouvido na lista acima.", "StrikeEQ", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (NativeApoInstallerService.LocateSourceDll() is null)
        {
            System.Windows.MessageBox.Show(
                "O arquivo StrikeEQ.Apo.dll não foi encontrado na pasta do StrikeEQ.\nMantenha os dois arquivos juntos na mesma pasta.",
                "StrikeEQ",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        bool isInstalled = NativeApoInstallerService.IsInstalledOnEndpoint(renderId);
        string deviceName = (CmbRenderDevices.SelectedItem as AudioDeviceInfo)?.Name ?? "Fone selecionado";

        var confirm = System.Windows.MessageBox.Show(
            isInstalled
                ? $"Desativar o StrikeEQ em:\n\n  {deviceName}\n\nO som original do Windows será restaurado (pausa rápida de 2 segundos). Continuar?"
                : $"Ativar o StrikeEQ diretamente no seu fone:\n\n  {deviceName}\n\n• Funciona em qualquer jogo (CS2, Valorant, Warzone, Apex...)\n• Sem atraso no áudio e sem programas extras\n• O som do Windows vai pausar por 2 segundos para ativar\n\nContinuar?",
            isInstalled ? "Desativar no Fone" : "Ativar StrikeEQ no Fone",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        BtnActivateHeadset.IsEnabled = false;
        TxtAudioPageStatusTitle.Text = isInstalled ? "Restaurando som original..." : "Ativando no fone (aguarde 2 seg)...";

        bool ok = NativeApoInstallerService.IsElevated()
            ? (isInstalled
                ? NativeApoInstallerService.UninstallFromEndpointElevated(renderId, out string msg)
                : NativeApoInstallerService.InstallOnEndpointElevated(renderId, out msg))
            : NativeApoInstallerService.RunElevatedAction(install: !isInstalled, renderId, out msg);

        BtnActivateHeadset.IsEnabled = true;

        SyncAudioEngineParameters();
        StartEngineForMode(AudioEngineMode.ApoSyncAndMonitor);
        UpdateHeadsetActivationUi();

        string friendlyMsg = ok
            ? (isInstalled ? "StrikeEQ desativado neste fone." : "Pronto! O StrikeEQ agora está ativo no seu fone.")
            : msg;

        System.Windows.MessageBox.Show(friendlyMsg, "StrikeEQ", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void OnRefreshDevicesClick(object sender, RoutedEventArgs e)
    {
        RefreshAudioDevices();
        StartEngineForMode(_settings.EngineMode);
        UpdateHeadsetActivationUi();
    }

    private void OnAudioDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || _settings is null || _audioEngine is null) return;
        _settings.RenderDeviceId = (CmbRenderDevices.SelectedItem as AudioDeviceInfo)?.Id;
        StartEngineForMode(_settings.EngineMode);
        UpdateHeadsetActivationUi();
    }

    private void OnLabScenarioChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || _soundLab is null || CmbSoundLabScenario.SelectedItem is not ComboBoxItem item) return;
        if (item.Tag is string tag && Enum.TryParse<SoundLabScenario>(tag, out var scenario))
            _soundLab.Scenario = scenario;
    }

    private void OnToggleSoundLabClick(object sender, RoutedEventArgs e)
    {
        if (_audioEngine.ActiveMode == AudioEngineMode.SoundTestLab && _audioEngine.IsRunning)
            StartEngineForMode(AudioEngineMode.ApoSyncAndMonitor);
        else
            StartEngineForMode(AudioEngineMode.SoundTestLab);
    }

    private void OnLoadWavClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Escolher Áudio .WAV para Testar",
            Filter = "Áudio WAV (*.wav)|*.wav"
        };
        if (dlg.ShowDialog() == true)
        {
            if (_soundLab.LoadWavFile(dlg.FileName, out string err))
            {
                CmbLabWavItem.Content = $"WAV: {_soundLab.LoadedWavName}";
                CmbSoundLabScenario.SelectedItem = CmbLabWavItem;
                StartEngineForMode(AudioEngineMode.SoundTestLab);
            }
            else
            {
                System.Windows.MessageBox.Show($"Não foi possível abrir o arquivo: {err}", "StrikeEQ", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void OnAutoDetectToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents || _settings is null || _gameDetector is null) return;
        bool val = sender is CheckBox cb && cb.IsChecked == true;
        _settings.AutoGameDetect = val;
        _gameDetector.Enabled = val;

        _suppressUiEvents = true;
        ChkAutoGameDetect.IsChecked = val;
        ChkAutoGameDetectProfiles.IsChecked = val;
        _suppressUiEvents = false;
    }

    private void OnMinimizeTrayToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents || _settings is null) return;
        _settings.MinimizeToTray = ChkMinimizeTray.IsChecked == true;
    }

    private async void OnCheckUpdatesClick(object sender, RoutedEventArgs e)
    {
        BtnCheckUpdates.IsEnabled = false;
        TxtUpdateStatus.Text = "Consultando GitHub Releases...";
        try
        {
            var res = await _updateService.CheckForUpdatesAsync();
            TxtUpdateStatus.Text = res.Message;

            if (res.UpdateAvailable && !string.IsNullOrWhiteSpace(res.ReleaseUrl))
            {
                var open = System.Windows.MessageBox.Show(
                    $"{res.Message}\n\nDeseja abrir a página de download no GitHub agora?",
                    "Nova Versão — StrikeEQ",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);
                if (open == MessageBoxResult.Yes)
                    GitHubUpdateService.OpenUrl(res.ReleaseUrl);
            }
        }
        finally
        {
            BtnCheckUpdates.IsEnabled = true;
        }
    }

    private void OnGameDetected(EqProfile matchedProfile, string processName)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _profileBeforeAutoSwitch ??= _activeProfile.Id;
            TxtActiveGame.Text = $"● JOGANDO: {processName.Replace(".exe", "", StringComparison.OrdinalIgnoreCase).ToUpperInvariant()}";
            GameBadgeBorder.Visibility = Visibility.Visible;

            if (_activeProfile.Id != matchedProfile.Id)
            {
                SelectProfileById(matchedProfile.Id);
                _trayService?.ShowNotification("StrikeEQ", $"Jogo detectado → {matchedProfile.Name}");
            }
        });
    }

    private void OnGameClosed()
    {
        Dispatcher.BeginInvoke(() =>
        {
            GameBadgeBorder.Visibility = Visibility.Collapsed;
            if (!string.IsNullOrEmpty(_profileBeforeAutoSwitch))
            {
                string restoreId = _profileBeforeAutoSwitch;
                _profileBeforeAutoSwitch = null;
                SelectProfileById(restoreId);
            }
        });
    }

    private void OnGlobalHotkeyPressed(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.ToggleBypass:
                ToggleBypass();
                break;
            case HotkeyAction.ToggleScoutMode:
                ToggleScoutMode();
                break;
            case HotkeyAction.PresetSlot1:
                SelectProfileById("cs2-precision");
                break;
            case HotkeyAction.PresetSlot2:
                SelectProfileById("valorant-clarity");
                break;
            case HotkeyAction.PresetSlot3:
                SelectProfileById("warzone-footsteps");
                break;
            case HotkeyAction.PresetSlot4:
                SelectProfileById("flat-studio");
                break;
        }
    }

    private void UpdateTrayMenu()
    {
        _trayService?.RebuildMenu(_profiles, _activeProfile.Id, _dspChain.Bypass, _activeProfile.Compressor.Enabled);
    }

    private void PersistSettings()
    {
        _settings.CustomProfiles = _profiles.Where(p => !p.IsBuiltIn).ToList();
        PresetLibrary.SaveSettings(_settings);
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        PersistSettings();
        if (!_isRealExit && _settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            _trayService?.ShowNotification("StrikeEQ em segundo plano", "Rodando na bandeja. Use Ctrl+Alt+B para ligar/desligar.");
            return;
        }
        Cleanup();
    }

    private void ExitApplication()
    {
        _isRealExit = true;
        PersistSettings();
        Cleanup();
        System.Windows.Application.Current.Shutdown();
    }

    private void Cleanup()
    {
        _uiTimer.Stop();
        _gameDetector.Dispose();
        _hotkeyService.Dispose();
        _audioEngine.Dispose();
        _nativeApoShm.Dispose();
        _trayService?.Dispose();
    }

    private static string FormatShortDb(double db) => db >= 0 ? $"+{db:0.0}" : $"{db:0.0}";
    private static string FormatShortFreq(double hz) => hz >= 1000 ? $"{hz / 1000.0:0.#}k" : $"{hz:0}";
}
