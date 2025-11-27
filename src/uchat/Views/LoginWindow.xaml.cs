using System.Windows;

namespace uchat.Views
{
    public partial class LoginWindow : Window
    {
        public LoginWindow()
        {
            InitializeComponent();
        }

        private void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            // Пока просто заглушка, чтобы проект собрался
            MessageBox.Show($"Попытка входа: {UsernameBox.Text}");
            
            // Здесь потом будет логика отправки на сервер
        }
    }
}