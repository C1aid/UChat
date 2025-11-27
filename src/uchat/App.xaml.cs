using System.Windows;

namespace uchat
{
    public partial class App : Application
    {
        public static string ServerIp { get; private set; } = "127.0.0.1";
        public static int ServerPort { get; private set; } = 8080;

        private void Application_Startup(object sender, StartupEventArgs e)
        {
            if (e.Args.Length == 2)
            {
                ServerIp = e.Args[0];
                if (int.TryParse(e.Args[1], out int port))
                {
                    ServerPort = port;
                }
            }

            var loginWindow = new Views.LoginWindow();
            loginWindow.Show();
        }
    }
}