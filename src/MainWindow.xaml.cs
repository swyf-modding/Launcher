using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ScamWYF.Launcher
{
    /// <summary>
    /// The main window: the four tabs, a status line, and the Play button.
    /// </summary>
    /// <remarks>
    /// Setup, Mods, Get mods, Log. Everything here exists to answer a question the player has.
    ///
    /// The game is launched from here rather than from Steam deliberately. Launching the exe directly
    /// skips Steam's own wrapper, and this game has not been observed to care - but starting from a
    /// launcher that a player did not install through Steam is also how you get two copies of the
    /// game running at once. So: if Steam can find and start it, do that; otherwise say so plainly
    /// and offer the exe.
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
        internal SetupRunner Setup { get; private set; }

        /// <summary>Whether something long-running is going, which the views use to grey things out.</summary>
        public bool Busy { get; private set; }

        private bool _shown;

        public MainWindow(string[] args)
        {
            InitializeComponent();

            var explicitPath = args != null && args.Length > 0 ? args[0] : null;

            Install = GameInstall.Resolve(explicitPath);
            Mods = new ModManager(Install.BepInExCore);
            Setup = new SetupRunner(FindSetupScript());

            Title = "Scam With Your Friends - modding " + BuildVersion();

            SetupPage.Host = this;
            ModsPage.Host = this;
            GetModsPage.Host = this;
            LogPage.Host = this;

            Loaded += OnLoaded;
        }

        /// <summary>
        /// Locate the setup scripts.
        /// </summary>
        /// <remarks>
        /// Next to this exe first, because that is where the release puts them: build.ps1 stages
        /// bin\Setup\ from the Setup repo, and the published zip contains it. The Setup tab has to work
        /// from an unpacked download, which is the whole point of shipping them.
        ///
        /// Then upwards, for a developer working in a source tree. A bin\ built with -NoSetup has no
        /// Setup\ folder in it, and the sibling checkout is right there - so falling back to it keeps
        /// the tab usable without a rebuild. The returned path is the beside-the-exe one either way, so
        /// a genuinely absent payload reports as missing rather than being resolved from somewhere the
        /// user did not put it.
        /// </remarks>
        private static string FindSetupScript()
        {
            var beside = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Setup", "setup.ps1");
            if (File.Exists(beside)) return beside;

            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            for (int depth = 0; depth < 3 && dir != null; depth++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "Setup", "setup.ps1");
                if (File.Exists(candidate)) return candidate;
            }

            return beside;
        }

        /// <summary>
        /// This build's version, for the title bar.
        /// </summary>
        /// <remarks>
        /// The informational version, not the assembly version, because the build fills the former from
        /// the git tag and it carries the commit - "which launcher are you on" is a support question
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

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Past this point the window has been measured once, so a tab switch has to be deferred rather
            // than laid out in the click handler.
            _shown = true;

            if (!Install.Exists)
            {
                Status("No game install found. Set SWYG_GAME_DIR, or pass a path as the first argument.", false);
            }

            SetupPage.Reload();
            ModsPage.Reload();
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
            // OnLoaded instead, once everything is wired.
            if (!_shown) return;

            // The important one: SelectionChanged is a *bubbling* routed event, so every SelectionChanged
            // raised by a ListView inside the selected tab arrives here too - the Mods and Log tabs both
            // have one, and both clear their rows when they reload.
            //
            // That closed a loop. Reloading the Mods tab clears its list, the list's SelectionChanged
            // bubbles up, this handler runs again with the same index, posts ShowTab, which reloads the
            // Mods tab again - about twenty-five times a second, forever. It looked like three separate
            // bugs: you could not select a row other than the first, because every pass reset the
            // selection to row zero; the Mods tab flickered, because the list was rebuilt faster than it
            // could be drawn; and the cursor flickered over the buttons, because UpdateButtons was
            // setting IsEnabled on whichever button the pointer was over that fast.
            //
            // A real tab change always moves the index, and an inner list clearing its own selection
            // never does, so comparing against the last index we reacted to separates the two exactly.
            var index = Tabs.SelectedIndex;
            if (index == _lastShownTab) return;
            _lastShownTab = index;

            // Deferred to the dispatcher, so the tab has been measured before it reloads. A tab that was
            // not selected at startup is only given its real width when it is first shown, and one that
            // lays itself out in the same turn as the click measures the width it is about to lose.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate { ShowTab(index); }));
        }

        private int _lastShownTab = -1;

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

        public void RunSetup(bool offline, Action<string> onLine, Action<int> onFinished)
        {
            new Thread(delegate ()
            {
                Setup.Run(Install, offline, onLine, onFinished);
            })
            {
                // Background: a script run must not hold up the window closing, and the output callback
                // marshals back itself.
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

                Process.Start("explorer.exe", "\"" + path + "\"");
            }
            catch (Exception ex)
            {
                Status("Could not open the folder: " + ex.Message, false);
            }
        }

        public void SetBusy(bool busy)
        {
            Busy = busy;

            // The whole window, not just the views. A download or a script run is the one moment where
            // starting a second one would produce two threads writing the same install.
            SetupPage.IsEnabled = !busy;
            ModsPage.IsEnabled = !busy;
            GetModsPage.IsEnabled = !busy;
            LogPage.IsEnabled = !busy;
            Tabs.IsEnabled = !busy;
            Mouse.OverrideCursor = busy ? Cursors.Wait : null;
        }

        public void Status(string message, bool good)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action<string, bool>(Status), message, good);
                return;
            }

            StatusText.Text = message ?? "";
            StatusText.Foreground = (Brush)Application.Current.FindResource(good ? "GoodBrush" : "BadBrush");
        }

        /// <summary>Let the UI catch up while a long operation is going.</summary>
        public void Pump()
        {
            // Background priority: run everything already queued, including layout and render, then return.
            // Background is what makes this a "catch up now" rather than a nested message loop - a nested
            // loop would let the user start a second operation, which is the thing being guarded against.
            Dispatcher.Invoke(DispatcherPriority.Background, new Action(delegate { }));
        }

        private void OnPlay(object sender, RoutedEventArgs e)
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

            try
            {
                // The exe rather than steam://: it needs no protocol handler, and a player whose
                // Steam client is not up still gets a game. If that turns out to matter, the URL
                // belongs in a setting rather than being guessed at here.
                Process.Start(new ProcessStartInfo(Install.Executable) { UseShellExecute = true, WorkingDirectory = Install.Path });
                Status("Started. Watch the Log tab for what the loader did.", true);
            }
            catch (Exception ex)
            {
                Status("Could not start the game: " + ex.Message, false);
            }
        }
    }
}