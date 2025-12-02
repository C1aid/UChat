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

        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .Build();

        var services = new ServiceCollection();
        ConfigureServices(services);
        var serviceProvider = services.BuildServiceProvider();

        await InitializeDatabase(serviceProvider);
        await StartTcpServer(port, serviceProvider);
    }

    static void ConfigureServices(IServiceCollection services)
    {
        services.AddLogging(builder => builder.AddConsole());

        services.AddDbContext<ChatContext>(options =>
            options.UseNpgsql("Host=localhost;Port=5432;Database=uchat;Username=postgres;Password=securepass"));

        services.AddSingleton<ConnectionManager>();
        services.AddScoped<AuthService>();
        services.AddScoped<ChatService>();
    }

    static async Task InitializeDatabase(IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ChatContext>();
        await context.Database.EnsureCreatedAsync();
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