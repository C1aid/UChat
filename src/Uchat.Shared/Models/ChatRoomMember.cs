namespace Uchat.Shared.Models
{
    public class ChatRoomMember
    {
        public int ChatRoomId { get; set; }
        public int UserId { get; set; }
        public bool IsAdmin { get; set; }
        public ChatRoom ChatRoom { get; set; } = null!;
        public User User { get; set; } = null!;
    }
}