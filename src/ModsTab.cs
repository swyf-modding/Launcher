using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace ScamWYF.Launcher
{
    /// <summary>
    /// The mods tab: what is installed, and the three things anyone wants to do to it.
    /// </summary>
    /// <remarks>
    /// Enable, disable, delete. BepInEx has no enable/disable of its own, so this moves files between
    /// plugins\ and plugins_disabled\ - the same thing a person would do by hand, which means it is
    /// unsurprising when it does not work.
    ///
    /// A mod with no [BepInPlugin] is listed but marked, not hidden. The shared library is a dll in
    /// plugins\ with no attribute, and someone seeing "unrecognised" for it would reasonably conclude
    /// something was broken. Saying why it is harmless is more useful than hiding it.
    ///
    /// Deleting asks for confirmation and has no undo. That asymmetry is deliberate: a delete that
    /// quietly kept a copy somewhere would grow without bound and would eventually be the thing that
    /// filled someone's disk.
    /// </remarks>
    internal sealed class ModsTab : UserControl
    {
        private readonly App _app;
        private readonly ListView _list = new ListView();
        private readonly Button _enable = new Button();
        private readonly Button _disable = new Button();
        private readonly Button _delete = new Button();
        private readonly Button _refresh = new Button();
        private readonly Label _summary = new Label();

        private List<PluginEntry> _entries = new List<PluginEntry>();

        public ModsTab(App app)
        {
            _app = app;
            Build();
        }

        private void Build()
        {
            Dock = DockStyle.Fill;
            Padding = new Padding(12);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _summary.AutoSize = true;
            _summary.Margin = new Padding(0, 0, 0, 8);
            root.Controls.Add(_summary, 0, 0);

            _list.Dock = DockStyle.Fill;
            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.MultiSelect = false;
            _list.HideSelection = false;
            _list.GridLines = true;
            _list.Columns.Add("mod", 260);
            _list.Columns.Add("version", 90);
            _list.Columns.Add("state", 90);
            _list.Columns.Add("guid", 280);
            _list.Columns.Add("note", 300);
            _list.SelectedIndexChanged += delegate { UpdateButtons(); };
            _list.DoubleClick += delegate { Toggle(); };
            root.Controls.Add(_list, 0, 1);

            var buttons = new FlowLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 8, 0, 0)
            };

            _enable.Text = "Enable";
            _enable.AutoSize = true;
            _enable.Padding = new Padding(10, 4, 10, 4);
            _enable.Click += delegate { Toggle(); };
            buttons.Controls.Add(_enable);

            _disable.Text = "Disable";
            _disable.AutoSize = true;
            _disable.Padding = new Padding(10, 4, 10, 4);
            _disable.Margin = new Padding(8, 0, 0, 0);
            _disable.Click += delegate { Toggle(); };
            buttons.Controls.Add(_disable);

            _delete.Text = "Delete...";
            _delete.AutoSize = true;
            _delete.Padding = new Padding(10, 4, 10, 4);
            _delete.Margin = new Padding(8, 0, 0, 0);
            _delete.Click += OnDelete;
            buttons.Controls.Add(_delete);

            _refresh.Text = "Rescan";
            _refresh.AutoSize = true;
            _refresh.Padding = new Padding(10, 4, 10, 4);
            _refresh.Margin = new Padding(8, 0, 0, 0);
            _refresh.Click += delegate { Reload(); };
            buttons.Controls.Add(_refresh);

            var openPlugins = new Button
            {
                Text = "Open plugins folder",
                AutoSize = true,
                Padding = new Padding(10, 4, 10, 4),
                Margin = new Padding(8, 0, 0, 0)
            };
            openPlugins.Click += delegate { _app.OpenFolder(_app.Install.PluginsFolder); };
            buttons.Controls.Add(openPlugins);

            var openDisabled = new Button
            {
                Text = "Open disabled folder",
                AutoSize = true,
                Padding = new Padding(10, 4, 10, 4),
                Margin = new Padding(8, 0, 0, 0)
            };
            openDisabled.Click += delegate
            {
                var folder = _app.Install.PluginsFolder.Replace("plugins", "plugins_disabled");
                _app.OpenFolder(folder);
            };
            buttons.Controls.Add(openDisabled);

            root.Controls.Add(buttons, 0, 2);

            var hint = new Label
            {
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(0, 8, 0, 0),
                Text = "Enable and disable move the file between plugins and plugins_disabled, which is " +
                       "what BepInEx watches. Changes take effect on the next launch."
            };
            root.Controls.Add(hint, 0, 3);

            Controls.Add(root);
        }

        public void Reload()
        {
            _entries.Clear();
            _list.BeginUpdate();
            _list.Items.Clear();

            if (!_app.Install.CanManageMods)
            {
                _summary.Text = "No BepInEx install found, so there is nothing to list.";
                _list.EndUpdate();
                UpdateButtons();
                return;
            }

            _entries = _app.Mods.Scan();

            foreach (var entry in _entries)
            {
                var item = new ListViewItem(entry.DisplayName);
                item.SubItems.Add(entry.DisplayVersion);
                item.SubItems.Add(entry.Enabled ? "enabled" : "disabled");
                item.SubItems.Add(entry.Guid ?? "");
                item.SubItems.Add(entry.LoadError ?? "");

                item.Tag = entry;
                if (!entry.Enabled) item.ForeColor = SystemColors.GrayText;
                else if (entry.LoadError != null) item.ForeColor = Color.FromArgb(176, 96, 0);

                _list.Items.Add(item);
            }

            _list.EndUpdate();

            var enabled = 0;
            foreach (var entry in _entries) if (entry.Enabled) enabled++;

            _summary.Text = _entries.Count + " mod file(s), " + enabled + " enabled";

            if (_list.Items.Count > 0) _list.Items[0].Selected = true;
            UpdateButtons();
        }

        private PluginEntry Selected
        {
            get
            {
                if (_list.SelectedItems.Count == 0) return null;
                return _list.SelectedItems[0].Tag as PluginEntry;
            }
        }

        private void UpdateButtons()
        {
            var entry = Selected;
            var running = false;
            string why = null;

            if (entry != null)
            {
                var check = _app.Mods.CanToggle(entry);
                running = check.Ok;
                if (!check.Ok) why = check.Message;
            }

            _enable.Enabled = running && !entry.Enabled;
            _disable.Enabled = running && entry.Enabled;

            // Delete has its own precondition rather than reusing the toggle's: it does not care about a
            // same-named file in the other folder, which a toggle does. Sharing one check would make it
            // impossible to remove an old copy while a newer one is enabled.
            var deletable = running || (entry != null && _app.Mods.CanDelete(entry).Ok);
            _delete.Enabled = deletable;

            // A disabled button with no stated reason is the worst outcome here: the person cannot tell
            // whether they need to close the game or whether the tool is broken. The note column is
            // where that explanation goes, since it stays visible after the selection moves.
            var enabled = _enable.Enabled || _disable.Enabled || deletable;
            if (!enabled && why != null && _list.SelectedItems.Count > 0)
            {
                var last = _list.SelectedItems[0];
                if (last.SubItems.Count > 4 && last.SubItems[4].Text.Length == 0) last.SubItems[4].Text = why;
            }
        }

        private void Toggle()
        {
            var entry = Selected;
            if (entry == null) return;

            var result = _app.Mods.Toggle(entry);
            _app.Status(result.Ok ? result.Message : result.Message, result.Ok);
            Reload();
        }

        private void OnDelete(object sender, EventArgs e)
        {
            var entry = Selected;
            if (entry == null) return;

            var what = entry.DisplayName;
            var extra = entry.Enabled ? "\n\nIt is enabled, so deleting it removes it for good." : "";

            var answer = MessageBox.Show(
                "Delete " + what + "?\n\n" + entry.Path + extra +
                "\n\nThis cannot be undone. If you only want to stop it loading, disable it instead.",
                "Delete mod", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

            if (answer != DialogResult.OK) return;

            var result = _app.Mods.Delete(entry);
            _app.Status(result.Ok ? result.Message : result.Message, result.Ok);
            Reload();
        }
    }
}
