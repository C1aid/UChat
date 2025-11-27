using Uchat.Shared.Enums;

namespace Uchat.Shared.DTOs
{
    public class MessageDto
    {
        public int Id { get; set; }
        public string Content { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public DateTime SentAt { get; set; }
        public MessageType MessageType { get; set; }
    }
}