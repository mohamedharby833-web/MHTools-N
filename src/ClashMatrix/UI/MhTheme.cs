using System;
using System.Windows;
using System.Windows.Media;
using ClashMatrix.Services;

namespace ClashMatrix.UI
{
    /// <summary>
    /// The look of every Clash Matrix window: the MH Tools theme (MH Tools 2.6.2), decided in ONE place.
    ///
    /// To change the look, change the values here: the colours, the sizes, the
    /// words of the signature. <c>UI/Theme.xaml</c> holds the styles (what a
    /// button or a heading is built from) and reads every colour and size from
    /// this class, and so do the header and footer (<see cref="MhHeader"/>,
    /// <see cref="MhFooter"/>) and the windows that are built in code. Nothing
    /// else in the add-in should name a colour.
    ///
    /// The current look is "Clean light": white, one teal accent, rounded
    /// controls, hairlines instead of boxes.
    ///
    /// Colours are <see cref="Color"/> values, not brushes, on purpose. A brush
    /// belongs to the thread that made it, and the export progress window runs
    /// on a thread of its own; a colour is just four numbers. Whoever needs a
    /// brush makes one from the colour (<see cref="Brush"/> - frozen, so it can
    /// go anywhere).
    /// </summary>
    public static class MhTheme
    {
        // ---------------- colours ----------------

        /// <summary>The one accent: the MH mark, the main button, ticks, selection.</summary>
        public static readonly Color Accent = Rgb(0x0B, 0x6E, 0x6E);
        public static readonly Color AccentHover = Rgb(0x09, 0x5C, 0x5C);
        public static readonly Color AccentPressed = Rgb(0x07, 0x4B, 0x4B);

        /// <summary>A wash of the accent: the selected row, the active item.</summary>
        public static readonly Color AccentTint = Rgb(0xE1, 0xF1, 0xF0);

        /// <summary>
        /// A chosen row in a list or a table: a clear teal, so it stands out at a glance
        /// (AccentTint was too faint for it). Text stays dark on it.
        /// </summary>
        public static readonly Color Selection = Rgb(0xA6, 0xD8, 0xD3);

        /// <summary>Text and marks drawn on the accent.</summary>
        public static readonly Color OnAccent = Rgb(0xFF, 0xFF, 0xFF);

        /// <summary>The window and every control's own background.</summary>
        public static readonly Color Surface = Rgb(0xFF, 0xFF, 0xFF);
        public static readonly Color SurfaceHover = Rgb(0xF4, 0xF6, 0xF8);
        public static readonly Color SurfacePressed = Rgb(0xE9, 0xED, 0xF1);

        /// <summary>Set-back areas: a note, a log, read-only details.</summary>
        public static readonly Color SurfaceSunken = Rgb(0xF4, 0xF6, 0xF8);

        public static readonly Color Text = Rgb(0x17, 0x20, 0x2A);

        /// <summary>Second-rank text: descriptions, hints, the signature. Still 5.9:1 on white.</summary>
        public static readonly Color TextMuted = Rgb(0x5B, 0x66, 0x72);

        /// <summary>Hairlines between the parts of a window.</summary>
        public static readonly Color Line = Rgb(0xE4, 0xE8, 0xEC);

        /// <summary>The outline of a button, a box, a list.</summary>
        public static readonly Color ControlBorder = Rgb(0xCB, 0xD2, 0xD9);

        public static readonly Color Success = Rgb(0x17, 0x61, 0x3A);
        public static readonly Color Warning = Rgb(0x6B, 0x52, 0x00);
        public static readonly Color Danger = Rgb(0x9B, 0x1C, 0x1C);

        // ---------------- type ----------------

        /// <summary>The font, by name - for the Windows Forms dialogs, which make their own.</summary>
        public const string FontName = "Segoe UI";
        public const string MonoFontName = "Consolas";

