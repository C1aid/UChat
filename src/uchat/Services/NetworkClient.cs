using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Uchat.Shared.DTOs;
using System.Text.Json;
using Uchat.Shared.Enums;

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
        private Task? _listeningTask; 
        private volatile bool _isConnectionActive = false;
        public bool IsConnected => _isConnectionActive && _client != null && _stream != null;

        public event Action<string>? MessageReceived;
        public event Action? ConnectionLost;

        public async Task<bool> ConnectAsync(string ip, int port)
        {
            await _connectSemaphore.WaitAsync();

            try
            {
                if (_isConnectionActive && _client != null && _stream != null)
                {
                    return true;
                }

                _client = new TcpClient();
                await _client.ConnectAsync(ip, port);

                _stream = _client.GetStream();
                _reader = new StreamReader(_stream, Encoding.UTF8, leaveOpen: true);
                _writer = new StreamWriter(_stream, Encoding.UTF8, bufferSize: 1024, leaveOpen: true) { AutoFlush = true };
                _isConnectionActive = true;

                return true;
            }
            catch (Exception)
            {
                _client?.Close();
                _client = null;
                _isConnectionActive = false;
                return false;
            }
            finally
            {
                _connectSemaphore.Release();
            }
        }

        public void StartBackgroundListening()
        {
            Console.WriteLine($"[Listener] StartBackgroundListening called. _reader={_reader != null}, _isBackgroundListeningStarted={_isBackgroundListeningStarted}");
            
            if (_reader == null || _isBackgroundListeningStarted) return;

            _isBackgroundListeningStarted = true;
            _isListening = true;
            _listeningCts = new CancellationTokenSource();

            _listeningTask = Task.Run(async () =>
            {
                Console.WriteLine($"[Listener] Background task started");
                
                try
                {
                    
                    while (_isListening && !_listeningCts.Token.IsCancellationRequested)
                    {
                        if (_reader == null)
                        {
                            await Task.Delay(50, _listeningCts.Token);
                            continue;
                        }

                        Console.WriteLine($"[Listener] Attempting to read line...");

                        string? message;
                        try
                        {
                            message = await _reader.ReadLineAsync(_listeningCts.Token);
                        }
                        catch (ObjectDisposedException)
                        {
                            await Task.Delay(50, _listeningCts.Token);
                            continue;
                        }
                        
                        Console.WriteLine($"[Listener] Read result: {(message == null ? "NULL" : message.Substring(0, Math.Min(50, message.Length)))}");
                        
                        if (message == null)
                        {
                            Console.WriteLine($"[Listener] Received NULL, calling OnConnectionLost");
                            OnConnectionLost();
                            break;
                        }

                        OnMessageReceived(message);
                    }
                    
                    Console.WriteLine($"[Listener] Exited while loop. _isListening={_isListening}");
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine($"[Listener] Cancelled");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Listener] Exception: {ex.Message}");
                    if (_isListening)
                    {
                        OnConnectionLost();
                    }
                }
            }, _listeningCts.Token);
        }

        private async Task StopListeningAsync()
        {
            Console.WriteLine($"[StopListening] Called. _isListening={_isListening}, _listeningCts={_listeningCts != null}");
            Console.WriteLine($"[StopListening] Stack trace: {Environment.StackTrace}");
            
            if (_isListening && _listeningCts != null)
            {
                _isListening = false;
                
                _listeningCts.Cancel();

                if (_listeningTask != null && !_listeningTask.IsCompleted)
                {
                    try
                    {
                        await _listeningTask;
                    }
                    catch (Exception) 
                    {
                    }
                }
                
                _listeningCts.Dispose();
                _listeningCts = null;
                _listeningTask = null;
                _isBackgroundListeningStarted = false;
            }
            
            Console.WriteLine($"[StopListening] Completed");
        }

        private void OnMessageReceived(string message)
        {
            MessageReceived?.Invoke(message);
        }

        private void OnConnectionLost()
        {
            Console.WriteLine($"[OnConnectionLost] Called");
            Console.WriteLine($"[OnConnectionLost] Stack trace: {Environment.StackTrace}");
            _isConnectionActive = false;
            
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
                using (var cts = new CancellationTokenSource(timeoutMs))
                {
                    var readTask = _reader.ReadLineAsync(cts.Token);
                    return await readTask;
                }
            }
            catch (OperationCanceledException)
            {
                return null;
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
                _isConnectionActive = false;

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

        private ApiResponse DeserializeApiResponse(string? json)
        {
            if (string.IsNullOrEmpty(json)) 
                return new ApiResponse { Success = false, Message = "No response received." };

            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<ApiResponse>(json)
                    ?? new ApiResponse { Success = false, Message = "Invalid JSON response." };
            }
            catch { 
                return new ApiResponse { Success = false, Message = "Failed to parse JSON." };
            }
        }

        private async Task<string?> ReadLineFromStreamAsync(NetworkStream stream, int timeoutMs = 10000)
        {
            var buffer = new List<byte>(256);
            var readBuffer = new byte[256];
            var cts = new CancellationTokenSource(timeoutMs);

            try
            {
                int totalRead = 0;
                while (totalRead < 8192) 
                {
                    int bytesRead = await stream.ReadAsync(readBuffer, 0, 1, cts.Token);
                    if (bytesRead == 0) return null;

                    byte b = readBuffer[0];
                    totalRead++;

                    if (b == '\n')
                    {
                        break;
                    }

                    if (b != '\r')
                    {
                        buffer.Add(b);
                    }
                }

                return Encoding.UTF8.GetString(buffer.ToArray());
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch
            {
                return null;
            }
        }



        public async Task<(bool Success, string Message)> DownloadFileAsync(string uniqueFileName, string destinationPath)
        {
            if (!_isConnectionActive || _stream == null)
                return (false, "Not connected to server.");

            try
            {
                await StopListeningAsync();
                Console.WriteLine($"[Download] Background listener stopped");

                var stream = _stream!;
                _reader = null;
                _writer = null;

                byte[] commandBytes = Encoding.UTF8.GetBytes($"/download {uniqueFileName}\n");
                await stream.WriteAsync(commandBytes, 0, commandBytes.Length);
                await stream.FlushAsync();
                Console.WriteLine($"[Download] Command sent, waiting for header...");

                string? headerLine = await ReadLineFromStreamAsync(stream, 15000);
                Console.WriteLine($"[Download] Header received: {headerLine}");

                if (string.IsNullOrEmpty(headerLine))
                {
                    RecreateStreamsAndRestart(stream);
                    return (false, "Timeout waiting for server response.");
                }

                var headerResponse = DeserializeApiResponse(headerLine);

                if (!headerResponse.Success || headerResponse.Message != "FILE_TRANSFER_START" || headerResponse.Data == null)
                {
                    RecreateStreamsAndRestart(stream);
                    return (false, headerResponse.Message ?? "Download failed.");
                }

                FileDownloadMetadata? metadata = null;
                if (headerResponse.Data is JsonElement jsonElement)
                {
                    metadata = JsonSerializer.Deserialize<FileDownloadMetadata>(jsonElement.GetRawText());
                }

                if (metadata == null)
                {
                    RecreateStreamsAndRestart(stream);
                    return (false, "Invalid metadata.");
                }

                long fileSize = metadata.FileSize;
                Console.WriteLine($"[Download] Starting to read {fileSize} bytes");

                using (var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write))
                {
                    byte[] buffer = new byte[81920];
                    long totalBytesRead = 0;
                    int bytesRead;

                    while (totalBytesRead < fileSize)
                    {
                        int bytesToRead = (int)Math.Min(buffer.Length, fileSize - totalBytesRead);
                        bytesRead = await stream.ReadAsync(buffer, 0, bytesToRead);
                        
                        if (bytesRead == 0) throw new IOException("Connection lost during download.");

                        await fileStream.WriteAsync(buffer, 0, bytesRead);
                        totalBytesRead += bytesRead;
                    }
                    Console.WriteLine($"[Download] Finished reading {totalBytesRead} bytes");
                }

                await Task.Delay(100);

                RecreateStreamsAndRestart(stream);

                return (true, "File downloaded successfully.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Download] Exception: {ex.Message}");
                if (File.Exists(destinationPath)) File.Delete(destinationPath);
                
                if (_stream != null)
                {
                    try { RecreateStreamsAndRestart(_stream); } catch { }
                }
                
                return (false, $"Error: {ex.Message}");
            }
        }

        private void RecreateStreamsAndRestart(NetworkStream stream)
        {
            Console.WriteLine($"[Download] Recreating streams...");
            _reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            _writer = new StreamWriter(stream, Encoding.UTF8, bufferSize: 1024, leaveOpen: true) { AutoFlush = true };
            Console.WriteLine($"[Download] Streams recreated, restarting listener...");
            StartBackgroundListening();
            Console.WriteLine($"[Download] Listener restarted");
        }

        public async Task<ApiResponse> SendFileMessageAsync(int roomId, string filePath, MessageType messageType)
        {
            if (!_isConnectionActive || _writer == null)
            {
                return new ApiResponse { Success = false, Message = "Not connected to server." };
            }
            if (!File.Exists(filePath))
            {
                return new ApiResponse { Success = false, Message = "Local file not found." };
            }

            try
            {
                FileInfo fileInfo = new FileInfo(filePath);
                long fileSize = fileInfo.Length;
                string fileName = fileInfo.Name;
                
                string command = $"/upload_file {roomId} {fileName.Replace(" ", "_")} {fileSize} {messageType.ToString()}";
                
                await _writeSemaphore.WaitAsync();
                try
                {
                    await _writer.WriteLineAsync(command);
                    await _writer.FlushAsync();
                }
                finally
                {
                    _writeSemaphore.Release();
                }

                var tcs = new TaskCompletionSource<ApiResponse>();
                var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                
                void readyHandler(string msg)
                {
                    try
                    {
                        var resp = DeserializeApiResponse(msg);
                        if (resp.Message?.Contains("Ready") == true || resp.Success)
                        {
                            tcs.TrySetResult(resp);
                        }
                    }
                    catch { }
                }

                MessageReceived += readyHandler;
                cts.Token.Register(() => tcs.TrySetCanceled());

                try
                {
                    var readyResponse = await tcs.Task;
                    if (!readyResponse.Success)
                    {
                        return readyResponse;
                    }
                }
                finally
                {
                    MessageReceived -= readyHandler;
                    cts.Dispose();
                }

                await StopListeningAsync();

                var networkStream = _stream!;
                _reader = null;
                _writer = null;

                using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                {
                    byte[] buffer = new byte[81920];
                    int bytesRead;

                    while ((bytesRead = await fileStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await networkStream.WriteAsync(buffer, 0, bytesRead);
                    }
                    await networkStream.FlushAsync();
                }

                string? completionLine = await ReadLineFromStreamAsync(networkStream, 60000);
                RecreateStreamsAndRestart(networkStream);

                if (string.IsNullOrEmpty(completionLine))
                {
                    return new ApiResponse { Success = false, Message = "No confirmation from server." };
                }

                return DeserializeApiResponse(completionLine);
            }
            catch (OperationCanceledException)
            {
                return new ApiResponse { Success = false, Message = "Upload timeout" };
            }
            catch (Exception ex)
            {
                return new ApiResponse { Success = false, Message = $"Upload error: {ex.Message}" };
            }
        }

        
    }
}