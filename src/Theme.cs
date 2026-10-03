using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace ScamWYF.Launcher
{
    /// <summary>
    /// How the launcher looks: one palette, one set of type sizes, and the few controls that WinForms
    /// will not draw correctly on its own.
    /// </summary>
    /// <remarks>
    /// The colours are the shared library's own, from mod-lib's UiTheme - which reads them from the
    /// game's live theme and falls back to these. So the launcher and the in-game menu it opens are the
    /// same palette rather than two unrelated ones, and if the game ever changes its colours there is
    /// one file to change rather than a set of hard-coded greys scattered through four tabs.
    ///
    /// Dark and low-chroma on purpose. A tool that sits in front of a game should recede; it is read
    /// while something else is being done, and it is mostly read at a glance to answer "is my install
    /// fine". Bright chrome competes with the thing it is reporting on.
    ///
    /// Nothing here needs a designer, and nothing is a visual designer. The three controls that are
    /// subclassed - the button, the list, the tab strip - are the three WinForms defaults that cannot
    /// be recoloured by setting properties: they draw their own borders and highlights from system
    /// colours that ignore BackColor.
    /// </remarks>
    internal static class Theme
    {
        // ---------------------------------------------------------------- palette
        //
        // Named for what they are, matching UiTheme's vocabulary, so the two read as the same system.

        /// <summary>Window and page background.</summary>
        public static readonly Color Window = Color.FromArgb(0x21, 0x21, 0x26);

        /// <summary>Raised surfaces: list views, the log, panels behind content.</summary>
        public static readonly Color Surface = Color.FromArgb(0x2B, 0x2B, 0x32);

        /// <summary>
        /// The other row. A difference of five levels out of 255 - present enough to guide the eye
        /// across a wide table, faint enough that it never competes with the text.
        /// </summary>
        public static readonly Color SurfaceShaded = Color.FromArgb(0x26, 0x26, 0x2C);

        /// <summary>Inputs, and the log's background - darker than a surface, as it is recessed.</summary>
        public static readonly Color Field = Color.FromArgb(0x15, 0x15, 0x1A);

        /// <summary>Primary text.</summary>
        public static readonly Color Text = Color.FromArgb(0xE6, 0xE6, 0xEA);

        /// <summary>Secondary text: labels, column heads, anything explanatory.</summary>
        public static readonly Color Muted = Color.FromArgb(0x9E, 0x9E, 0xAA);

        /// <summary>Hairlines. A border should be visible without being noticed.</summary>
        public static readonly Color Border = Color.FromArgb(0x3A, 0x3A, 0x44);

        /// <summary>Button face.</summary>
        public static readonly Color ButtonFace = Color.FromArgb(0x38, 0x38, 0x42);

        /// <summary>Button face under the pointer.</summary>
        public static readonly Color ButtonHover = Color.FromArgb(0x4C, 0x4C, 0x58);

        /// <summary>Selected tab, selected row, anything carrying the accent.</summary>
        public static readonly Color Accent = Color.FromArgb(0x29, 0x6B, 0xB8);

        /// <summary>Accent at rest, for a selected row that should not shout.</summary>
        public static readonly Color AccentMuted = Color.FromArgb(0x24, 0x30, 0x44);

        public static readonly Color Good = Color.FromArgb(0x5F, 0xC1, 0x8A);
        public static readonly Color Warn = Color.FromArgb(0xF3, 0xC2, 0x59);
        public static readonly Color Bad = Color.FromArgb(0xF3, 0x6B, 0x66);

        // ---------------------------------------------------------------- type
        //
        // Three sizes and two weights, which is all four tabs need. Segoe UI because it is what Windows
        // uses everywhere else, so the launcher looks like part of the system it is running in.

        public static readonly string Family = "Segoe UI";

        public static readonly Font Body = new Font(Family, 9f);
        public static readonly Font Small = new Font(Family, 8.25f);
        public static readonly Font Strong = new Font(Family, 9f, FontStyle.Bold);
        public static readonly Font Heading = new Font(Family, 12.5f, FontStyle.Bold);
        public static readonly Font Mono = new Font("Consolas", 9f);

        /// <summary>Comfortable click target height. Windows' 23px default is small for a tool used repeatedly.</summary>
        public const int ControlHeight = 30;

        /// <summary>The standard gap. Every padding and margin in the app is a multiple of this.</summary>
        public const int Gap = 8;

        /// <summary>Page padding.</summary>
        public static readonly Padding PagePadding = new Padding(Gap * 2, Gap * 2, Gap * 2, Gap * 2);

        // ---------------------------------------------------------------- application

        /// <summary>Style a form: colours, font, and a borderless-flat frame.</summary>
        public static void Apply(Form form)
        {
            form.BackColor = Window;
            form.ForeColor = Text;
            form.Font = Body;
        }

        /// <summary>
        /// Style a tab control, replacing the Windows 2003 tab strip.
        /// </summary>
        /// <remarks>
        /// The default strip is drawn from system colours and ignores BackColor entirely, which is why a
        /// dark form with a stock TabControl still has a light grey strip along the top. Owner-drawing it
        /// is the only way to make the one control every tab lives inside match the rest.
        /// </remarks>
        public static TabStrip Strip(TabControl tabs)
        {
            var strip = new TabStrip();
            strip.Dock = DockStyle.Fill;
            strip.TabPages.AddRange(tabs.TabPages.Cast<TabPage>().ToArray());
            strip.SelectedIndex = tabs.SelectedIndex;
            return strip;
        }

        // ---------------------------------------------------------------- text helpers

        /// <summary>The one-line statement of what is true, at the top of a tab.</summary>
        public static Label Headline(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Font = Heading,
                ForeColor = Text,
                Margin = new Padding(0, 0, 0, Gap)
            };
        }

        /// <summary>
        /// Lets a headline wrap instead of being clipped.
        /// </summary>
        /// <remarks>
        /// A Label with AutoSize and no MaximumSize is one line, whatever its text - so a path followed by
        /// a line count simply runs off the edge. Given the width of whatever it sits above, it wraps.
        /// Called from each tab's Reload, because that is the first point at which the width is known.
        /// </remarks>
        public static void FitHeadline(Label label, int width)
        {
            if (label == null) return;

            var room = width > 120 ? width : 120;
            if (label.MaximumSize.Width == room) return;

            label.MaximumSize = new Size(room, 0);
        }

        /// <summary>Explanatory text. Deliberately smaller and quieter than everything else.</summary>
        public static Label Note(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                MaximumSize = new Size(880, 0),
                Font = Small,
                ForeColor = Muted,
                Margin = new Padding(0, Gap, 0, 0)
            };
        }

        /// <summary>A horizontal rule. Cheap, and it separates without another panel.</summary>
        public static Panel Rule()
        {
            return new Panel { Height = 1, BackColor = Border, Margin = new Padding(0, Gap / 2, 0, Gap / 2) };
        }

        /// <summary>A bordered container for grouped content.</summary>
        public static Panel Card()
        {
            return new Panel { BackColor = Surface, Padding = new Padding(Gap) };
        }

        /// <summary>A row of buttons with the app's spacing between them.</summary>
        public static FlowLayoutPanel ButtonRow()
        {
            return new FlowLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                WrapContents = false,
                Margin = new Padding(0, Gap, 0, 0),
                BackColor = Color.Transparent
            };
        }
    }

    /// <summary>
    /// A flat button. WinForms' default draws a 3D border from system colours that ignore BackColor,
    /// so a dark form still gets light grey buttons with a raised edge.
    /// </summary>
    internal sealed class FlatButton : Button
    {
        private bool _hover;

        public FlatButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            FlatAppearance.MouseOverBackColor = Theme.ButtonHover;
            FlatAppearance.MouseDownBackColor = Theme.Accent;
            BackColor = Theme.ButtonFace;
            ForeColor = Theme.Text;
            Font = Theme.Body;
            AutoSize = true;
            Size = new Size(120, Theme.ControlHeight);
            Padding = new Padding(10, 0, 10, 0);
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        }

        /// <summary>Steps out of the row a little, so buttons read as separate.</summary>
        public FlatButton Spaced()
        {
            Margin = new Padding(0, 0, Theme.Gap, 0);
            return this;
        }

        /// <summary>A quieter treatment for actions that are available but not the point of the tab.</summary>
        public FlatButton Quiet()
        {
            BackColor = Color.Transparent;
            ForeColor = Theme.Muted;
            return this;
        }

        /// <summary>
        /// The primary action on a tab: accent-coloured, and Enter activates it.
        /// </summary>
        /// <remarks>
        /// Not ButtonBase.IsDefault, which is protected - and it would mean the wrong thing anyway, since
        /// that highlights the button when Enter is pressed rather than saying which action matters.
        /// </remarks>
        public bool Accent { get; set; }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            Invalidate();
            base.OnEnabledChanged(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // Painted rather than left to FlatStyle.Flat, because the stock flat rendering centres the
            // text but still lays it out for a default button height, and this one is 30px.
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Window);

            var bounds = new Rectangle(0, 0, Width, Height);

            Color face;
            if (!Enabled) face = Theme.Window;
            else if (Accent) face = Theme.Accent;
            else if (_hover) face = Theme.ButtonHover;
            else face = BackColor;

            if (face != Theme.Window && (Enabled || Accent))
            {
                using (var brush = new SolidBrush(face)) g.FillRectangle(brush, bounds);
            }

            TextRenderer.DrawText(g, Text, this.Font, bounds, Enabled ? ForeColor : Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>
    /// A tab strip that looks like one, rather than like the Windows 2003 default.
    /// </summary>
    /// <remarks>
    /// Owner-drawn for the same reason the button is: the stock strip draws from system colours and
    /// ignores the form's palette entirely.
    /// </remarks>
    internal sealed class TabStrip : TabControl
    {
        public TabStrip()
        {
            // Deliberately no SetStyle(UserPaint) here. TabControl is a native control, and forcing
            // UserPaint on one suppresses the painting that draws the tab strip - which is how this ended
            // up with content and no tabs at all. Owner-drawing a TabControl needs DrawMode and the
            // DrawItem event and nothing else.
            DrawMode = TabDrawMode.OwnerDrawFixed;
            SizeMode = TabSizeMode.Normal;
            Padding = new Point(Theme.Gap * 2, Theme.Gap);
            Font = Theme.Body;
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            var g = e.Graphics;
            var bounds = e.Bounds;
            var selected = e.Index == SelectedIndex;

            // No g.Clear() here. Graphics.Clear fills the whole clipping region, which for a TabControl
            // owner-draw is the entire control rather than just this tab - so it erased every tab drawn
            // before it. WinForms draws the selected tab last, which left exactly one tab visible and
            // made it look like the strip was broken rather than over-painting itself.
            //
            // Filling this tab's own rectangle is enough, and is what was wanted all along.
            var face = selected ? Theme.Surface : Theme.Window;
            using (var brush = new SolidBrush(face)) g.FillRectangle(brush, bounds);

            // An underline rather than a raised border: it marks the selection without adding chrome.
            if (selected)
            {
                using (var pen = new Pen(Theme.Accent, 2f))
                {
                    g.DrawLine(pen, bounds.Left + 1, bounds.Bottom - 1, bounds.Right - 2, bounds.Bottom - 1);
                }
            }

            var text = selected ? Theme.Text : Theme.Muted;
            TextRenderer.DrawText(g, TabPages[e.Index].Text, Font,
                new Rectangle(bounds.Left, bounds.Top, bounds.Width, bounds.Height - 2), text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            using (var pen = new Pen(Theme.Border))
            {
                g.DrawLine(pen, bounds.Right - 1, bounds.Top + 2, bounds.Right - 1, bounds.Bottom - 2);
            }
        }
    }

    /// <summary>
    /// A list view that respects the palette.
    /// </summary>
    /// <remarks>
    /// Owner-drawn because setting BackColor alone leaves the column headers light and the selection
    /// highlight the system blue, which is the one combination that makes a dark list look broken.
    ///
    /// Rows are drawn rather than using OwnerDrawFixed's striping, so that a row can be coloured -
    /// enabled, disabled, and problems have all been coloured from the start - and so the alternate
    /// row tint can be a barely-there difference rather than the stock grey.
    /// </remarks>
    internal sealed class ThemedListView : ListView
    {
        public ThemedListView()
        {
            OwnerDraw = true;
            View = View.Details;
            FullRowSelect = true;
            MultiSelect = false;
            HideSelection = false;
            BorderStyle = BorderStyle.None;
            BackColor = Theme.Surface;
            ForeColor = Theme.Text;
            Font = Theme.Body;
            DoubleBuffered = true;
        }

        /// <summary>
        /// Gives any slack to the last column, so no filler header or horizontal scrollbar appears.
        /// </summary>
        /// <remarks>
        /// A ListView whose columns do not add up to its width leaves a gap to the right of the last one,
        /// and WinForms fills that gap with a header panel it does not pass to OnDrawColumnHeader. It
        /// then renders in the system colour - a small white block in the corner of an otherwise dark
        /// list. Filling the width removes the gap rather than painting over the symptom.
        ///
        /// The vertical scrollbar is allowed for, because a list long enough to need one would otherwise
        /// overflow once it appears. Call <see cref="Refit"/> after populating a list so the width is
        /// right for the row count rather than for an empty one.
        ///
        /// Guarded against re-entry: setting a column width raises SizeChanged, and an unguarded version
        /// oscillates.
        /// </remarks>
        private int _fittedWidth = -1;
        private bool _fitting;

        /// <summary>
        /// Height of the column header row, used to work out how many rows are actually visible.
        /// </summary>
        /// <remarks>
        /// Hard-coded because WinForms exposes no getter for it and it is stable at this font size. Only
        /// used to decide whether a vertical scrollbar is needed, so being a few pixels out costs nothing.
        /// </remarks>
        private const int HeaderHeight = 21;

        /// <summary>
        /// Width deliberately left over, and painted as part of the last column's header.
        /// </summary>
        /// <remarks>
        /// Landing exactly on the available width is a coin toss: get it one pixel wrong in either
        /// direction and WinForms adds a horizontal scrollbar, while leaving a gap leaves a filler header
        /// panel it renders in the system colour - a white block in the corner of a dark list. So the
        /// columns stop short by this much and OnDrawColumnHeader paints the remainder, which cannot
        /// produce either artefact.
        /// </remarks>
        private const int HeaderSlack = 22;

        /// <summary>
        /// How far the width must move before the columns are re-fitted.
        /// </summary>
        /// <remarks>
        /// The dead band that stops the fit chasing its own tail. Wider than any rounding WinForms does
        /// on a resize, and narrow enough that dragging the window still re-fits promptly.
        /// </remarks>
        private const int FitTolerance = 12;

        /// <summary>Re-fits the columns to the current width. Safe to call after adding items.</summary>
        public void Refit()
        {
            _fittedWidth = -1;
            FitColumns();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            FitColumns();
        }

        /// <summary>
        /// Also fits on layout, not just on resize.
        /// </summary>
        /// <remarks>
        /// Resize alone was not enough: a tab that is not selected at startup is laid out at its final
        /// size later, when it is first shown, and no resize event accompanies that. The Log tab's
        /// problems list kept its startup width and grew a horizontal scrollbar as a result.
        /// </remarks>
        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            FitColumns();
        }

        private void FitColumns()
        {
            if (_fitting || Columns.Count == 0) return;

            // A vertical scrollbar only takes width when there is one. Assuming either way is wrong in
            // the other direction: reserving it for a list too short to need one leaves a gap that shows
            // as a white filler header; ignoring it for a long list overflows into a scrollbar. So it is
            // worked out from the row count against the height actually available.
            var rowHeight = Math.Max(1, this.Font.Height + 2);
            var visible = (ClientSize.Height - HeaderHeight) / rowHeight;
            var scrollbar = Items.Count > visible ? SystemInformation.VerticalScrollBarWidth : 0;

            var available = ClientSize.Width - 4 - scrollbar - HeaderSlack;
            if (available < 80) return;

            // Ignore small changes. Setting a column width changes the control's layout, which changes
            // the width we just measured, which sets the column again - and without a dead band it never
            // settles. It was observed cycling 998 -> 1204 -> 1128, which is also wider than the window
            // it is inside. A few pixels of slack in a column nobody reads is a fair price for stopping.
            if (_fittedWidth > 0 && Math.Abs(available - _fittedWidth) < FitTolerance) return;

            var total = 0;
            foreach (ColumnHeader column in Columns) total += column.Width;

            var last = Columns[Columns.Count - 1];
            var wanted = last.Width + (available - total);
            if (wanted < 40) wanted = 40;
            if (wanted == last.Width) return;

            _fitting = true;
            try
            {
                last.Width = wanted;
                _fittedWidth = available;
            }
            finally
            {
                _fitting = false;
            }
        }

        /// <summary>
        /// Shortens a cell to fit rather than letting it be cut mid-glyph.
        /// </summary>
        /// <remarks>
        /// A path clipped to "C:\Program Files (x86)\Steam\steamapps\comm" tells the reader nothing they
        /// can act on; an ellipsis at least says there is more, and the full text is one click away in the
        /// status line.
        /// </remarks>
        private string Fit(Graphics g, string text, int width)
        {
            if (string.IsNullOrEmpty(text) || width <= 0) return "";

            using (var probe = new StringFormat())
            {
                if (g.MeasureString(text, this.Font, int.MaxValue, probe).Width <= width) return text;
            }

            var trimmed = text;
            while (trimmed.Length > 1)
            {
                trimmed = trimmed.Substring(0, trimmed.Length - 1);

                using (var probe = new StringFormat())
                {
                    if (g.MeasureString(trimmed + "\u2026", this.Font, int.MaxValue, probe).Width <= width)
                    {
                        return trimmed + "\u2026";
                    }
                }
            }

            return "\u2026";
        }

        protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
        {
            using (var g = e.Graphics)
            using (var back = new SolidBrush(Theme.Window))
            using (var pen = new Pen(Theme.Border))
            {
                // The last column's header is painted out to the full width, over the slack left by
                // FitColumns. WinForms would otherwise fill that gap with a panel of its own that never
                // reaches this handler, and render it in the system colour.
                var bounds = e.Bounds;
                if (e.ColumnIndex == Columns.Count - 1)
                {
                    bounds = new Rectangle(bounds.Left, bounds.Top,
                        Math.Max(bounds.Width, ClientSize.Width - bounds.Left), bounds.Height);
                }

                g.FillRectangle(back, bounds);
                g.DrawLine(pen, bounds.Left, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);

                var text = Fit(g, e.Header.Text, e.Bounds.Width - 12);

                TextRenderer.DrawText(g, text, new Font(this.Font.FontFamily, this.Font.SizeInPoints, FontStyle.Bold),
                    new Rectangle(bounds.Left + 6, bounds.Top, bounds.Width - 12, bounds.Height),
                    Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }

        /// <summary>
        /// Row background, and the first column's text.
        /// </summary>
        /// <remarks>
        /// Cell positions come from WinForms rather than from arithmetic on column widths. An earlier
        /// version walked the columns itself and incremented x by each width, which put every cell one
        /// column out - the state ("ok") rendered under Detail, and the check name under State. The
        /// bounds WinForms hands to OnDrawSubItem are the only positions guaranteed to match the headers
        /// it also draws.
        ///
        /// The alternating tint is cached in a field rather than recomputed, because OnDrawSubItem is not
        /// given the row index - only the column - and WinForms raises DrawItem then the subitems for one
        /// row before moving on, so the value set here is the one that applies.
        /// </remarks>
        private Color _rowTint = Theme.Surface;
        private Color _rowColor = Theme.Text;

        protected override void OnDrawItem(DrawListViewItemEventArgs e)
        {
            _rowTint = e.Item.Selected
                ? Theme.AccentMuted
                : (e.ItemIndex % 2 == 1 ? Theme.Surface : Theme.SurfaceShaded);

            using (var g = e.Graphics)
            {
                var row = new Rectangle(e.Bounds.Left, e.Bounds.Top, e.Bounds.Width, e.Bounds.Height);
                using (var brush = new SolidBrush(_rowTint)) g.FillRectangle(brush, row);

                // A row's own colour is set by whoever built the list - an unrecognised or unloadable mod
                // is coloured from the start, and the colour survives to here.
                var color = RowColor(e.Item);
                _rowColor = color;

                var first = Columns.Count > 0 ? Columns[0].Width : row.Width;

                TextRenderer.DrawText(g, Fit(g, e.Item.Text, first - 12), this.Font,
                    new Rectangle(row.Left + 6, row.Top, first - 12, row.Height),
                    e.Item.Selected ? Theme.Text : color,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

                if (e.Item.Selected)
                {
                    using (var pen = new Pen(Theme.Accent, 2f))
                    {
                        g.DrawLine(pen, row.Left + 1, row.Top, row.Left + 1, row.Bottom);
                    }
                }
            }
        }

        protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
        {
            using (var g = e.Graphics)
            {
                var cell = new Rectangle(e.Bounds.Left, e.Bounds.Top, e.Bounds.Width, e.Bounds.Height);
                using (var brush = new SolidBrush(_rowTint)) g.FillRectangle(brush, cell);

                var row = _rowColor;

                // The first column carries the identity and keeps the row's colour. The rest are detail
                // under a column header, so they are muted - except a warning or error, which is the
                // reason the row is being read at all and must not be quieted.
                var color = row == Theme.Warn || row == Theme.Bad ? row : Theme.Muted;

                TextRenderer.DrawText(g, Fit(g, e.SubItem.Text, e.Bounds.Width - 12), this.Font,
                    new Rectangle(cell.Left + 6, cell.Top, e.Bounds.Width - 12, cell.Height),
                    e.Item.Selected ? Theme.Text : color,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }
        }

        /// <summary>The colour a row was given, treating the default as "no opinion".</summary>
        private static Color RowColor(ListViewItem item)
        {
            if (item == null) return Theme.Text;
            return item.ForeColor == SystemColors.ControlText || item.ForeColor.IsEmpty
                ? Theme.Text
                : item.ForeColor;
        }
    }
}