        public static readonly FontFamily Font = new FontFamily(FontName);
        public static readonly FontFamily MonoFont = new FontFamily(MonoFontName);

        // Sizes are all double, and have to stay so: Theme.xaml reads them with
        // x:Static, which the build does not type-check. A size turned into an
        // int here still compiles, and fails when the first window opens.
        public const double FontSize = 12;
        public const double SmallFontSize = 11;
        public const double SectionFontSize = 13;

        /// <summary>A line that leads a block: a person's name, the letters in the MH mark.</summary>
        public const double LeadFontSize = 14;

        public const double TitleFontSize = 20;

        // ---------------- shape ----------------

        /// <summary>Corners of buttons and boxes.</summary>
        public static readonly CornerRadius Corner = new CornerRadius(6);

        /// <summary>Corners of larger areas: a list, a note, the MH mark.</summary>
        public static readonly CornerRadius PanelCorner = new CornerRadius(8);

        /// <summary>Corners of a row in a list, and of what is inside a box.</summary>
        public static readonly CornerRadius RowCorner = new CornerRadius(4);

        /// <summary>Corners of the smallest things: a tick box, a progress bar.</summary>
        public static readonly CornerRadius SmallCorner = new CornerRadius(3);

        /// <summary>The keyboard-focus ring sits 3 px outside a control; this is its corner.</summary>
        public const double FocusRingRadius = 8;

        /// <summary>A text box, a drop-down list.</summary>
        public const double ControlHeight = 30;

        /// <summary>The buttons at the foot of a window.</summary>
        public const double ButtonHeight = 32;
        public const double ButtonMinWidth = 96;

        /// <summary>Buttons inside the window: a toolbar, "Browse...", a button in a row.</summary>
        public const double CompactButtonHeight = 28;

        /// <summary>A row in a list or a table, and the table's heading row.</summary>
        public const double RowHeight = 30;

        /// <summary>The square of a tick box and the circle of a radio button.</summary>
        public const double TickSize = 16;

        /// <summary>How thick a progress bar is.</summary>
        public const double BarHeight = 6;

        /// <summary>Space between a window's edge and what is in it, left and right.</summary>
        public const double Gutter = 20;

        // ---------------- the signature ----------------

        public const string Mark = "MH";
        public const string Author = "Mohamed Ali Harby";

        /// <summary>"MHTools-N 1.6.0".</summary>
        /// <summary>"1.1.1": the add-in's version (Major.Minor.Build of the assembly).</summary>
        public static string Version
        {
            get
            {
                System.Version v = typeof(MhTheme).Assembly.GetName().Version;
                return v.Major + "." + v.Minor + "." + v.Build;
            }
        }

        public static string Product
        {
            get { return "MHTools-N " + Version; }
        }

        /// <summary>"MHTools-N 1.6.0 - Mohamed Ali Harby": the line at the foot of every window.</summary>
        public static string Signature
        {
            get { return Product + " - " + Author; }
        }

        // ---------------- use ----------------


        /// <summary>
        /// One load at a time. Each caller gets its own dictionary, but WPF
        /// fetches the compiled file through a part it shares between callers
        /// and does not guard - and the export progress window loads from a
        /// thread of its own.
        /// </summary>
        private static readonly object LoadLock = new object();

        /// <summary>Set on a window once it has the look, so asking twice does nothing the second time.</summary>
        private static readonly DependencyProperty ThemedProperty = DependencyProperty.RegisterAttached(
            "Themed", typeof(bool), typeof(MhTheme), new PropertyMetadata(false));

