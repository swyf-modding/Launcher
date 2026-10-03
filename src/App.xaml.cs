using System;
using System.Windows;

namespace ScamWYF.Launcher
{
    /// <summary>
    /// Process entry point.
    /// </summary>
    /// <remarks>
    /// Built by hand in OnStartup rather than through StartupUri, because the launcher accepts a path to
    /// the game as its first argument and StartupUri has no way to pass arguments to a window's
    /// constructor.
    ///
    /// Unhandled exceptions are reported rather than left to the default handler, which in a WinExe
    /// means a crash with no window at all and nothing in the event log a player would ever look at. A
    /// launcher that fails should say so in words.
    /// </remarks>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            DispatcherUnhandledException += OnUnhandled;

            var window = new MainWindow(e.Args);
            MainWindow = window;
            window.Show();
        }

        private void OnUnhandled(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            Dialogs.Error("Something went wrong", Describe(e.Exception));
            e.Handled = true;
        }

        /// <summary>
        /// An exception as something a person can act on.
        /// </summary>
        /// <remarks>
        /// The outermost message is often useless on its own - "Provide value on
        /// TypeConverterMarkupExtension threw an exception" says only that something in the XAML is
        /// wrong, not what - so this walks to the innermost cause and keeps a few stack frames to say
        /// where it came from. A launcher that fails silently is worse than one that fails loudly.
        /// </remarks>
        private static string Describe(Exception error)
        {
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

                // XAML failures carry the line they happened on, which is the only thing that actually
                // localises them. Without this a bad Style setter is reported against a class name and
                // there is nothing to go on.
                var parse = inner as System.Windows.Markup.XamlParseException;
                var line = parse != null && parse.LineNumber > 0
                    ? "  (line " + parse.LineNumber + ", position " + parse.LinePosition + ")"
                    : "";

                text += (text.Length == 0 ? "" : Environment.NewLine + Environment.NewLine)
                      + inner.GetType().Name + ": " + inner.Message + line
                      + (frame.Length == 0 ? "" : Environment.NewLine + "  at " + frame);
            }

            return text;
        }
    }
}