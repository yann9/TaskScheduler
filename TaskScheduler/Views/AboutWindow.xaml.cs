using System.Windows;
using TaskScheduler.ViewModels;

namespace TaskScheduler.Views
{
    public partial class AboutWindow : Window
    {
        public AboutWindow()
        {
            InitializeComponent();
            DataContext = new AboutViewModel();
        }

        private void Ok_Click(object sender, RoutedEventArgs e) => Close();
    }
}