        /// <summary>
        /// Gives a window the look: its own colours and font, and the styles for
        /// what is inside it. Call it FIRST in the window's constructor - before
        /// InitializeComponent, or before the first control is made - on the
        /// window's own thread.
        ///
        /// Before, because a window's own styles are then read with the theme
        /// already there: a local style that only adds a margin can say
        /// BasedOn="{StaticResource {x:Type CheckBox}}" and get the theme's tick
        /// box, where the other way round it would get Windows' plain one. For
        /// the same reason a window keeps its own resources in its root panel
        /// (Grid.Resources), and leaves Window.Resources to this method.
        ///
        /// Each window gets its OWN copy of the styles. They are never put into
        /// the application's resources: that application is Navisworks, and every
        /// other add-in's windows would be restyled with ours.
        ///
        /// Never throws. If the styles cannot be loaded the window still opens,
        /// plain, and the reason is in the Clash Matrix log - a tool that
        /// looks wrong is better than one that will not start. (That covers a
        /// file that cannot be loaded. A mistake INSIDE a style - a value of the
        /// wrong type - only shows when the style is first used, and is not
        /// caught here.)
        /// </summary>
        public static void Apply(Window window)
        {
            if (window == null) return;

            try
            {
                if ((bool)window.GetValue(ThemedProperty)) return;
                window.SetValue(ThemedProperty, true);

                window.Background = Brush(Surface);
                window.Foreground = Brush(Text);
                window.FontFamily = Font;
                window.FontSize = FontSize;

                // Small text on a light ground: drawn on whole pixels.
                window.UseLayoutRounding = true;
                window.SnapsToDevicePixels = true;
                TextOptions.SetTextFormattingMode(window, TextFormattingMode.Display);
            }
            catch (Exception ex)
            {
                Watchdog.LogException("Theme: colours and font", ex);
            }

            try
            {
                window.Resources.MergedDictionaries.Add(LoadStyles());
            }
            catch (Exception ex)
            {
                Watchdog.LogException("Theme: UI/Theme.xaml could not be loaded; the window opens without it", ex);
            }
        }

        /// <summary>
        /// A new copy of the styles in UI/Theme.xaml, made on the calling thread.
        ///
        /// Asked for by the assembly's exact name and version, as the windows'
        /// own XAML is: with two builds of Clash Matrix (or MH Tools) loaded, each gets its
        /// own.
        /// </summary>
        internal static ResourceDictionary LoadStyles()
        {
            var name = typeof(MhTheme).Assembly.GetName();
            var where = "/" + name.Name + ";v" + name.Version + ";component/ui/theme.xaml";

            lock (LoadLock)
            {
                return (ResourceDictionary)Application.LoadComponent(new Uri(where, UriKind.Relative));
            }
        }

        /// <summary>A brush of one colour, frozen: usable on any thread, and cheaper to draw.</summary>
        public static SolidColorBrush Brush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        /// <summary>
        /// The same colour for Windows Forms, whose colours are another type. The
        /// support dialogs are Windows Forms; they take every colour from here
        /// through this, so the two kinds of window cannot drift apart.
        /// </summary>
        public static System.Drawing.Color Gdi(Color color)
        {
            return System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B);
        }

        private static Color Rgb(byte red, byte green, byte blue)
        {
            return Color.FromRgb(red, green, blue);
        }

        // ADDED BY forms: the washes behind the rows of the two support reports.
        // A report colours each row by what happened to it, and the dialogs named
        // those colours themselves until now. Dark text reads on all four. The
        // fifth state, a row left as it was, uses SurfaceSunken; a chosen row
        // uses AccentTint, as everywhere.

        /// <summary>A row where everything asked for was done.</summary>
        public static readonly Color SuccessTint = Rgb(0xE7, 0xF4, 0xEC);

        /// <summary>A row where part was left out and wants a look.</summary>
        public static readonly Color WarningTint = Rgb(0xFF, 0xF6, 0xD9);

        /// <summary>A row where nothing was done although something could have been.</summary>
        public static readonly Color WarningStrongTint = Rgb(0xFC, 0xE6, 0xB8);

        /// <summary>A row with errors.</summary>
        public static readonly Color DangerTint = Rgb(0xFB, 0xE3, 0xE3);
    }
}
