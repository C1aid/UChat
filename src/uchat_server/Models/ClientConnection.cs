namespace uchat_server.Models
{
    public class ClientConnection
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public DateTime ConnectedAt { get; set; } = DateTime.UtcNow;
        public string? Username { get; set; }
    }
}