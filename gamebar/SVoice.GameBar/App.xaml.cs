using System;
using Microsoft.Gaming.XboxGameBar;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace SVoice.GameBar
{
    sealed partial class App : Application
    {
        private XboxGameBarWidget _widget;

        public App()
        {
            InitializeComponent();
            Suspending += OnSuspending;
        }

        protected override void OnActivated(IActivatedEventArgs args)
        {
            XboxGameBarWidgetActivatedEventArgs widgetArgs = null;

            if (args.Kind == ActivationKind.Protocol &&
                args is IProtocolActivatedEventArgs protocolArgs &&
                protocolArgs.Uri.Scheme.Equals("ms-gamebarwidget", StringComparison.OrdinalIgnoreCase))
            {
                widgetArgs = args as XboxGameBarWidgetActivatedEventArgs;
            }

            if (widgetArgs == null || !widgetArgs.IsLaunchActivation)
            {
                return;
            }

            var frame = CreateFrame();
            Window.Current.Content = frame;
            _widget = new XboxGameBarWidget(widgetArgs, Window.Current.CoreWindow, frame);
            frame.Navigate(typeof(WidgetPage), _widget);
            Window.Current.Closed += WidgetWindowClosed;
            Window.Current.Activate();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            var frame = Window.Current.Content as Frame ?? CreateFrame();
            Window.Current.Content = frame;

            if (frame.Content == null)
            {
                frame.Navigate(typeof(WidgetPage));
            }

            Window.Current.Activate();
        }

        private static Frame CreateFrame()
        {
            var frame = new Frame();
            frame.NavigationFailed += (_, eventArgs) =>
                throw new Exception($"Não foi possível abrir {eventArgs.SourcePageType.FullName}");
            return frame;
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
