using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace uchat.Services
{
    public class NetworkClient : IDisposable
    {
        private TcpClient? _client;
        private NetworkStream? _stream;
        private StreamReader? _reader;
        private StreamWriter? _writer;
        private bool _isListening = false;
        private CancellationTokenSource? _listeningCts;
        private bool _isBackgroundListeningStarted = false;
        private SemaphoreSlim _writeSemaphore = new SemaphoreSlim(1, 1);
        private SemaphoreSlim _connectSemaphore = new SemaphoreSlim(1, 1);

        public bool IsConnected => _client != null && _client.Connected;

        public event Action<string>? MessageReceived;
        public event Action? ConnectionLost;

        public async Task<bool> ConnectAsync(string ip, int port)
        {
            await _connectSemaphore.WaitAsync();

            try
            {
                if (_client != null && _client.Connected)
                {
                    return true;
                }

                _client = new TcpClient();
                await _client.ConnectAsync(ip, port);

                _stream = _client.GetStream();
                _reader = new StreamReader(_stream, Encoding.UTF8);
                _writer = new StreamWriter(_stream, Encoding.UTF8) { AutoFlush = true };

                return true;
            }
            catch (Exception ex)
            {
                _client?.Close();
                _client = null;
                return false;
            }
            finally
            {
                _connectSemaphore.Release();
            }
        }

        public void StartBackgroundListening()
        {
            if (_reader == null || _isBackgroundListeningStarted) return;

            _isBackgroundListeningStarted = true;
            _isListening = true;
            _listeningCts = new CancellationTokenSource();

            Task.Run(async () =>
            {
                while (_isListening && _client?.Connected == true && !_listeningCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        var message = await _reader.ReadLineAsync();

                        if (message == null)
                        {
                            OnConnectionLost();
                            break;
                        }

                        OnMessageReceived(message);
                    }
                    catch (Exception)
                    {
                        OnConnectionLost();
                        break;
                    }
                }
            }, _listeningCts.Token);
        }

        private void OnMessageReceived(string message)
        {
            MessageReceived?.Invoke(message);
        }

        private void OnConnectionLost()
        {
            if (Application.Current?.Dispatcher != null)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    ConnectionLost?.Invoke();
                });
            }
            else
            {
                ConnectionLost?.Invoke();
            }
        }

        public async Task<string?> ReceiveMessageAsync(int timeoutMs = 10000)
        {
            if (!IsConnected || _reader == null)
            {
                return null;
            }

            try
            {
                var readTask = _reader.ReadLineAsync();
                var timeoutTask = Task.Delay(timeoutMs);

                var completedTask = await Task.WhenAny(readTask, timeoutTask);
                if (completedTask == timeoutTask)
                {
                    return null;
                }

                return await readTask;
            }
            catch
            {
                return null;
            }
        }

        public async Task<bool> SendMessageAsync(string message)
        {
            if (!IsConnected || _writer == null)
            {
                return false;
            }

            await _writeSemaphore.WaitAsync();
            try
            {
                await _writer.WriteLineAsync(message);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                _writeSemaphore.Release();
            }
        }

        public void Disconnect()
        {
            try
            {
                _isListening = false;
                _listeningCts?.Cancel();
                _isBackgroundListeningStarted = false;

                _writer?.Close();
                _reader?.Close();
                _client?.Close();

                _writer?.Dispose();
                _reader?.Dispose();
                _client?.Dispose();

                _writer = null;
                _reader = null;
                _client = null;
                _stream = null;
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}