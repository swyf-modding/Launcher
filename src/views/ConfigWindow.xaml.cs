using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace ScamWYF.Launcher.Views
{
    /// <summary>
    /// Edit one mod's BepInEx config file.
    /// </summary>
    /// <remarks>
    /// Modelled on the config file rather than on a schema, because there is no schema to read. What the
    /// mod wrote above each value - the type, the default, and usually an explanation - is the only
    /// description of that setting that exists, so it is shown next to the control rather than replaced
    /// by anything this tool could infer.
    ///
    /// Saving rewrites only the lines whose values changed, so the documentation survives. See
    /// <see cref="ModConfigFile"/> for why round-tripping rather than regenerating is the whole design.
    /// </remarks>
    public partial class ConfigWindow : Window
    {
        private readonly ModConfigFile _config;
        private readonly List<ModConfigEntry> _entries;
        private readonly List<KeyValuePair<Border, ModConfigEntry>> _cards =
            new List<KeyValuePair<Border, ModConfigEntry>>();

        public ConfigWindow(ModConfigFile config, string modName)
        {
            InitializeComponent();

            _config = config;
            _entries = config.Entries;

            Title = "Config - " + modName;
            ModName.Text = modName;

            var folder = Path.GetDirectoryName(config.Path);
            ModFile.Text = (string.IsNullOrEmpty(config.CreatedBy) ? "" : config.CreatedBy + "  -  ")
                           + (folder == null ? config.Path : folder);

            Build();
            RefreshStatus();
        }

        /// <summary>Where this window came from, so a save can say what it touched.</summary>
        public string ConfigPath { get { return _config.Path; } }

        private void Build()
        {
            if (_entries.Count == 0)
            {
                Settings.Children.Add(new TextBlock
                {
                    Text = "This config file has no settings this tool recognises. It is left exactly as it is.",
                    Style = (Style)FindResource("SettingDoc")
                });
                return;
            }

            // Grouped by section, in the order the file declares them, so the editor reads in the same
            // order the mod wrote.
            foreach (var section in _config.Sections)
            {
                var inSection = new List<ModConfigEntry>();
                foreach (var entry in _entries)
                {
                    if (string.Equals(entry.Section, section, StringComparison.Ordinal)) inSection.Add(entry);
                }

                if (inSection.Count == 0) continue;

                Settings.Children.Add(new TextBlock
                {
                    Text = section,
                    Style = (Style)FindResource("SectionHeading")
                });

                foreach (var entry in inSection) Settings.Children.Add(BuildCard(entry));
            }
        }

        private Border BuildCard(ModConfigEntry entry)
        {
            var card = new Border { Style = (Style)FindResource("SettingCard") };
            var panel = new StackPanel();

            // The mod's own words first: what this setting is for is not something to be inferred.
            if (entry.Comments != null)
            {
                foreach (var comment in entry.Comments)
                {
                    if (comment.Length == 0) continue;
                    panel.Children.Add(new TextBlock { Text = comment, Style = (Style)FindResource("SettingDoc") });
                }
            }

            panel.Children.Add(new TextBlock { Text = entry.Key, Style = (Style)FindResource("SettingName") });

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var hint = new TextBlock { Style = (Style)FindResource("SettingHint"), Margin = new Thickness(0, 0, 12, 0) };
            hint.Text = DescribeType(entry);
            hint.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(hint, 0);
            row.Children.Add(hint);

            var editor = BuildEditor(entry);
            Grid.SetColumn(editor, 1);
            row.Children.Add(editor);

            panel.Children.Add(row);
            card.Child = panel;

            // Tracked so a changed setting can be spotted by eye: the card picks up an accent border.
            _cards.Add(new KeyValuePair<Border, ModConfigEntry>(card, entry));

            return card;
        }

        private FrameworkElement BuildEditor(ModConfigEntry entry)
        {
            FrameworkElement editor;

            // A yes/no setting is a checkbox, because that is what it is and it cannot be got wrong.
            if (entry.IsBoolean)
            {
                var box = new CheckBox { IsChecked = string.Equals(entry.Value, "true", StringComparison.OrdinalIgnoreCase) };
                box.Checked += delegate { entry.Value = "true"; Changed(); };
                box.Unchecked += delegate { entry.Value = "false"; Changed(); };
                editor = box;
            }
            // A short list of accepted values is a row of chips. The list is only shown when it is short
            // enough to read at a glance; the Key type declares eighty-odd, and those get a text box with
            // the values written underneath instead.
            else if (entry.HasFixedValues && entry.Acceptable.Count <= 5)
            {
                var chips = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
                foreach (var allowed in entry.Acceptable)
                {
                    var chip = new ToggleButton
                    {
                        Content = allowed,
                        Style = (Style)FindResource("ChoiceChip"),
                        IsChecked = string.Equals(allowed, entry.Value, StringComparison.OrdinalIgnoreCase)
                    };

                    var value = allowed;
                    chip.Click += delegate { entry.Value = value; Changed(); };
                    chips.Children.Add(chip);
                }
                editor = chips;
            }
            else
            {
                var text = new TextBox { Text = entry.Value, MinWidth = 220, VerticalContentAlignment = VerticalAlignment.Center };
                text.TextChanged += delegate { entry.Value = text.Text; Changed(); };
                editor = text;
            }

            // The editor names itself for UI Automation. The setting's name is a separate TextBlock above
            // it rather than a label bound to it, so without this a screen reader announces an unnamed
            // checkbox or text box and the person editing their AI backend has no idea which setting
            // they are on.
            System.Windows.Automation.AutomationProperties.SetName(editor, entry.Key);

            return editor;
        }

        private static string DescribeType(ModConfigEntry entry)
        {
            var text = entry.SettingType == null ? "text" : entry.SettingType.ToLowerInvariant();

            if (entry.DefaultValue != null) text += ", default " + entry.DefaultValue;
            if (entry.HasFixedValues && entry.Acceptable.Count > 5) text += ", one of " + entry.Acceptable.Count + " values";

            return text;
        }

        private void Changed()
        {
            RefreshStatus();
        }

        private void RefreshStatus()
        {
            var changes = _config.Changes();

            SaveButton.IsEnabled = changes.Count > 0;
            RevertButton.IsEnabled = changes.Count > 0;

            // A changed setting is outlined, so scrolling a long file shows what has been touched.
            var accent = (Brush)FindResource("AccentBrush");
            var plain = (Brush)FindResource("BorderBrush");
            foreach (var pair in _cards) pair.Key.BorderBrush = pair.Value.Modified ? accent : plain;

            Status.Text = changes.Count == 0
                ? "Nothing changed yet."
                : changes.Count + " setting(s) changed. They take effect the next time the game starts.";

            Status.Foreground = (Brush)FindResource(changes.Count == 0 ? "MutedBrush" : "AccentBrush");
        }

        /// <summary>Put every value back the way the file had it.</summary>
        private void OnRevert(object sender, RoutedEventArgs e)
        {
            foreach (var entry in _entries) entry.Value = entry.OriginalValue;

            Settings.Children.Clear();
            _cards.Clear();
            Build();
            RefreshStatus();
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            // BepInEx writes a plugin's config as the game shuts down. Saving over it while the game is
            // running works, and then gets silently overwritten a moment later, so it is refused here
            // rather than left to look like the change failed.
            string running;
            if (ModManager.GameRunning(out running))
            {
                Dialogs.Error("Close the game first",
                    "The game is running (" + running + ").\n\n" +
                    "BepInEx saves a mod's config as it shuts down, so anything written now would be " +
                    "overwritten a moment later.");
                return;
            }

            // Checked before writing rather than trusted: the alternative is a config file the game
            // rejects on the next launch and silently falls back to its default.
            foreach (var entry in _entries)
            {
                var problem = _config.ProblemWith(entry, entry.Value);
                if (problem == null) continue;

                Dialogs.Error("That value will not be accepted",
                    entry.Key + "\n\n" + problem + "\n\nNothing was written.");
                return;
            }

            try
            {
                _config.Save();
            }
            catch (Exception ex)
            {
                Dialogs.Error("Could not write the config", ex.Message);
                return;
            }

            DialogResult = true;
        }
    }
}