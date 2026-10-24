using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Switchboard
{
    /// <summary>How insistent a dialog is, which picks the glyph and its colour.</summary>
    internal enum DialogSeverity
    {
        Question,
        Information,
        Warning,
        Error
    }

    /// <summary>
    /// The switchboard's modal dialogs, built from the theme.
    /// </summary>
    /// <remarks>
    /// WPF's MessageBox is a Win32 dialog and cannot be themed, so a dark window would put a light grey
    /// box in the middle of itself. Avalonia has no MessageBox at all, so the same reasoning applies for
    /// the same reason: these are built from the same styles as the rest of the UI.
    ///
    /// Written out in code rather than XAML because the body text varies and a dialog that has to be
    /// assembled from parts at runtime does not benefit from markup.
    ///
    /// They are asynchronous now, and that is the one real change from the .NET Framework version. A
    /// modal dialog blocks the message loop in WPF, so <c>ShowDialog</c> could return a bool; Avalonia's
    /// <c>ShowDialog</c> is a task, and blocking on it from the UI thread would deadlock. So every caller
    /// awaits instead, and the call sites read as <c>await Dialogs.ConfirmAsync(...)</c>.
    ///
    /// <see cref="ConfirmAsync"/> defaults to Cancel being the default button. Three of its callers ask
    /// "replace these files, there is no undo", and Enter meaning yes on those is how files get lost.
    /// </remarks>
    internal static class Dialogs
    {
        /// <summary>Ask a yes/no question. Returns true only if the accept button was chosen.</summary>
        public static Task<bool> ConfirmAsync(string title, string message, string acceptText,
                                              DialogSeverity severity = DialogSeverity.Question)
        {
            return ShowAsync(title, message, severity, acceptText, "Cancel", defaultAccept: false);
        }

        /// <summary>
        /// Ask whether to do an optional thing, where declining moves on rather than aborting.
        /// </summary>
        /// <remarks>
        /// "Cancel" would be the wrong word here: declining one setup step offers the next one, so the
        /// button says Skip. Still defaults away from running, like every other confirmation.
        /// </remarks>
        public static Task<bool> ConfirmSkippableAsync(string title, string message, string acceptText,
                                                       DialogSeverity severity = DialogSeverity.Question)
        {
            return ShowAsync(title, message, severity, acceptText, "Skip", defaultAccept: false);
        }

        /// <summary>Say something that needs acknowledging.</summary>
        public static Task InformAsync(string title, string message)
        {
            return ShowAsync(title, message, DialogSeverity.Information, "OK", null, defaultAccept: true);
        }

        /// <summary>Report a failure.</summary>
        public static Task ErrorAsync(string title, string message)
        {
            return ShowAsync(title, message, DialogSeverity.Error, "OK", null, defaultAccept: true);
        }

        private static async Task<bool> ShowAsync(string title, string message, DialogSeverity severity,
                                                  string acceptText, string cancelText, bool defaultAccept)
        {
            var answer = false;

            var window = new Window
            {
                Title = title,
                Icon = App.NewWindowIcon(),
                Width = 520,
                SizeToContent = SizeToContent.Height,
                MinHeight = 170,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                Background = Palette.Brush(Palette.Surface),
                FontSize = 12,
                Foreground = Palette.Brush(Palette.Text)
            };

            var accept = MakeButton(acceptText, isDefault: defaultAccept, isCancel: false,
                                    accent: severity == DialogSeverity.Error);
            var cancel = cancelText == null
                ? null
                : MakeButton(cancelText, isDefault: !defaultAccept, isCancel: true, accent: false);

            accept.Click += delegate { answer = true; window.Close(); };
            if (cancel != null) cancel.Click += delegate { answer = false; window.Close(); };

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0)
            };

            // Cancel first, so it sits closest to nothing in particular, and Enter still goes to whichever
            // button is marked default rather than to the first one on the row.
            if (cancel != null) buttons.Children.Add(cancel);
            buttons.Children.Add(accept);

            var text = new TextBlock
            {
                Text = message ?? "",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Palette.Brush(Palette.Text)
            };

            // Scrolled rather than capped: the install plan is the thing the user is supposed to read
            // before agreeing to run new code, so clipping its tail would defeat the confirmation.
            var body = new ScrollViewer
            {
                Content = text,
                MaxHeight = 420,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            var glyph = Glyph(severity);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            if (glyph != null)
            {
                Grid.SetColumn(glyph, 0);
                grid.Children.Add(glyph);
            }

            var right = new StackPanel();
            right.Children.Add(body);
            right.Children.Add(buttons);
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);

            var padding = new Border
            {
                Padding = new Thickness(18, 16, 18, 16),
                Child = grid
            };

            window.Content = padding;
            window.Opened += delegate
            {
                // Focus the default rather than nothing, so Enter works before the user clicks anything.
                (defaultAccept ? accept : cancel ?? accept).Focus();
            };

            var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (owner != null && owner != window && owner.IsVisible)
            {
                await window.ShowDialog(owner);
                return answer;
            }

            // No owner to be modal to - a dialog raised before the main window exists, or after it has
            // closed. Shown non-modally but still awaited, so a caller that needs an answer still gets one.
            var closed = new TaskCompletionSource<bool>();
            window.Closed += delegate { closed.TrySetResult(true); };
            window.Show();
            await closed.Task;
            return answer;
        }

        private static Button MakeButton(string text, bool isDefault, bool isCancel, bool accent)
        {
            return new Button
            {
                Content = text,
                MinWidth = 96,
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = isDefault,
                // Escape closes the dialog as "no". On the OK-only dialogs there is no cancel button, so
                // the OK button is not marked cancel either - that would let Escape close without an
                // answer, which reads as "declined" and is wrong when there was nothing to decline.
                IsCancel = isCancel,
                Theme = Palette.ControlTheme(accent ? "AccentButton" : "FlatButton")
            };
        }

        private static TextBlock Glyph(DialogSeverity severity)
        {
            string character;
            string key;

            switch (severity)
            {
                case DialogSeverity.Error:
                    character = "✕";
                    key = Palette.Bad;
                    break;
                case DialogSeverity.Warning:
                    character = "!";
                    key = Palette.Warn;
                    break;
                case DialogSeverity.Question:
                    character = "?";
                    key = Palette.Accent;
                    break;
                default:
                    character = "i";
                    key = Palette.Muted;
                    break;
            }

            return new TextBlock
            {
                Text = character,
                FontSize = 18,
                FontWeight = FontWeight.Bold,
                Foreground = Palette.Brush(key),
                HorizontalAlignment = HorizontalAlignment.Left
            };
        }
    }
}
