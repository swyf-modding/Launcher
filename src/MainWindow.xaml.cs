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
        /// Looked for next to this exe, because that is where a person who cloned the Setup repo
        /// alongside it will have it, and because the scripts carry the vendored Doorstop and corlib
        /// that this tool deliberately does not duplicate. Also tried as a sibling folder, for the
        /// common case of all four repos checked out together.
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
            // constructed - before the views have been given a Host - and again before the window has
            // been laid out, when a tab measures itself against a width it is about to lose. The first
            // load is done by OnLoaded instead, once everything is wired.
            if (!_shown) return;

            // Deferred to the dispatcher, for the sizing reason above. It is still the right thing to do in
            // WPF, which lays out lazily for the same reason WinForms did.
            var index = Tabs.SelectedIndex;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate { ShowTab(index); }));
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