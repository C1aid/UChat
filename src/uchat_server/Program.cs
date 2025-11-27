using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;
using uchat_server;
using uchat_server.Data;
using uchat_server.Services;

class Program
{
    private static ILogger<Program> _logger = null!;
    private static List<ClientHandler> _clients = new();
    private static readonly object _lock = new();

    static async Task Main(string[] args)
    {
        if (args.Length != 1 || !int.TryParse(args[0], out int port))
        {
            Console.WriteLine("Usage: uchat_server <port>");
            return;
        }

        Console.WriteLine($"Server PID: {System.Environment.ProcessId}");

        //ЗАПРОС ПАРОЛЯ ДЛЯ БД
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write("Enter PostgreSQL password: ");
        Console.ResetColor();
        
        string password = Console.ReadLine()?.Trim() ?? "";

        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .Build();

        var services = new ServiceCollection();
        ConfigureServices(services, configuration, password);
        var serviceProvider = services.BuildServiceProvider();

        _logger = serviceProvider.GetRequiredService<ILogger<Program>>();

        await InitializeDatabase(serviceProvider);

        _logger.LogInformation($"Daemon starting on port {port}...");
        await StartTcpServer(port, serviceProvider);

        _logger.LogInformation($"Daemon starting on port {port}...");

        ClientHandler.OnMessageBroadcast += async (json, senderId, roomId) =>
        {
            using (var scope = serviceProvider.CreateScope())
            {
                var chatService = scope.ServiceProvider.GetRequiredService<ChatService>();
                List<int> membersInChat = await chatService.GetRoomUserIdsAsync(roomId);
                List<ClientHandler> onlineClients;
                lock (_lock)
                {
                    onlineClients = _clients.ToList();
                }
                foreach (var client in onlineClients)
                {                    
                    if (client.UserId != 0 && membersInChat.Contains(client.UserId))
                    {
                        try 
                        {
                            await client.SendRawMessageAsync(json);
                        }
                        catch { }
                    }
                }
            }
        };
        await StartTcpServer(port, serviceProvider);
    }

    static void ConfigureServices(IServiceCollection services, IConfiguration configuration, string manualPassword)
    {
        services.AddLogging(builder => builder.AddConsole());

        var connectionString = $"Host=localhost;Port=5432;Database=uchat;Username=postgres;Password={manualPassword}";

        services.AddDbContext<ChatContext>(options =>
            options.UseNpgsql(connectionString));

        services.AddScoped<AuthService>();
        services.AddScoped<ChatService>();
    }

    static async Task InitializeDatabase(IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ChatContext>();
        try
        {
            await context.Database.MigrateAsync();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("--> [Success] Database connected and migrated.");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"--> [Error] DB Connection failed: {ex.Message}");
            Console.WriteLine("--> Проверьте пароль и перезапустите сервер.");
            Console.ResetColor();
        }
    }

    static async Task StartTcpServer(int port, IServiceProvider serviceProvider)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();

        _logger.LogInformation("TCP Server started on port {Port}", port);

        while (true)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync();
                _logger.LogInformation("New client connected");

                // Запускаем обработку клиента в отдельном потоке
                _ = Task.Run(async () =>
                {
                    // Создаем scope, чтобы сервисы (DbContext) жили только пока жив клиент
                    using var scope = serviceProvider.CreateScope();
                    
                    var handler = new ClientHandler(
                        client,
                        scope.ServiceProvider.GetRequiredService<ChatService>(),
                        scope.ServiceProvider.GetRequiredService<AuthService>(),
                        scope.ServiceProvider.GetRequiredService<ILogger<ClientHandler>>()
                    );

                    // БЕЗОПАСНО добавляем в список (lock)
                    lock (_lock)
                    {
                        _clients.Add(handler);
                    }

                    try
                    {
                        await handler.StartAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error inside client handler");
                    }
                    finally
                    {
                        // БЕЗОПАСНО удаляем из списка при отключении
                        lock (_lock)
                        {
                            _clients.Remove(handler);
                        }
                        _logger.LogInformation("Client disconnected");
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error accepting client connection");
            }
        }
    }
}