namespace uchat_server.Models
{
    public class ServerConfig
    {
        public string ConnectionString { get; set; } = string.Empty;
        public int MaxConnections { get; set; } = 100;
        public int MessageHistoryLimit { get; set; } = 100;
    }
}