using System.Windows;
using System.Windows.Input;
using uchat.Services;
using System.Text.Json;
using System.Collections.ObjectModel;
using Uchat.Shared.DTOs;

namespace uchat
{
    public partial class MainWindow : Window
    {
        private readonly NetworkClient _network;
        private int _currentRoomId = 0;
        private bool _isClosing = false;
        public ObservableCollection<MessageDto> ChatMessages { get; set; } = new ObservableCollection<MessageDto>();

        public MainWindow(NetworkClient network)
        {
            InitializeComponent();
            _network = network;
            ListenForMessages();
        }

        private void SendButton_Click(object sender, RoutedEventArgs e)
        {
            SendMessage();
        }
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

            // СЦЕНАРИЙ 1: Это команда (например: /chat <user>)
            if (text.StartsWith("/"))
            {
                await _network.SendMessageAsync(text);
            }
            // СЦЕНАРИЙ 2: Это обычное сообщение
            else
            {
                if (_currentRoomId == 0)
                {
                    AddSystemMessage("Сначала начните диалог командой: /chat <username>");
                    MessageInput.Clear();
                    return;
                }

                // Упаковываем в JSON с ID комнаты
                var dto = new ClientMessageDto
                {
                    Content = text,
                    RoomId = _currentRoomId
                };

                string json = JsonSerializer.Serialize(dto);
                await _network.SendMessageAsync(json);
            }

            MessageInput.Clear();
        }

        // --- ПОЛУЧЕНИЕ СООБЩЕНИЙ (САМОЕ ВАЖНОЕ) ---

        private async void ListenForMessages()
        {
            try
            {
                while (!_isClosing)
                {
                    // 1. Ждем строку от сервера (await не вешает интерфейс)
                    string? json = await _network.ReceiveMessageAsync();

                    // Если пришел null, значит сервер отключился
                    if (json == null) 
                    {
                        AddSystemMessage("Потеряно соединение с сервером.");
                        break; 
                    }

                    try 
                    {
                        // ЛОГИКА РАСПОЗНАВАНИЯ: Что нам прислали?

                        // ВАРИАНТ А: Это сервисный ответ (ApiResponse)
                        // (Обычно содержит поля "Success" и "Message")
                        if (json.Contains("\"Success\":"))
                        {
                            var response = JsonSerializer.Deserialize<ApiResponse>(json);
                            if (response != null)
                            {
                                if (response.Success)
                                {
                                    // Особый случай: Сервер сообщил, что чат создан.
                                    // Нам нужно достать RoomId из поля Data.
                                    if (response.Message.StartsWith("Chat started") && response.Data is JsonElement data)
                                    {
                                        if (data.TryGetProperty("RoomId", out var idProp))
                                        {
                                            _currentRoomId = idProp.GetInt32();
                                            
                                            // Очищаем экран для нового чата
                                            Dispatcher.Invoke(() => ChatMessages.Clear());
                                            
                                            AddSystemMessage($"--- Вы перешли в чат #{_currentRoomId} ---");
                                        }
                                    }
                                    else
                                    {
                                        // Просто успешное действие (например, "Message sent") - можно игнорировать или логировать
                                        // AddSystemMessage($"[Инфо]: {response.Message}");
                                    }
                                }
                                else
                                {
                                    // Сервер вернул ошибку (Success = false)
                                    AddSystemMessage($"ОШИБКА: {response.Message}");
                                }
                            }
                        }
                        // ВАРИАНТ Б: Это сообщение чата (MessageDto)
                        // (Обычно содержит "Username" и "Content")
                        else 
                        {
                            var msg = JsonSerializer.Deserialize<MessageDto>(json);
                            if (msg != null)
                            {
                                // Важно: проверяем, для этой ли комнаты сообщение?
                                // (Если вы оставили серверную фильтрацию, это не обязательно, но полезно для надежности)
                                /*
                                if (msg.ChatRoomId != 0 && msg.ChatRoomId != _currentRoomId) 
                                    return; // Игнорируем сообщения из других чатов
                                */

                                AddMessageToUi(msg);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Если пришел битый JSON
                        // AddSystemMessage($"Ошибка обработки: {ex.Message}");
                    }
                }
            }
            catch
            {
                if (!_isClosing) AddSystemMessage("Ошибка сети.");
            }
        }

        // Вспомогательный метод для добавления сообщений в список (строго в UI потоке)
        private void AddMessageToUi(MessageDto msg)
        {
            Dispatcher.Invoke(() => 
            {
                ChatMessages.Add(msg);
                
                // Автопрокрутка вниз
                if (MessagesList.Items.Count > 0)
                {
                    MessagesList.ScrollIntoView(MessagesList.Items[MessagesList.Items.Count - 1]);
                }
            });
        }

        // Вспомогательный метод для системных сообщений (ошибки, инфо)
        private void AddSystemMessage(string text)
        {
            AddMessageToUi(new MessageDto 
            { 
                Username = "СИСТЕМА", 
                Content = text, 
                SentAt = DateTime.Now 
            });
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