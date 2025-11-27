namespace Uchat.Shared.DTOs
{
    public class ClientMessageDto
    {
        public int RoomId { get; set; }
        public string Content { get; set; } = string.Empty;
    }
}