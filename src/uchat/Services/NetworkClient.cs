using System;
using System.Collections.Concurrent;
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
        private readonly ConcurrentDictionary<string, FileDownloadSession> _fileDownloads = new();
        private readonly JsonSerializerOptions _responseJsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        // Reconnection support
        private string? _serverIp;
        private int _serverPort;
        private string? _savedUsername;
        private string? _savedPassword;
        private bool _isReconnecting = false;
        private bool _autoReconnectEnabled = false;
        private CancellationTokenSource? _reconnectCts;
        private readonly object _reconnectLock = new object();

        public bool IsConnected => _client != null && _client.Connected;
        public bool IsReconnecting => _isReconnecting;

        public event Action<string>? MessageReceived;
        public event Action? ConnectionLost;
        public event Action? Reconnecting;
        public event Action? Reconnected;

        public async Task<bool> ConnectAsync(string ip, int port, string? username = null, string? password = null)
        {
            await _connectSemaphore.WaitAsync();

            try
            {
                if (_client != null && _client.Connected)
                {
                    return true;
                }

                // Save connection info for reconnection
                _serverIp = ip;
                _serverPort = port;
                if (!string.IsNullOrEmpty(username))
                    _savedUsername = username;
                if (!string.IsNullOrEmpty(password))
                    _savedPassword = password;

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

        public void EnableAutoReconnect(bool enable)
        {
            _autoReconnectEnabled = enable;
        }

        public void SaveCredentials(string username, string password)
        {
            _savedUsername = username;
            _savedPassword = password;
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
            if (TryHandleFileTransferMessage(message))
            {
                return;
            }

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
            // Stop listening
            _isListening = false;
            _listeningCts?.Cancel();
            _isBackgroundListeningStarted = false;

            // Clean up connection
            try
            {
                _writer?.Close();
                _reader?.Close();
                _client?.Close();
                _writer?.Dispose();
                _reader?.Dispose();
                _client?.Dispose();
            }
            catch { }

            _writer = null;
            _reader = null;
            _stream = null;
            _client = null;

            // Notify about connection loss
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

            // Start reconnection if enabled
            if (_autoReconnectEnabled && !string.IsNullOrEmpty(_serverIp) && _serverPort > 0)
            {
                _ = Task.Run(async () => await AttemptReconnectionAsync());
            }
        }

        private async Task AttemptReconnectionAsync()
        {
            lock (_reconnectLock)
            {
                if (_isReconnecting)
                    return;
                _isReconnecting = true;
            }

            _reconnectCts = new CancellationTokenSource();

            try
            {
                // Notify about reconnection attempt
                if (Application.Current?.Dispatcher != null)
                {
                    _ = Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        Reconnecting?.Invoke();
                    });
                }
                else
                {
                    Reconnecting?.Invoke();
                }

                const int maxAttempts = 10;
                const int delayBetweenAttempts = 3000; // 3 seconds
                int attempt = 0;

                while (attempt < maxAttempts && !_reconnectCts.Token.IsCancellationRequested)
                {
                    attempt++;

                    // Try to reconnect
                    bool connected = await ConnectAsync(_serverIp!, _serverPort);
                    
                    if (connected)
                    {
                        // Re-authenticate if credentials are saved
                        if (!string.IsNullOrEmpty(_savedUsername) && !string.IsNullOrEmpty(_savedPassword))
                        {
                            await SendMessageAsync($"/login {_savedUsername} {_savedPassword}");
                            
                            // Wait for login response (skip welcome message)
                            int responseCount = 0;
                            while (responseCount < 5)
                            {
                                var response = await ReceiveMessageAsync(2000);
                                if (string.IsNullOrEmpty(response))
                                    break;

                                try
                                {
                                    var apiResponse = JsonSerializer.Deserialize<ApiResponse>(response, _responseJsonOptions);
                                    if (apiResponse != null && apiResponse.Message != "Welcome to Uchat! Use /help for commands")
                                    {
                                        if (apiResponse.Success)
                                        {
                                            // Login successful, restart listening
                                            StartBackgroundListening();
                                            
                                            // Notify about successful reconnection
                                            if (Application.Current?.Dispatcher != null)
                                            {
                                                _ = Application.Current.Dispatcher.InvokeAsync(() =>
                                                {
                                                    Reconnected?.Invoke();
                                                });
                                            }
                                            else
                                            {
                                                Reconnected?.Invoke();
                                            }

                                            lock (_reconnectLock)
                                            {
                                                _isReconnecting = false;
                                            }
                                            return;
                                        }
                                        else
                                        {
                                            // Login failed, break and retry
                                            break;
                                        }
                                    }
                                }
                                catch { }

                                responseCount++;
                            }
                        }
                        else
                        {
                            // No credentials, just restart listening
                            StartBackgroundListening();
                            
                            if (Application.Current?.Dispatcher != null)
                            {
                                _ = Application.Current.Dispatcher.InvokeAsync(() =>
                                {
                                    Reconnected?.Invoke();
                                });
                            }
                            else
                            {
                                Reconnected?.Invoke();
                            }

                            lock (_reconnectLock)
                            {
                                _isReconnecting = false;
                            }
                            return;
                        }
                    }

                    // If we get here, connection failed or login failed
                    // Wait before next attempt
                    if (attempt < maxAttempts)
                    {
                        await Task.Delay(delayBetweenAttempts, _reconnectCts.Token);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Reconnection was cancelled
            }
            catch
            {
                // Ignore errors during reconnection
            }
            finally
            {
                lock (_reconnectLock)
                {
                    _isReconnecting = false;
                }
            }
        }

        public void CancelReconnection()
        {
            _reconnectCts?.Cancel();
            lock (_reconnectLock)
            {
                _isReconnecting = false;
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

        public async Task<ApiResponse> UploadFileAsync(int chatRoomId, string filePath, MessageType messageType)
        {
            if (!IsConnected || _writer == null || _stream == null)
            {
                return new ApiResponse { Success = false, Message = "Нет подключения к серверу" };
            }

            if (!File.Exists(filePath))
            {
                return new ApiResponse { Success = false, Message = "Файл не найден" };
            }

            await _writeSemaphore.WaitAsync();
            try
            {
                var fileInfo = new FileInfo(filePath);
                var sanitizedName = SanitizeFileName(fileInfo.Name);
                var command = $"/upload_file {chatRoomId} {sanitizedName} {fileInfo.Length} {messageType}";

                await _writer.WriteLineAsync(command);

                var readyResponse = DeserializeApiResponse(await ReceiveMessageAsync(10000));
                if (!readyResponse.Success)
                {
                    return readyResponse;
                }

                using (var fileStream = File.OpenRead(filePath))
                {
                    byte[] buffer = new byte[81920];
                    int bytesRead;
                    while ((bytesRead = await fileStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await _stream.WriteAsync(buffer, 0, bytesRead);
                    }
                }

                var completionResponse = DeserializeApiResponse(await ReceiveMessageAsync(30000));
                return completionResponse;
            }
            catch (Exception ex)
            {
                return new ApiResponse { Success = false, Message = ex.Message };
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

        public async Task<(bool Success, string Message, byte[]? Data)> DownloadFileInlineAsync(string uniqueFileName)
        {
            if (!IsConnected || _writer == null)
            {
                return (false, "Нет подключения", null);
            }

            await _writeSemaphore.WaitAsync();
            try
            {
                var command = $"/download_inline {uniqueFileName}";
                await _writer.WriteLineAsync(command);

                var responseJson = await ReceiveMessageAsync(15000);
                if (string.IsNullOrEmpty(responseJson))
                {
                    return (false, "Нет ответа от сервера", null);
                }

                var response = DeserializeApiResponse(responseJson);
                if (!response.Success)
                {
                    return (false, response.Message ?? "Загрузка отклонена", null);
                }

                if (response.Data is JsonElement element)
                {
                    if (element.TryGetProperty("Data", out var dataProp))
                    {
                        var base64 = dataProp.GetString();
                        if (!string.IsNullOrWhiteSpace(base64))
                        {
                            return (true, response.Message ?? string.Empty, Convert.FromBase64String(base64));
                        }
                    }
                    else if (element.ValueKind == JsonValueKind.String)
                    {
                        var base64 = element.GetString();
                        if (!string.IsNullOrWhiteSpace(base64))
                        {
                            return (true, response.Message ?? string.Empty, Convert.FromBase64String(base64));
                        }
                    }
                }

                return (false, "Неверный формат ответа сервера", null);
            }
            catch (Exception ex)
            {
                return (false, ex.Message, null);
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
                // Disable auto-reconnect when manually disconnecting
                _autoReconnectEnabled = false;
                CancelReconnection();

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

        private bool TryHandleFileTransferMessage(string message)
        {
            ApiResponse? response;
            try
            {
                response = JsonSerializer.Deserialize<ApiResponse>(message, _responseJsonOptions);
            }
            catch
            {
                return false;
            }

            if (response == null || string.IsNullOrWhiteSpace(response.Message))
            {
                return false;
            }

            return response.Message switch
            {
                "FILE_TRANSFER_START" => HandleFileTransferStart(response),
                "FILE_TRANSFER_CHUNK" => HandleFileTransferChunk(response),
                "FILE_TRANSFER_COMPLETE" => HandleFileTransferComplete(response),
                "FILE_TRANSFER_FAILED" => HandleFileTransferFailed(response),
                _ => false
            };
        }

        private bool HandleFileTransferStart(ApiResponse response)
        {
            if (response.Data is not JsonElement element)
            {
                return true;
            }

            FileDownloadMetadata? metadata;
            try
            {
                metadata = JsonSerializer.Deserialize<FileDownloadMetadata>(element.GetRawText(), _responseJsonOptions);
            }
            catch
            {
                return true;
            }

            if (metadata == null || string.IsNullOrWhiteSpace(metadata.FileName))
            {
                return true;
            }

            if (!_fileDownloads.TryGetValue(metadata.FileName, out var session))
            {
                return true;
            }

            try
            {
                session.Initialize(metadata.FileSize);
            }
            catch (Exception ex)
            {
                FinalizeDownloadSession(metadata.FileName, session, false, $"Не удалось подготовить файл: {ex.Message}");
            }

            return true;
        }

        private bool HandleFileTransferChunk(ApiResponse response)
        {
            if (response.Data is not JsonElement element)
            {
                return true;
            }

            FileChunkDto? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<FileChunkDto>(element.GetRawText(), _responseJsonOptions);
            }
            catch
            {
                return true;
            }

            if (chunk == null || string.IsNullOrWhiteSpace(chunk.FileName))
            {
                return true;
            }

            if (!_fileDownloads.TryGetValue(chunk.FileName, out var session))
            {
                return true;
            }

            try
            {
                session.WriteChunk(chunk);
            }
            catch (Exception ex)
            {
                FinalizeDownloadSession(chunk.FileName, session, false, $"Ошибка записи файла: {ex.Message}");
            }

            return true;
        }

        private bool HandleFileTransferComplete(ApiResponse response)
        {
            if (response.Data is not JsonElement element)
            {
                return true;
            }

            string? fileName = null;
            try
            {
                if (element.ValueKind == JsonValueKind.String)
                {
                    fileName = element.GetString();
                }
                else if (element.TryGetProperty("FileName", out var nameProp))
                {
                    fileName = nameProp.GetString();
                }
            }
            catch
            {
                fileName = null;
            }

            if (string.IsNullOrWhiteSpace(fileName))
            {
                return true;
            }

            if (!_fileDownloads.TryGetValue(fileName, out var session))
            {
                return true;
            }

            FinalizeDownloadSession(fileName, session, true, "Файл загружен успешно.");
            return true;
        }

        private bool HandleFileTransferFailed(ApiResponse response)
        {
            string? fileName = null;
            string? reason = response.Message;

            if (response.Data is JsonElement element)
            {
                try
                {
                    if (element.ValueKind == JsonValueKind.String)
                    {
                        fileName = element.GetString();
                    }
                    else
                    {
                        if (element.TryGetProperty("FileName", out var fileProp))
                        {
                            fileName = fileProp.GetString();
                        }
                        if (element.TryGetProperty("Reason", out var reasonProp))
                        {
                            reason = reasonProp.GetString();
                        }
                    }
                }
                catch
                {
                }
            }

            if (string.IsNullOrWhiteSpace(fileName))
            {
                return true;
            }

            if (!_fileDownloads.TryGetValue(fileName, out var session))
            {
                return true;
            }

            FinalizeDownloadSession(fileName, session, false, reason ?? "Загрузка отменена сервером.");
            return true;
        }

        private void FinalizeDownloadSession(string fileName, FileDownloadSession session, bool success, string message)
        {
            if (_fileDownloads.TryRemove(fileName, out var current) && !ReferenceEquals(current, session))
            {
                current.Dispose();
                session = current;
            }
            else
            {
                _fileDownloads.TryRemove(fileName, out _);
            }

            if (success)
            {
                session.Complete(message);
            }
            else
            {
                session.Fail(message);
            }
        }

        private sealed class FileDownloadSession : IDisposable
        {
            public string FileName { get; }
            public string DestinationPath { get; }
            public TaskCompletionSource<(bool Success, string Message)> Completion { get; } =
                new TaskCompletionSource<(bool Success, string Message)>(TaskCreationOptions.RunContinuationsAsynchronously);

            private FileStream? _stream;
            private long _expectedSize;
            private long _received;

            public FileDownloadSession(string fileName, string destinationPath)
            {
                FileName = fileName;
                DestinationPath = destinationPath;
            }

            public void Initialize(long expectedBytes)
            {
                if (_stream != null)
                {
                    return;
                }

                _expectedSize = expectedBytes;

                var directory = Path.GetDirectoryName(DestinationPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                _stream = new FileStream(DestinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
                _received = 0;
            }

            public void WriteChunk(FileChunkDto chunk)
            {
                if (_stream == null)
                {
                    throw new InvalidOperationException("Download stream is not initialized yet.");
                }

                if (string.IsNullOrWhiteSpace(chunk.Data))
                {
                    return;
                }

                var buffer = Convert.FromBase64String(chunk.Data);
                _stream.Write(buffer, 0, buffer.Length);
                _received += buffer.Length;
            }

            public void Complete(string message)
            {
                try
                {
                    _stream?.Flush();
                    _stream?.Dispose();
                    if (_expectedSize > 0 && _received != _expectedSize)
                    {
                        Completion.TrySetResult((false, "Размер файла не совпадает с ожидаемым."));
                        return;
                    }

                    Completion.TrySetResult((true, message));
                }
                finally
                {
                    _stream = null;
                }
            }

            public void Fail(string message)
            {
                try
                {
                    _stream?.Dispose();
                    if (File.Exists(DestinationPath))
                    {
                        File.Delete(DestinationPath);
                    }
                }
                catch
                {
                }
                finally
                {
                    _stream = null;
                }

                Completion.TrySetResult((false, message));
            }

            public void Dispose()
            {
                _stream?.Dispose();
            }
        }

        public async Task<(bool Success, string Message)> DownloadFileAsync(string uniqueFileName, string destinationPath)
        {
            if (!IsConnected || _writer == null)
            {
                return (false, "Нет подключения к серверу.");
            }

            var session = new FileDownloadSession(uniqueFileName, destinationPath);
            if (!_fileDownloads.TryAdd(uniqueFileName, session))
            {
                return (false, "Загрузка этого файла уже выполняется.");
            }

            try
            {
                await _writeSemaphore.WaitAsync();
                var commandBytes = $"/download {uniqueFileName}";
                await _writer.WriteLineAsync(commandBytes);
            }
            catch (Exception ex)
            {
                _fileDownloads.TryRemove(uniqueFileName, out _);
                session.Fail($"Не удалось отправить команду скачивания: {ex.Message}");
                return await session.Completion.Task;
            }
            finally
            {
                _writeSemaphore.Release();
            }

            var timeoutTask = Task.Delay(TimeSpan.FromMinutes(5));
            var completedTask = await Task.WhenAny(session.Completion.Task, timeoutTask);

            if (completedTask != session.Completion.Task)
            {
                if (_fileDownloads.TryRemove(uniqueFileName, out var pendingSession))
                {
                    pendingSession.Fail("Загрузка прервана по таймауту.");
                }

                return await session.Completion.Task;
            }

            var result = await session.Completion.Task;
            _fileDownloads.TryRemove(uniqueFileName, out _);
            return result;
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

        private static string SanitizeFileName(string fileName)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            foreach (var ch in invalidChars)
            {
                fileName = fileName.Replace(ch, '_');
            }
            return fileName.Replace(' ', '_');
        }

        // Методы для работы с группами
        public async Task<ApiResponse?> CreateGroupAsync(string groupName)
        {
            if (!IsConnected || _writer == null || _reader == null)
            {
                return null;
            }

            await _writeSemaphore.WaitAsync();
            try
            {
                var command = $"/creategroup {groupName}";
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

        public async Task<ApiResponse?> GetGroupInfoAsync(int groupId)
        {
            if (!IsConnected || _writer == null || _reader == null)
            {
                return null;
            }

            await _writeSemaphore.WaitAsync();
            try
            {
                var command = $"/groupinfo {groupId}";
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

        public async Task<ApiResponse?> LeaveGroupAsync(int groupId)
        {
            if (!IsConnected || _writer == null || _reader == null)
            {
                return null;
            }

            await _writeSemaphore.WaitAsync();
            try
            {
                var command = $"/leavegroup {groupId}";
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

        public async Task<ApiResponse?> UpdateGroupAsync(int groupId, string? name = null, string? description = null)
        {
            if (!IsConnected || _writer == null || _reader == null)
            {
                return null;
            }

            await _writeSemaphore.WaitAsync();
            try
            {
                var parts = new List<string> { $"/updategroup {groupId}" };
                
                if (name != null)
                {
                    parts.Add($"name:{name}");
                }
                
                if (description != null)
                {
                    parts.Add($"desc:{description}");
                }

                var command = string.Join(" ", parts);
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

        public async Task<ApiResponse?> AddMemberToGroupAsync(int groupId, string username)
        {
            if (!IsConnected || _writer == null || _reader == null)
            {
                return null;
            }

            await _writeSemaphore.WaitAsync();
            try
            {
                var command = $"/addmember {groupId} {username}";
                await _writer.WriteLineAsync(command);

                var response = await ReceiveMessageAsync(5000);
                if (string.IsNullOrEmpty(response))
                {
                    return null;
                }

                return DeserializeApiResponse(response);
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
    }
}