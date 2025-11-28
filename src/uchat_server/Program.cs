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

        // 2. Вывод PID
        Console.WriteLine($"Server PID: {System.Environment.ProcessId}");

        // 3. ЗАПРОС ПАРОЛЯ ДЛЯ БД
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write("Enter PostgreSQL password: ");
        Console.ResetColor();
        
        string password = Console.ReadLine()?.Trim() ?? "";

        // Настраиваем конфигурацию
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .Build();

        var services = new ServiceCollection();
        ConfigureServices(services, configuration, password);
        var serviceProvider = services.BuildServiceProvider();

        _logger = serviceProvider.GetRequiredService<ILogger<Program>>();

        // 4. Подключаем БД
        await InitializeDatabase(serviceProvider);

        _logger.LogInformation($"Daemon starting on port {port}...");

        // ====================================================================
        //  ↓↓↓ ВСТАВЛЕНО: ЛОГИКА РАССЫЛКИ (BROADCAST) ↓↓↓
        // ====================================================================
        ClientHandler.OnMessageBroadcast += async (jsonMessage, senderId, roomId) =>
        {
            // Создаем scope для доступа к базе данных
            using (var scope = serviceProvider.CreateScope())
            {
                var chatService = scope.ServiceProvider.GetRequiredService<ChatService>();
                
                // Получаем список участников комнаты
                List<int> memberIds = await chatService.GetRoomUserIdsAsync(roomId);

                List<ClientHandler> activeClients;
                lock (_lock)
                {
                    activeClients = _clients.ToList();
                }

                foreach (var client in activeClients)
                {
                    // Отправляем сообщение только тем, кто:
                    // 1. Залогинен (UserId != 0)
                    // 2. Состоит в этой комнате (есть в списке memberIds)
                    if (client.UserId != 0 && memberIds.Contains(client.UserId))
                    {
                        try 
                        {
                            await client.SendRawMessageAsync(jsonMessage);
                        }
                        catch 
                        {
                            // Игнорируем ошибки (клиент мог отвалиться)
                        }
                    }
                }
            }
        };
        // ====================================================================
        //  ↑↑↑ КОНЕЦ ВСТАВКИ ↑↑↑
        // ====================================================================

        // 5. Запускаем "Демона"
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
            // EnsureCreatedAsync: Создает базу и таблицы без миграций
            bool created = await context.Database.EnsureCreatedAsync();

            if (created)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("--> [Success] Database created from scratch (No migrations used).");
            }
            else
            {
                Console.WriteLine("--> [System] Database already exists.");
            }
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"--> [Error] DB Creation failed: {ex.Message}");
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

                _ = Task.Run(async () =>
                {
                    using var scope = serviceProvider.CreateScope();
                    
                    var handler = new ClientHandler(
                        client,
                        scope.ServiceProvider.GetRequiredService<ChatService>(),
                        scope.ServiceProvider.GetRequiredService<AuthService>(),
                        scope.ServiceProvider.GetRequiredService<ILogger<ClientHandler>>()
                    );

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