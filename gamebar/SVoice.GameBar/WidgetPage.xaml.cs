using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Gaming.XboxGameBar;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Devices;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace SVoice.GameBar
{
    public sealed partial class WidgetPage : Page
    {
        private enum PanelKind { None, Voices, Settings }

        private const int MaxHistory = 12;
        private const double CompactHeight = 118;
        private const double DefaultExpandedHeight = 300;
        private const string EchoSettingKey = "echoEnabled";
        private const string VoiceSettingKey = "selectedVoiceProfileId";
        private const string SpeedSettingKey = "speechSpeed";
        private const string VolumeSettingKey = "volume";
        private const string OutputSettingKey = "outputDeviceId";
        private const string CompactSettingKey = "compactMode";
        private const string ExpandedHeightSettingKey = "expandedHeight";
        private const string HistoryFileName = "history.json";
        private const string WindowsVoiceId = "";

        private static readonly Color Accent = Color.FromArgb(255, 139, 233, 210);
        private static readonly Color Warning = Color.FromArgb(255, 255, 199, 118);
        private static readonly Color Working = Color.FromArgb(255, 191, 166, 255);
        private static readonly Color Danger = Color.FromArgb(255, 255, 124, 135);

        private readonly SpeechSynthesizer _synthesizer = new SpeechSynthesizer();
        private readonly MediaPlayer _player = new MediaPlayer();
        private readonly MediaPlayer _echoPlayer = new MediaPlayer();
        private readonly XttsBridgeClient _xttsBridge = new XttsBridgeClient();
        private readonly List<string> _history = new List<string>();
        private readonly List<OutputDeviceChoice> _outputDevices = new List<OutputDeviceChoice>();
        private readonly DispatcherTimer _jobTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private readonly DispatcherTimer _errorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };

        private IRandomAccessStream? _currentStream;
        private IRandomAccessStream? _echoStream;
        private IRandomAccessStream? _synthesisKeepAliveTemplateStream;
        private IRandomAccessStream? _synthesisKeepAliveStream;
        private XboxGameBarWidget? _widget;
        private XboxGameBarWidgetActivity? _speechActivity;
        private IReadOnlyList<ClonedVoiceProfile> _profiles = Array.Empty<ClonedVoiceProfile>();
        private XttsHealth? _lastHealth;
        private PanelKind _panel = PanelKind.None;
        private string? _selectedProfileId;
        private string _backendLabel = string.Empty;
        private bool _initialized;
        private bool _usingVirtualCable;
        private bool _echoEnabled;
        private bool _isGenerating;
        private bool _xttsAvailable;
        private bool _xttsServiceRunning;
        private bool _serviceStoppedByUser;
        private bool _suppressSelectionEvents;
        private bool _suppressKeepLoadedChange;
        private bool _compact;
        private bool _panelExpandedTemporarily;
        private double _expandedHeight = DefaultExpandedHeight;
        private double _speed = 1.0;
        private double _volume = 1.0;
        private string? _outputDeviceId;

        public WidgetPage()
        {
            InitializeComponent();
            _player.MediaEnded += Player_MediaEnded;
            _player.MediaFailed += Player_MediaFailed;
            _echoPlayer.MediaFailed += EchoPlayer_MediaFailed;
            _jobTimer.Tick += JobTimer_Tick;
            _errorTimer.Tick += (_, _) => { _errorTimer.Stop(); ErrorPanel.Visibility = Visibility.Collapsed; };
            ChatArea.SizeChanged += (_, _) => UpdateBubbleWidths();
        }

        // ------------------------------------------------------------- lifecycle

        protected override void OnNavigatedTo(NavigationEventArgs args)
        {
            if (_widget != null)
            {
                _widget.RequestedOpacityChanged -= Widget_RequestedOpacityChanged;
                _widget.SettingsClicked -= Widget_SettingsClicked;
            }

            _widget = args.Parameter as XboxGameBarWidget;
            if (_widget != null)
            {
                _widget.RequestedOpacityChanged += Widget_RequestedOpacityChanged;
                try
                {
                    _widget.SettingsSupported = true;
                    _widget.SettingsClicked += Widget_SettingsClicked;
                }
                catch (Exception exception)
                {
                    App.Log($"Widget settings hook unavailable: {exception.Message}");
                }

                ApplyRequestedOpacity();
            }
        }

        private async void Page_Loaded(object sender, RoutedEventArgs args)
        {
            if (_initialized)
            {
                MessageBox.Focus(FocusState.Programmatic);
                return;
            }

            _initialized = true;
            try
            {
                App.Log($"WidgetPage loaded. GameBarContext={_widget != null}");
                LoadSettings();
                await LoadHistoryAsync();
                await ConfigureAudioOutputAsync();
                await PrepareSynthesisAudioKeepAliveAsync();
                await ApplyCompactAsync(_compact, persist: false);
                await RefreshXttsAsync();
                await Ui(() => MessageBox.Focus(FocusState.Programmatic));
                App.Log("WidgetPage initialized.");
            }
            catch (Exception exception)
            {
                App.Log($"WidgetPage initialization failed: {exception}");
                await Ui(() => ShowError("Não foi possível inicializar o SVoice.", "Feche e abra o widget novamente."));
            }
        }

        private void Page_Unloaded(object sender, RoutedEventArgs args)
        {
            // Dismissing Game Bar unloads the visual tree even though a speech
            // request may still be running. Keep the XboxGameBarWidgetActivity
            // and MediaPlayer alive; they are completed by the normal
            // synthesis/playback end path. Stopping them here silently discarded
            // speech generated while the overlay was hidden.
            App.Log($"WidgetPage unloaded. Generating={_isGenerating}; Playback={_player.PlaybackSession.PlaybackState}; Activity={_speechActivity != null}.");
            if (!_isGenerating)
            {
                _jobTimer.Stop();
            }
        }

        // -------------------------------------------------------------- settings

        private void LoadSettings()
        {
            var values = ApplicationData.Current.LocalSettings.Values;
            _echoEnabled = values.TryGetValue(EchoSettingKey, out var echo) && echo is bool enabled && enabled;
            _speed = values.TryGetValue(SpeedSettingKey, out var speed) && speed is double storedSpeed ? Math.Clamp(storedSpeed, 0.5, 1.5) : 1.0;
            _volume = values.TryGetValue(VolumeSettingKey, out var volume) && volume is double storedVolume ? Math.Clamp(storedVolume, 0, 1) : 1.0;
            _outputDeviceId = values.TryGetValue(OutputSettingKey, out var output) ? output as string : null;
            _selectedProfileId = values.TryGetValue(VoiceSettingKey, out var voice) ? voice as string : null;
            _compact = values.TryGetValue(CompactSettingKey, out var compact) && compact is bool storedCompact && storedCompact;
            _expandedHeight = values.TryGetValue(ExpandedHeightSettingKey, out var height) && height is double storedHeight && storedHeight >= 200
                ? storedHeight
                : DefaultExpandedHeight;
            _player.Volume = _volume;
            _echoPlayer.Volume = _volume;
            _synthesizer.Options.SpeakingRate = _speed;
            _suppressSelectionEvents = true;
            SpeedSlider.Minimum = 50;
            SpeedSlider.Maximum = 150;
            SpeedSlider.StepFrequency = 5;
            SpeedSlider.Value = Math.Round(_speed * 100);
            VolumeSlider.Minimum = 0;
            VolumeSlider.Maximum = 100;
            VolumeSlider.Value = Math.Round(_volume * 100);
            _suppressSelectionEvents = false;
            SpeedValueText.Text = $"{_speed:0.00}×";
            VolumeValueText.Text = $"{Math.Round(_volume * 100)}%";

            ComputeModeBox.Items.Clear();
            ComputeModeBox.Items.Add(new ComputeModeChoice("auto", "Automático (recomendado)"));
            ComputeModeBox.Items.Add(new ComputeModeChoice("cuda", "NVIDIA CUDA"));
            ComputeModeBox.Items.Add(new ComputeModeChoice("directml", "AMD DirectML (experimental)"));
            ComputeModeBox.Items.Add(new ComputeModeChoice("cpu", "CPU"));
            EchoButton.IsChecked = _echoEnabled;
            UpdateEchoVisual();
        }

        private async Task LoadHistoryAsync()
        {
            try
            {
                var path = Path.Combine(ApplicationData.Current.LocalFolder.Path, HistoryFileName);
                if (File.Exists(path))
                {
                    var items = JsonSerializer.Deserialize<List<string>>(await File.ReadAllTextAsync(path));
                    if (items != null)
                    {
                        _history.AddRange(items.Where(item => !string.IsNullOrWhiteSpace(item)).Take(MaxHistory));
                    }
                }
            }
            catch (Exception exception)
            {
                App.Log($"History could not be loaded: {exception.Message}");
            }

            await Ui(RenderChat);
        }

        private void SaveHistory()
        {
            try
            {
                File.WriteAllText(Path.Combine(ApplicationData.Current.LocalFolder.Path, HistoryFileName), JsonSerializer.Serialize(_history));
            }
            catch (Exception exception)
            {
                App.Log($"History could not be saved: {exception.Message}");
            }
        }

        private void AddToHistory(string text)
        {
            _history.RemoveAll(item => string.Equals(item, text, StringComparison.Ordinal));
            _history.Insert(0, text);
            while (_history.Count > MaxHistory)
            {
                _history.RemoveAt(_history.Count - 1);
            }

            SaveHistory();
            RenderChat();
        }

        // ------------------------------------------------------------------ chat

        private void RenderChat()
        {
            ChatStack.Children.Clear();
            ChatEmptyText.Visibility = _history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            // Oldest first, newest at the bottom like a conversation.
            foreach (var text in Enumerable.Reverse(_history))
            {
                ChatStack.Children.Add(CreateBubble(text));
            }

            UpdateBubbleWidths();
            ChatScroll.UpdateLayout();
            ChatScroll.ChangeView(null, ChatScroll.ScrollableHeight, null, disableAnimation: true);
        }

        private Button CreateBubble(string text)
        {
            var bubble = new Button
            {
                Tag = text,
                HorizontalAlignment = HorizontalAlignment.Right,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(11, 6, 11, 7),
                Background = new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(24, 139, 233, 210)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12, 12, 3, 12),
                Content = new TextBlock
                {
                    Text = text,
                    FontSize = 11.5,
                    Foreground = new SolidColorBrush(Color.FromArgb(225, 255, 255, 255)),
                    TextWrapping = TextWrapping.Wrap,
                },
            };
            ToolTipService.SetToolTip(bubble, "Repetir esta frase");
            bubble.Click += HistoryItem_Click;
            return bubble;
        }

        private void UpdateBubbleWidths()
        {
            var width = ChatArea.ActualWidth;
            if (width <= 0)
            {
                return;
            }

            foreach (var child in ChatStack.Children.OfType<Button>())
            {
                child.MaxWidth = Math.Max(120, width * 0.8);
            }
        }

        private async void HistoryItem_Click(object sender, RoutedEventArgs args)
        {
            if ((sender as FrameworkElement)?.Tag is string text)
            {
                await SpeakAsync(text);
            }
        }

        // --------------------------------------------------------------- compact

        private async void CompactButton_Click(object sender, RoutedEventArgs args)
        {
            await ApplyCompactAsync(!_compact, persist: true);
        }

        private async Task ApplyCompactAsync(bool compact, bool persist)
        {
            if (compact && !_compact)
            {
                var current = Window.Current?.Bounds.Height ?? 0;
                if (current >= 200)
                {
                    _expandedHeight = current;
                    ApplicationData.Current.LocalSettings.Values[ExpandedHeightSettingKey] = _expandedHeight;
                }
            }

            _compact = compact;
            if (persist)
            {
                ApplicationData.Current.LocalSettings.Values[CompactSettingKey] = compact;
            }

            if (compact && _panel != PanelKind.None)
            {
                ShowPanel(PanelKind.None);
            }

            ChatArea.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            ContentRow.Height = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            CompactIcon.Glyph = compact ? "" : "";
            ToolTipService.SetToolTip(CompactButton, compact ? "Mostrar o histórico" : "Modo compacto: esconder o histórico");
            await ResizeWindowAsync(compact ? CompactHeight : _expandedHeight);
        }

        private async Task ResizeWindowAsync(double height)
        {
            if (_widget == null)
            {
                return;
            }

            try
            {
                var width = Window.Current?.Bounds.Width ?? 0;
                if (width < 200)
                {
                    width = 500;
                }

                await _widget.TryResizeWindowAsync(new Size(width, height));
            }
            catch (Exception exception)
            {
                App.Log($"Window resize failed: {exception.Message}");
            }
        }

        // ---------------------------------------------------------- audio output

        private async Task ConfigureAudioOutputAsync()
        {
            _outputDevices.Clear();
            _outputDevices.Add(new OutputDeviceChoice("Padrão do Windows", null, false));
            try
            {
                var devices = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector());
                foreach (var device in devices.OrderBy(device => device.Name))
                {
                    var name = device.Name ?? string.Empty;
                    var isCable = name.IndexOf("CABLE Input", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                  name.IndexOf("VB-Audio", StringComparison.OrdinalIgnoreCase) >= 0;
                    _outputDevices.Add(new OutputDeviceChoice(name, device.Id, isCable));
                }
            }
            catch (Exception exception)
            {
                App.Log($"Audio device enumeration failed: {exception.Message}");
            }

            var selected = _outputDevices.FirstOrDefault(device => device.Id != null && device.Id == _outputDeviceId)
                ?? _outputDevices.FirstOrDefault(device => device.IsVirtualCable)
                ?? _outputDevices[0];

            await Ui(() =>
            {
                _suppressSelectionEvents = true;
                OutputDeviceBox.Items.Clear();
                foreach (var device in _outputDevices)
                {
                    OutputDeviceBox.Items.Add(device);
                }

                OutputDeviceBox.SelectedItem = selected;
                _suppressSelectionEvents = false;
            });
            await ApplyOutputDeviceAsync(selected);
        }

        private async Task ApplyOutputDeviceAsync(OutputDeviceChoice choice)
        {
            DeviceInformation? device = null;
            if (choice.Id != null)
            {
                try
                {
                    device = await DeviceInformation.CreateFromIdAsync(choice.Id);
                }
                catch (Exception exception)
                {
                    App.Log($"Audio device unavailable: {exception.Message}");
                }
            }

            await Ui(() => ApplyOutputDevice(choice, device));
        }

        private void ApplyOutputDevice(OutputDeviceChoice choice, DeviceInformation? device)
        {
            _player.AudioDevice = device;
            _usingVirtualCable = choice.IsVirtualCable && device != null;
            EchoButton.IsEnabled = _usingVirtualCable;
            if (!_usingVirtualCable)
            {
                StopEchoPlayback();
            }

            UpdateEchoVisual();
            OutputSummaryText.Text = _usingVirtualCable
                ? "A voz é enviada para CABLE Input. No Discord, em Configurações › Voz e vídeo, escolha CABLE Output como microfone. Eco (alto-falante no cabeçalho) reproduz a fala também nos seus fones."
                : _outputDevices.Any(item => item.IsVirtualCable)
                    ? $"Saída atual: {choice.Label}. Selecione CABLE Input para enviar a voz ao Discord."
                    : "VB-CABLE não encontrado. Execute Iniciar › SVoice › Reparar SVoice para instalar o microfone virtual.";
        }

        private async void OutputDeviceBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
        {
            if (_suppressSelectionEvents || OutputDeviceBox.SelectedItem is not OutputDeviceChoice choice)
            {
                return;
            }

            _outputDeviceId = choice.Id;
            ApplicationData.Current.LocalSettings.Values[OutputSettingKey] = choice.Id ?? string.Empty;
            StopPlayback();
            await ApplyOutputDeviceAsync(choice);
        }

        private void EchoButton_Click(object sender, RoutedEventArgs args)
        {
            if (!_usingVirtualCable)
            {
                EchoButton.IsChecked = false;
                ShowError("O Eco só funciona com o VB-CABLE selecionado como saída.", "Abra Ajustes › Saída de áudio e escolha CABLE Input.");
                UpdateEchoVisual();
                return;
            }

            _echoEnabled = EchoButton.IsChecked == true;
            ApplicationData.Current.LocalSettings.Values[EchoSettingKey] = _echoEnabled;
            if (!_echoEnabled)
            {
                StopEchoPlayback();
            }

            UpdateEchoVisual();
        }

        private void UpdateEchoVisual()
        {
            var active = _echoEnabled && _usingVirtualCable;
            EchoIcon.Foreground = new SolidColorBrush(active ? Accent : Color.FromArgb(_usingVirtualCable ? (byte)156 : (byte)70, 255, 255, 255));
            EchoIcon.Glyph = active ? "" : "";
            ToolTipService.SetToolTip(EchoButton, _usingVirtualCable
                ? (active ? "Eco ligado: você ouve a fala nos seus fones" : "Eco desligado: ligar para ouvir a fala nos seus fones")
                : "Eco requer o VB-CABLE como saída (Ajustes › Saída de áudio)");
        }

        private void SpeedSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs args)
        {
            if (_suppressSelectionEvents)
            {
                return;
            }

            _speed = Math.Clamp(args.NewValue / 100.0, 0.5, 1.5);
            SpeedValueText.Text = $"{_speed:0.00}×";
            _synthesizer.Options.SpeakingRate = _speed;
            ApplicationData.Current.LocalSettings.Values[SpeedSettingKey] = _speed;
        }

        private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs args)
        {
            if (_suppressSelectionEvents)
            {
                return;
            }

            _volume = Math.Clamp(args.NewValue / 100.0, 0, 1);
            VolumeValueText.Text = $"{Math.Round(_volume * 100)}%";
            _player.Volume = _volume;
            _echoPlayer.Volume = _volume;
            ApplicationData.Current.LocalSettings.Values[VolumeSettingKey] = _volume;
        }

        // ------------------------------------------------------------- xtts state

        private async Task RefreshXttsAsync(string? preferredProfileId = null)
        {
            if (_serviceStoppedByUser)
            {
                await Ui(() => SetServicePowerState(false));
                return;
            }

            await Ui(() => SetState("CONECTANDO", Working));

            XttsHealth? health = null;
            IReadOnlyList<ClonedVoiceProfile> profiles = Array.Empty<ClonedVoiceProfile>();
            Exception? failure = null;
            try
            {
                health = await _xttsBridge.PingAsync();
                profiles = await _xttsBridge.GetProfilesAsync();
                App.Log($"XTTS health: state={health.State}; model_ready={health.ModelReady}; backend={health.ActiveBackend}; profiles={profiles.Count}.");
            }
            catch (Exception exception)
            {
                failure = exception;
                App.Log($"XTTS unavailable: {exception}");
            }

            _lastHealth = health;
            _xttsAvailable = health != null;
            if (health != null)
            {
                _profiles = profiles;
                _backendLabel = ShortBackend(health.ActiveBackend) ?? ShortBackend(health.ComputeMode == "auto" ? null : health.ComputeMode) ?? string.Empty;
            }

            if (preferredProfileId != null)
            {
                _selectedProfileId = preferredProfileId;
                ApplicationData.Current.LocalSettings.Values[VoiceSettingKey] = preferredProfileId;
            }

            await Ui(() =>
            {
                SetServicePowerState(health != null);
                EnsureSelectedVoice();
                RenderVoiceChip();
                RenderVoicesPanel();
                ApplyHealth(health, failure);
            });
        }

        private static string? ShortBackend(string? backend)
        {
            return backend switch
            {
                "cuda" => "CUDA",
                "directml" => "DIRECTML",
                "rocm" => "ROCM",
                "cpu" => "CPU",
                _ => null,
            };
        }

        private ClonedVoiceProfile? SelectedProfile =>
            string.IsNullOrEmpty(_selectedProfileId) ? null : _profiles.FirstOrDefault(profile => profile.Id == _selectedProfileId);

        private bool IsWindowsVoiceSelected => _selectedProfileId == WindowsVoiceId;

        private void EnsureSelectedVoice()
        {
            if (_selectedProfileId == WindowsVoiceId || SelectedProfile != null)
            {
                return;
            }

            // Prefer a usable cloned voice; the Windows voice is an explicit choice, never a silent fallback
            // unless there is no cloned voice at all.
            var candidate = _profiles.FirstOrDefault(profile => profile.IsUsable) ?? _profiles.FirstOrDefault();
            _selectedProfileId = candidate?.Id ?? WindowsVoiceId;
            ApplicationData.Current.LocalSettings.Values[VoiceSettingKey] = _selectedProfileId;
        }

        private void RenderVoiceChip()
        {
            var profile = SelectedProfile;
            if (IsWindowsVoiceSelected || profile == null)
            {
                VoiceChipText.Text = "Voz do Windows";
                VoiceChipIcon.Glyph = "";
            }
            else
            {
                VoiceChipText.Text = profile.Name;
                VoiceChipIcon.Glyph = profile.IsUsable ? "" : "";
            }
        }

        private void ApplyHealth(XttsHealth? health, Exception? failure)
        {
            if (health == null)
            {
                SetState("XTTS INDISPONÍVEL", Danger);
                if (failure != null)
                {
                    ShowError(failure.Message, (failure as XttsBridgeException)?.Action ?? "Use RECONECTAR em Ajustes ou execute o reparo do SVoice.");
                }

                return;
            }

            if (!health.ModelReady)
            {
                SetState("MODELO AUSENTE", Warning);
                ShowError("O modelo XTTS v2 ainda não foi baixado.", "Abra Ajustes › Diagnóstico e use MODELO para baixá-lo.");
                return;
            }

            SetReadyState();
            SyncComputeModeBox(health.ComputeMode);
        }

        private void SyncComputeModeBox(string mode)
        {
            _suppressSelectionEvents = true;
            ComputeModeBox.SelectedItem = ComputeModeBox.Items.OfType<ComputeModeChoice>().FirstOrDefault(choice => choice.WireName == mode)
                ?? ComputeModeBox.Items.OfType<ComputeModeChoice>().First();
            _suppressSelectionEvents = false;
        }

        private async void ComputeModeBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
        {
            if (_suppressSelectionEvents || ComputeModeBox.SelectedItem is not ComputeModeChoice choice)
            {
                return;
            }

            if (_isGenerating)
            {
                SyncComputeModeBox(_lastHealth?.ComputeMode ?? "auto");
                ShowError("Aguarde a operação atual antes de trocar o processamento.");
                return;
            }

            try
            {
                SetState("REINICIANDO", Working);
                using var response = await _xttsBridge.SetComputeModeAsync(choice.WireName);
                App.Log($"Compute mode set to {choice.WireName}.");
            }
            catch (Exception exception)
            {
                App.Log($"Compute mode change failed: {exception}");
                await Ui(() => ShowError(exception));
            }

            await RefreshXttsAsync();
            if (_panel == PanelKind.Settings)
            {
                await LoadDiagnosticsAsync();
            }
        }

        // ---------------------------------------------------------------- voices

        private void VoiceChip_Click(object sender, RoutedEventArgs args) => TogglePanel(PanelKind.Voices);

        private void RenderVoicesPanel()
        {
            var items = new List<VoiceItem>();
            foreach (var profile in _profiles)
            {
                items.Add(new VoiceItem(profile, profile.Id == _selectedProfileId));
            }

            items.Add(new VoiceItem(null, IsWindowsVoiceSelected));
            VoicesList.ItemsSource = items;
            VoicesHintText.Text = _profiles.Count == 0
                ? "Nenhuma voz clonada ainda. Use CLONAR VOZ com áudios limpos de uma única pessoa. Áudios longos são cortados automaticamente nas pausas. Use apenas vozes com autorização do titular."
                : "Clique para selecionar a voz usada no chat. Use apenas vozes com autorização do titular.";
        }

        private void SelectVoice_Click(object sender, RoutedEventArgs args)
        {
            if ((sender as FrameworkElement)?.Tag is not VoiceItem item)
            {
                return;
            }

            _selectedProfileId = item.Profile?.Id ?? WindowsVoiceId;
            ApplicationData.Current.LocalSettings.Values[VoiceSettingKey] = _selectedProfileId;
            RenderVoiceChip();
            RenderVoicesPanel();
            ShowPanel(PanelKind.None);
        }

        private async void PanelAction_Click(object sender, RoutedEventArgs args)
        {
            if (_panel == PanelKind.Voices)
            {
                await CloneVoiceAsync();
            }
        }

        private async Task CloneVoiceAsync()
        {
            if (_isGenerating)
            {
                return;
            }

            try
            {
                var picker = new FileOpenPicker
                {
                    SuggestedStartLocation = PickerLocationId.MusicLibrary,
                    ViewMode = PickerViewMode.List,
                };
                foreach (var extension in new[] { ".wav", ".mp3", ".m4a", ".flac", ".ogg" })
                {
                    picker.FileTypeFilter.Add(extension);
                }

                var files = await picker.PickMultipleFilesAsync();
                if (files.Count == 0)
                {
                    return;
                }

                var invalidFile = files.FirstOrDefault(file => string.IsNullOrWhiteSpace(file.Path));
                if (invalidFile != null)
                {
                    throw new XttsBridgeException(
                        $"O arquivo {invalidFile.Name} não possui um caminho local acessível.",
                        "invalid_path",
                        "Copie o áudio para uma pasta local (Músicas ou Documentos) e tente novamente.");
                }

                // The picker continuation may resume off the UI thread; XAML objects
                // must be created and shown on the dispatcher thread.
                var requestedName = await Ui(async () =>
                {
                    var nameBox = new TextBox
                    {
                        Header = "Nome da voz",
                        MaxLength = 80,
                        PlaceholderText = "Ex.: Minha voz",
                        Text = Path.GetFileNameWithoutExtension(files[0].Name),
                    };
                    var content = new StackPanel { Spacing = 10 };
                    content.Children.Add(new TextBlock
                    {
                        Text = $"{files.Count} áudio(s) selecionado(s). Use gravações limpas da mesma pessoa. Áudios longos demoram mais e são cortados automaticamente nas pausas.",
                        TextWrapping = TextWrapping.Wrap,
                    });
                    content.Children.Add(nameBox);
                    var dialog = new ContentDialog
                    {
                        Title = "Clonar voz com XTTS v2",
                        Content = content,
                        PrimaryButtonText = "Clonar",
                        CloseButtonText = "Cancelar",
                        DefaultButton = ContentDialogButton.Primary,
                    };
                    return await dialog.ShowAsync() == ContentDialogResult.Primary ? nameBox.Text : null;
                });
                if (requestedName == null)
                {
                    return;
                }

                await Ui(() =>
                {
                    _isGenerating = true;
                    PanelActionButton.IsEnabled = false;
                    BeginJob("CLONANDO", "Preparando os áudios de referência…");
                });

                IReadOnlyList<string> referencePaths = files.Select(file => file.Path).ToArray();
                _serviceStoppedByUser = false;
                var profile = await _xttsBridge.CreateProfileAsync(requestedName, referencePaths);
                App.Log($"XTTS profile created: {profile.Id}.");
                await Ui(EndJob);
                await RefreshXttsAsync(profile.Id);
            }
            catch (XttsBridgeException exception) when (exception.IsCancellation)
            {
                await Ui(() => { EndJob(); SetReadyState(); });
            }
            catch (Exception exception)
            {
                App.Log($"XTTS profile creation failed: {exception}");
                await Ui(() => { EndJob(); ShowError(exception); });
            }
            finally
            {
                await Ui(() =>
                {
                    _isGenerating = false;
                    PanelActionButton.IsEnabled = true;
                });
            }
        }

        private async void RenameVoice_Click(object sender, RoutedEventArgs args)
        {
            if ((sender as FrameworkElement)?.Tag is not VoiceItem { Profile: { } profile } || _isGenerating)
            {
                return;
            }

            try
            {
                var nameBox = new TextBox { Header = "Novo nome", MaxLength = 80, Text = profile.Name };
                var dialog = new ContentDialog
                {
                    Title = "Renomear voz",
                    Content = nameBox,
                    PrimaryButtonText = "Salvar",
                    CloseButtonText = "Cancelar",
                    DefaultButton = ContentDialogButton.Primary,
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(nameBox.Text))
                {
                    return;
                }

                _serviceStoppedByUser = false;
                await _xttsBridge.RenameProfileAsync(profile.Id, nameBox.Text.Trim());
                await RefreshXttsAsync();
            }
            catch (Exception exception)
            {
                App.Log($"Rename failed: {exception}");
                await Ui(() => ShowError(exception));
            }
        }

        private async void DeleteVoice_Click(object sender, RoutedEventArgs args)
        {
            if ((sender as FrameworkElement)?.Tag is not VoiceItem { Profile: { } profile } || _isGenerating)
            {
                return;
            }

            try
            {
                var dialog = new ContentDialog
                {
                    Title = "Excluir voz clonada?",
                    Content = new TextBlock
                    {
                        Text = $"A voz “{profile.Name}” e os áudios de referência processados serão removidos. Esta ação não pode ser desfeita.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    PrimaryButtonText = "Excluir",
                    CloseButtonText = "Cancelar",
                    DefaultButton = ContentDialogButton.Close,
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                {
                    return;
                }

                _serviceStoppedByUser = false;
                await _xttsBridge.DeleteProfileAsync(profile.Id);
                App.Log($"XTTS profile deleted: {profile.Id}.");
                if (_selectedProfileId == profile.Id)
                {
                    _selectedProfileId = null;
                }

                await RefreshXttsAsync();
            }
            catch (Exception exception)
            {
                App.Log($"Delete failed: {exception}");
                await Ui(() => ShowError(exception));
            }
        }

        // ------------------------------------------------------------- speaking

        private async void SpeakButton_Click(object sender, RoutedEventArgs args)
        {
            if (_isGenerating || _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
            {
                await StopAsync();
                return;
            }

            await SpeakAsync(MessageBox.Text);
        }

        private async void Composer_KeyDown(object sender, KeyRoutedEventArgs args)
        {
            if (args.Key == VirtualKey.Enter)
            {
                args.Handled = true;
                await SpeakAsync(MessageBox.Text);
            }
            else if (args.Key == VirtualKey.Escape)
            {
                args.Handled = true;
                await StopAsync();
            }
        }

        private async Task StopAsync()
        {
            var wasGenerating = _isGenerating;
            StopPlayback();
            if (wasGenerating)
            {
                SetState("CANCELANDO", Warning);
                await _xttsBridge.CancelAsync();
            }
            else
            {
                SetReadyState();
            }
        }

        private async Task SpeakAsync(string? value)
        {
            if (_isGenerating)
            {
                return;
            }

            var text = value?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            var profile = SelectedProfile;
            var useWindowsVoice = IsWindowsVoiceSelected;
            if (!useWindowsVoice && profile == null)
            {
                ShowError(_xttsAvailable ? "Selecione uma voz clonada em Vozes." : "O mecanismo XTTS não está disponível.",
                    _xttsAvailable ? "Toque no chip de voz ao lado do campo de texto." : "Use RECONECTAR em Ajustes › Diagnóstico.");
                return;
            }

            if (!useWindowsVoice && !profile!.IsUsable)
            {
                ShowError(
                    "Este perfil está sem o áudio de referência.",
                    "Exclua o perfil em Vozes e crie-o novamente com o áudio original.");
                return;
            }

            if (_panel != PanelKind.None)
            {
                ShowPanel(PanelKind.None);
            }

            try
            {
                _serviceStoppedByUser = false;
                _isGenerating = true;
                StopPlayback();
                HideError();
                StartSpeechActivity();
                await StartSynthesisAudioKeepAliveAsync();
                AddToHistory(text);
                SpeakIcon.Glyph = "";
                MessageBox.Text = string.Empty;

                string contentType;
                if (!useWindowsVoice)
                {
                    BeginJob("GERANDO", "Preparando a voz clonada…");
                    App.Log($"XTTS synthesis requested. Profile={profile!.Id}; Characters={text.Length}.");
                    var result = await _xttsBridge.SynthesizeAsync(text, profile.Id, _speed);
                    _currentStream = await CreateAudioStreamAsync(result.Bytes);
                    contentType = result.ContentType;
                    await Ui(() =>
                    {
                        _xttsAvailable = true;
                        SetServicePowerState(true);
                        EndJob();
                        var label = ShortBackend(result.BackendLabel?.ToLowerInvariant() switch
                        {
                            "nvidia cuda" => "cuda",
                            "amd directml" => "directml",
                            "amd rocm" => "rocm",
                            "cpu" => "cpu",
                            _ => null,
                        });
                        if (label != null)
                        {
                            _backendLabel = label;
                        }
                    });
                }
                else
                {
                    SetSpeakingState();
                    var speechStream = await _synthesizer.SynthesizeTextToStreamAsync(text);
                    _currentStream = speechStream;
                    contentType = speechStream.ContentType;
                }

                await Ui(() =>
                {
                    SetSpeakingState();
                    _player.IsLoopingEnabled = false;
                    if (_echoEnabled && _usingVirtualCable)
                    {
                        _echoStream = _currentStream.CloneStream();
                        _echoPlayer.Source = MediaSource.CreateFromStream(_echoStream, contentType);
                    }

                    _player.Source = MediaSource.CreateFromStream(_currentStream, contentType);
                    _player.Play();
                    App.Log("Speech playback started.");
                    _synthesisKeepAliveStream?.Dispose();
                    _synthesisKeepAliveStream = null;
                    if (_echoPlayer.Source != null)
                    {
                        _echoPlayer.Play();
                    }

                    MessageBox.Focus(FocusState.Programmatic);
                });
            }
            catch (XttsBridgeException exception) when (exception.IsCancellation)
            {
                App.Log("XTTS synthesis cancelled.");
                await Ui(() => { EndJob(); StopPlayback(); SetReadyState(); });
            }
            catch (Exception exception)
            {
                App.Log($"Speech failed: {exception}");
                await Ui(() =>
                {
                    EndJob();
                    StopPlayback();
                    ShowError(exception);
                });
            }
            finally
            {
                await Ui(() =>
                {
                    _isGenerating = false;
                    if (_player.Source == null)
                    {
                        SpeakIcon.Glyph = "";
                    }
                });
            }
        }

        private static async Task<IRandomAccessStream> CreateAudioStreamAsync(byte[] audio)
        {
            var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(audio);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }

            stream.Seek(0);
            return stream;
        }

        private async Task PrepareSynthesisAudioKeepAliveAsync()
        {
            if (_synthesisKeepAliveTemplateStream == null)
            {
                _synthesisKeepAliveTemplateStream = await CreateAudioStreamAsync(CreateSilentWave());
            }
        }

        private async Task StartSynthesisAudioKeepAliveAsync()
        {
            // Game Bar reliably preserves an audio session that was already
            // playing when the overlay is dismissed, but may silence a new
            // session first started after it is hidden. Looping one second of
            // silence keeps the same MediaPlayer session active while XTTS is
            // generating; the real source replaces it as soon as it is ready.
            await PrepareSynthesisAudioKeepAliveAsync();
            _synthesisKeepAliveStream?.Dispose();
            _synthesisKeepAliveStream = _synthesisKeepAliveTemplateStream!.CloneStream();
            _player.IsLoopingEnabled = true;
            _player.Source = MediaSource.CreateFromStream(_synthesisKeepAliveStream, "audio/wav");
            _player.Play();
            App.Log("Synthesis audio keep-alive started.");
        }

        private static byte[] CreateSilentWave()
        {
            const int sampleRate = 8000;
            const short channels = 1;
            const short bitsPerSample = 16;
            const int seconds = 1;
            const int dataLength = sampleRate * channels * (bitsPerSample / 8) * seconds;

            using var output = new MemoryStream(44 + dataLength);
            using (var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + dataLength);
                writer.Write(Encoding.ASCII.GetBytes("WAVE"));
                writer.Write(Encoding.ASCII.GetBytes("fmt "));
                writer.Write(16);
                writer.Write((short)1);
                writer.Write(channels);
                writer.Write(sampleRate);
                writer.Write(sampleRate * channels * (bitsPerSample / 8));
                writer.Write((short)(channels * (bitsPerSample / 8)));
                writer.Write(bitsPerSample);
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(dataLength);
                writer.Write(new byte[dataLength]);
                writer.Flush();
            }

            return output.ToArray();
        }

        private void StartSpeechActivity()
        {
            if (_widget == null)
            {
                return;
            }

            try
            {
                _speechActivity = new XboxGameBarWidgetActivity(_widget, "svoice-speech");
                App.Log("Speech background activity started.");
            }
            catch (Exception exception)
            {
                // Speech still works when the host rejects an activity request.
                App.Log($"Speech background activity could not start: {exception.Message}");
            }
        }

        private void StopPlayback()
        {
            _player.IsLoopingEnabled = false;
            _player.Pause();
            _player.Source = null;
            _currentStream?.Dispose();
            _currentStream = null;
            _synthesisKeepAliveStream?.Dispose();
            _synthesisKeepAliveStream = null;
            StopEchoPlayback();
            CompleteSpeechActivity();
            SpeakIcon.Glyph = "";
        }

        private void StopEchoPlayback()
        {
            _echoPlayer.Pause();
            _echoPlayer.Source = null;
            _echoStream?.Dispose();
            _echoStream = null;
        }

        private void CompleteSpeechActivity()
        {
            var activity = _speechActivity;
            _speechActivity = null;
            if (activity == null)
            {
                return;
            }

            try
            {
                activity.Complete();
            }
            catch
            {
                // The host may already have closed the widget window.
            }
        }

        private async void Player_MediaEnded(MediaPlayer sender, object args)
        {
            App.Log("Speech playback completed.");
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                StopPlayback();
                SetReadyState();
            });
        }

        private async void Player_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                StopPlayback();
                ShowError($"Não foi possível reproduzir o áudio: {args.ErrorMessage}", "Verifique a saída de áudio em Ajustes.");
            });
        }

        private async void EchoPlayer_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                StopEchoPlayback();
                _echoEnabled = false;
                EchoButton.IsChecked = false;
                ApplicationData.Current.LocalSettings.Values[EchoSettingKey] = false;
                UpdateEchoVisual();
            });
        }

        // ---------------------------------------------------------------- jobs

        private void BeginJob(string state, string message)
        {
            SetState(state, Working);
            JobMessage.Text = message;
            JobMessage.Visibility = Visibility.Visible;
            JobProgress.IsIndeterminate = true;
            JobProgress.Visibility = Visibility.Visible;
            _jobTimer.Start();
        }

        private void EndJob()
        {
            _jobTimer.Stop();
            JobMessage.Visibility = Visibility.Collapsed;
            JobProgress.Visibility = Visibility.Collapsed;
        }

        private async void JobTimer_Tick(object? sender, object args)
        {
            try
            {
                var health = await _xttsBridge.PingAsync();
                _lastHealth = health;
                await Ui(() =>
                {
                    if (!_jobTimer.IsEnabled)
                    {
                        return;
                    }

                    if (health.Busy && !string.IsNullOrWhiteSpace(health.JobMessage))
                    {
                        JobMessage.Text = health.JobMessage;
                    }

                    if (health.JobProgress is double progress && health.Busy)
                    {
                        JobProgress.IsIndeterminate = false;
                        JobProgress.Value = progress * 100;
                    }
                    else
                    {
                        JobProgress.IsIndeterminate = true;
                    }
                });
            }
            catch (Exception exception)
            {
                App.Log($"Job polling failed: {exception.Message}");
            }
        }

        // ---------------------------------------------------------------- panels

        private void VoicesButton_Click(object sender, RoutedEventArgs args) => TogglePanel(PanelKind.Voices);

        private void SettingsButton_Click(object sender, RoutedEventArgs args) => TogglePanel(PanelKind.Settings);

        private async void Widget_SettingsClicked(XboxGameBarWidget sender, object args)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => ShowPanel(PanelKind.Settings));
        }

        private void ClosePanel_Click(object sender, RoutedEventArgs args) => ShowPanel(PanelKind.None);

        private void TogglePanel(PanelKind kind) => ShowPanel(_panel == kind ? PanelKind.None : kind);

        private async void ShowPanel(PanelKind kind)
        {
            _panel = kind;
            var open = kind != PanelKind.None;
            ChatArea.Visibility = open || _compact ? Visibility.Collapsed : Visibility.Visible;
            PanelView.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            VoicesPanel.Visibility = kind == PanelKind.Voices ? Visibility.Visible : Visibility.Collapsed;
            SettingsPanel.Visibility = kind == PanelKind.Settings ? Visibility.Visible : Visibility.Collapsed;
            PanelActionButton.Visibility = kind == PanelKind.Voices ? Visibility.Visible : Visibility.Collapsed;
            PanelTitleText.Text = kind switch
            {
                PanelKind.Voices => "VOZES",
                PanelKind.Settings => "AJUSTES",
                _ => string.Empty,
            };

            // Panels need room: temporarily leave compact mode while one is open.
            if (open && _compact)
            {
                _panelExpandedTemporarily = true;
                ContentRow.Height = new GridLength(1, GridUnitType.Star);
                await ResizeWindowAsync(_expandedHeight);
            }
            else if (!open && _panelExpandedTemporarily)
            {
                _panelExpandedTemporarily = false;
                ContentRow.Height = new GridLength(0);
                await ResizeWindowAsync(CompactHeight);
            }

            if (kind == PanelKind.Voices)
            {
                await RefreshXttsAsync();
            }
            else if (kind == PanelKind.Settings)
            {
                await LoadDiagnosticsAsync();
            }
            else
            {
                MessageBox.Focus(FocusState.Programmatic);
            }
        }

        // ------------------------------------------------------------ diagnostics

        private async Task LoadDiagnosticsAsync()
        {
            if (_serviceStoppedByUser)
            {
                await Ui(() =>
                {
                    SetServicePowerState(false);
                    DiagnosticsSummary.Text = "XTTS encerrado. Use INICIAR XTTS ou envie uma fala com voz clonada.";
                    DiagnosticsList.ItemsSource = new List<LabeledValue>
                    {
                        new LabeledValue("Memória", "RAM e VRAM do modelo foram liberadas"),
                    };
                });
                return;
            }

            await Ui(() =>
            {
                DiagnosticsSummary.Text = "Consultando o mecanismo XTTS…";
                DiagnosticsList.ItemsSource = null;
            });

            try
            {
                using var document = await _xttsBridge.DiagnosticsAsync();
                var root = document.RootElement;
                var items = new List<LabeledValue>();
                var engine = root.TryGetProperty("engine", out var engineValue) ? engineValue : default;
                var system = root.TryGetProperty("system", out var systemValue) ? systemValue : default;
                var modelInfo = root.TryGetProperty("model", out var modelValue) ? modelValue : default;
                var runtime = engine.ValueKind == JsonValueKind.Object && engine.TryGetProperty("runtime", out var runtimeValue) ? runtimeValue : default;
                var keepLoaded = engine.ValueKind == JsonValueKind.Object &&
                    engine.TryGetProperty("keep_xtts_loaded", out var keepLoadedValue) &&
                    keepLoadedValue.ValueKind == JsonValueKind.True;

                var gpuNames = system.ValueKind == JsonValueKind.Object && system.TryGetProperty("gpus", out var gpus) && gpus.ValueKind == JsonValueKind.Array
                    ? gpus.EnumerateArray().Select(gpu =>
                    {
                        var driver = gpu.GetStringOrNull("nvidia_driver") ?? gpu.GetStringOrNull("driver_version");
                        return driver != null ? $"{gpu.GetStringOrNull("name")} (driver {driver})" : gpu.GetStringOrNull("name");
                    }).Where(name => name != null).ToList()
                    : new List<string?>();
                items.Add(new LabeledValue("GPU detectada", gpuNames.Count > 0 ? string.Join("; ", gpuNames) : "nenhuma GPU dedicada"));

                var activeLabel = engine.GetStringOrNull("active_backend_label");
                var recommended = engine.GetStringOrNull("recommended_backend");
                items.Add(new LabeledValue("Modo configurado", ComputeModeLabel(engine.GetStringOrDefault("compute_mode", "auto"))));
                items.Add(new LabeledValue("Permanência", keepLoaded ? "mantém o XTTS carregado" : "encerra após 15 min sem uso"));
                items.Add(new LabeledValue("Backend ativo", activeLabel ?? "modelo ainda não carregado"));
                items.Add(new LabeledValue("Backend recomendado", $"{ComputeModeLabel(recommended ?? "cpu")} — {engine.GetStringOrNull("recommended_reason")}"));
                if (engine.GetStringOrNull("fallback_reason") is string fallback)
                {
                    items.Add(new LabeledValue("Motivo do fallback", fallback));
                }

                var possibleBackends = new List<string>();
                if (engine.TryGetProperty("backends", out var backends) && backends.ValueKind == JsonValueKind.Object)
                {
                    foreach (var backend in backends.EnumerateObject())
                    {
                        var available = backend.Value.TryGetProperty("available", out var availableValue) && availableValue.ValueKind == JsonValueKind.True;
                        var label = backend.Value.GetStringOrDefault("label", backend.Name);
                        var device = backend.Value.GetStringOrNull("device_name");
                        var reason = backend.Value.GetStringOrNull("reason");
                        var validation = "não testado";
                        if (engine.TryGetProperty("validations", out var validations) && validations.TryGetProperty(backend.Name, out var record))
                        {
                            var ok = record.TryGetProperty("ok", out var okValue) && okValue.ValueKind == JsonValueKind.True;
                            var seconds = record.TryGetProperty("test_synthesis_seconds", out var secondsValue) && secondsValue.ValueKind == JsonValueKind.Number
                                ? $"{secondsValue.GetDouble():0.0} s"
                                : null;
                            validation = ok ? $"validado (síntese de teste em {seconds ?? "?"})" : $"falhou: {record.GetStringOrNull("reason")}";
                        }

                        items.Add(new LabeledValue(label, available ? $"{device} · {validation}" : $"indisponível · {reason}"));
                        if (available)
                        {
                            possibleBackends.Add(backend.Name);
                        }
                    }
                }

                var modelReady = modelInfo.ValueKind == JsonValueKind.Object && modelInfo.TryGetProperty("ready", out var readyValue) && readyValue.ValueKind == JsonValueKind.True;
                items.Add(new LabeledValue("Modelo XTTS v2", modelReady ? "instalado e verificado" : "ausente ou incompleto — use MODELO"));
                items.Add(new LabeledValue("Runtime", $"{runtime.GetStringOrNull("torch_pack") ?? "desenvolvimento"} · PyTorch {engine.GetStringOrNull("torch_version")}"));
                items.Add(new LabeledValue("Serviço", $"v{root.GetStringOrNull("service_version")} · protocolo {root.GetProperty("protocol_version").GetInt32()} · Python {engine.GetStringOrNull("python")}"));
                items.Add(new LabeledValue("Dados", root.GetStringOrNull("data_dir")));
                items.Add(new LabeledValue("Logs", root.GetStringOrNull("logs_dir")));

                await Ui(() =>
                {
                    SetServicePowerState(true);
                    _suppressKeepLoadedChange = true;
                    KeepXttsLoadedToggle.IsOn = keepLoaded;
                    _suppressKeepLoadedChange = false;
                    DiagnosticsSummary.Text = activeLabel != null
                        ? $"XTTS pronto em {activeLabel}."
                        : $"XTTS pronto; o modelo será carregado em {ComputeModeLabel(recommended ?? "cpu")} na primeira fala.";
                    DiagnosticsList.ItemsSource = items;
                    TestBackendBox.Items.Clear();
                    foreach (var backend in possibleBackends)
                    {
                        TestBackendBox.Items.Add(new ComputeModeChoice(backend, ComputeModeLabel(backend)));
                    }

                    TestBackendBox.SelectedIndex = TestBackendBox.Items.Count > 0 ? 0 : -1;
                    TestBackendButton.IsEnabled = TestBackendBox.Items.Count > 0;
                });
            }
            catch (Exception exception)
            {
                App.Log($"Diagnostics failed: {exception}");
                await Ui(() =>
                {
                    SetServicePowerState(false);
                    DiagnosticsSummary.Text = $"O mecanismo XTTS não respondeu: {exception.Message}";
                    DiagnosticsList.ItemsSource = new List<LabeledValue>
                    {
                        new LabeledValue("Ação", (exception as XttsBridgeException)?.Action ?? "Use RECONECTAR ou execute o reparo do SVoice."),
                    };
                    TestBackendButton.IsEnabled = false;
                });
            }
        }

        private static string ComputeModeLabel(string mode)
        {
            return mode switch
            {
                "cuda" => "NVIDIA CUDA",
                "directml" => "AMD DirectML",
                "rocm" => "AMD ROCm",
                "cpu" => "CPU",
                _ => "Automático",
            };
        }

        private async void KeepXttsLoadedToggle_Toggled(object sender, RoutedEventArgs args)
        {
            if (_suppressKeepLoadedChange)
            {
                return;
            }

            var requested = KeepXttsLoadedToggle.IsOn;
            KeepXttsLoadedToggle.IsEnabled = false;
            try
            {
                using var response = await _xttsBridge.SetKeepLoadedAsync(requested);
                App.Log($"XTTS keep-loaded set to {requested}.");
                await LoadDiagnosticsAsync();
            }
            catch (Exception exception)
            {
                App.Log($"XTTS keep-loaded change failed: {exception}");
                _suppressKeepLoadedChange = true;
                KeepXttsLoadedToggle.IsOn = !requested;
                _suppressKeepLoadedChange = false;
                ShowError(exception);
            }
            finally
            {
                KeepXttsLoadedToggle.IsEnabled = _xttsServiceRunning;
            }
        }

        private async void TestBackend_Click(object sender, RoutedEventArgs args)
        {
            if (_isGenerating || TestBackendBox.SelectedItem is not ComputeModeChoice choice)
            {
                return;
            }

            try
            {
                _isGenerating = true;
                BeginJob("TESTANDO", $"Testando {choice.Label}…");
                DiagnosticsSummary.Text = $"Executando síntese de teste em {choice.Label}…";
                using var document = await _xttsBridge.TestBackendAsync(choice.WireName);
                var report = document.RootElement.GetProperty("report");
                var ok = report.TryGetProperty("ok", out var okValue) && okValue.ValueKind == JsonValueKind.True;
                var elapsed = report.TryGetProperty("elapsed_seconds", out var elapsedValue) ? elapsedValue.GetDouble() : 0;
                App.Log($"Backend test {choice.WireName}: ok={ok}; {report.GetStringOrNull("reason")}");
                await Ui(() =>
                {
                    EndJob();
                    DiagnosticsSummary.Text = ok
                        ? $"{choice.Label}: síntese completa validada em {elapsed:0.0} s."
                        : $"{choice.Label} falhou: {report.GetStringOrNull("reason")}";
                });
            }
            catch (Exception exception)
            {
                App.Log($"Backend test failed: {exception}");
                await Ui(() => { EndJob(); ShowError(exception); });
            }
            finally
            {
                _isGenerating = false;
                await RefreshXttsAsync();
                await LoadDiagnosticsAsync();
            }
        }

        private async void RestartService_Click(object sender, RoutedEventArgs args)
        {
            if (_isGenerating)
            {
                return;
            }

            try
            {
                _serviceStoppedByUser = false;
                SetState("REINICIANDO", Working);
                DiagnosticsSummary.Text = "Reiniciando o mecanismo XTTS…";
                using var response = await _xttsBridge.RestartServiceAsync();
            }
            catch (Exception exception)
            {
                App.Log($"Restart failed: {exception}");
                await Ui(() => ShowError(exception));
            }

            await RefreshXttsAsync();
            await LoadDiagnosticsAsync();
        }

        private async void ShutdownService_Click(object sender, RoutedEventArgs args)
        {
            if (_isGenerating)
            {
                ShowError("Aguarde a operação atual antes de encerrar o XTTS.");
                return;
            }

            ShutdownServiceButton.IsEnabled = false;
            try
            {
                if (!_xttsServiceRunning)
                {
                    _serviceStoppedByUser = false;
                    SetState("INICIANDO XTTS", Working);
                    DiagnosticsSummary.Text = "Iniciando o mecanismo XTTS…";
                    await RefreshXttsAsync();
                    if (!_xttsAvailable)
                    {
                        return;
                    }

                    await LoadDiagnosticsAsync();
                    return;
                }

                DiagnosticsSummary.Text = "Encerrando o mecanismo XTTS…";
                using var response = await _xttsBridge.ShutdownServiceAsync();
                var stopped = response.RootElement.TryGetProperty("stopped", out var stoppedValue) &&
                    stoppedValue.ValueKind == JsonValueKind.True;
                if (!stopped)
                {
                    throw new InvalidOperationException("O processo XTTS não pôde ser encerrado.");
                }

                _lastHealth = null;
                _xttsAvailable = false;
                _backendLabel = string.Empty;
                _serviceStoppedByUser = true;
                SetServicePowerState(false);
                SetState("XTTS ENCERRADO", Warning);
                DiagnosticsSummary.Text = "XTTS encerrado. Ele inicia novamente quando você usar uma voz clonada.";
                DiagnosticsList.ItemsSource = new List<LabeledValue>
                {
                    new LabeledValue("Memória", "RAM e VRAM do modelo foram liberadas"),
                };
                App.Log("XTTS service stopped by the user.");
            }
            catch (Exception exception)
            {
                App.Log($"XTTS shutdown failed: {exception}");
                ShowError(exception);
            }
            finally
            {
                ShutdownServiceButton.IsEnabled = true;
            }
        }

        private void SetServicePowerState(bool running)
        {
            _xttsServiceRunning = running;
            if (running)
            {
                _serviceStoppedByUser = false;
            }
            ShutdownServiceButton.Content = running ? "ENCERRAR XTTS" : "INICIAR XTTS";
            KeepXttsLoadedToggle.IsEnabled = running;
            var diagnosticControlsEnabled = running || !_serviceStoppedByUser;
            ComputeModeBox.IsEnabled = diagnosticControlsEnabled;
            RestartServiceButton.IsEnabled = diagnosticControlsEnabled;
            EnsureModelButton.IsEnabled = diagnosticControlsEnabled;
            TestBackendButton.IsEnabled = diagnosticControlsEnabled && TestBackendBox.Items.Count > 0;
            ToolTipService.SetToolTip(
                ShutdownServiceButton,
                running
                    ? "Encerra o processo e libera a RAM e a VRAM usadas pelo XTTS."
                    : "Inicia o XTTS agora. Uma fala com voz clonada também o inicia automaticamente.");
        }

        private async void EnsureModel_Click(object sender, RoutedEventArgs args)
        {
            if (_isGenerating)
            {
                return;
            }

            try
            {
                _serviceStoppedByUser = false;
                _isGenerating = true;
                BeginJob("BAIXANDO MODELO", "Verificando o modelo XTTS v2…");
                DiagnosticsSummary.Text = "Verificando e baixando o modelo XTTS v2 (1,9 GB)…";
                using var response = await _xttsBridge.EnsureModelAsync();
                await Ui(() => { EndJob(); DiagnosticsSummary.Text = "Modelo XTTS v2 verificado."; });
            }
            catch (Exception exception)
            {
                App.Log($"Model download failed: {exception}");
                await Ui(() => { EndJob(); ShowError(exception); });
            }
            finally
            {
                _isGenerating = false;
                await RefreshXttsAsync();
                await LoadDiagnosticsAsync();
            }
        }

        // ---------------------------------------------------------------- states

        private void SetState(string text, Color color)
        {
            StatusText.Text = text;
            StatusText.Foreground = new SolidColorBrush(Color.FromArgb(230, color.R, color.G, color.B));
            StatusDot.Fill = new SolidColorBrush(color);
        }

        private void SetReadyState()
        {
            SetState(string.IsNullOrEmpty(_backendLabel) ? "PRONTO" : $"PRONTO · {_backendLabel}", Accent);
        }

        private void SetSpeakingState() => SetState("FALANDO", Warning);

        private void ShowError(Exception exception)
        {
            ShowError(exception.Message, (exception as XttsBridgeException)?.Action);
        }

        private void ShowError(string message, string? action = null)
        {
            SetState("ERRO", Danger);
            ErrorText.Text = string.IsNullOrWhiteSpace(message) ? "Não foi possível concluir a operação." : message;
            ErrorAction.Text = action ?? string.Empty;
            ErrorAction.Visibility = string.IsNullOrWhiteSpace(action) ? Visibility.Collapsed : Visibility.Visible;
            ErrorPanel.Visibility = Visibility.Visible;
            _errorTimer.Stop();
            _errorTimer.Start();
        }

        private void HideError()
        {
            _errorTimer.Stop();
            ErrorPanel.Visibility = Visibility.Collapsed;
        }

        private void DismissError_Click(object sender, RoutedEventArgs args)
        {
            HideError();
            if (StatusText.Text == "ERRO")
            {
                SetReadyState();
            }
        }

        private async void Widget_RequestedOpacityChanged(XboxGameBarWidget sender, object args)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, ApplyRequestedOpacity);
        }

        private void ApplyRequestedOpacity()
        {
            if (_widget != null)
            {
                Surface.Opacity = Math.Max(0.48, _widget.RequestedOpacity);
            }
        }

        // ------------------------------------------------------------- threading

        private async Task Ui(Action action)
        {
            if (Dispatcher.HasThreadAccess)
            {
                action();
                return;
            }

            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => action());
        }

        private async Task<T> Ui<T>(Func<Task<T>> action)
        {
            if (Dispatcher.HasThreadAccess)
            {
                return await action();
            }

            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                try
                {
                    completion.TrySetResult(await action());
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            });
            return await completion.Task;
        }
    }

    /// <summary>Row of the voices panel: a cloned profile or the Windows voice.</summary>
    internal sealed class VoiceItem
    {
        private static readonly SolidColorBrush SelectedBorder = new SolidColorBrush(Color.FromArgb(120, 139, 233, 210));
        private static readonly SolidColorBrush QuietBorder = new SolidColorBrush(Color.FromArgb(16, 255, 255, 255));
        private static readonly SolidColorBrush Checked = new SolidColorBrush(Color.FromArgb(255, 139, 233, 210));
        private static readonly SolidColorBrush Unchecked = new SolidColorBrush(Color.FromArgb(0, 255, 255, 255));

        public VoiceItem(ClonedVoiceProfile? profile, bool selected)
        {
            Profile = profile;
            Selected = selected;
        }

        public ClonedVoiceProfile? Profile { get; }
        public bool Selected { get; }
        public string Name => Profile?.Name ?? "Voz do Windows";
        public string Summary => Profile?.Summary ?? "Voz de sistema do Windows (sem XTTS)";
        public Brush BorderBrush => Selected ? SelectedBorder : QuietBorder;
        public Brush CheckBrush => Selected ? Checked : Unchecked;
        public Visibility ManageVisibility => Profile == null ? Visibility.Collapsed : Visibility.Visible;
    }
}
