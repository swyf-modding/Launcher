using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Switchboard
{
    /// <summary>
    /// Process entry point for the UI.
    /// </summary>
    /// <remarks>
    /// The window is built here rather than from a StartupUri, because the switchboard accepts a path to
    /// the game as its first argument and there is no way to pass arguments to a window's constructor
    /// from markup.
    ///
    /// Unhandled exceptions are reported rather than left to the default handler, which in a windowed
    /// build means a crash with no window at all and nothing a player would ever look at. A switchboard
    /// that fails should say so in words.
    /// </remarks>
    public partial class App : Application
    {
        /// <summary>Where the window icon lives inside the assembly.</summary>
        private const string IconUri = "avares://Switchboard/assets/switchboard.png";

        /// <summary>
        /// The switchboard's icon, decoded once, from the same drawing as the site's favicon.
        /// </summary>
        /// <remarks>
        /// The main window and the config editor name the asset directly in their markup. This is
        /// for the dialogs, which are built in code: they show for a moment, so decoding a 5 KB png
        /// for each one is not worth it - the bitmap is loaded once and every icon shares it.
        /// </remarks>
        private static readonly Bitmap IconBitmap = LoadIconBitmap();

        /// <summary>A window icon for a window built in code, or null when the asset is missing.</summary>
        internal static WindowIcon NewWindowIcon()
        {
            return IconBitmap == null ? null : new WindowIcon(IconBitmap);
        }

        private static Bitmap LoadIconBitmap()
        {
            try
            {
                using (var stream = AssetLoader.Open(new Uri(IconUri)))
                {
                    return new Bitmap(stream);
                }
            }
            catch (Exception)
            {
                // A missing icon is not worth failing a launch over. The window simply shows
                // whatever the platform would have shown.
                return null;
            }
        }

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            // Dispatcher failures are the ones raised by an event handler - a click that throws - and
            // they can be contained, so the window survives them.
            Dispatcher.UIThread.UnhandledException += (sender, e) =>
            {
                e.Handled = true;
                _ = Dialogs.ErrorAsync("Something went wrong", Describe(e.Exception));
            };

            // Anything that escapes a non-UI thread cannot be shown in a dialog: there is no window
            // left once the runtime is unwinding, so this only writes what happened where someone can
            // find it. stderr, because the process may be started from a terminal.
            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                try
                {
                    Console.Error.WriteLine("Unhandled: " + Describe(e.ExceptionObject as Exception));
                }
                catch (Exception)
                {
                    // Nothing left to do. Reporting must not itself throw.
                }
            };

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = new MainWindow(desktop.Args);
            }

            base.OnFrameworkInitializationCompleted();
        }

        /// <summary>
        /// An exception as something a person can act on.
        /// </summary>
        /// <remarks>
        /// The outermost message is often useless on its own - "Unable to resolve property" says only
        /// that something in the XAML is wrong, not what - so this walks to the innermost cause and
        /// keeps a few stack frames to say where it came from. A switchboard that fails silently is worse
        /// than one that fails loudly.
        /// </remarks>
        internal static string Describe(Exception error)
        {
            if (error == null) return "Something went wrong, and no further detail was recorded.";

            var text = "";

            for (var inner = error; inner != null; inner = inner.InnerException)
            {
                var where = inner.StackTrace;
                var frame = "";

                if (!string.IsNullOrEmpty(where))
                {
                    var lines = where.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length > 0) frame = lines[0].Trim();
                }

                text += (text.Length == 0 ? "" : Environment.NewLine + Environment.NewLine)
                      + inner.GetType().Name + ": " + inner.Message
                      + (frame.Length == 0 ? "" : Environment.NewLine + "  at " + frame);
            }

            return text;
        }
    }
}
