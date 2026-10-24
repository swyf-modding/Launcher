using System;
using System.Threading;
using Avalonia;
using Avalonia.Media;

namespace Switchboard
{
    /// <summary>
    /// Process entry point.
    /// </summary>
    /// <remarks>
    /// The desktop lifetime, not the single-view one: this is a window on Windows, Linux and macOS,
    /// and <c>StartWithClassicDesktopLifetime</c> is the Avalonia equivalent of WPF's
    /// <c>Application.Run</c> - it owns the main window and the message loop, so
    /// <c>OnFrameworkInitializationCompleted</c> in <see cref="App"/> has somewhere to put the window
    /// it builds.
    ///
    /// The arguments are forwarded rather than set aside: the switchboard accepts a path to the game as
    /// its first argument, which is how the tests and an unusual install point it at a folder.
    /// </remarks>
    internal static class Program
    {
        private static Mutex _singleInstance;

        [STAThread]
        public static void Main(string[] args)
        {
            // A protocol click starts a second process even when the app is open. Hand the
            // request to the window that is already there instead of opening another one.
            // Launches without a request keep the old behaviour and open as normal. The
            // mutex is held by every first window no matter how it was launched, so a
            // later click can always tell one is already there.
            bool createdNew = true;
            try
            {
                _singleInstance = new Mutex(true, "SwitchboardSingleInstance", out createdNew);
            }
            catch (Exception)
            {
                createdNew = true;
            }

            InstallRequest request;
            if (!createdNew && InstallRequest.TryFind(args, out request))
            {
                PendingInstalls.WriteRequest(request);
                return;
            }

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        /// <summary>
        /// The app builder, named and public so a designer or a test host could reach it.
        /// </summary>
        public static AppBuilder BuildAvaloniaApp()
        {
            return AppBuilder.Configure<App>()
                .UsePlatformDetect()
                // The site's body face, bundled, so the app looks the same on a machine that has
                // neither it nor anything else installed, and does not fall back to a serif
                // default on Linux. The display face is set in Theme.xaml.
                .With(new FontManagerOptions
                {
                    DefaultFamilyName = "avares://Switchboard/fonts/body/#Nunito Sans",
                })
                .LogToTrace();
        }
    }
}
