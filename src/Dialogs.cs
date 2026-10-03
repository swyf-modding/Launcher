using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ScamWYF.Launcher
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
    /// The launcher's modal dialogs, built from the theme.
    /// </summary>
    /// <remarks>
    /// WPF's MessageBox is a Win32 dialog and cannot be themed, so a dark window would put a light grey
    /// box in the middle of itself. These are built from the same styles as the rest of the UI instead.
    ///
    /// Written out in code rather than XAML because the body text varies and a dialog that has to be
    /// assembled from parts at runtime does not benefit from markup - and because MessageBox.Show
    /// returned in one line, which made the call sites read well and is worth keeping.
    ///
    /// <see cref="Confirm"/> defaults to Cancel being the default button. Three of its callers ask
    /// "replace these files, there is no undo", and Enter meaning yes on those is how files get lost.
    /// </remarks>
    internal static class Dialogs
    {
        /// <summary>Ask a yes/no question. Returns true only if the accept button was chosen.</summary>
        public static bool Confirm(string title, string message, string acceptText,
                                   DialogSeverity severity = DialogSeverity.Question)
        {
            return Show(title, message, severity, acceptText, "Cancel", defaultAccept: false);
        }

        /// <summary>Say something that needs acknowledging.</summary>
        public static void Inform(string title, string message)
        {
            Show(title, message, DialogSeverity.Information, "OK", null, defaultAccept: true);
        }

        /// <summary>Report a failure.</summary>
        public static void Error(string title, string message)
        {
            Show(title, message, DialogSeverity.Error, "OK", null, defaultAccept: true);
        }

private static bool Show(string title, string message, DialogSeverity severity, string acceptText,
                                 string cancelText, bool defaultAccept)
        {
            // Marshalled, because a Window has to be created on the dispatcher thread and this is the
            // one place every confirmation funnels through. Every current caller is already on it, but
            // the alternative is a modal dialog that throws the moment a future caller is not - and a
            // dialog is exactly what gets called from a failure path, which is the likeliest place for
            // that to go wrong.
            var dispatcher = Application.Current.Dispatcher;
            if (!dispatcher.CheckAccess())
            {
                var answer = false;
                dispatcher.Invoke(new Action(delegate
                {
                    answer = Show(title, message, severity, acceptText, cancelText, defaultAccept);
                }));
                return answer;
            }

            var window = new Window
            {
                Title = title,
                Width = 520,
                SizeToContent = SizeToContent.Height,
                MinHeight = 170,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                Background = (Brush)Application.Current.FindResource("SurfaceBrush")
            };

            // Attached properties, so set after construction rather than in the initialiser - the
            // initialiser form only accepts instance properties, and these are static setters.
            TextElement.SetForeground(window, (Brush)Application.Current.FindResource("TextBrush"));
            TextElement.SetFontFamily(window, new FontFamily("Segoe UI"));
            TextElement.SetFontSize(window, 12d);

            if (Application.Current.Windows.Count > 0 &&
                Application.Current.Windows[Application.Current.Windows.Count - 1].IsActive)
            {
                window.Owner = Application.Current.Windows[Application.Current.Windows.Count - 1];
            }

            var accept = MakeButton(acceptText, isDefault: defaultAccept, isCancel: false,
                                    accent: severity == DialogSeverity.Error);
            var cancel = cancelText == null
                ? null
                : MakeButton(cancelText, isDefault: !defaultAccept, isCancel: true, accent: false);

            accept.Click += delegate { window.DialogResult = true; };
            if (cancel != null) cancel.Click += delegate { window.DialogResult = false; };

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
                Foreground = (Brush)Application.Current.FindResource("TextBrush")
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

            var right = new StackPanel { Margin = new Thickness(0, 0, 0, 0) };
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
            window.Loaded += delegate
            {
                // Focus the default rather than nothing, so Enter works before the user clicks anything.
                (defaultAccept ? accept : cancel ?? accept).Focus();
            };

            return window.ShowDialog() == true;
        }

        private static Button MakeButton(string text, bool isDefault, bool isCancel, bool accent)
        {
            return new Button
            {
                Content = text,
                MinWidth = 96,
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = isDefault,
                // Escape closes the dialog as "no". On the OK-only dialogs there is no cancel button, and
                // setting this on the OK button would let Escape close without setting DialogResult - which
                // reads as "declined" and is wrong when there was nothing to decline.
                IsCancel = isCancel,
                Style = (Style)Application.Current.FindResource(accent ? "AccentButton" : "FlatButton")
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
                    key = "BadBrush";
                    break;
                case DialogSeverity.Warning:
                    character = "!";
                    key = "WarnBrush";
                    break;
                case DialogSeverity.Question:
                    character = "?";
                    key = "AccentBrush";
                    break;
                default:
                    character = "i";
                    key = "MutedBrush";
                    break;
            }

            return new TextBlock
            {
                Text = character,
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)Application.Current.FindResource(key),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 0)
            };
        }
    }
}