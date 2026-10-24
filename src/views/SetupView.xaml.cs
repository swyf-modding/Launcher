using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ScamWYF.Setup;

namespace Switchboard.Views
{
    /// <summary>One line of the setup status table.</summary>
    internal sealed class CheckRow
    {
        public CheckRow(string name, bool ok, string detail)
            : this(name, ok ? "ok" : "missing", detail, ok ? Palette.Text : Palette.Bad)
        {
        }

        /// <summary>A row whose state is neither ok nor missing, such as a stale override.</summary>
        public CheckRow(string name, string state, string detail, string brushKey)
        {
            Name = name;
            State = state;
            Detail = detail ?? "";
            Brush = Palette.Brush(brushKey);
        }

        public string Name { get; private set; }
        public string State { get; private set; }
        public string Detail { get; private set; }
        public IBrush Brush { get; private set; }
    }

    /// <summary>
    /// The setup tab: what is missing from the install, and a button to run the setup.
    /// </summary>
    public partial class SetupView : UserControl
    {
        /// <summary>Set by the window once both are constructed; see MainWindow's remarks.</summary>
        public MainWindow Host { get; set; }

        /// <summary>What this tab is for, in one line. Shown under the tab strip.</summary>
        public string Purpose
        {
            get
            {
                return "Checks what your install is missing and repairs it. Nothing here changes " +
                       "the game folder until you press Set up / repair.";
            }
        }

        private readonly ObservableCollection<CheckRow> _rows = new ObservableCollection<CheckRow>();

        public SetupView()
        {
            InitializeComponent();
            PurposeText.Text = Purpose;
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
            // The folder existing is not the same as the folder being current. Setup records which
            // patched copies came from the game's own assemblies, and a game update leaves those
            // copies behind while Managed\ moves on. Doorstop loads the override first, so a stale
            // copy is the difference between "seeded" and "boots".
            var stale = install.StaleOverrides;
            if (!install.HasCorlibOverride)
            {
                _rows.Add(new CheckRow("Corlib override", false,
                    "required: the shipped mscorlib is stripped, and the loader cannot start without this"));
            }
            else if (stale.Count == 0)
            {
                _rows.Add(new CheckRow("Corlib override", true, "unstripped_corlib seeded and current"));
            }
            else
            {
                _rows.Add(new CheckRow("Corlib override", "stale",
                    "built from an older game build: " + string.Join(", ", stale) + " - run Set up / repair",
                    Palette.Warn));
            }
            _rows.Add(new CheckRow("Override wired up", install.DoorstopPointsAtOverride,
                install.DoorstopPointsAtOverride
                    ? "doorstop_config.ini points at it"
                    : "doorstop_config.ini does not set dll_search_path_override"));
            _rows.Add(new CheckRow("BepInEx", install.HasBepInEx,
                install.HasBepInEx ? install.BepInExCore : "not installed"));

            // The switchboard check, unlike the five above it. Those are facts about the game install;
            // this one is a fact about this download, and it is the only way to tell a complete build
            // from a partial one. The payload is embedded in the exe (or read from vendor\ during
            // development), so there is no Setup\ folder beside it to go missing any more.
            string payload;
            var payloadOk = true;
            try
            {
                payload = SetupPayload.ResolveDefault().Describe;
            }
            catch (Exception ex)
            {
                payloadOk = false;
                payload = ex.Message;
            }
            _rows.Add(new CheckRow("Setup payload", payloadOk, payload));

            // Linux only: the game runs under Proton, and without the winhttp override Wine's own
            // winhttp.dll wins and Doorstop never loads. Windows has no prefix and no such problem.
            //
            // The override is applied here, not only when the user clicks Play, because a player who
            // launched the game through Steam already has a prefix and should not have to find the launch
            // option themselves. Best effort: if the prefix is missing or user.reg is not writable, the
            // row says so and the manual launch option remains the fallback.
            var proton = new ProtonIntegration();
            if (proton.IsLinux)
            {
                // Apply the override automatically when the prefix exists and the game is not running.
                // Writing user.reg while Wine is using it is unsafe, and a running game has already
                // loaded (or failed to load) the override, so there is nothing to gain from writing then.
                string running;
                var gameRunning = ModManager.GameRunning(out running);
                if (!proton.DllOverrideIsSet(install) && install.Steam.PrefixDir != null && !gameRunning)
                {
                    proton.EnsureDllOverride(install, NullSetupLog.Instance);
                }

                var overridden = proton.DllOverrideIsSet(install);
                var detail = overridden
                    ? "winhttp is overridden in this prefix"
                    : (install.Steam.PrefixDir == null
                        ? "not set - launch the game once under Proton, then reopen this tab"
                        : (gameRunning
                            ? "not set - quit the game first, then reopen this tab"
                            : "not set - could not write user.reg; add this game's Steam launch option: " + GameInstall.ProtonLaunchOption));
                _rows.Add(new CheckRow("Proton winhttp override", overridden, detail));
            }

            // The setup itself is always present now, so the buttons depend only on there being a game.
            RunButton.IsEnabled = install.HasGame;
            OfflineButton.IsEnabled = install.HasGame;
        }

