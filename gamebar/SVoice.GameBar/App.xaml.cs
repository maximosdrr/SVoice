using System;
using System.IO;
using Microsoft.Gaming.XboxGameBar;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.AppService;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace SVoice.GameBar
{
    sealed partial class App : Application
    {
        private XboxGameBarWidget? _widget;

        public App()
        {
            InitializeComponent();
            Suspending += OnSuspending;
            UnhandledException += (_, eventArgs) =>
            {
                Log($"Unhandled exception: {eventArgs.Message} | {eventArgs.Exception}");
            };
        }

        protected override void OnActivated(IActivatedEventArgs args)
        {
            try
            {
                Log($"Activation received. Kind={args.Kind}; RuntimeType={args.GetType().FullName}");
                XboxGameBarWidgetActivatedEventArgs? widgetArgs = null;

                if (args.Kind == ActivationKind.Protocol)
                {
                    widgetArgs = GetWidgetActivationArgs(args);
                }

                if (widgetArgs == null)
                {
                    Log("Game Bar activation arguments could not be projected.");
                    ShowActivationError("A Game Bar não conseguiu inicializar o widget.");
                    return;
                }

                Log($"Widget activation. Extension={widgetArgs.AppExtensionId}; Launch={widgetArgs.IsLaunchActivation}");
                if (!widgetArgs.IsLaunchActivation)
                {
                    return;
                }

                var frame = CreateFrame();
                Window.Current.Content = frame;
                _widget = new XboxGameBarWidget(widgetArgs, Window.Current.CoreWindow, frame);
                if (!frame.Navigate(typeof(WidgetPage), _widget))
                {
                    throw new InvalidOperationException("A página do widget recusou a navegação.");
                }

                Window.Current.Closed += WidgetWindowClosed;
                Window.Current.Activate();
                Log("Widget window activated successfully.");
            }
            catch (Exception exception)
            {
                Log($"Widget activation failed: {exception}");
                ShowActivationError("Não foi possível carregar o SVoice. Feche o widget e tente novamente.");
            }
        }

        private static XboxGameBarWidgetActivatedEventArgs? GetWidgetActivationArgs(IActivatedEventArgs args)
        {
            if (args is XboxGameBarWidgetActivatedEventArgs projectedArgs)
            {
                return projectedArgs;
            }

            if (args is WinRT.IWinRTObject winRtObject)
            {
                return XboxGameBarWidgetActivatedEventArgs.FromAbi(winRtObject.NativeObject.ThisPtr);
            }

            return null;
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            if (string.Equals(args.Arguments, "--startup-probe", StringComparison.Ordinal))
            {
                try
                {
                    var probeFrame = CreateFrame();
                    Window.Current.Content = probeFrame;
                    if (!probeFrame.Navigate(typeof(WidgetPage)))
                    {
                        throw new InvalidOperationException("A página do widget recusou a navegação de diagnóstico.");
                    }

                    Log("Startup probe passed.");
                }
                catch (Exception exception)
                {
                    Log($"Startup probe failed: {exception}");
                }
                finally
                {
                    Exit();
                }

                return;
            }

            var frame = Window.Current.Content as Frame ?? CreateFrame();
            Window.Current.Content = frame;

            if (frame.Content == null)
            {
                frame.Navigate(typeof(WidgetPage));
            }

            Window.Current.Activate();
        }

        protected override void OnBackgroundActivated(BackgroundActivatedEventArgs args)
        {
            if (args.TaskInstance.TriggerDetails is AppServiceTriggerDetails details &&
                details.Name.Equals(XttsBridgeChannel.ServiceName, StringComparison.Ordinal))
            {
                var deferral = args.TaskInstance.GetDeferral();
                XttsBridgeChannel.Accept(details.AppServiceConnection, deferral);
                Log("XTTS App Service connection accepted.");
                return;
            }

            base.OnBackgroundActivated(args);
        }

        private static Frame CreateFrame()
        {
            var frame = new Frame();
            frame.NavigationFailed += (_, eventArgs) =>
                throw new Exception($"Não foi possível abrir {eventArgs.SourcePageType.FullName}");
            return frame;
        }

        private static void ShowActivationError(string message)
        {
            var text = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(24),
            };
            Window.Current.Content = new Grid { Children = { text } };
            Window.Current.Activate();
        }

        internal static void Log(string message)
        {
            try
            {
                var path = Path.Combine(
                    Windows.Storage.ApplicationData.Current.LocalFolder.Path,
                    "gamebar.log");
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
            catch
            {
                // Logging must never prevent the widget from opening.
            }
        }

        private void WidgetWindowClosed(object sender, Windows.UI.Core.CoreWindowEventArgs args)
        {
            _widget = null;
            Window.Current.Closed -= WidgetWindowClosed;
        }

        private void OnSuspending(object sender, SuspendingEventArgs args)
        {
            var deferral = args.SuspendingOperation.GetDeferral();
            _widget = null;
            deferral.Complete();
        }
    }
}
