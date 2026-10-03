using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ScamWYF.Launcher.Views
{
    /// <summary>One line of the picked-out-problems table.</summary>
    internal sealed class ProblemRow
    {
        public ProblemRow(string line, string kind, string detail, bool fatal)
        {
            Line = line;
            Kind = kind;
            Detail = detail;
            Brush = (Brush)Application.Current.FindResource(fatal ? "BadBrush" : "WarnBrush");
        }

        public string Line { get; private set; }
        public string Kind { get; private set; }
        public string Detail { get; private set; }
        public Brush Brush { get; private set; }
    }

    /// <summary>
    /// The log tab: what the game and the loader said last time it ran.
    /// </summary>
    public partial class LogView : UserControl
    {
        public MainWindow Host { get; set; }

        private readonly ObservableCollection<ProblemRow> _problems = new ObservableCollection<ProblemRow>();
        private DispatcherTimer _timer;
        private DateTime _lastWritten = DateTime.MinValue;

        public LogView()
        {
            InitializeComponent();
            Problems.ItemsSource = _problems;
            Follow.Checked += OnFollowChanged;
            Follow.Unchecked += OnFollowChanged;
        }

        public void Reload()
        {
            var path = Host.Install.LogFile;

            if (!File.Exists(path))
            {
                Headline.Text = "No log yet - it appears after the game has been launched once.";
                Raw.Clear();
                _problems.Clear();
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
                return;
            }

            Raw.Text = string.Join(Environment.NewLine, lines);
            Raw.CaretIndex = 0;
            Raw.ScrollToHome();

            var written = File.GetLastWriteTime(path);
            Headline.Text = path + "   (" + lines.Length + " lines, written " +
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
            _problems.Clear();

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (!IsInteresting(line)) continue;

                _problems.Add(new ProblemRow(
                    (i + 1).ToString(), Kind(line), Detail(line), IsFatal(line)));
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

        private void OnOpenLog(object sender, RoutedEventArgs e)
        {
            var path = Host.Install.LogFile;

            if (File.Exists(path))
            {
                Host.OpenFolder(Host.Install.Path);
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
    }
}