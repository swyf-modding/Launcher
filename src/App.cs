using System;
using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace ScamWYF.Launcher
{
    /// <summary>
    /// The main window. Three tabs, and not much else.
    /// </summary>
    /// <remarks>
    /// Setup, Mods, Log. A launcher earns its place by making three things easy: fix a broken
    /// install, see what is installed, and read what the game said. Anything else belongs in a mod.
    ///
    /// The game is launched from here rather than from Steam deliberately. Launching the exe directly
    /// skips Steam's own wrapper, and this game has not been observed to care - but starting from a
    /// launcher that a player did not install through Steam is also how you get two copies of the
    /// game running at once. So: if Steam can find and start it, do that; otherwise say so plainly
    /// and offer the exe.
    /// </remarks>
    internal sealed class App : Form
    {
        // Assigned in Build() rather than at the field, because the constructor calls Build and the tabs
        // need this instance to exist first. Non-readonly for exactly that reason.
        private SetupTab _setup;
        private ModsTab _mods;
        private GetModsTab _getMods;
        private LogTab _log;

        /// <summary>
        /// The tab strip, which is also the page container.
        /// </summary>
        /// <remarks>
        /// One control, not a themed strip wrapped around a hidden stock one. Two TabControls sharing
        /// TabPages is fragile - moving a page between them leaves the second one's collection empty and
        /// it silently draws no tabs at all, which is exactly what happened the first time.
        /// </remarks>
        private readonly TabStrip _strip = new TabStrip();

        private readonly ToolStripStatusLabel _status = new ToolStripStatusLabel();

        private bool _busy;

        public GameInstall Install { get; private set; }
        public ModManager Mods { get; private set; }
        public SetupRunner Setup { get; private set; }

        /// <summary>Whether something long-running is going, which the UI uses to grey things out.</summary>
        public bool Busy { get { return _busy; } }

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new App(args));
        }

        private App(string[] args)
        {
            var explicitPath = args != null && args.Length > 0 ? args[0] : null;

            Install = GameInstall.Resolve(explicitPath);
            Mods = new ModManager(Install.BepInExCore);
            Setup = new SetupRunner(FindSetupScript());

            Theme.Apply(this);
            Text = "Scam With Your Friends - modding " + BuildVersion();
            MinimumSize = new Size(940, 620);
            Size = new Size(1060, 720);
            StartPosition = FormStartPosition.CenterScreen;

            Build();

            if (!Install.Exists)
            {
                Status("No game install found. Set SWYG_GAME_DIR, or pass a path as the first argument.", false);
            }

            _setup.Reload();
            _mods.Reload();
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
        /// The informational version, not the assembly version, because build.ps1 fills the former from
        /// the git tag and it carries the commit - "which launcher are you on" is a support question
        /// and the commit is the answer. Falls back to the assembly version, which is all a build with
        /// no tag behind it has.
        /// </remarks>
        private static string BuildVersion()
        {
            var assembly = typeof(App).Assembly;

            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            if (informational != null && !string.IsNullOrEmpty(informational.InformationalVersion))
            {
                return informational.InformationalVersion;
            }

            var version = assembly.GetName().Version;
            return version != null ? version.ToString() : "unknown version";
        }

        private void Build()
        {
            _setup = new SetupTab(this);
            _mods = new ModsTab(this);
            _getMods = new GetModsTab(this);
            _log = new LogTab(this);

            _strip.Dock = DockStyle.Fill;
            _strip.TabPages.Add(Page("Setup", _setup));
            _strip.TabPages.Add(Page("Mods", _mods));
            _strip.TabPages.Add(Page("Get mods", _getMods));
            _strip.TabPages.Add(Page("Log", _log));
            _strip.SelectedIndexChanged += delegate { OnStripChanged(); };
            Controls.Add(_strip);

            var strip = new ToolStrip
            {
                Dock = DockStyle.Bottom,
                BackColor = Theme.Surface,
                ForeColor = Theme.Text,
                RenderMode = ToolStripRenderMode.System,
                Padding = new Padding(Theme.Gap, 2, Theme.Gap, 2)
            };
            _status.Spring = true;
            _status.TextAlign = ContentAlignment.MiddleLeft;
            _status.ForeColor = Theme.Muted;
            _status.Font = Theme.Small;
            strip.Items.Add(_status);
            strip.Items.Add(new ToolStripStatusLabel(" "));
            strip.Items.Add(LaunchButton());
            Controls.Add(strip);
        }

        /// <summary>
        /// Re-probe whichever tab was just arrived at.
        /// </summary>
        /// <remarks>
        /// On arrival rather than on a timer: the only thing that changes under the app's feet is the
        /// install, and that changes when the user runs setup or the game updates.
        /// </remarks>
        private void OnStripChanged()
        {
            var index = _strip.SelectedIndex;

            // Deferred until after layout. A tab that was not selected at startup is only given its
            // final size when it is first shown, so a tab that measures itself in the same turn as the
            // click measures the width it had before - and sizes its columns to that, which left the Log
            // tab's problems list with a horizontal scrollbar it did not need.
            if (!IsHandleCreated)
            {
                ShowTab(index);
                return;
            }

            BeginInvoke(new Action(delegate { ShowTab(index); }));
        }

        private void ShowTab(int index)
        {
            switch (index)
            {
                case 0: _setup.Reload(); break;
                case 1: _mods.Reload(); break;
                case 2: _getMods.OnTabShown(); break;
                case 3: _log.Reload(); break;
            }
        }

        private static TabPage Page(string title, Control content)
        {
            var page = new TabPage(title) { BackColor = Theme.Window };
            page.Controls.Add(content);
            return page;
        }

        private ToolStripButton LaunchButton()
        {
            var button = new ToolStripButton("Play") { DisplayStyle = ToolStripItemDisplayStyle.Text };
            button.Click += delegate { LaunchGame(); };
            return button;
        }

        /// <summary>First paint: settle the selection so the first tab does what it says on arrival.</summary>
        private void OnTabShown()
        {
            OnStripChanged();
        }

        /// <summary>
        /// Re-read what is on disk, in both mod-related tabs.
        /// </summary>
        /// <remarks>
        /// The two tabs answer the same question from opposite ends - "what is installed" and "what can
        /// I install" - so a change made in one has to show up in the other. Deleting a mod on the Mods
        /// tab leaves the Get mods row claiming it is installed, and installing one left that row
        /// unchanged too, which is the kind of thing that makes a person distrust the whole window.
        ///
        /// No recursion: RefreshInstalled only touches this tab's own rows.
        /// </remarks>
        public void RefreshModViews()
        {
            _mods.Reload();
            _getMods.RefreshInstalled();
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
            _busy = busy;
            _setup.Enabled = !busy;
            _strip.Enabled = !busy;
            UseWaitCursor = busy;
        }

        public void Status(string message, bool good)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string, bool>(Status), message, good);
                return;
            }

            _status.Text = message ?? "";
            _status.ForeColor = good ? Theme.Good : Theme.Bad;
        }

        /// <summary>Let the UI catch up while a long operation is going.</summary>
        public void Pump()
        {
            Application.DoEvents();
        }

        private void LaunchGame()
        {
            if (!Install.HasGame)
            {
                Status("No game install found at " + Install.Path, false);
                return;
            }

            if (ModManager.GameRunning(out var running))
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
