using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ScamWYF.Launcher.Views
{
    /// <summary>One installed mod, shaped for the list. <see cref="Entry"/> is what the actions use.</summary>
    internal sealed class ModRow
    {
        private string _note;

        public ModRow(PluginEntry entry)
        {
            Entry = entry;
            _note = entry.LoadError ?? "";
        }

        public PluginEntry Entry { get; private set; }
        public string Name { get { return Entry.DisplayName; } }
        public string Version { get { return Entry.DisplayVersion; } }
        public string State { get { return Entry.Enabled ? "enabled" : "disabled"; } }
        public string Guid { get { return Entry.Guid ?? ""; } }

        /// <summary>
        /// The trailing column. Writable, because a button being disabled with no stated reason is the
        /// worst outcome on this tab, and the note column is where that explanation goes - it stays
        /// visible after the selection moves.
        /// </summary>
        public string Note
        {
            get { return _note; }
            set { if (_note != value) { _note = value; OnChanged(); } }
        }

        public Brush Brush
        {
            get
            {
                if (!Entry.Enabled) return (Brush)Application.Current.FindResource("MutedBrush");
                if (Entry.LoadError != null) return (Brush)Application.Current.FindResource("WarnBrush");
                return (Brush)Application.Current.FindResource("TextBrush");
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnChanged()
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs("Note"));
        }
    }

    /// <summary>
    /// The mods tab: what is installed, and the three things anyone wants to do to it.
    /// </summary>
    /// <remarks>
    /// A mod with no [BepInPlugin] is listed but marked, not hidden. The shared library is a dll in
    /// plugins\ with no attribute, and someone seeing "unrecognised" for it would reasonably conclude
    /// something was broken. Saying why it is harmless is more useful than hiding it.
    ///
    /// Deleting asks for confirmation and has no undo. That asymmetry is deliberate: a delete that
    /// quietly kept a copy somewhere would grow without bound and would eventually be the thing that
    /// filled someone's disk.
    /// </remarks>
    public partial class ModsView : UserControl
    {
        public MainWindow Host { get; set; }

        private readonly ObservableCollection<ModRow> _rows = new ObservableCollection<ModRow>();

        public ModsView()
        {
            InitializeComponent();
            List.ItemsSource = _rows;
        }

        public void Reload()
        {
            _rows.Clear();

            if (!Host.Install.CanManageMods)
            {
                Summary.Text = "No BepInEx install found, so there is nothing to list.";
                UpdateButtons();
                return;
            }

            var entries = Host.Mods.Scan();
            var enabled = 0;

            foreach (var entry in entries)
            {
                if (entry.Enabled) enabled++;
                _rows.Add(new ModRow(entry));
            }

            Summary.Text = entries.Count + " mod file(s), " + enabled + " enabled";

            if (_rows.Count > 0) List.SelectedIndex = 0;
            UpdateButtons();
        }

        private ModRow Selected
        {
            get { return List.SelectedItem as ModRow; }
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateButtons();
        }

        private void OnDoubleClick(object sender, MouseButtonEventArgs e)
        {
            Toggle();
        }

        private void OnRescan(object sender, RoutedEventArgs e) { Reload(); }

        private void OnOpenPlugins(object sender, RoutedEventArgs e)
        {
            Host.OpenFolder(Host.Install.PluginsFolder);
        }

        private void OnOpenDisabled(object sender, RoutedEventArgs e)
        {
            Host.OpenFolder(Host.Install.PluginsFolder.Replace("plugins", "plugins_disabled"));
        }

        private void UpdateButtons()
        {
            var row = Selected;
            var available = false;
            string why = null;

            EnableButton.IsEnabled = false;
            DisableButton.IsEnabled = false;
            DeleteButton.IsEnabled = false;

            if (row == null)
            {
                return;
            }

            var check = Host.Mods.CanToggle(row.Entry);
            available = check.Ok;
            if (!check.Ok) why = check.Message;

            EnableButton.IsEnabled = available && !row.Entry.Enabled;
            DisableButton.IsEnabled = available && row.Entry.Enabled;

            // Delete has its own precondition rather than reusing the toggle's: it does not care about a
            // same-named file in the other folder, which a toggle does. Sharing one check would make it
            // impossible to remove an old copy while a newer one is enabled.
            var deletable = available || Host.Mods.CanDelete(row.Entry).Ok;
            DeleteButton.IsEnabled = deletable;

            // A disabled button with no stated reason is the worst outcome here: the person cannot tell
            // whether they need to close the game or whether the tool is broken. The note column is
            // where that explanation goes, since it stays visible after the selection moves.
            var anyEnabled = EnableButton.IsEnabled || DisableButton.IsEnabled || deletable;
            if (!anyEnabled && why != null && row.Note.Length == 0) row.Note = why;
        }

        private void Toggle()
        {
            var row = Selected;
            if (row == null || Host.Busy) return;

            var result = Host.Mods.Toggle(row.Entry);
            Host.Status(result.Message, result.Ok);
            Host.RefreshModViews();
        }

        private void OnToggle(object sender, RoutedEventArgs e) { Toggle(); }

        private void OnDelete(object sender, RoutedEventArgs e)
        {
            var row = Selected;
            if (row == null || Host.Busy) return;

            var entry = row.Entry;
            var extra = entry.Enabled ? "\n\nIt is enabled, so deleting it removes it for good." : "";

            if (!Dialogs.Confirm("Delete mod",
                    "Delete " + entry.DisplayName + "?\n\n" + entry.Path + extra +
                    "\n\nThis cannot be undone. If you only want to stop it loading, disable it instead.",
                    "Delete", DialogSeverity.Warning))
            {
                return;
            }

            var result = Host.Mods.Delete(entry);
            Host.Status(result.Message, result.Ok);
            Host.RefreshModViews();
        }
    }
}