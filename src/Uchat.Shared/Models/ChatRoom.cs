namespace Uchat.Shared.Models
{
    public class ChatRoom
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool IsGroup { get; set; } = false;
        public string? Description { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public List<ChatRoomMember> Members { get; set; } = new();
        public List<Message> Messages { get; set; } = new();
    }
}