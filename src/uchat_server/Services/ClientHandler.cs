using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Uchat.Shared.DTOs;
using Uchat.Shared.Models;
using Uchat.Shared.Enums;
using uchat_server.Services;

public class ClientHandler
{
    private readonly TcpClient _client;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly ChatService _chatService;
    private readonly AuthService _authService;
    private readonly ILogger<ClientHandler> _logger;

    private User? _currentUser;

    public ClientHandler(TcpClient client, ChatService chatService, AuthService authService, ILogger<ClientHandler> logger)
    {
        _client = client;
        _chatService = chatService;
        _authService = authService;
        _logger = logger;

        var stream = client.GetStream();
        _reader = new StreamReader(stream, Encoding.UTF8);
        _writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
    }

    public async Task StartAsync()
    {
        try
        {
            await SendWelcomeMessage();

            string? line;
            while ((line = await _reader.ReadLineAsync()) != null)
            {
                await ProcessCommand(line);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Client handling error");
        }
        finally
        {
            _client.Close();
        }
    }

    private async Task ProcessCommand(string input)
    {
        if (input.StartsWith("/"))
        {
            await ProcessSlashCommand(input);
        }
        else
        {
            await ProcessMessage(input);
        }
    }

    private async Task ProcessSlashCommand(string command)
    {
        var parts = command.Split(' ');
        var cmd = parts[0].ToLower();

        switch (cmd)
        {
            case "/register":
                await RegisterUser(parts);
                break;
            case "/login":
                await LoginUser(parts);
                break;
            case "/help":
                await ShowHelp();
                break;
            default:
                await SendResponse(new ApiResponse { Success = false, Message = "Unknown command" });
                break;
        }
    }

    private async Task RegisterUser(string[] parts)
    {
        if (parts.Length < 3)
        {
            await SendResponse(new ApiResponse { Success = false, Message = "Usage: /register <username> <password>" });
            return;
        }

        var result = await _authService.RegisterAsync(parts[1], parts[2]);
        await SendResponse(result);
    }

    private async Task LoginUser(string[] parts)
    {
        if (parts.Length < 3)
        {
            await SendResponse(new ApiResponse { Success = false, Message = "Usage: /login <username> <password>" });
            return;
        }

        var result = await _authService.LoginAsync(parts[1], parts[2]);
        if (result.Success)
        {
            _currentUser = await _authService.GetUserByUsernameAsync(parts[1]);
            await SendResponse(result);
            await SendRecentMessages();
        }
        else
        {
            await SendResponse(result);
        }
    }

    private async Task ProcessMessage(string content)
    {
        if (_currentUser == null)
        {
            await SendResponse(new ApiResponse { Success = false, Message = "Please login first" });
            return;
        }

        var message = new Message
        {
            Content = content,
            UserId = _currentUser.Id,
            SentAt = DateTime.UtcNow,
            MessageType = MessageType.Text
        };

        var result = await _chatService.SendMessageAsync(message);
        await SendResponse(result);

        _logger.LogInformation("Message from {User}: {Content}", _currentUser.Username, content);
    }

    private async Task SendRecentMessages()
    {
        var messages = await _chatService.GetRecentMessagesAsync(10);
        foreach (var msg in messages)
        {
            await _writer.WriteLineAsync(JsonSerializer.Serialize(msg));
        }
    }

    private async Task SendWelcomeMessage()
    {
        await SendResponse(new ApiResponse
        {
            Success = true,
            Message = "Welcome to Uchat! Use /help for commands",
            Data = new { Commands = new[] { "/register", "/login", "/help" } }
        });
    }

    private async Task ShowHelp()
    {
        var help = new ApiResponse
        {
            Success = true,
            Message = "Available commands",
            Data = new
            {
                Commands = new[]
                {
                    "/register <username> <password> - Create account",
                    "/login <username> <password> - Login",
                    "/help - Show this help"
                }
            }
        };
        await SendResponse(help);
    }

    private async Task SendResponse(ApiResponse response)
    {
        var json = JsonSerializer.Serialize(response);
        await _writer.WriteLineAsync(json);
    }
}