        private void OnRun(object sender, Avalonia.Interactivity.RoutedEventArgs e) { Start(false); }
        private void OnRunOffline(object sender, Avalonia.Interactivity.RoutedEventArgs e) { Start(true); }
        private void OnOpenFolder(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            Host.OpenFolder(Host.Install.Path);
        }

        private void Start(bool offline)
        {
            if (Host.Busy) return;

            Append("");
            Append("=== running setup" + (offline ? " -Offline" : "") + " ===");

            Host.SetBusy(true);
            RunButton.IsEnabled = false;
            OfflineButton.IsEnabled = false;

            // Both callbacks are marshalled, and neither may be simplified back to touching a control
            // directly.
            //
            // They arrive on other threads: each line from the setup's own log, and the exit code from
            // the worker MainWindow started. Reading a control off the UI thread throws, and an unhandled
            // exception on a thread-pool thread does not raise a dialog - it terminates the process. That
            // is not a hypothetical: reading the follow checkbox here is what made "Set up / repair" kill
            // the switchboard outright, after a green build, because nothing else in the pipeline ever ran
            // a setup.
            Host.RunSetup(offline,
                line => OnUi(delegate
                {
                    Append(line);

                    // "Follow" means keep the newest line visible. There is nothing to force here: the
                    // window repaints once this callback returns, and pumping from inside a per-line
                    // callback only invites re-entrancy.
                    if (FollowLog.IsChecked == true) ScrollToEnd();
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
        /// The setup reports from a thread of its own, so every callback it is handed goes through here.
        /// Running inline when already on the dispatcher keeps a burst of output lines in order, which a
        /// Post per line would only preserve by accident.
        /// </remarks>
        private void OnUi(Action action)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                action();
                return;
            }

            try
            {
                Dispatcher.UIThread.Post(action);
            }
            catch (Exception)
            {
                // The window closed while setup was still running. There is nobody left to tell.
            }
        }

        private void Append(string line)
        {
            // Belt and braces: every current caller arrives via OnUi, but this is the one method that
            // writes to the window from setup output, so it keeps its own guard rather than trusting
            // each call site to remember.
            OnUi(delegate
            {
                Output.Text = (Output.Text ?? "") + line + Environment.NewLine;
                Output.CaretIndex = Output.Text.Length;
            });
        }

        /// <summary>
        /// Keep the newest line visible.
        /// </summary>
        /// <remarks>
        /// Avalonia's TextBox has no ScrollToEnd, so the internal ScrollViewer is asked instead. It is
        /// only in the visual tree once the tab has been laid out, which is exactly when following
        /// matters; when it is not, there is nothing to scroll.
        /// </remarks>
        private void ScrollToEnd()
        {
            var scroll = Output.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (scroll != null) scroll.ScrollToEnd();
        }
    }
}
