using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Switchboard.Views
{
    /// <summary>One line of the picked-out-problems table.</summary>
    internal sealed class ProblemRow
    {
        public ProblemRow(string line, string kind, string detail, bool fatal)
        {
            Line = line;
            Kind = kind;
            Detail = detail;
            Brush = Palette.Brush(fatal ? Palette.Bad : Palette.Warn);
        }

        public string Line { get; private set; }
        public string Kind { get; private set; }
        public string Detail { get; private set; }
        public IBrush Brush { get; private set; }
    }

    /// <summary>
    /// The log tab: what the game and the loader said last time it ran.
    /// </summary>
    public partial class LogView : UserControl
    {
        public MainWindow Host { get; set; }

        /// <summary>What this tab is for, in one line. Shown under the tab strip.</summary>
        public string Purpose
        {
            get
            {
                return "What the game and the loader said the last time it ran. The problems worth " +
                       "knowing about are listed above the raw log.";
            }
        }

        private readonly ObservableCollection<ProblemRow> _problems = new ObservableCollection<ProblemRow>();
        private DispatcherTimer _timer;
        private DateTime _lastWritten = DateTime.MinValue;

        public LogView()
        {
            InitializeComponent();
            PurposeText.Text = Purpose;
            Problems.ItemsSource = _problems;

            // Subscribed after InitializeComponent, so the IsChecked="True" in the markup does not
            // start the poll before there is a Host to poll.
            Follow.IsCheckedChanged += OnFollowChanged;
        }

        public void Reload()
        {
            var path = Host.Install.LogFile;

            if (!File.Exists(path))
            {
                Headline.Text = "No log yet - it appears after the game has been launched once.";
                Raw.Text = "";
                _problems.Clear();
                ShowEmpty("No log yet",
                    "BepInEx writes one on every launch, in the game folder. Launch the game once " +
                    "and this fills in.");
                return;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(path);
            }
            catch (Exception ex)
            {
                Headline.Text = "Could not read the log: " + ex.Message;
                ShowEmpty("Could not read the log", ex.Message);
                return;
            }

            Raw.Text = string.Join(Environment.NewLine, lines);
            Raw.CaretIndex = 0;
            ScrollToHome();

            var written = File.GetLastWriteTime(path);
            Headline.Text = path + "   (" + lines.Length + " lines, written " +
                written.ToString("yyyy-MM-dd HH:mm:ss") + ")";

            FindProblems(lines);
        }

        /// <summary>Show the empty state in place of the table, with the given words.</summary>
        private void ShowEmpty(string title, string hint)
        {
            EmptyTitle.Text = title;
            EmptyHint.Text = hint;
            Empty.IsVisible = true;
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
            _problems.Clear();

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (!IsInteresting(line)) continue;

                _problems.Add(new ProblemRow(
                    (i + 1).ToString(), Kind(line), Detail(line), IsFatal(line)));
            }

            // "Nothing found" is an answer, and it is the answer a person looking for a fault
            // most wants: an empty table would read as a tab that is broken rather than as a log
            // with no errors in it.
            if (_problems.Count == 0)
            {
                ShowEmpty("No problems found",
                    "Nothing in the log looks like a failure. The raw log is below if you want to " +
                    "read it all.");
            }
            else
            {
                Empty.IsVisible = false;
            }
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

        private void OnReload(object sender, RoutedEventArgs e) { Reload(); }

        private void OnOpenGame(object sender, RoutedEventArgs e)
        {
            Host.OpenFolder(Host.Install.Path);
        }

        /// <summary>
        /// Open the folder the log is in, which is BepInEx rather than the game root.
        /// </summary>
        /// <remarks>
        /// The .NET Framework build said "Open log in notepad" and opened the game root, which was both
        /// Windows-specific wording and not where the file actually is. This opens the folder that holds
        /// LogOutput.log, which is the honest version of the same button.
        /// </remarks>
        private void OnOpenLog(object sender, RoutedEventArgs e)
        {
            var path = Host.Install.LogFile;
            var folder = Path.GetDirectoryName(path);

            if (File.Exists(path) && !string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            {
                Host.OpenFolder(folder);
                return;
            }

            Host.Status("No log yet: " + path, false);
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
        private void OnFollowChanged(object sender, RoutedEventArgs e)
        {
            var wanted = Follow.IsChecked == true;

            if (wanted && _timer == null)
            {
                _lastWritten = DateTime.MinValue;

                _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _timer.Tick += delegate
                {
                    var path = Host.Install.LogFile;
                    if (!File.Exists(path)) return;

                    var written = File.GetLastWriteTime(path);
                    if (written <= _lastWritten) return;

                    _lastWritten = written;
                    Reload();
                };
                _timer.Start();
            }
            else if (!wanted && _timer != null)
            {
                _timer.Stop();
                _timer = null;
            }
        }

        /// <summary>
        /// Put the view back at the top of a freshly loaded log.
        /// </summary>
        /// <remarks>
        /// Avalonia's TextBox has no ScrollToHome, so the internal ScrollViewer is asked instead; it is
        /// only in the visual tree once the tab has been laid out.
        /// </remarks>
        private void ScrollToHome()
        {
            var scroll = Raw.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (scroll != null) scroll.ScrollToHome();
        }
    }
}
