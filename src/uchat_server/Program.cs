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
    
    // Используем обычный список, но будем его блокировать при изменении
    private static List<ClientHandler> _clients = new();
    private static readonly object _lock = new(); // Объект для блокировки (замок)

    static async Task Main(string[] args)
    {
        // 1. Проверка порта (Строгая, как вы просили)
        if (args.Length != 1 || !int.TryParse(args[0], out int port))
        {
            Console.WriteLine("Ошибка: Не указан порт!");
            Console.WriteLine("Использование: uchat_server.exe <port>");
            return; // Завершаем программу, если порта нет
        }

        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .Build();

        var services = new ServiceCollection();
        ConfigureServices(services, configuration);
        var serviceProvider = services.BuildServiceProvider();

        _logger = serviceProvider.GetRequiredService<ILogger<Program>>();

        // 2. АВТО-МИГРАЦИЯ (Чтобы не писать dotnet ef update)
        await InitializeDatabase(serviceProvider);

        _logger.LogInformation("Server PID: {ProcessId}", Environment.ProcessId);
        _logger.LogInformation("Starting server on port {Port}", port);

        await StartTcpServer(port, serviceProvider);
    }

    static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddLogging(builder => builder.AddConsole());

        // --- ИЗМЕНЕНИЕ: Ввод пароля вручную ---
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write("Введите пароль для БД (пользователь postgres): ");
        Console.ResetColor();

        // Читаем пароль с консоли. Если нажали просто Enter -> будет пустая строка
        string password = Console.ReadLine()?.Trim() ?? "";

        // Формируем строку подключения, подставляя введенный пароль
        var connectionString = $"Host=localhost;Port=5432;Database=uchat;Username=postgres;Password={password}";
        // --------------------------------------

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
            // Эта строка сама накатит миграции при запуске exe
            await context.Database.MigrateAsync();
            Console.WriteLine("--> [System] База данных проверена и обновлена.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"--> [System] Ошибка подключения к БД: {ex.Message}");
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