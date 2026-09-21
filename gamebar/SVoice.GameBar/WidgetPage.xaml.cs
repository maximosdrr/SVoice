using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Gaming.XboxGameBar;
using Windows.Devices.Enumeration;
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
        private enum PanelKind { None, History, Voices, Settings, Diagnostics }

        private const int MaxHistory = 8;
        private const string EchoSettingKey = "echoEnabled";
        private const string VoiceSettingKey = "selectedVoiceProfileId";
        private const string SpeedSettingKey = "speechSpeed";
        private const string VolumeSettingKey = "volume";
        private const string OutputSettingKey = "outputDeviceId";
        private const string HistoryFileName = "history.json";

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
        private XboxGameBarWidget? _widget;
        private XboxGameBarWidgetActivity? _speechActivity;
        private IReadOnlyList<ClonedVoiceProfile> _profiles = Array.Empty<ClonedVoiceProfile>();
        private XttsHealth? _lastHealth;
        private PanelKind _panel = PanelKind.None;
        private bool _initialized;
        private bool _usingVirtualCable;
        private bool _echoEnabled;
        private bool _isGenerating;
        private bool _xttsAvailable;
        private bool _suppressSelectionEvents;
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
        }

        // ------------------------------------------------------------- lifecycle

        protected override void OnNavigatedTo(NavigationEventArgs args)
        {
            if (_widget != null)
            {
                _widget.RequestedOpacityChanged -= Widget_RequestedOpacityChanged;
            }

            _widget = args.Parameter as XboxGameBarWidget;
            if (_widget != null)
            {
                _widget.RequestedOpacityChanged += Widget_RequestedOpacityChanged;
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
            // Game Bar can unload and reload the same page when a widget is hidden.
            StopPlayback();
            _jobTimer.Stop();
        }

        // -------------------------------------------------------------- settings

        private void LoadSettings()
        {
            var values = ApplicationData.Current.LocalSettings.Values;
            _echoEnabled = values.TryGetValue(EchoSettingKey, out var echo) && echo is bool enabled && enabled;
            _speed = values.TryGetValue(SpeedSettingKey, out var speed) && speed is double storedSpeed ? Math.Clamp(storedSpeed, 0.5, 1.5) : 1.0;
            _volume = values.TryGetValue(VolumeSettingKey, out var volume) && volume is double storedVolume ? Math.Clamp(storedVolume, 0, 1) : 1.0;
            _outputDeviceId = values.TryGetValue(OutputSettingKey, out var output) ? output as string : null;
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
        }

        private async Task LoadHistoryAsync()
        {
            try
            {
                var path = Path.Combine(ApplicationData.Current.LocalFolder.Path, HistoryFileName);
                if (!File.Exists(path))
                {
                    return;
                }

                var items = JsonSerializer.Deserialize<List<string>>(await File.ReadAllTextAsync(path));
                if (items != null)
                {
                    _history.AddRange(items.Where(item => !string.IsNullOrWhiteSpace(item)).Take(MaxHistory));
                }
            }
            catch (Exception exception)
            {
                App.Log($"History could not be loaded: {exception.Message}");
            }

            await Ui(RenderHistory);
        }

        private void SaveHistory()
        {
            try
            {
                var path = Path.Combine(ApplicationData.Current.LocalFolder.Path, HistoryFileName);
                File.WriteAllText(path, JsonSerializer.Serialize(_history));
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
            RenderHistory();
        }

        private void RenderHistory()
        {
            HistoryList.ItemsSource = null;
            HistoryList.ItemsSource = _history.ToList();
            HistoryEmptyText.Visibility = _history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_history.Count > 0)
            {
                LastPhraseText.Text = _history[0];
                LastPhrasePanel.Visibility = Visibility.Visible;
            }
            else
            {
                LastPhrasePanel.Visibility = Visibility.Collapsed;
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
            if (_usingVirtualCable)
            {
                OutputText.Text = "CABLE INPUT";
                OutputIcon.Foreground = new SolidColorBrush(Accent);
                EchoButton.IsEnabled = true;
                EchoButton.IsChecked = _echoEnabled;
                HintText.Text = "Enter: falar  •  Esc: parar  •  Discord: CABLE Output";
            }
            else
            {
                OutputText.Text = choice.Id == null ? "SAÍDA PADRÃO" : Shorten(choice.Label, 18).ToUpperInvariant();
                OutputIcon.Foreground = new SolidColorBrush(Warning);
                EchoButton.IsEnabled = false;
                EchoButton.IsChecked = false;
                StopEchoPlayback();
                HintText.Text = _outputDevices.Any(item => item.IsVirtualCable)
                    ? "Enter: falar  •  Esc: parar"
                    : "VB-CABLE não encontrado: execute o reparo do SVoice";
            }
        }

        private static string Shorten(string value, int max)
        {
            return value.Length <= max ? value : value.Substring(0, max - 1) + "…";
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
                return;
            }

            _echoEnabled = EchoButton.IsChecked == true;
            ApplicationData.Current.LocalSettings.Values[EchoSettingKey] = _echoEnabled;
            if (!_echoEnabled)
            {
                StopEchoPlayback();
            }
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
            await Ui(() =>
            {
                RefreshVoicesButton.IsEnabled = false;
                SetState("CONECTANDO XTTS", Working);
            });

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
            }

            await Ui(() =>
            {
                PopulateVoiceChoices(preferredProfileId);
                RenderVoicesPanel();
                ApplyHealth(health, failure);
                RefreshVoicesButton.IsEnabled = true;
            });
        }

        private void PopulateVoiceChoices(string? preferredProfileId)
        {
            var savedProfileId = preferredProfileId ?? ApplicationData.Current.LocalSettings.Values[VoiceSettingKey] as string;
            _suppressSelectionEvents = true;
            VoiceBox.Items.Clear();
            foreach (var profile in _profiles)
            {
                var usable = _xttsAvailable && profile.IsUsable;
                var suffix = profile.IsUsable ? "Clonada" : "sem áudio (!)";
                VoiceBox.Items.Add(new VoiceChoice($"{profile.Name}  ·  {suffix}", profile.Id, usable));
            }

            VoiceBox.Items.Add(new VoiceChoice("Voz do Windows (sem XTTS)"));

            VoiceChoice? selection = null;
            if (!string.IsNullOrWhiteSpace(savedProfileId))
            {
                selection = VoiceBox.Items.OfType<VoiceChoice>().FirstOrDefault(choice => choice.ProfileId == savedProfileId);
            }

            selection ??= VoiceBox.Items.OfType<VoiceChoice>().FirstOrDefault(choice => choice.IsCloned && choice.Usable)
                ?? VoiceBox.Items.OfType<VoiceChoice>().FirstOrDefault(choice => choice.IsCloned)
                ?? VoiceBox.Items.OfType<VoiceChoice>().First();
            VoiceBox.SelectedItem = selection;
            _suppressSelectionEvents = false;

            ToolTipService.SetToolTip(
                VoiceBox,
                _profiles.Count == 0
                    ? "Nenhuma voz clonada ainda. Use CLONAR para criar uma."
                    : "Selecione uma voz clonada pelo XTTS ou a voz do Windows.");
        }

        private void ApplyHealth(XttsHealth? health, Exception? failure)
        {
            if (health == null)
            {
                SetState("XTTS INDISPONÍVEL", Danger);
                BackendBadge.Text = string.Empty;
                if (failure != null)
                {
                    var bridgeError = failure as XttsBridgeException;
                    ShowError(failure.Message, bridgeError?.Action ?? "Use RECONECTAR no diagnóstico ou execute o reparo do SVoice.");
                }

                return;
            }

            if (!health.ModelReady)
            {
                SetState("MODELO AUSENTE", Warning);
                BackendBadge.Text = string.Empty;
                ShowError("O modelo XTTS v2 ainda não foi baixado.", "Abra o diagnóstico e use VERIFICAR MODELO para baixá-lo.");
                return;
            }

            SetReadyState();
            BackendBadge.Text = health.ActiveBackendLabel ?? ComputeModeLabel(health.ComputeMode);
            SyncComputeModeBox(health.ComputeMode);
        }

        private static string ComputeModeLabel(string mode)
        {
            return mode switch
            {
                "cuda" => "NVIDIA CUDA",
                "directml" => "AMD DirectML",
                "rocm" => "AMD ROCm",
                "cpu" => "CPU",
                _ => "Auto",
            };
        }

        private void SyncComputeModeBox(string mode)
        {
            _suppressSelectionEvents = true;
            ComputeModeBox.SelectedItem = ComputeModeBox.Items.OfType<ComputeModeChoice>().FirstOrDefault(choice => choice.WireName == mode)
                ?? ComputeModeBox.Items.OfType<ComputeModeChoice>().First();
            _suppressSelectionEvents = false;
        }

        private async void RefreshVoicesButton_Click(object sender, RoutedEventArgs args)
        {
            await RefreshXttsAsync();
        }

        private void VoiceBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
        {
            if (_suppressSelectionEvents)
            {
                return;
            }

            if (VoiceBox.SelectedItem is VoiceChoice choice)
            {
                ApplicationData.Current.LocalSettings.Values[VoiceSettingKey] = choice.ProfileId ?? string.Empty;
            }
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
                SetState("REINICIANDO XTTS", Working);
                using var response = await _xttsBridge.SetComputeModeAsync(choice.WireName);
                App.Log($"Compute mode set to {choice.WireName}.");
                await RefreshXttsAsync();
            }
            catch (Exception exception)
            {
                App.Log($"Compute mode change failed: {exception}");
                await Ui(() => ShowError(exception));
                await RefreshXttsAsync();
            }
        }

        // ------------------------------------------------------------- cloning

        private async void CloneVoiceButton_Click(object sender, RoutedEventArgs args)
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
                        Text = $"{files.Count} áudio(s) selecionado(s). Use gravações limpas da mesma pessoa; de 10 segundos a 30 minutos no total. Use apenas vozes com autorização do titular.",
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
                    BeginJob("CLONANDO VOZ", "Preparando os áudios de referência…");
                    CloneVoiceButton.IsEnabled = false;
                    RefreshVoicesButton.IsEnabled = false;
                });

                IReadOnlyList<string> referencePaths = files.Select(file => file.Path).ToArray();
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
                    CloneVoiceButton.IsEnabled = true;
                    RefreshVoicesButton.IsEnabled = true;
                });
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

        private async void RepeatLastButton_Click(object sender, RoutedEventArgs args)
        {
            if (_history.Count > 0)
            {
                await SpeakAsync(_history[0]);
            }
        }

        private async void HistoryItem_Click(object sender, RoutedEventArgs args)
        {
            if ((sender as FrameworkElement)?.Tag is string text)
            {
                ShowPanel(PanelKind.None);
                await SpeakAsync(text);
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

            var choice = VoiceBox.SelectedItem as VoiceChoice;
            if (choice == null)
            {
                return;
            }

            if (choice.IsCloned && !choice.Usable)
            {
                ShowError(
                    _xttsAvailable ? "Este perfil está sem o áudio de referência." : "O mecanismo XTTS não está disponível.",
                    _xttsAvailable ? "Exclua o perfil e crie-o novamente com o áudio original." : "Use RECONECTAR no diagnóstico.");
                return;
            }

            try
            {
                _isGenerating = true;
                StopPlayback();
                HideError();
                StartSpeechActivity();
                AddToHistory(text);
                SpeakIcon.Glyph = "";

                string contentType;
                if (choice.IsCloned)
                {
                    BeginJob("GERANDO XTTS", "Preparando a voz clonada…");
                    App.Log($"XTTS synthesis requested. Profile={choice.ProfileId}; Characters={text.Length}.");
                    var result = await _xttsBridge.SynthesizeAsync(text, choice.ProfileId!, _speed);
                    _currentStream = await CreateAudioStreamAsync(result.Bytes);
                    contentType = result.ContentType;
                    await Ui(() =>
                    {
                        EndJob();
                        if (!string.IsNullOrWhiteSpace(result.BackendLabel))
                        {
                            BackendBadge.Text = result.BackendLabel;
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
                    if (_echoEnabled && _usingVirtualCable)
                    {
                        _echoStream = _currentStream.CloneStream();
                        _echoPlayer.Source = MediaSource.CreateFromStream(_echoStream, contentType);
                    }

                    _player.Source = MediaSource.CreateFromStream(_currentStream, contentType);
                    _player.Play();
                    if (_echoPlayer.Source != null)
                    {
                        _echoPlayer.Play();
                    }

                    MessageBox.Text = string.Empty;
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

        private void StartSpeechActivity()
        {
            if (_widget == null)
            {
                return;
            }

            try
            {
                _speechActivity = new XboxGameBarWidgetActivity(_widget, "svoice-speech");
            }
            catch
            {
                // Speech still works when the host rejects an activity request.
            }
        }

        private void StopPlayback()
        {
            _player.Pause();
            _player.Source = null;
            _currentStream?.Dispose();
            _currentStream = null;
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
                ShowError($"Não foi possível reproduzir o áudio: {args.ErrorMessage}", "Verifique o dispositivo de saída em Ajustes.");
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

        private void HistoryButton_Click(object sender, RoutedEventArgs args) => TogglePanel(PanelKind.History);

        private void VoicesButton_Click(object sender, RoutedEventArgs args) => TogglePanel(PanelKind.Voices);

        private void SettingsButton_Click(object sender, RoutedEventArgs args) => TogglePanel(PanelKind.Settings);

        private async void DiagnosticsButton_Click(object sender, RoutedEventArgs args)
        {
            TogglePanel(PanelKind.Diagnostics);
            if (_panel == PanelKind.Diagnostics)
            {
                await LoadDiagnosticsAsync();
            }
        }

        private void ClosePanel_Click(object sender, RoutedEventArgs args) => ShowPanel(PanelKind.None);

        private void TogglePanel(PanelKind kind) => ShowPanel(_panel == kind ? PanelKind.None : kind);

        private void ShowPanel(PanelKind kind)
        {
            _panel = kind;
            MainView.Visibility = kind == PanelKind.None ? Visibility.Visible : Visibility.Collapsed;
            PanelView.Visibility = kind == PanelKind.None ? Visibility.Collapsed : Visibility.Visible;
            HistoryPanel.Visibility = kind == PanelKind.History ? Visibility.Visible : Visibility.Collapsed;
            VoicesPanel.Visibility = kind == PanelKind.Voices ? Visibility.Visible : Visibility.Collapsed;
            SettingsPanel.Visibility = kind == PanelKind.Settings ? Visibility.Visible : Visibility.Collapsed;
            DiagnosticsPanel.Visibility = kind == PanelKind.Diagnostics ? Visibility.Visible : Visibility.Collapsed;
            PanelActionButton.Visibility = kind == PanelKind.History || kind == PanelKind.Diagnostics ? Visibility.Visible : Visibility.Collapsed;
            PanelActionText.Text = kind == PanelKind.History ? "LIMPAR" : "ATUALIZAR";
            PanelTitleText.Text = kind switch
            {
                PanelKind.History => "HISTÓRICO",
                PanelKind.Voices => "VOZES CLONADAS",
                PanelKind.Settings => "AJUSTES",
                PanelKind.Diagnostics => "DIAGNÓSTICO",
                _ => string.Empty,
            };
            if (kind == PanelKind.None)
            {
                MessageBox.Focus(FocusState.Programmatic);
            }
        }

        private async void PanelAction_Click(object sender, RoutedEventArgs args)
        {
            if (_panel == PanelKind.History)
            {
                _history.Clear();
                SaveHistory();
                RenderHistory();
            }
            else if (_panel == PanelKind.Diagnostics)
            {
                await LoadDiagnosticsAsync();
            }
        }

        private void RenderVoicesPanel()
        {
            VoicesList.ItemsSource = null;
            VoicesList.ItemsSource = _profiles.ToList();
            VoicesEmptyText.Visibility = _profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void RenameVoice_Click(object sender, RoutedEventArgs args)
        {
            if ((sender as FrameworkElement)?.Tag is not ClonedVoiceProfile profile || _isGenerating)
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

                await _xttsBridge.RenameProfileAsync(profile.Id, nameBox.Text.Trim());
                await RefreshXttsAsync();
                await Ui(() => ShowPanel(PanelKind.Voices));
            }
            catch (Exception exception)
            {
                App.Log($"Rename failed: {exception}");
                await Ui(() => ShowError(exception));
            }
        }

        private async void DeleteVoice_Click(object sender, RoutedEventArgs args)
        {
            if ((sender as FrameworkElement)?.Tag is not ClonedVoiceProfile profile || _isGenerating)
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

                await _xttsBridge.DeleteProfileAsync(profile.Id);
                App.Log($"XTTS profile deleted: {profile.Id}.");
                await RefreshXttsAsync();
                await Ui(() => ShowPanel(PanelKind.Voices));
            }
            catch (Exception exception)
            {
                App.Log($"Delete failed: {exception}");
                await Ui(() => ShowError(exception));
            }
        }

        // ------------------------------------------------------------ diagnostics

        private async Task LoadDiagnosticsAsync()
        {
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
                        string validation = "não testado";
                        if (engine.TryGetProperty("validations", out var validations) && validations.TryGetProperty(backend.Name, out var record))
                        {
                            var ok = record.TryGetProperty("ok", out var okValue) && okValue.ValueKind == JsonValueKind.True;
                            var seconds = record.TryGetProperty("test_synthesis_seconds", out var secondsValue) && secondsValue.ValueKind == JsonValueKind.Number
                                ? $"{secondsValue.GetDouble():0.0} s"
                                : null;
                            validation = ok
                                ? $"validado (síntese de teste em {seconds ?? "?"})"
                                : $"falhou: {record.GetStringOrNull("reason")}";
                        }

                        items.Add(new LabeledValue(label, available ? $"{device} · {validation}" : $"indisponível · {reason}"));
                        if (available)
                        {
                            possibleBackends.Add(backend.Name);
                        }
                    }
                }

                var modelReady = modelInfo.ValueKind == JsonValueKind.Object && modelInfo.TryGetProperty("ready", out var readyValue) && readyValue.ValueKind == JsonValueKind.True;
                items.Add(new LabeledValue("Modelo XTTS v2", modelReady ? "instalado e verificado" : "ausente ou incompleto — use VERIFICAR MODELO"));
                items.Add(new LabeledValue("Runtime", $"{runtime.GetStringOrNull("torch_pack") ?? "desenvolvimento"} · PyTorch {engine.GetStringOrNull("torch_version")}"));
                items.Add(new LabeledValue("Serviço", $"v{root.GetStringOrNull("service_version")} · protocolo {root.GetProperty("protocol_version").GetInt32()} · Python {engine.GetStringOrNull("python")}"));
                items.Add(new LabeledValue("Dados", root.GetStringOrNull("data_dir")));
                items.Add(new LabeledValue("Logs", root.GetStringOrNull("logs_dir")));

                await Ui(() =>
                {
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
                    DiagnosticsSummary.Text = $"O mecanismo XTTS não respondeu: {exception.Message}";
                    DiagnosticsList.ItemsSource = new List<LabeledValue>
                    {
                        new LabeledValue("Ação", (exception as XttsBridgeException)?.Action ?? "Use RECONECTAR ou execute o reparo do SVoice."),
                    };
                    TestBackendButton.IsEnabled = false;
                });
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
                SetState("REINICIANDO XTTS", Working);
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

        private async void EnsureModel_Click(object sender, RoutedEventArgs args)
        {
            if (_isGenerating)
            {
                return;
            }

            try
            {
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

        private void SetReadyState() => SetState("PRONTO", Accent);

        private void SetSpeakingState() => SetState("FALANDO", Warning);

        private void ShowError(Exception exception)
        {
            var bridgeError = exception as XttsBridgeException;
            ShowError(exception.Message, bridgeError?.Action);
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
}
