using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using ScamWYF.Setup;

namespace Switchboard
{
    /// <summary>
    /// The main window: the four tabs, a status line, and the Play button.
    /// </summary>
    /// <remarks>
    /// Setup, Mods, Get mods, Log. Everything here exists to answer a question the player has.
    ///
    /// The game is launched from here rather than from Steam deliberately. On Windows the exe is started
    /// directly, which skips Steam's own wrapper - and this game has not been observed to care. On Linux
    /// the game is a Windows build under Proton, so the switchboard asks Steam to start it instead, since
    /// that keeps the user's Proton version, the Steam runtime and the overlay intact; the winhttp
    /// override the loader needs is put in place first when it can be.
    ///
    /// The setup itself no longer shells out to PowerShell. It runs in-process through ScamWYF.Setup.Core,
    /// so the switchboard, the CLI and the tests all execute the same code, and the log the window shows is
    /// the setup's own log rather than a process's stdout.
    ///
    /// This type also acts as the host that the four views talk to. That is the same shape the WinForms
    /// version had, and it is kept deliberately: the views need a status line, a busy flag and a way to
    /// re-read both mod tabs, and threading an interface through four constructors to provide three
    /// members would be ceremony without benefit.
    /// </remarks>
    public partial class MainWindow : Window
    {
        internal GameInstall Install { get; private set; }
        internal ModManager Mods { get; private set; }

        /// <summary>The setup, in-process. Reused, because it holds nothing between runs.</summary>
        internal SetupRunner Setup { get; private set; }

        /// <summary>Whether something long-running is going, which the views use to grey things out.</summary>
        public bool Busy { get; private set; }

        private bool _shown;
        private int _lastShownTab = -1;

        private SwitchboardRelease _latestRelease;
        private volatile bool _checkingUpdates;

        public MainWindow(string[] args)
        {
            InitializeComponent();

            var explicitPath = args != null && args.Length > 0 ? args[0] : null;

            Install = GameInstall.Resolve(explicitPath);
            Mods = new ModManager(Install.BepInExCore);
            Setup = new SetupRunner();

            Title = "Scam With Your Friends - modding " + BuildVersion();

            SetupPage.Host = this;
            ModsPage.Host = this;
            GetModsPage.Host = this;
            LogPage.Host = this;

            Opened += OnOpened;
        }

        /// <summary>
        /// This build's version, for the title bar.
        /// </summary>
        /// <remarks>
        /// The informational version, not the assembly version, because the build fills the former from
        /// the git tag and it carries the commit - "which switchboard are you on" is a support question
        /// and the commit is the answer. Falls back to the assembly version, which is all a build with
        /// no tag behind it has.
        /// </remarks>
        private static string BuildVersion()
        {
            var assembly = typeof(MainWindow).Assembly;

            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            if (informational != null && !string.IsNullOrEmpty(informational.InformationalVersion))
            {
                return informational.InformationalVersion;
            }

            var version = assembly.GetName().Version;
            return version != null ? version.ToString() : "unknown version";
        }

        private void OnOpened(object sender, EventArgs e)
        {
            // Past this point the window has been measured once, so a tab switch has to be deferred rather
            // than laid out in the click handler.
            _shown = true;

            if (!Install.Exists)
            {
                Status("No game install found. Set SWYG_GAME_DIR, or pass a path as the first argument.", false);
            }
            else if (!Install.HasBepInEx)
            {
                // The most common first run, and the one where the status bar is the only place that
                // can say it: the window opens on the Setup tab, and the status line is what is left
                // when the eye has finished reading the table.
                State("Ready. BepInEx is not installed yet - the Setup tab will do that.");
            }
            else
            {
                State("Ready.");
            }
            SetupPage.Reload();
            ModsPage.Reload();

            CheckForSwitchboardUpdate(true);
        }



