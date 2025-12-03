namespace Uchat.Shared.DTOs
{
    public class ChatRoomDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool IsGroup { get; set; }
        public string? LastMessage { get; set; }
        public DateTime LastMessageTime { get; set; }
        public int ChatRoomId { get; set; }
    }
}