using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Gaming.XboxGameBar;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Devices;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace SVoice.GameBar
{
    public sealed partial class WidgetPage : Page
    {
        private readonly SpeechSynthesizer _synthesizer = new SpeechSynthesizer();
        private readonly MediaPlayer _player = new MediaPlayer();
        private readonly MediaPlayer _echoPlayer = new MediaPlayer();
        private readonly XttsBridgeClient _xttsBridge = new XttsBridgeClient();
        private IRandomAccessStream? _currentStream;
        private IRandomAccessStream? _echoStream;
        private XboxGameBarWidget? _widget;
        private XboxGameBarWidgetActivity? _speechActivity;
        private bool _initialized;
        private bool _usingVirtualCable;
        private bool _echoEnabled;
        private bool _isGenerating;

        private const string EchoSettingKey = "echoEnabled";
        private const string VoiceSettingKey = "selectedVoiceProfileId";

        public WidgetPage()
        {
            InitializeComponent();
            _player.MediaEnded += Player_MediaEnded;
            _player.MediaFailed += Player_MediaFailed;
            _echoPlayer.MediaFailed += EchoPlayer_MediaFailed;
        }

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
                LoadEchoSetting();
                await ConfigureAudioOutputAsync();
                await LoadVoiceChoicesAsync();
                await RunOnUiThreadAsync(() =>
                {
                    SetReadyState();
                    MessageBox.Focus(FocusState.Programmatic);
                });
                App.Log("WidgetPage initialized successfully.");
            }
            catch (Exception exception)
            {
                App.Log($"WidgetPage initialization failed: {exception}");
                await RunOnUiThreadAsync(() =>
                    SetErrorState("Não foi possível inicializar o SVoice."));
            }
        }

        private void Page_Unloaded(object sender, RoutedEventArgs args)
        {
            // Game Bar can unload and reload the same page when a widget is hidden.
            // Keep the media objects alive so the widget remains usable after restore.
            StopPlayback();
        }

        private async Task ConfigureAudioOutputAsync()
        {
            try
            {
                var devices = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector());
                var cable = devices.FirstOrDefault(device =>
                    device.Name.IndexOf("CABLE Input", StringComparison.OrdinalIgnoreCase) >= 0);

                await RunOnUiThreadAsync(() =>
                {
                    if (cable != null)
                    {
                        _player.AudioDevice = cable;
                        _usingVirtualCable = true;
                        OutputText.Text = "CABLE INPUT";
                        OutputIcon.Foreground = new SolidColorBrush(Color.FromArgb(255, 139, 233, 210));
                        EchoButton.IsEnabled = true;
                        EchoButton.IsChecked = _echoEnabled;
                        return;
                    }

                    _usingVirtualCable = false;
                    OutputText.Text = "SAÍDA PADRÃO";
                    OutputIcon.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 199, 118));
                    EchoButton.IsEnabled = false;
                    EchoButton.IsChecked = false;
                });
            }
            catch
            {
                await RunOnUiThreadAsync(() =>
                {
                    _usingVirtualCable = false;
                    OutputText.Text = "SAÍDA PADRÃO";
                    EchoButton.IsEnabled = false;
                    EchoButton.IsChecked = false;
                });
            }
        }

        private void LoadEchoSetting()
        {
            var values = ApplicationData.Current.LocalSettings.Values;
            _echoEnabled = values.TryGetValue(EchoSettingKey, out var value) &&
                value is bool enabled && enabled;
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

        private async Task LoadVoiceChoicesAsync(string? preferredProfileId = null)
        {
            var savedProfileId = preferredProfileId ??
                ApplicationData.Current.LocalSettings.Values[VoiceSettingKey] as string;
            await RunOnUiThreadAsync(() =>
            {
                RefreshVoicesButton.IsEnabled = false;
                VoiceBox.Items.Clear();
                VoiceBox.Items.Add(new VoiceChoice("Voz do Windows"));
                VoiceBox.SelectedIndex = 0;
                StatusText.Text = "CONECTANDO XTTS";
            });

            try
            {
                var profiles = await _xttsBridge.GetProfilesAsync();
                App.Log($"XTTS profiles loaded: {profiles.Count}.");
                await RunOnUiThreadAsync(() =>
                {
                    foreach (var profile in profiles)
                    {
                        VoiceBox.Items.Add(new VoiceChoice($"{profile.Name}  ·  Clonada", profile.Id));
                    }

                    if (!string.IsNullOrWhiteSpace(savedProfileId))
                    {
                        var savedChoice = VoiceBox.Items
                            .OfType<VoiceChoice>()
                            .FirstOrDefault(choice => choice.ProfileId == savedProfileId);
                        if (savedChoice != null)
                        {
                            VoiceBox.SelectedItem = savedChoice;
                        }
                    }

                    ToolTipService.SetToolTip(
                        VoiceBox,
                        profiles.Count == 0
                            ? "Crie uma voz clonada no aplicativo principal do SVoice."
                            : "Selecione uma voz do Windows ou um perfil clonado do XTTS.");
                });
            }
            catch (Exception exception)
            {
                App.Log($"XTTS profiles unavailable: {exception}");
                await RunOnUiThreadAsync(() =>
                    ToolTipService.SetToolTip(
                        VoiceBox,
                        $"XTTS indisponível: {exception.Message}"));
            }
            finally
            {
                await RunOnUiThreadAsync(() => RefreshVoicesButton.IsEnabled = true);
            }
        }

        private async void RefreshVoicesButton_Click(object sender, RoutedEventArgs args)
        {
            await LoadVoiceChoicesAsync();
            await RunOnUiThreadAsync(SetReadyState);
        }

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
                picker.FileTypeFilter.Add(".wav");
                picker.FileTypeFilter.Add(".mp3");
                picker.FileTypeFilter.Add(".m4a");
                picker.FileTypeFilter.Add(".flac");
                picker.FileTypeFilter.Add(".ogg");

                var files = await picker.PickMultipleFilesAsync();
                if (files.Count == 0)
                {
                    return;
                }

                var invalidFile = files.FirstOrDefault(file => string.IsNullOrWhiteSpace(file.Path));
                if (invalidFile != null)
                {
                    throw new InvalidOperationException(
                        $"O arquivo {invalidFile.Name} não possui um caminho local acessível.");
                }

                // The picker continuation may resume off the UI thread; XAML objects
                // must be created and shown on the dispatcher thread.
                var requestedName = await RunOnUiThreadAsync(async () =>
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
                        Text = $"{files.Count} áudio(s) selecionado(s). Use gravações limpas da mesma pessoa, de preferência entre 10 e 30 segundos.",
                        TextWrapping = TextWrapping.Wrap,
                    });
                    content.Children.Add(nameBox);

                    var dialog = new ContentDialog
                    {
                        Title = "Clonar voz com XTTS",
                        Content = content,
                        PrimaryButtonText = "Clonar",
                        CloseButtonText = "Cancelar",
                        DefaultButton = ContentDialogButton.Primary,
                    };
                    return await dialog.ShowAsync() == ContentDialogResult.Primary
                        ? nameBox.Text
                        : null;
                });
                if (requestedName == null)
                {
                    return;
                }

                await RunOnUiThreadAsync(() =>
                {
                    _isGenerating = true;
                    SpeakButton.IsEnabled = false;
                    CloneVoiceButton.IsEnabled = false;
                    RefreshVoicesButton.IsEnabled = false;
                    SetCloningState();
                });

                IReadOnlyList<string> referencePaths = files.Select(file => file.Path).ToArray();
                var profile = await _xttsBridge.CreateProfileAsync(requestedName, referencePaths);
                await LoadVoiceChoicesAsync(profile.Id);
                await RunOnUiThreadAsync(SetReadyState);
                App.Log($"XTTS profile created: {profile.Id} ({profile.Name}).");
            }
            catch (Exception exception)
            {
                App.Log($"XTTS profile creation failed: {exception}");
                await RunOnUiThreadAsync(() => SetErrorState(exception.Message));
            }
            finally
            {
                await RunOnUiThreadAsync(() =>
                {
                    _isGenerating = false;
                    SpeakButton.IsEnabled = true;
                    CloneVoiceButton.IsEnabled = true;
                    RefreshVoicesButton.IsEnabled = true;
                });
            }
        }

        private void VoiceBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
        {
            if (VoiceBox.SelectedItem is VoiceChoice choice)
            {
                ApplicationData.Current.LocalSettings.Values[VoiceSettingKey] = choice.ProfileId ?? string.Empty;
            }
        }

        private async void SpeakButton_Click(object sender, RoutedEventArgs args)
        {
            await SpeakAsync();
        }

        private async void Composer_KeyDown(object sender, KeyRoutedEventArgs args)
        {
            if (args.Key == VirtualKey.Enter)
            {
                args.Handled = true;
                await SpeakAsync();
            }
            else if (args.Key == VirtualKey.Escape)
            {
                args.Handled = true;
                StopPlayback();
                SetReadyState();
            }
        }

        private async Task SpeakAsync()
        {
            if (_isGenerating)
            {
                return;
            }

            var text = MessageBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            try
            {
                _isGenerating = true;
                SpeakButton.IsEnabled = false;
                StopPlayback();
                StartSpeechActivity();

                var choice = VoiceBox.SelectedItem as VoiceChoice;
                string contentType;
                if (choice?.IsCloned == true)
                {
                    SetGeneratingState();
                    App.Log($"XTTS synthesis requested. Profile={choice.ProfileId}; Characters={text.Length}.");
                    var result = await _xttsBridge.SynthesizeAsync(text, choice.ProfileId!);
                    _currentStream = await CreateAudioStreamAsync(result.Bytes);
                    contentType = result.ContentType;
                }
                else
                {
                    SetSpeakingState();
                    var speechStream = await _synthesizer.SynthesizeTextToStreamAsync(text);
                    _currentStream = speechStream;
                    contentType = speechStream.ContentType;
                }

                await RunOnUiThreadAsync(() =>
                {
                    SetSpeakingState();

                    if (_echoEnabled && _usingVirtualCable)
                    {
                        _echoStream = _currentStream.CloneStream();
                        _echoPlayer.Source = MediaSource.CreateFromStream(
                            _echoStream,
                            contentType);
                    }

                    _player.Source = MediaSource.CreateFromStream(_currentStream, contentType);

                    _player.Play();
                    if (_echoPlayer.Source != null)
                    {
                        _echoPlayer.Play();
                    }

                    HistoryText.Text = text;
                    HistoryPanel.Visibility = Visibility.Visible;
                    MessageBox.Text = string.Empty;
                    MessageBox.Focus(FocusState.Programmatic);
                });
            }
            catch (Exception exception)
            {
                App.Log($"Speech failed: {exception}");
                await RunOnUiThreadAsync(() =>
                {
                    StopPlayback();
                    SetErrorState(exception.Message);
                });
            }
            finally
            {
                await RunOnUiThreadAsync(() =>
                {
                    _isGenerating = false;
                    SpeakButton.IsEnabled = true;
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
            await Dispatcher.RunAsync(
                CoreDispatcherPriority.Normal,
                () =>
                {
                    StopPlayback();
                    SetErrorState(args.ErrorMessage);
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

        private void SetReadyState()
        {
            StatusText.Text = "PRONTO";
            StatusText.Foreground = new SolidColorBrush(Color.FromArgb(217, 139, 233, 210));
            StatusDot.Fill = new SolidColorBrush(Color.FromArgb(255, 139, 233, 210));
        }

        private void SetSpeakingState()
        {
            StatusText.Text = "FALANDO";
            StatusText.Foreground = new SolidColorBrush(Color.FromArgb(230, 255, 199, 118));
            StatusDot.Fill = new SolidColorBrush(Color.FromArgb(255, 255, 199, 118));
        }

        private void SetGeneratingState()
        {
            StatusText.Text = "GERANDO XTTS";
            StatusText.Foreground = new SolidColorBrush(Color.FromArgb(230, 191, 166, 255));
            StatusDot.Fill = new SolidColorBrush(Color.FromArgb(255, 191, 166, 255));
        }

        private void SetCloningState()
        {
            StatusText.Text = "CLONANDO VOZ";
            StatusText.Foreground = new SolidColorBrush(Color.FromArgb(230, 191, 166, 255));
            StatusDot.Fill = new SolidColorBrush(Color.FromArgb(255, 191, 166, 255));
        }

        private void SetErrorState(string message)
        {
            StatusText.Text = "ERRO";
            StatusText.Foreground = new SolidColorBrush(Color.FromArgb(230, 255, 124, 135));
            StatusDot.Fill = new SolidColorBrush(Color.FromArgb(255, 255, 124, 135));
            MessageBox.PlaceholderText = string.IsNullOrWhiteSpace(message)
                ? "Não foi possível reproduzir a voz."
                : message;
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

        private async Task RunOnUiThreadAsync(Action action)
        {
            if (Dispatcher.HasThreadAccess)
            {
                action();
                return;
            }

            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => action());
        }

        private async Task<T> RunOnUiThreadAsync<T>(Func<Task<T>> action)
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