        /// <summary>
        /// Re-probe whichever tab was just arrived at.
        /// </summary>
        /// <remarks>
        /// On arrival rather than on a timer: the only thing that changes under the app's feet is the
        /// install, and that changes when the user runs setup or the game updates.
        /// </remarks>
        private void OnTabChanged(object sender, SelectionChangedEventArgs e)
        {
            // Two reasons to do nothing here. SelectionChanged fires once while the XAML is being
            // constructed - before the views have been given a Host - and the first load is done by
            // OnOpened instead, once everything is wired.
            if (!_shown) return;

            // The important one: SelectionChanged is a *bubbling* event, so every SelectionChanged
            // raised by a ListBox inside the selected tab arrives here too - the Mods and Log tabs both
            // have one, and both clear their rows when they reload.
            //
            // That closed a loop in the WPF build. Reloading the Mods tab clears its list, the list's
            // SelectionChanged bubbles up, this handler runs again with the same index, posts ShowTab,
            // which reloads the Mods tab again - about twenty-five times a second, forever. It looked
            // like three separate bugs: you could not select a row other than the first, the tab
            // flickered, and the cursor flickered over the buttons.
            //
            // A real tab change always moves the index, and an inner list clearing its own selection
            // never does, so comparing against the last index we reacted to separates the two exactly.
            var index = Tabs.SelectedIndex;
            if (index == _lastShownTab) return;
            _lastShownTab = index;


            // Deferred to the dispatcher, so the tab has been measured before it reloads. A tab that was
            // not selected at startup is only given its real width when it is first shown, and one that
            // lays itself out in the same turn as the click measures the width it is about to lose.
            Dispatcher.UIThread.Post(delegate { ShowTab(index); }, DispatcherPriority.Background);
        }

        private void ShowTab(int index)
        {
            switch (index)
            {
                case 0: SetupPage.Reload(); break;
                case 1: ModsPage.Reload(); break;
                case 2: GetModsPage.OnTabShown(); break;
                case 3: LogPage.Reload(); break;
            }
        }

        /// <summary>
        /// Re-read what is on disk, in both mod-related tabs.
        /// </summary>
        /// <remarks>
        /// The two tabs answer the same question from opposite ends - "what is installed" and "what can
        /// I install" - so a change made in one has to show up in the other. Deleting a mod on the Mods
        /// tab leaves the Get mods row claiming it is installed, and installing one left that row
        /// unchanged too, which is the kind of thing that makes a person distrust the whole window.
        /// </remarks>
        public void RefreshModViews()
        {
            ModsPage.Reload();
            GetModsPage.RefreshInstalled();
        }

        /// <summary>
        /// Run the setup off the UI thread, streaming its log back as it goes.
        /// </summary>
        /// <remarks>
        /// The setup is a plain method call now, not a process, so it is run on a background thread and
        /// every line it reports is marshalled back by its caller. It can throw (a missing payload, a
        /// file it cannot write), and a throw on this thread would otherwise terminate the process, so it
        /// is caught here and reported as a final line plus a failing exit code.
        /// </remarks>
        public void RunSetup(bool offline, Action<string> onLine, Action<int> onFinished)
        {
            new Thread(delegate ()
            {
                try
                {
                    var options = new SetupOptions();
                    options.Install.Offline = offline;

                    var log = new DelegateSetupLog(delegate (SetupLogLevel level, string message)
                    {
                        if (onLine != null) onLine(message);
                    });

                    var result = Setup.Run(Install, SetupPayload.ResolveDefault(), options, log);
                    if (onFinished != null) onFinished(result.Ok ? 0 : 1);
                }
                catch (Exception ex)
                {
                    if (onLine != null) onLine(ex.Message);
                    if (onFinished != null) onFinished(-1);
                }
            })
            {
                // Background: a run must not hold up the window closing, and the line callback marshals
                // back itself.
                IsBackground = true
            }.Start();
        }

        public void OpenFolder(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                {
                    Status("That folder does not exist yet: " + path, false);
                    return;
                }

                // The platform's own file manager. explorer.exe is Windows-only; xdg-open is what every
                // Linux desktop actually ships.
                if (OperatingSystem.IsWindows())
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
                }
                else
                {
                    Process.Start(new ProcessStartInfo("xdg-open", "\"" + path + "\"") { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                Status("Could not open the folder: " + ex.Message, false);
            }
        }

        public void SetBusy(bool busy)
        {
            Busy = busy;

            // The whole window, not just the views. A download or a setup run is the one moment where
            // starting a second one would produce two threads writing the same install.
            SetupPage.IsEnabled = !busy;
            ModsPage.IsEnabled = !busy;
            GetModsPage.IsEnabled = !busy;
            LogPage.IsEnabled = !busy;
            Tabs.IsEnabled = !busy;
            Cursor = busy ? new Cursor(StandardCursorType.Wait) : new Cursor(StandardCursorType.Arrow);
        }

        public void Status(string message, bool good)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(delegate { Status(message, good); });
                return;
            }

