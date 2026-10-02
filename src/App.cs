using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
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
        private LogTab _log;

        private readonly TabControl _tabs = new TabControl();
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

            Text = "Scam With Your Friends - modding";
            MinimumSize = new Size(880, 560);
            Size = new Size(940, 640);
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

        private void Build()
        {
            _setup = new SetupTab(this);
            _mods = new ModsTab(this);
            _log = new LogTab(this);

            _tabs.Dock = DockStyle.Fill;
            _tabs.TabPages.Add(Page("Setup", _setup));
            _tabs.TabPages.Add(Page("Mods", _mods));
            _tabs.TabPages.Add(Page("Log", _log));
            _tabs.SelectedIndexChanged += delegate { OnTabShown(); };
            Controls.Add(_tabs);

            var strip = new ToolStrip();
            strip.Dock = DockStyle.Bottom;
            _status.Spring = true;
            _status.TextAlign = ContentAlignment.MiddleLeft;
            strip.Items.Add(_status);
            strip.Items.Add(new ToolStripStatusLabel(" "));
            strip.Items.Add(LaunchButton());
            Controls.Add(strip);
        }

        private static TabPage Page(string title, Control content)
        {
            var page = new TabPage(title);
            page.Controls.Add(content);
            return page;
        }

        private ToolStripButton LaunchButton()
        {
            var button = new ToolStripButton("Play") { DisplayStyle = ToolStripItemDisplayStyle.Text };
            button.Click += delegate { LaunchGame(); };
            return button;
        }

        private void OnTabShown()
        {
            // Re-probe on arrival rather than on a timer: the only thing that changes under the app's
            // feet is the install, and that changes when the user runs setup or the game updates.
            switch (_tabs.SelectedIndex)
            {
                case 0: _setup.Reload(); break;
                case 1: _mods.Reload(); break;
                case 2: _log.Reload(); break;
            }
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
            _tabs.Enabled = !busy;
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
            _status.ForeColor = good ? Color.FromArgb(0, 110, 40) : Color.FromArgb(176, 0, 32);
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
