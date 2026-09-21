using System;
using System.Threading.Tasks;
using Windows.ApplicationModel.AppService;
using Windows.ApplicationModel.Background;

namespace SVoice.GameBar
{
    internal static class XttsBridgeChannel
    {
        internal const string ServiceName = "SVoice.XttsBridge";

        private static readonly object Sync = new object();
        private static AppServiceConnection? _connection;
        private static BackgroundTaskDeferral? _deferral;
        private static TaskCompletionSource<AppServiceConnection> _pending = CreatePending();

        internal static void Accept(
            AppServiceConnection connection,
            BackgroundTaskDeferral deferral)
        {
            lock (Sync)
            {
                if (_connection != null)
                {
                    _connection.ServiceClosed -= Connection_ServiceClosed;
                    _connection.Dispose();
                }

                _deferral?.Complete();
                _connection = connection;
                _deferral = deferral;
                connection.ServiceClosed += Connection_ServiceClosed;
                _pending.TrySetResult(connection);
            }
        }

        internal static async Task<AppServiceConnection> WaitAsync(TimeSpan timeout)
        {
            Task<AppServiceConnection> pending;
            lock (Sync)
            {
                if (_connection != null)
                {
                    return _connection;
                }

                pending = _pending.Task;
            }

            return await pending.WaitAsync(timeout);
        }

        private static void Connection_ServiceClosed(
            AppServiceConnection sender,
            AppServiceClosedEventArgs args)
        {
            lock (Sync)
            {
                if (!ReferenceEquals(_connection, sender))
                {
                    return;
                }

                sender.ServiceClosed -= Connection_ServiceClosed;
                sender.Dispose();
                _connection = null;
                _deferral?.Complete();
                _deferral = null;
                _pending = CreatePending();
            }

            App.Log($"XTTS App Service connection closed: {args.Status}.");
        }

        private static TaskCompletionSource<AppServiceConnection> CreatePending()
        {
            return new TaskCompletionSource<AppServiceConnection>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
