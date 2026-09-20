using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Gaming.XboxGameBar;
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
        private SpeechSynthesisStream _currentStream;
        private XboxGameBarWidget _widget;
        private bool _initialized;

        public WidgetPage()
        {
            InitializeComponent();
            _player.MediaEnded += Player_MediaEnded;
            _player.MediaFailed += Player_MediaFailed;
        }

        protected override void OnNavigatedTo(NavigationEventArgs args)
        {
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
            await ConfigureAudioOutputAsync();
            SetReadyState();
            MessageBox.Focus(FocusState.Programmatic);
        }

        private void Page_Unloaded(object sender, RoutedEventArgs args)
        {
            if (_widget != null)
            {
                _widget.RequestedOpacityChanged -= Widget_RequestedOpacityChanged;
            }

            StopPlayback();
            _player.Dispose();
            _synthesizer.Dispose();
        }

        private async Task ConfigureAudioOutputAsync()
        {
            try
            {
                var devices = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector());
                var cable = devices.FirstOrDefault(device =>
                    device.Name.IndexOf("CABLE Input", StringComparison.OrdinalIgnoreCase) >= 0);

                if (cable != null)
                {
                    _player.AudioDevice = cable;
                    OutputText.Text = "CABLE INPUT";
                    OutputIcon.Foreground = new SolidColorBrush(Color.FromArgb(255, 139, 233, 210));
                    return;
                }

                OutputText.Text = "SAÍDA PADRÃO";
                OutputIcon.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 199, 118));
            }
            catch
            {
                OutputText.Text = "SAÍDA PADRÃO";
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
            var text = MessageBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            try
            {
                SetSpeakingState();
                StopPlayback();
                _currentStream = await _synthesizer.SynthesizeTextToStreamAsync(text);
                _player.Source = MediaSource.CreateFromStream(_currentStream, _currentStream.ContentType);
                _player.Play();

                HistoryText.Text = text;
                HistoryPanel.Visibility = Visibility.Visible;
                MessageBox.Text = string.Empty;
                MessageBox.Focus(FocusState.Programmatic);
            }
            catch (Exception exception)
            {
                SetErrorState(exception.Message);
            }
        }

        private void StopPlayback()
        {
            _player.Pause();
            _player.Source = null;
            _currentStream?.Dispose();
            _currentStream = null;
        }

        private async void Player_MediaEnded(MediaPlayer sender, object args)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, SetReadyState);
        }

        private async void Player_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            await Dispatcher.RunAsync(
                CoreDispatcherPriority.Normal,
                () => SetErrorState(args.ErrorMessage));
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
    }
}
