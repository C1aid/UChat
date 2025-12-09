using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;
using uchat_server.Data;
using uchat_server.Services;

class Program
{
    static async Task Main(string[] args)
    {
        if (args.Length != 1 || !int.TryParse(args[0], out int port))
        {
            Console.WriteLine("Usage: uchat_server <port>");
            return;
        }

        Console.WriteLine($"Server PID: {Environment.ProcessId}");

        string dbPassword = GetPasswordInteractive();

        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .Build();

        var services = new ServiceCollection();
        ConfigureServices(services, dbPassword);
        var serviceProvider = services.BuildServiceProvider();

        await InitializeDatabase(serviceProvider);
        await PerformInitialCleanup(serviceProvider);
        await StartTcpServer(port, serviceProvider);
    }

    static string GetPasswordInteractive()
    {
        Console.Write("Enter PostgreSQL password for 'postgres': ");
        var password = string.Empty;
        ConsoleKeyInfo key;
        do
        {
            key = Console.ReadKey(true);
            if (key.Key == ConsoleKey.Enter)
                break;
            if (key.Key == ConsoleKey.Backspace && password.Length > 0)
            {
                password = password[0..^1];
                Console.Write("\b \b");
            }
            else if (!char.IsControl(key.KeyChar))
            {
                password += key.KeyChar;
                Console.Write("*");
            }
        } while (key.Key != ConsoleKey.Enter);
        Console.WriteLine();
        return password;
    }

    static void ConfigureServices(IServiceCollection services, string dbPassword)
    {
        services.AddLogging(builder => builder.AddConsole());

        services.AddDbContext<ChatContext>(options =>
            options.UseNpgsql($"Host=localhost;Port=5432;Database=uchat;Username=postgres;Password={dbPassword}"));

        services.AddSingleton<ConnectionManager>();
        services.AddSingleton<FileStorageService>();
        services.AddScoped<AuthService>();
        services.AddScoped<ChatService>();
        services.AddScoped<DatabaseCleanupService>();
    }

    static async Task InitializeDatabase(IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ChatContext>();
        await context.Database.EnsureCreatedAsync();
    }

    static async Task PerformInitialCleanup(IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var cleanupService = scope.ServiceProvider.GetRequiredService<DatabaseCleanupService>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        
        logger.LogInformation("Performing initial database cleanup and validation...");
        var result = await cleanupService.PerformFullCleanup(messageRetentionDays: 90);
        logger.LogInformation("Cleanup completed: Fixed {FixedUsers} users, Deleted {DeletedMessages} messages, Deleted {DeletedChatRooms} chat rooms",
            result.FixedUsers, result.DeletedMessages, result.DeletedChatRooms);
    }

    static async Task StartTcpServer(int port, IServiceProvider serviceProvider)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        Console.WriteLine($"TCP Server started on port {port}");

        while (true)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync();
                Console.WriteLine("New client connected");

                _ = Task.Run(async () =>
                {
                    using var scope = serviceProvider.CreateScope();
                    var handler = new ClientHandler(
                        client,
                        scope.ServiceProvider.GetRequiredService<AuthService>(),
                        scope.ServiceProvider.GetRequiredService<ChatService>(),
                        scope.ServiceProvider.GetRequiredService<ConnectionManager>(),
                        scope.ServiceProvider.GetRequiredService<FileStorageService>(),
                        scope.ServiceProvider.GetRequiredService<ILogger<ClientHandler>>()
                    );

                    await handler.StartAsync();
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error accepting client connection: {ex.Message}");
            }
        }
    }
}