            StatusText.Text = message ?? "";
            StatusText.Foreground = Palette.Brush(good ? Palette.Good : Palette.Bad);
        }

        /// <summary>
        /// Say what state the tool is in, without claiming it is a result.
        /// </summary>
        /// <remarks>
        /// <see cref="Status"/> is for the outcome of something that just happened, so it is coloured
        /// green or red. A plain state - what is installed, what is not - is neither, and painting it
        /// green would make everything look like a success.
        /// </remarks>
        public void State(string message)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(delegate { State(message); });
                return;
            }

            StatusText.Text = message ?? "";
            StatusText.Foreground = Palette.Brush(Palette.Faint);
        }

        /// <summary>Let the UI catch up while a long operation is going.</summary>
        public void Pump()
        {
            // Background priority: run everything already queued, including layout and render, then
            // return. That is what makes this a "catch up now" rather than a nested message loop - a
            // nested loop would let the user start a second operation, which is the thing being guarded
            // against.
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
        }

        /// <summary>
        /// Ask GitHub whether a newer switchboard release exists.
        /// </summary>
        /// <remarks>
        /// Off the UI thread, because it is a network round trip and the window has just opened - hanging
        /// it there would read as a slow start. Deliberately outside the Busy flag: a version lookup must
        /// not grey out the tabs, and two of them racing would only waste one request against the rate
        /// limit. An automatic check that fails says nothing: no network on launch is ordinary, and a
        /// modal about it would scold everyone who opens the tool offline. A manual check reports whatever
        /// it found on the status line, because the button was pressed and an answer is owed.
        /// </remarks>
        public void CheckForSwitchboardUpdate(bool automatic)
        {
            if (_checkingUpdates) return;
            _checkingUpdates = true;
            CheckUpdatesButton.IsEnabled = false;

            if (!automatic) Status("Checking GitHub for a newer switchboard release...", true);

            new Thread(delegate ()
            {
                try
                {
                    var latest = UpdateChecker.FetchLatest();
                    var current = UpdateChecker.CurrentVersion();
                    var newer = UpdateChecker.IsUpdateAvailable(current, latest.TagName);

                    OnUi(delegate ()
                    {
                        _checkingUpdates = false;
                        CheckUpdatesButton.IsEnabled = true;

                        if (newer)
                        {
                            _latestRelease = latest;

                            // Two lines rather than one long one: what is happening, and what to do
                            // about it. The instruction is the part people have to act on, and it
                            // reads as an instruction when it is not buried in a sentence.
                            UpdateText.Text = "Switchboard " + latest.TagName + " is available - you have " + current + ".";

                            // The button is only offered when the release actually publishes this
                            // program, so it is never a button that cannot work. A release published
                            // before the rename carries the old name, and installing that would
                            // replace this program with a different one.
                            var asset = latest.AssetForCurrentPlatform();
                            var installable = asset != null && SelfUpdate.IsThisProgram(asset);

                            UpdateNowButton.IsVisible = installable;
                            UpdateNowButton.IsEnabled = installable;

                            UpdateHint.Text = installable
                                ? "Update now and it replaces this one. Your game install is untouched."
                                : "Download it from the release page and put it over this one. Your game install is untouched.";

                            UpdateBar.IsVisible = true;
                            Status("Switchboard " + latest.TagName + " is available - see the bar above.", true);
                        }
                        else if (!automatic)
                        {
                            UpdateBar.IsVisible = false;
                            Status("You're up to date (" + current + ").", true);
                        }
                    });
                }
                catch (Exception ex)
                {
                    var message = ex.Message;
                    OnUi(delegate ()
                    {
                        _checkingUpdates = false;
                        CheckUpdatesButton.IsEnabled = true;

                        // Automatic and failed: stay quiet, so an offline launch looks like a launch.
                        if (!automatic) Status("Could not check for updates: " + message, false);
                    });
                }
            })
            {
                IsBackground = true
            }.Start();
        }

        private void OnCheckUpdates(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            CheckForSwitchboardUpdate(false);
        }

        /// <summary>
        /// Replace this program with the published build.
        /// </summary>
        /// <remarks>
        /// The release is one executable, so there is nothing to unpack: the app fetches it, checks
        /// it against the checksum GitHub published, and hands the replacement to a script that
        /// puts it in place once this process has gone.
        ///
        /// The whole thing runs off the UI thread, because it is a network round trip for tens of
        /// megabytes, and the window stays up to show the progress.
        /// </remarks>
        private void OnUpdateNow(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (Busy) return;

            var release = _latestRelease;
            if (release == null)
            {
                Status("There is no release to update to.", false);
                return;
            }

            var asset = release.AssetForCurrentPlatform();
            if (asset == null)
            {
                Status("That release has no download for this platform - the release page has what there is.", false);
                return;
            }

            if (!SelfUpdate.IsThisProgram(asset))
            {
                Status("That release publishes \"" + asset.Name + "\", which is a different program from " +
                       "this one, so it was not installed. The release page is still there.", false);
                return;
            }

            SetBusy(true);
            UpdateNowButton.IsEnabled = false;

            // Neutral, not green: a download in progress is not a success, and painting the
            // status line green for it would make every ordinary moment look like an achievement.
            State("Downloading the update...");

            new Thread(delegate ()
            {
                string failure = null;

                try
                {
                    SelfUpdate.Apply(asset, delegate (string line)
                    {
                        OnUi(delegate { State(line); });
                    });
                }
                catch (Exception ex)
                {
                    failure = ex.Message;
                }

                if (failure != null)
                {
                    OnUi(delegate
                    {
                        SetBusy(false);
                        UpdateNowButton.IsEnabled = true;
                        Status("The update did not go through: " + failure, false);
                    });
                }
            })
            {
                IsBackground = true
            }.Start();
        }

        private void OnViewRelease(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var release = _latestRelease;
            if (release == null || string.IsNullOrWhiteSpace(release.PageUrl))
            {
                Status("There is no release page to open.", false);
                return;
            }

            if (!Http.IsAcceptable(release.PageUrl))
            {
                Status("That release page is not an https URL, so it was not opened.", false);
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(release.PageUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Status("Could not open the release page: " + ex.Message, false);
            }
        }

        private void OnDismissUpdate(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            UpdateBar.IsVisible = false;

            // The status line was pointing at the bar that is now gone, so it cannot be left saying
            // "see the bar above" - that is how a person ends up looking for a bar that is not there.
            // The update has not gone anywhere, though, so the line says that instead.
            var release = _latestRelease;
            State(release != null && !string.IsNullOrWhiteSpace(release.TagName)
                ? "Dismissed. Switchboard " + release.TagName + " is still available - press Check for updates to see it."
                : "Dismissed.");
        }

        /// <summary>Run work on the UI thread.</summary>
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
                // The window closed while the check was in flight. There is nobody left to tell.
            }
        }

        private void OnPlay(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (Busy) return;

            if (!Install.HasGame)
            {
                Status("No game install found at " + Install.Path, false);
                return;
            }

            string running;
            if (ModManager.GameRunning(out running))
            {
                Status(running + " is already running.", false);
                return;
            }

            var proton = new ProtonIntegration();

            try
            {
                if (proton.IsLinux)
                {
                    // The override first, best effort: without it Wine loads its own winhttp.dll and
                    // Doorstop never runs, which looks exactly like a game that started unmodded. If the
                    // prefix cannot be written, Launch still goes through Steam and says what to paste.
                    var log = new DelegateSetupLog(delegate (SetupLogLevel level, string message)
                    {
                        var bad = level == SetupLogLevel.Warning || level == SetupLogLevel.Error;
                        OnUi(delegate { Status(message, !bad); });
                    });

                    try
                    {
                        proton.EnsureDllOverride(Install, log);
                    }
                    catch (Exception)
                    {
                        // Best effort. The Steam launch option is the authoritative fix, and Launch
                        // reports whether it was needed.
                    }

                    proton.Launch(Install, LaunchMethod.Steam, log);
                    Status("Started through Steam. Watch the Log tab for what the loader did.", true);
                }
                else
                {
                    // The exe directly on Windows: it needs no protocol handler, and a player whose Steam
                    // client is not up still gets a game.
                    Process.Start(new ProcessStartInfo(Install.Executable)
                    {
                        UseShellExecute = true,
                        WorkingDirectory = Install.Path
                    });
                    Status("Started. Watch the Log tab for what the loader did.", true);
                }
            }
            catch (Exception ex)
            {
                Status("Could not start the game: " + ex.Message, false);
            }
        }
    }
}
