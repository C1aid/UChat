using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Uchat.Shared.DTOs;
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
        private TaskCompletionSource<string>? _pendingResponse;
        private readonly object _pendingResponseLock = new object();

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
            catch
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
                    catch (System.IO.IOException ioEx) when (ioEx.InnerException is System.Net.Sockets.SocketException socketEx && 
                                                              (socketEx.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionReset ||
                                                               socketEx.SocketErrorCode == System.Net.Sockets.SocketError.Shutdown))
                    {
                        OnConnectionLost();
                        break;
                    }
                    catch (System.Net.Sockets.SocketException socketEx) when (socketEx.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionReset ||
                                                                             socketEx.SocketErrorCode == System.Net.Sockets.SocketError.Shutdown)
                    {
                        OnConnectionLost();
                        break;
                    }
                    catch
                    {
                        if (_client == null || !_client.Connected)
                        {
                            OnConnectionLost();
                            break;
                        }
                        else
                        {
                            await Task.Delay(100);
                        }
                    }
                }
            }, _listeningCts.Token);
        }

        private void OnMessageReceived(string message)
        {
            MessageReceived?.Invoke(message);

            TaskCompletionSource<string>? pendingResponse = null;
            lock (_pendingResponseLock)
            {
                pendingResponse = _pendingResponse;
                _pendingResponse = null;
            }

            if (pendingResponse != null)
            {
                pendingResponse.TrySetResult(message);
            }
        }

        private void OnConnectionLost()
        {
            if (Application.Current?.Dispatcher != null)
            {
                _ = Application.Current.Dispatcher.InvokeAsync(() =>
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

            if (_isBackgroundListeningStarted)
            {
                var tcs = new TaskCompletionSource<string>();
                
                lock (_pendingResponseLock)
                {
                    _pendingResponse = tcs;
                }

                try
                {
                    var timeoutTask = Task.Delay(timeoutMs);
                    var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);
                    
                    if (completedTask == timeoutTask)
                    {
                        lock (_pendingResponseLock)
                        {
                            if (_pendingResponse == tcs)
                                _pendingResponse = null;
                        }
                        return null;
                    }

                    var result = await tcs.Task;
                    return result;
                }
                catch
                {
                    lock (_pendingResponseLock)
                    {
                        if (_pendingResponse == tcs)
                            _pendingResponse = null;
                    }
                    return null;
                }
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

                var result = await readTask;
                return result;
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
            catch
            {
                return false;
            }
            finally
            {
                _writeSemaphore.Release();
            }
        }

        public async Task<ApiResponse?> GetUserProfileAsync(int userId)
        {
            if (!IsConnected || _writer == null || _reader == null)
            {
                return null;
            }

            await _writeSemaphore.WaitAsync();
            try
            {
                var command = $"/getprofile {userId}";
                await _writer.WriteLineAsync(command);

                var response = await ReceiveMessageAsync(5000);
                if (string.IsNullOrEmpty(response))
                {
                    return null;
                }

                return JsonSerializer.Deserialize<ApiResponse>(response);
            }
            catch
            {
                return null;
            }
            finally
            {
                _writeSemaphore.Release();
            }
        }

        public async Task<ApiResponse?> UpdateProfileAsync(UpdateProfileRequest request)
        {
            if (!IsConnected || _writer == null || _reader == null)
            {
                return null;
            }

            await _writeSemaphore.WaitAsync();
            try
            {
                var options = new JsonSerializerOptions
                {
                    WriteIndented = false,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                
                var json = JsonSerializer.Serialize(request, options);
                
                var command = $"/updateprofile {json}";

                await _writer.WriteLineAsync(command);

                var response = await ReceiveMessageAsync(10000);
                if (string.IsNullOrEmpty(response))
                {
                    return null;
                }
                return JsonSerializer.Deserialize<ApiResponse>(response);
            }
            catch
            {
                return null;
            }
            finally
            {
                _writeSemaphore.Release();
            }
        }

        public async Task<ApiResponse?> DeleteAccountAsync()
        {
            if (!IsConnected || _writer == null || _reader == null)
            {
                return null;
            }

            await _writeSemaphore.WaitAsync();
            try
            {
                var command = "/deleteaccount";
                await _writer.WriteLineAsync(command);

                var response = await ReceiveMessageAsync(5000);
                if (string.IsNullOrEmpty(response))
                {
                    return null;
                }

                try
                {
                    return JsonSerializer.Deserialize<ApiResponse>(response);
                }
                catch
                {
                    return new ApiResponse { Success = false, Message = "Invalid response format from server" };
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                _writeSemaphore.Release();
            }
        }

        public async Task<ApiResponse?> DeleteChatAsync(int chatRoomId)
        {
            if (!IsConnected || _writer == null || _reader == null)
            {
                return null;
            }

            await _writeSemaphore.WaitAsync();
            try
            {
                var command = $"/deletechat {chatRoomId}";
                await _writer.WriteLineAsync(command);

                var response = await ReceiveMessageAsync(5000);
                if (string.IsNullOrEmpty(response))
                {
                    return null;
                }

                try
                {
                    return JsonSerializer.Deserialize<ApiResponse>(response);
                }
                catch
                {
                    return new ApiResponse { Success = false, Message = "Invalid response format from server" };
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                _writeSemaphore.Release();
            }
        }

        public async Task<ApiResponse?> UploadAvatarAsync(byte[] avatarData)
        {
            if (!IsConnected || _writer == null || _reader == null)
            {
                return null;
            }

            await _writeSemaphore.WaitAsync();
            try
            {
                var base64 = Convert.ToBase64String(avatarData);
                var command = $"/uploadavatar {base64}";

                await _writer.WriteLineAsync(command);

                var response = await ReceiveMessageAsync(5000);
                if (string.IsNullOrEmpty(response))
                {
                    return null;
                }

                return JsonSerializer.Deserialize<ApiResponse>(response);
            }
            catch
            {
                return null;
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

        private ApiResponse DeserializeApiResponse(string? json)
        {
            if (string.IsNullOrEmpty(json))
                return new ApiResponse { Success = false, Message = "No response received." };

            try
            {
                return JsonSerializer.Deserialize<ApiResponse>(json)
                    ?? new ApiResponse { Success = false, Message = "Invalid JSON response." };
            }
            catch
            {
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
            if (_stream == null || _client == null || !_client.Connected)
                return (false, "Not connected to server.");

            try
            {
                var stream = _stream!;
                byte[] commandBytes = Encoding.UTF8.GetBytes($"/download {uniqueFileName}\n");
                await stream.WriteAsync(commandBytes, 0, commandBytes.Length);
                await stream.FlushAsync();

                string? headerLine = await ReadLineFromStreamAsync(stream, 15000);

                if (string.IsNullOrEmpty(headerLine))
                {
                    return (false, "Timeout waiting for server response.");
                }

                var headerResponse = DeserializeApiResponse(headerLine);

                if (!headerResponse.Success || headerResponse.Message != "FILE_TRANSFER_START" || headerResponse.Data == null)
                {
                    return (false, headerResponse.Message ?? "Download failed.");
                }

                FileDownloadMetadata? metadata = null;
                if (headerResponse.Data is JsonElement jsonElement)
                {
                    metadata = JsonSerializer.Deserialize<FileDownloadMetadata>(jsonElement.GetRawText());
                }

                if (metadata == null)
                {
                    return (false, "Invalid metadata.");
                }

                long fileSize = metadata.FileSize;

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
                }

                return (true, "File downloaded successfully.");
            }
            catch (Exception ex)
            {
                if (File.Exists(destinationPath)) File.Delete(destinationPath);
                return (false, $"Error: {ex.Message}");
            }
        }

        public async Task<ApiResponse> EditMessageAsync(int messageId, string newContent)
        {
            if (!IsConnected || _writer == null)
            {
                return new ApiResponse { Success = false, Message = "Not connected" };
            }

            try
            {
                await _writeSemaphore.WaitAsync();
                string command = $"/edit_message {messageId} {newContent}";
                await _writer.WriteLineAsync(command);
                await _writer.FlushAsync();

                string? response = await ReceiveMessageAsync();
                if (string.IsNullOrEmpty(response))
                {
                    return new ApiResponse { Success = false, Message = "No response from server" };
                }

                return DeserializeApiResponse(response);
            }
            catch (Exception ex)
            {
                return new ApiResponse { Success = false, Message = $"Error: {ex.Message}" };
            }
            finally
            {
                _writeSemaphore.Release();
            }
        }

        public async Task<ApiResponse> DeleteMessageAsync(int messageId)
        {
            if (!IsConnected || _writer == null)
            {
                return new ApiResponse { Success = false, Message = "Not connected" };
            }

            try
            {
                await _writeSemaphore.WaitAsync();
                string command = $"/delete_message {messageId}";
                await _writer.WriteLineAsync(command);
                await _writer.FlushAsync();

                string? response = await ReceiveMessageAsync();
                if (string.IsNullOrEmpty(response))
                {
                    return new ApiResponse { Success = false, Message = "No response from server" };
                }

                return DeserializeApiResponse(response);
            }
            catch (Exception ex)
            {
                return new ApiResponse { Success = false, Message = $"Error: {ex.Message}" };
            }
            finally
            {
                _writeSemaphore.Release();
            }
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}