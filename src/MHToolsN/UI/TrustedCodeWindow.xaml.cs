using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MHToolsN.Bridge;

namespace MHToolsN.UI
{
    /// <summary>
    /// Ask or Auto for Trusted C# (as in MHTools-R). Choice stays Off unless Turn on
    /// was clicked - closing the window any other way leaves it off.
    /// </summary>
    internal partial class TrustedCodeWindow : Window
    {
        /// <summary>What was chosen: Ask or Auto. Off when the window was closed or cancelled.</summary>
        public CodeMode Choice { get; private set; }

        public TrustedCodeWindow()
        {
            MhTheme.Apply(this);
            InitializeComponent();
            Choice = CodeMode.Off;
            Mark();
        }

        // The card's padding is outside the radio button, so a click there has
        // to pick it too.
        private void OnPickAsk(object sender, MouseButtonEventArgs e) { AskOption.IsChecked = true; }

        private void OnPickAuto(object sender, MouseButtonEventArgs e) { AutoOption.IsChecked = true; }

        // Checked fires while InitializeComponent is still building the window,
        // before the cards exist.
        private void OnModeChanged(object sender, RoutedEventArgs e)
        {
            if (AskCard != null && AutoCard != null) Mark();
        }

        private void Mark()
        {
            Mark(AskCard, AskOption.IsChecked == true);
            Mark(AutoCard, AutoOption.IsChecked == true);
        }

        private static void Mark(Border card, bool picked)
        {
            card.SetResourceReference(Border.BorderBrushProperty, picked ? "MhAccentBrush" : "MhControlBorderBrush");
            card.SetResourceReference(Border.BackgroundProperty, picked ? "MhAccentTintBrush" : "MhSurfaceBrush");
        }

        private void OnTurnOn(object sender, RoutedEventArgs e)
        {
            Choice = AutoOption.IsChecked == true ? CodeMode.Auto : CodeMode.Ask;
            Close();
        }
    }
}
