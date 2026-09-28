using System.Windows;

namespace TaskScheduler.Views
{
    public partial class SettingsWindow : Window
    {
        public SettingsWindow() => InitializeComponent();

        private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
