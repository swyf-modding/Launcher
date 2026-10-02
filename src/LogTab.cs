using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace ScamWYF.Launcher
{
    /// <summary>
    /// The log tab: what the game and the loader said last time it ran.
    /// </summary>
    /// <remarks>
    /// The first thing anyone does with a broken mod is open LogOutput.log, and it lives in the game
    /// folder where a player will not think to look. Showing it here, and following it while the game
    /// runs, turns the usual copy-paste-into-a-chat-box into one click.
    ///
    /// Errors are picked out and listed above the raw text. A BepInEx log is thousands of lines of
    /// which the useful part is three, and scrolling for them is the whole problem.
    /// </remarks>
    internal sealed class LogTab : UserControl
    {
        private readonly App _app;
        private readonly ListView _problems = new ListView();
        private readonly TextBox _raw = new TextBox();
        private readonly Label _headline = new Label();
        private readonly CheckBox _follow = new CheckBox();

        public LogTab(App app)
        {
            _app = app;
            Build();
        }

        private void Build()
        {
            Dock = DockStyle.Fill;
            Padding = new Padding(12);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 160));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _headline.AutoSize = true;
            _headline.Margin = new Padding(0, 0, 0, 8);
            root.Controls.Add(_headline, 0, 0);

            _problems.Dock = DockStyle.Fill;
            _problems.View = View.Details;
            _problems.FullRowSelect = true;
            _problems.HideSelection = false;
            _problems.GridLines = true;
            _problems.Columns.Add("line", 60);
            _problems.Columns.Add("what", 200);
            _problems.Columns.Add("detail", 700);
            root.Controls.Add(_problems, 0, 1);

            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 4) };

            var reload = new Button { Text = "Reload", AutoSize = true, Padding = new Padding(10, 4, 10, 4) };
            reload.Click += delegate { Reload(); };
            buttons.Controls.Add(reload);

            var openLog = new Button { Text = "Open log in notepad", AutoSize = true, Padding = new Padding(10, 4, 10, 4), Margin = new Padding(8, 0, 0, 0) };
            openLog.Click += delegate
            {
                var path = _app.Install.LogFile;
                if (File.Exists(path)) _app.OpenFolder(_app.Install.Path);
                else _app.Status("No log yet: " + path, false);
            };
            buttons.Controls.Add(openLog);

            var openGame = new Button { Text = "Open game folder", AutoSize = true, Padding = new Padding(10, 4, 10, 4), Margin = new Padding(8, 0, 0, 0) };
            openGame.Click += delegate { _app.OpenFolder(_app.Install.Path); };
            buttons.Controls.Add(openGame);

            _follow.Text = "Follow while the game runs";
            _follow.AutoSize = true;
            _follow.Margin = new Padding(16, 10, 0, 0);
            _follow.CheckedChanged += OnFollowChecked;
            buttons.Controls.Add(_follow);

            root.Controls.Add(buttons, 0, 2);

            _raw.Multiline = true;
            _raw.ReadOnly = true;
            _raw.ScrollBars = ScrollBars.Both;
            _raw.WordWrap = false;
            _raw.Dock = DockStyle.Fill;
            _raw.BackColor = SystemColors.Window;
            _raw.Font = new Font(FontFamily.GenericMonospace, 9f);
            root.Controls.Add(_raw, 0, 3);

            var hint = new Label
            {
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(0, 6, 0, 0),
                Text = "BepInEx writes this on every launch. A preloader crash, before BepInEx starts, " +
                       "goes to preloader_*.log in the game folder instead."
            };
            root.Controls.Add(hint, 0, 4);

            Controls.Add(root);
        }

        public void Reload()
        {
            var path = _app.Install.LogFile;

            if (!File.Exists(path))
            {
                _headline.Text = "No log yet - it appears after the game has been launched once.";
                _raw.Clear();
                _problems.Items.Clear();
                return;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(path);
            }
            catch (Exception ex)
            {
                _headline.Text = "Could not read the log: " + ex.Message;
                return;
            }

            _raw.Lines = lines;
            _raw.SelectionStart = 0;
            _raw.SelectionLength = 0;

            var written = File.GetLastWriteTime(path);
            _headline.Text = path + "   (" + lines.Length + " lines, written " +
                written.ToString("yyyy-MM-dd HH:mm:ss") + ")";

            FindProblems(lines);
        }

        /// <summary>
        /// Pull the lines that matter to the top.
        /// </summary>
        /// <remarks>
        /// Matched on the words BepInEx and Harmony actually use when something has gone wrong, rather
        /// than on a severity level, because the levels are set by each plugin and several mods in this
        /// ecosystem log their load failures at Warning.
        /// </remarks>
        private void FindProblems(string[] lines)
        {
            _problems.BeginUpdate();
            _problems.Items.Clear();

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (!IsInteresting(line)) continue;

                var item = new ListViewItem((i + 1).ToString());
                item.SubItems.Add(Kind(line));
                item.SubItems.Add(Detail(line));
                item.ForeColor = IsFatal(line) ? Color.FromArgb(176, 0, 32) : Color.FromArgb(176, 96, 0);
                _problems.Items.Add(item);
            }

            _problems.EndUpdate();
        }

        private static bool IsInteresting(string line)
        {
            return Contains(line, "Error") || Contains(line, "Exception") ||
                   Contains(line, "failed") || Contains(line, "Failed") ||
                   Contains(line, "Coul") || Contains(line, "could not");
        }

        private static bool IsFatal(string line)
        {
            return Contains(line, "Exception") || Contains(line, "Coul") ||
                   Contains(line, "failed to") || Contains(line, "Error");
        }

        private static string Kind(string line)
        {
            if (Contains(line, "Coul")) return "chainloader";
            if (Contains(line, "Exception")) return "exception";
            if (Contains(line, "Error")) return "error";
            return "warning";
        }

        private static string Detail(string line)
        {
            var text = line;

            // BepInEx prefixes its own lines with a bracketed channel and timestamp. Dropping them
            // makes the list readable; the raw box above still has them.
            var open = text.IndexOf(']');
            if (open >= 0 && text.IndexOf('[') < open) text = text.Substring(open + 1).Trim();

            return text.Length > 220 ? text.Substring(0, 220) + "..." : text;
        }

        private static bool Contains(string haystack, string needle)
        {
            return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Poll the log while the game runs, if asked.
        /// </summary>
        /// <remarks>
        /// A timer rather than a FileSystemWatcher, for the same reason the mod library polls: a
        /// managed-stripped BCL leaves FileSystemWatcher with almost nothing, and this is a desktop
        /// tool with no game assemblies to compile against, so it cannot rely on the same surface.
        /// Polling once a second is cheap and always works.
        /// </remarks>
        private System.Windows.Forms.Timer _timer;

        private void OnFollowChecked(object sender, EventArgs e)
        {
            var wanted = ((CheckBox)sender).Checked;

            if (wanted && _timer == null)
            {
                var last = DateTime.MinValue;
                _timer = new System.Windows.Forms.Timer { Interval = 1000 };
                _timer.Tick += delegate
                {
                    var path = _app.Install.LogFile;
                    if (!File.Exists(path)) return;

                    var written = File.GetLastWriteTime(path);
                    if (written <= last) return;

                    last = written;
                    Reload();
                };
                _timer.Start();
            }
            else if (!wanted && _timer != null)
            {
                _timer.Stop();
                _timer.Dispose();
                _timer = null;
            }
        }
    }
}
