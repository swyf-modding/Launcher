using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace ScamWYF.Launcher
{
    /// <summary>
    /// The setup tab: what is missing from the install, and a button to run the setup script.
    /// </summary>
    /// <remarks>
    /// The status list is generated from probing the install rather than hardcoded, so a new piece of
    /// setup can be added to the scripts without this file learning about it. Anything the install
    /// needs shows a way to get it, because "not ready" without a next step is the least useful thing
    /// a setup tool can say.
    /// </remarks>
    internal sealed class SetupTab : UserControl
    {
        private readonly App _app;
        private readonly ThemedListView _checks = new ThemedListView();
        private readonly TextBox _output = new TextBox();
        private readonly FlatButton _run = new FlatButton();
        private readonly FlatButton _offline = new FlatButton();
        private readonly CheckBox _openLog = new CheckBox();
        private readonly Label _headline = new Label();

        public SetupTab(App app)
        {
            _app = app;
            Build();
        }

        private void Build()
        {
            Dock = DockStyle.Fill;
            Padding = Theme.PagePadding;
            BackColor = Theme.Window;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 240));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _headline.AutoSize = true;
            _headline.Font = Theme.Heading;
            _headline.ForeColor = Theme.Text;
            _headline.Margin = new Padding(0, 0, 0, Theme.Gap);
            root.Controls.Add(_headline, 0, 0);

            _checks.Dock = DockStyle.Fill;
            _checks.Columns.Add("Check", 230);
            _checks.Columns.Add("State", 90);
            _checks.Columns.Add("Detail", 620);
            root.Controls.Add(_checks, 0, 1);

            var buttons = Theme.ButtonRow();

            _run.Text = "Set up / repair";
            _run.Accent = true;
            _run.Click += OnRun;
            buttons.Controls.Add(_run);

            _offline.Text = "Offline";
            _offline.Spaced();
            _offline.Click += OnRunOffline;
            buttons.Controls.Add(_offline);

            var openFolder = new FlatButton { Text = "Open game folder" }.Spaced();
            openFolder.Click += delegate { _app.OpenFolder(_app.Install.Path); };
            buttons.Controls.Add(openFolder);

            root.Controls.Add(buttons, 0, 2);

            _output.Multiline = true;
            _output.ReadOnly = true;
            _output.ScrollBars = ScrollBars.Vertical;
            _output.Dock = DockStyle.Fill;
            _output.BackColor = Theme.Field;
            _output.ForeColor = Theme.Text;
            _output.BorderStyle = BorderStyle.None;
            _output.Font = Theme.Mono;
            _output.WordWrap = false;
            root.Controls.Add(_output, 0, 3);

            _openLog.Text = "Follow the log while it runs";
            _openLog.AutoSize = true;
            _openLog.ForeColor = Theme.Muted;
            _openLog.Font = Theme.Small;
            _openLog.Checked = true;
            root.Controls.Add(_openLog, 0, 4);

            Controls.Add(root);
        }

        /// <summary>Re-probe and redraw. Cheap, so called whenever the tab is shown.</summary>
        public void Reload()
        {
            var install = _app.Install;

            _headline.Text = install.Exists
                ? install.Path + "  -  " + install.Summary()
                : "No game install found";

            _checks.BeginUpdate();
            _checks.Items.Clear();

            AddCheck("Game install", install.HasGame,
                install.HasGame ? install.ManagedFolder : "not found - check the path above");
            AddCheck("Loader (Doorstop)", install.HasDoorstop,
                install.HasDoorstop ? "winhttp.dll present" : "the loader half, which starts everything");
            AddCheck("Corlib override", install.HasCorlibOverride,
                install.HasCorlibOverride
                    ? "unstripped_corlib\\ seeded"
                    : "required: the shipped mscorlib is stripped, and the loader cannot start without this");
            AddCheck("Override wired up", install.DoorstopPointsAtOverride,
                install.DoorstopPointsAtOverride
                    ? "doorstop_config.ini points at it"
                    : "doorstop_config.ini does not set dll_search_path_override");
            AddCheck("BepInEx", install.HasBepInEx,
                install.HasBepInEx ? install.BepInExCore : "not installed");

            _checks.EndUpdate();

            Theme.FitHeadline(_headline, _checks.ClientSize.Width);
            _checks.Refit();

            var canRun = install.HasGame;
            _run.Enabled = canRun && _app.Setup.IsAvailable;
            _offline.Enabled = _run.Enabled;

            if (!_app.Setup.IsAvailable)
            {
                Append("");
                Append(_app.Setup.MissingReason);
            }
        }

        private void AddCheck(string name, bool ok, string detail)
        {
            var item = new ListViewItem(name);
            item.SubItems.Add(ok ? "ok" : "missing");
            item.SubItems.Add(detail ?? "");
            item.ForeColor = ok ? Theme.Text : Theme.Bad;
            _checks.Items.Add(item);
        }

        private void OnRun(object sender, EventArgs e) { Start(false); }
        private void OnRunOffline(object sender, EventArgs e) { Start(true); }

        private void Start(bool offline)
        {
            if (_app.Busy) return;

            Append("");
            Append("=== running setup.ps1" + (offline ? " -Offline" : "") + " ===");

            _app.SetBusy(true);
            _run.Enabled = false;
            _offline.Enabled = false;

            _app.RunSetup(offline, line =>
            {
                Append(line);
                if (_openLog.Checked) _app.Pump();
            },
            exitCode =>
            {
                Append("");
                Append(exitCode == 0
                    ? "=== setup finished ==="
                    : "=== setup reported problems (exit " + exitCode + ") ===");

                _app.SetBusy(false);
                Reload();
                _app.Pump();
            });
        }

        private void Append(string line)
        {
            // BeginInvoke because the runner reports from its own thread, and a control can only be
            // touched from the thread that created it.
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(Append), line);
                return;
            }

            _output.AppendText(line + Environment.NewLine);
        }
    }
}
