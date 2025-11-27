using System.Windows;
using uchat.Services; // Не забудьте подключить namespace

namespace uchat.Views
{
    public partial class LoginWindow : Window
    {
        // Создаем нашего связиста
        private NetworkClient _network = new NetworkClient();

        public LoginWindow()
        {
            InitializeComponent();
        }

        private async void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            string username = UsernameBox.Text;
            string password = PasswordBox.Password;

            // 1. Сначала пробуем подключиться (берем IP и порт из глобальных настроек App.xaml.cs)

            bool connected = await _network.ConnectAsync(App.ServerIp, App.ServerPort);

            if (connected)
            {
                // 2. Если подключились — отправляем команду логина
                // Формат команды должен совпадать с тем, что ждет сервер!
                // Например: "/login name password"
                await _network.SendMessageAsync($"/login {username} {password}");

                // 3. Тут можно ждать ответа от сервера (успешно или нет)
                // string response = await _network.ReceiveMessageAsync();
                
                // 4. Пока просто переходим в чат для теста
                MainWindow chatWindow = new MainWindow(_network);
                chatWindow.Show();
                this.Close();
            }
        }
    }
}