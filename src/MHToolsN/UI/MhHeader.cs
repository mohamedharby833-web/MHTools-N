using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MHToolsN.UI
{
    /// <summary>
    /// The top of every MH Tools window: the MH mark, the tool's name, one line
    /// on what it does, and which MH Tools this is.
    ///
    /// Built in code from <see cref="MhTheme"/> alone - no XAML, no resources -
    /// so it looks the same in a window written in XAML, in one built in code,
    /// and in one whose styles failed to load. It is half of the signature;
    /// <see cref="MhFooter"/> is the other half.
    ///
    /// In XAML: <c>&lt;ui:MhHeader Title="Export IFC" Subtitle="..." /&gt;</c>.
    /// </summary>
    public sealed class MhHeader : Border
    {
        public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
            "Title", typeof(string), typeof(MhHeader), new PropertyMetadata(string.Empty, OnWordsChanged));

        public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
            "Subtitle", typeof(string), typeof(MhHeader), new PropertyMetadata(string.Empty, OnWordsChanged));

        public static readonly DependencyProperty ShowProductProperty = DependencyProperty.Register(
            "ShowProduct", typeof(bool), typeof(MhHeader), new PropertyMetadata(true, OnWordsChanged));

        private readonly TextBlock _title;
        private readonly TextBlock _subtitle;
        private readonly TextBlock _product;

        public MhHeader()
        {
            Background = MhTheme.Brush(MhTheme.Surface);
            BorderBrush = MhTheme.Brush(MhTheme.Line);
            BorderThickness = new Thickness(0, 0, 0, 1);
            Padding = new Thickness(MhTheme.Gutter, 14, MhTheme.Gutter, 14);
            SnapsToDevicePixels = true;

            var mark = new Border
            {
                Width = 38,
                Height = 38,
                CornerRadius = MhTheme.PanelCorner,
                Background = MhTheme.Brush(MhTheme.Accent),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = MhTheme.Mark,
                    FontFamily = MhTheme.Font,
                    FontSize = MhTheme.LeadFontSize,
                    FontWeight = FontWeights.Bold,
                    Foreground = MhTheme.Brush(MhTheme.OnAccent),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };

            _title = new TextBlock
            {
                FontFamily = MhTheme.Font,
                FontSize = MhTheme.TitleFontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = MhTheme.Brush(MhTheme.Text),
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            _subtitle = new TextBlock
            {
                FontFamily = MhTheme.Font,
                FontSize = MhTheme.FontSize,
                Foreground = MhTheme.Brush(MhTheme.TextMuted),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            };

            var words = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(14, 0, 14, 0)
            };
            words.Children.Add(_title);
            words.Children.Add(_subtitle);

            _product = new TextBlock
            {
                Text = MhTheme.Product,
                FontFamily = MhTheme.Font,
                FontSize = MhTheme.SmallFontSize,
                Foreground = MhTheme.Brush(MhTheme.TextMuted),
                VerticalAlignment = VerticalAlignment.Center
            };

            var layout = new Grid();
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(mark, 0);
            Grid.SetColumn(words, 1);
            Grid.SetColumn(_product, 2);

            layout.Children.Add(mark);
            layout.Children.Add(words);
            layout.Children.Add(_product);

            Child = layout;
            ShowWords();
        }

        /// <summary>The tool's name.</summary>
        public string Title
        {
            get { return (string)GetValue(TitleProperty); }
            set { SetValue(TitleProperty, value); }
        }

        /// <summary>One line on what the tool does. Empty: the line is left out.</summary>
        public string Subtitle
        {
            get { return (string)GetValue(SubtitleProperty); }
            set { SetValue(SubtitleProperty, value); }
        }

        /// <summary>"MH Tools 2.6.0" at the right. Off where the title already says it - About.</summary>
        public bool ShowProduct
        {
            get { return (bool)GetValue(ShowProductProperty); }
            set { SetValue(ShowProductProperty, value); }
        }

        private static void OnWordsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((MhHeader)d).ShowWords();
        }

        private void ShowWords()
        {
            _title.Text = Title ?? string.Empty;
            _subtitle.Text = Subtitle ?? string.Empty;
            _subtitle.Visibility = string.IsNullOrWhiteSpace(Subtitle) ? Visibility.Collapsed : Visibility.Visible;
            _product.Visibility = ShowProduct ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
