using System;
using System.Text.Json.Serialization;
using Uchat.Shared.Enums;

namespace Uchat.Shared.DTOs
{
    public class MessageDto
    {
        public int Id { get; set; }
        public string Content { get; set; } = string.Empty;
        public DateTime SentAt { get; set; }
        public DateTime? EditedAt { get; set; }
        public int UserId { get; set; }
        public string Username { get; set; } = string.Empty;
        public int ChatRoomId { get; set; }
        public MessageType MessageType { get; set; } 

        public string FileUrl { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string MimeType { get; set; } = string.Empty; // e.g., "image/png", "application/pdf"
        public long FileSize { get; set; }

        [JsonIgnore]
        public string? LocalFilePath { get; set; }
    }
}