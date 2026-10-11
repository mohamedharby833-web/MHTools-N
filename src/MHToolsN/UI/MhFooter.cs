using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace MHToolsN.UI
{
    /// <summary>
    /// The foot of every MH Tools window: the signature at the left
    /// ("MH Tools 2.6.0 - Mohamed Ali Harby"), the window's buttons at the right,
    /// and between them an optional line of status - one line, cut short with
    /// an ellipsis if it does not fit, so keep it to a few words.
    ///
    /// Built in code from <see cref="MhTheme"/> alone, like <see cref="MhHeader"/>,
    /// and for the same reason.
    ///
    /// In XAML the buttons are simply written inside it:
    /// <code>
    /// &lt;ui:MhFooter Status="4 of 5 models ticked"&gt;
    ///     &lt;StackPanel Orientation="Horizontal"&gt; ...buttons... &lt;/StackPanel&gt;
    /// &lt;/ui:MhFooter&gt;
    /// </code>
    /// What is written inside goes to <see cref="Actions"/>, not to the border's
    /// own Child - that holds the layout.
    /// </summary>
    [ContentProperty("Actions")]
    public sealed class MhFooter : Border
    {
        public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(
            "Actions", typeof(UIElement), typeof(MhFooter), new PropertyMetadata(null, OnActionsChanged));

        public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
            "Status", typeof(string), typeof(MhFooter), new PropertyMetadata(string.Empty, OnStatusChanged));

        private readonly Border _actions;
        private readonly TextBlock _status;

        public MhFooter()
        {
            Background = MhTheme.Brush(MhTheme.Surface);
            BorderBrush = MhTheme.Brush(MhTheme.Line);
            BorderThickness = new Thickness(0, 1, 0, 0);
            Padding = new Thickness(MhTheme.Gutter, 12, MhTheme.Gutter, 12);
            SnapsToDevicePixels = true;

            var signature = new TextBlock
            {
                Text = MhTheme.Signature,
                FontFamily = MhTheme.Font,
                FontSize = MhTheme.SmallFontSize,
                Foreground = MhTheme.Brush(MhTheme.TextMuted),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            // The status is the part that gives way: it takes what is left
            // between the signature and the buttons, and is cut short with an
            // ellipsis when that is not enough. The buttons are never pushed
            // out of the window by a long sentence.
            _status = new TextBlock
            {
                FontFamily = MhTheme.Font,
                FontSize = MhTheme.FontSize,
                Foreground = MhTheme.Brush(MhTheme.TextMuted),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(12, 0, 12, 0),
                Visibility = Visibility.Collapsed
            };

            _actions = new Border { VerticalAlignment = VerticalAlignment.Center };

            // A DockPanel shares the width out in the order its children were
            // added: the buttons first, then the signature, the status last.
            var layout = new DockPanel { LastChildFill = true };

            DockPanel.SetDock(_actions, Dock.Right);
            DockPanel.SetDock(signature, Dock.Left);

            layout.Children.Add(_actions);
            layout.Children.Add(signature);
            layout.Children.Add(_status);

            Child = layout;
        }

        /// <summary>The window's buttons - one element, usually a horizontal StackPanel of them.</summary>
        public UIElement Actions
        {
            get { return (UIElement)GetValue(ActionsProperty); }
            set { SetValue(ActionsProperty, value); }
        }

        /// <summary>A short line beside the buttons: "4 of 5 models ticked". Empty: left out.</summary>
        public string Status
        {
            get { return (string)GetValue(StatusProperty); }
            set { SetValue(StatusProperty, value); }
        }

        private static void OnActionsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((MhFooter)d)._actions.Child = e.NewValue as UIElement;
        }

        private static void OnStatusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var footer = (MhFooter)d;
            var text = e.NewValue as string;

            footer._status.Text = text ?? string.Empty;
            footer._status.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;

            // Whatever was cut short can still be read in full.
            footer._status.ToolTip = string.IsNullOrWhiteSpace(text) ? null : text;
        }
    }
}
