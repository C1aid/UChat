using System.Windows;
using System.Windows.Input;
using uchat.Services;

namespace uchat
{
    public partial class MainWindow : Window
    {
        private readonly NetworkClient _network;
        private bool _isClosing = false; // Флаг, чтобы корректно выйти

        // Конструктор принимает УЖЕ ПОДКЛЮЧЕННОГО клиента
        public MainWindow(NetworkClient network)
        {
            InitializeComponent();
            _network = network;

            // Запускаем прослушку входящих сообщений сразу при старте окна
            ListenForMessages();
        }

        // --- ОТПРАВКА СООБЩЕНИЙ ---

        private void SendButton_Click(object sender, RoutedEventArgs e)
        {
            SendMessage();
        }

        // Чтобы отправлять по нажатию Enter
        private void MessageInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                SendMessage();
            }
        }

        private async void SendMessage()
        {
            string text = MessageInput.Text;
            if (string.IsNullOrWhiteSpace(text)) return;

            // 1. Отправляем на сервер
            await _network.SendMessageAsync(text);

            // 2. Очищаем поле ввода
            MessageInput.Clear();
            
            // ВАЖНО: Мы НЕ добавляем свое сообщение в список вручную.
            // Хороший чат ждет, пока сервер вернет сообщение обратно (эхо), 
            // либо сервер разошлет его всем, включая нас.
            // Но пока можно добавить визуально для себя:
            // AddMessageToChat($"Я: {text}"); 
        }

        // --- ПОЛУЧЕНИЕ СООБЩЕНИЙ (САМОЕ ВАЖНОЕ) ---

        private async void ListenForMessages()
        {
            try
            {
                while (!_isClosing)
                {
                    // Ждем сообщение от сервера (это не блокирует окно, т.к. await)
                    string? message = await _network.ReceiveMessageAsync();

                    if (message == null) 
                    {
                        // Если пришел null, значит сервер разорвал соединение
                        AddMessageToChat("[СИСТЕМА]: Связь с сервером потеряна.");
                        break; 
                    }

                    // Добавляем полученный текст в список
                    AddMessageToChat(message);
                }
            }
            catch (Exception ex)
            {
                if (!_isClosing)
                    AddMessageToChat($"[ОШИБКА]: {ex.Message}");
            }
        }

        // Метод для безопасного обновления интерфейса
        private void AddMessageToChat(string message)
        {
            // WPF запрещает менять интерфейс из чужого потока.
            // Dispatcher.Invoke говорит: "Эй, главное окно, сделай это сам, когда освободишься"
            Dispatcher.Invoke(() => 
            {
                MessagesList.Items.Add(message);
                // Автопрокрутка вниз
                MessagesList.ScrollIntoView(MessagesList.Items[MessagesList.Items.Count - 1]);
            });
        }

        // Когда окно закрывается - останавливаем всё
        protected override void OnClosed(EventArgs e)
        {
            _isClosing = true;
            base.OnClosed(e);
            Application.Current.Shutdown(); // Полностью убиваем приложение
        }
    }
}