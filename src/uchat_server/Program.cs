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

    static async Task Main(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json")
            .Build();

        var services = new ServiceCollection();
        ConfigureServices(services, configuration);
        var serviceProvider = services.BuildServiceProvider();

        // Инициализация логгера/БД
        _logger = serviceProvider.GetRequiredService<ILogger<Program>>();
        await InitializeDatabase(serviceProvider);

        if (args.Length != 1 || !int.TryParse(args[0], out int port))
        {
            Console.WriteLine("Usage: uchat_server <port>");
            return;
        }

        // Запуск TCP
        _logger.LogInformation("Server PID: {ProcessId}", Environment.ProcessId);
        _logger.LogInformation("Starting server on port {Port}", port);

        await StartTcpServer(port, serviceProvider);
    }

    static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // Логирование
        services.AddLogging(builder => builder.AddConsole());

        var connectionString = configuration.GetConnectionString("DefaultConnection")
                              ?? configuration.GetSection("ServerConfig")["ConnectionString"]
                              ?? "Host=localhost;Port=5432;Database=uchat;Username=postgres;Password=securepass";

        services.AddDbContext<ChatContext>(options =>
            options.UseNpgsql(connectionString));

        // Сервисы
        services.AddScoped<AuthService>();
        services.AddScoped<ChatService>();
        services.AddScoped<DatabaseInitializer>();
    }

    static async Task InitializeDatabase(IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ChatContext>();
        await context.Database.MigrateAsync();
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
                    _clients.Add(handler);
                    await handler.StartAsync();
                    _clients.Remove(handler);
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error accepting client connection");
            }
        }
    }
}