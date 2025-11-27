using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Windows;

namespace uchat.Services
{
    public class NetworkClient
    {
        // 1. Добавляем '?' - теперь эти поля могут быть null, если мы не подключены
        private TcpClient? _client;
        private StreamReader? _reader;
        private StreamWriter? _writer;

        public bool IsConnected => _client != null && _client.Connected;

        public async Task<bool> ConnectAsync(string ip, int port)
        {
            try
            {
                _client = new TcpClient();
                await _client.ConnectAsync(ip, port);
                
                var stream = _client.GetStream();
                _reader = new StreamReader(stream, Encoding.UTF8);
                _writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
                
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка подключения: {ex.Message}");
                return false;
            }
        }

        public async Task SendMessageAsync(string message)
        {
            // Проверяем _writer на null перед использованием
            if (IsConnected && _writer != null)
            {
                await _writer.WriteLineAsync(message);
            }
        }
        
        // 2. Изменяем Task<string> на Task<string?> (разрешаем возвращать null)
        public async Task<string?> ReceiveMessageAsync()
        {
            if (!IsConnected || _reader == null) return null;
            
            return await _reader.ReadLineAsync();
        }
    }
}