using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace Switchboard
{
    /// <summary>
    /// The switchboard's palette, read out of the resource dictionary by key.
    /// </summary>
    /// <remarks>
    /// WPF resolved these with <c>Application.Current.FindResource(key)</c> and an unchecked cast to
    /// <see cref="Brush"/>. Avalonia's equivalent is <c>TryGetResource</c>, which reports a miss rather
    /// than throwing, and a cast that can fail. Both are wrapped here so a renamed or missing key
    /// yields a visible fallback instead of a null brush - a null Background is invisible, and an
    /// exception from a resource lookup in a property getter is exactly the kind of thing that turns a
    /// list row into a crash.
    ///
    /// The keys are the ones declared in <c>src/Theme.xaml</c>.
    /// </remarks>
    internal static class Palette
    {
        public const string Text = "TextBrush";
        public const string Muted = "MutedBrush";
        public const string Accent = "AccentBrush";
        public const string AccentMuted = "AccentMutedBrush";
        public const string Good = "GoodBrush";
        public const string Warn = "WarnBrush";
        public const string Bad = "BadBrush";
        public const string Field = "FieldBrush";
        public const string Border = "BorderBrush";
        public const string Surface = "SurfaceBrush";

        /// <summary>
        /// The brush for a key, or a neutral fallback.
        /// </summary>
        /// <remarks>
        /// Application.Current is null before the app is up, which a brush read from a data class's
        /// property getter can hit while a design-time host or a test builds one. Providing something
        /// rather than throwing keeps that from being fatal.
        /// </remarks>
        public static IBrush Brush(string key)
        {
            var application = Application.Current;
            if (application != null && application.TryGetResource(key, null, out var value) && value is IBrush brush)
            {
                return brush;
            }

            return Brushes.Transparent;
        }

        /// <summary>
        /// The control theme for a key, or null.
        /// </summary>
        /// <remarks>
        /// Null rather than a fallback, unlike <see cref="Brush"/>: a control with no theme still draws
        /// with its stock one, which is the right behaviour for a dialog built in code that cannot find
        /// the switchboard's own button theme. Handing back a substituted theme would be inventing one.
        /// </remarks>
        public static ControlTheme ControlTheme(string key)
        {
            var application = Application.Current;
            if (application != null && application.TryGetResource(key, null, out var value) && value is ControlTheme theme)
            {
                return theme;
            }

            return null;
        }
    }
}
