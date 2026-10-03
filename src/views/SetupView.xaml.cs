using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ScamWYF.Launcher.Views
{
    /// <summary>One line of the setup status table.</summary>
    internal sealed class CheckRow
    {
        public CheckRow(string name, bool ok, string detail)
        {
            Name = name;
            State = ok ? "ok" : "missing";
            Detail = detail ?? "";
            Brush = (Brush)Application.Current.FindResource(ok ? "TextBrush" : "BadBrush");
        }

        public string Name { get; private set; }
        public string State { get; private set; }
        public string Detail { get; private set; }
        public Brush Brush { get; private set; }
    }

    /// <summary>
    /// The setup tab: what is missing from the install, and a button to run the setup script.
    /// </summary>
    public partial class SetupView : UserControl
    {
        /// <summary>Set by the window once both are constructed; see MainWindow's remarks.</summary>
        public MainWindow Host { get; set; }

        private readonly ObservableCollection<CheckRow> _rows = new ObservableCollection<CheckRow>();

        public SetupView()
        {
            InitializeComponent();
            Checks.ItemsSource = _rows;
        }

        /// <summary>Re-probe and redraw. Cheap, so called whenever the tab is shown.</summary>
        public void Reload()
        {
            var install = Host.Install;

            Headline.Text = install.Exists
                ? install.Path + "  -  " + install.Summary()
                : "No game install found";

            _rows.Clear();

            _rows.Add(new CheckRow("Game install", install.HasGame,
                install.HasGame ? install.ManagedFolder : "not found - check the path above"));
            _rows.Add(new CheckRow("Loader (Doorstop)", install.HasDoorstop,
                install.HasDoorstop ? "winhttp.dll present" : "the loader half, which starts everything"));
            _rows.Add(new CheckRow("Corlib override", install.HasCorlibOverride,
                install.HasCorlibOverride
                    ? "unstripped_corlib\\ seeded"
                    : "required: the shipped mscorlib is stripped, and the loader cannot start without this"));
            _rows.Add(new CheckRow("Override wired up", install.DoorstopPointsAtOverride,
                install.DoorstopPointsAtOverride
                    ? "doorstop_config.ini points at it"
                    : "doorstop_config.ini does not set dll_search_path_override"));
            _rows.Add(new CheckRow("BepInEx", install.HasBepInEx,
                install.HasBepInEx ? install.BepInExCore : "not installed"));

            var canRun = install.HasGame;
            RunButton.IsEnabled = canRun && Host.Setup.IsAvailable;
            OfflineButton.IsEnabled = RunButton.IsEnabled;

            if (!Host.Setup.IsAvailable && Output.Text.Length == 0)
            {
                // Only into an empty box. Reload runs every time the tab is shown, and appending a
                // constant message each time made it look as though setup had been run repeatedly.
                Append("");
                Append(Host.Setup.MissingReason);
            }
        }

        private void OnRun(object sender, RoutedEventArgs e) { Start(false); }
        private void OnRunOffline(object sender, RoutedEventArgs e) { Start(true); }
        private void OnOpenFolder(object sender, RoutedEventArgs e) { Host.OpenFolder(Host.Install.Path); }

        private void Start(bool offline)
        {
            if (Host.Busy) return;

            Append("");
            Append("=== running setup.ps1" + (offline ? " -Offline" : "") + " ===");

            Host.SetBusy(true);
            RunButton.IsEnabled = false;
            OfflineButton.IsEnabled = false;

            // Both callbacks are marshalled, and neither may be simplified back to touching a control
            // directly.
            //
            // They arrive on other threads: each output line from the process's async reader, and the
            // exit code from the thread SetupRunner started. Reading a DependencyProperty off the
            // dispatcher throws InvalidOperationException, and an unhandled exception on a thread-pool
            // thread does not raise a dialog - it terminates the process. That is not a hypothetical:
            // reading the follow checkbox here is what made "Set up / repair" kill the launcher outright,
            // after a green build, because nothing else in the pipeline ever ran a script.
            Host.RunSetup(offline,
                line => OnUi(delegate
                {
                    Append(line);

                    // "Follow" means keep the newest line visible. It used to be a call to Pump() after
                    // every line, which was WinForms' Application.DoEvents: WPF repaints once this
                    // callback returns, so there is nothing to force, and pumping from inside a
                    // per-line callback only invites re-entrancy.
                    if (FollowLog.IsChecked == true) Output.ScrollToEnd();
                }),
                exitCode => OnUi(delegate
                {
                    Append("");
                    Append(exitCode == 0
                        ? "=== setup finished ==="
                        : "=== setup reported problems (exit " + exitCode + ") ===");

                    Host.SetBusy(false);
                    Reload();
                }));
        }

        /// <summary>
        /// Run work on the UI thread.
        /// </summary>
        /// <remarks>
        /// The script runner reports from threads of its own, so every callback it is handed goes
        /// through here. Running inline when already on the dispatcher keeps a burst of output lines in
        /// order, which a BeginInvoke per line would only preserve by accident.
        /// </remarks>
        private void OnUi(Action action)
        {
            if (Dispatcher.CheckAccess())
            {
                action();
                return;
            }

            try
            {
                Dispatcher.BeginInvoke(action);
            }
            catch (Exception)
            {
                // The window closed while the script was still running. There is nobody left to tell.
            }
        }

        private void Append(string line)
        {
            // Belt and braces: every current caller arrives via OnUi, but this is the one method that
            // writes to the window from script output, so it keeps its own guard rather than trusting
            // each call site to remember.
            OnUi(delegate
            {
                Output.AppendText(line + Environment.NewLine);
                Output.CaretIndex = Output.Text.Length;
            });
        }
    }
}