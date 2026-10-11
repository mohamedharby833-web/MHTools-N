using System;
using System.Windows;
using MHToolsN.Bridge;

namespace MHToolsN.UI
{
    internal partial class TrustedCodeWindow : Window
    {
        /// <summary>What was chosen: Ask or Auto. Off when the window was closed or cancelled.</summary>
        public CodeMode Choice { get; private set; }

        public TrustedCodeWindow()
        {
            MhTheme.Apply(this);
            InitializeComponent();
            Choice = CodeMode.Off;
        }

        private void OnAsk(object sender, RoutedEventArgs e)
        {
            Choice = CodeMode.Ask;
            Close();
        }

        private void OnAuto(object sender, RoutedEventArgs e)
        {
            Choice = CodeMode.Auto;
            Close();
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            Choice = CodeMode.Off;
            Close();
        }
    }
}
