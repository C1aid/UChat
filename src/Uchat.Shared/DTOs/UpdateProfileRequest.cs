namespace Uchat.Shared.DTOs
{
    public class UpdateProfileRequest
    {
        public string Username { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string ProfileInfo { get; set; } = string.Empty;
        public string Theme { get; set; } = string.Empty;
        public byte[]? Avatar { get; set; }
    }

    public class UserProfileDto
    {
        public int Id { get; set; }
        public string Username { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string ProfileInfo { get; set; } = string.Empty;
        public string Theme { get; set; } = string.Empty;
        public byte[]? Avatar { get; set; }
    }
}