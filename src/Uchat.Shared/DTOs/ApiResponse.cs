using System.Text.Json;

namespace Uchat.Shared.DTOs
{
    public class ApiResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public object? Data { get; set; }

        public T? GetData<T>() where T : class
        {
            if (Data is JsonElement jsonElement)
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                return JsonSerializer.Deserialize<T>(jsonElement.GetRawText(), options);
            }
            return Data as T;
        }
    }

    public class AuthResponse
    {
        public int UserId { get; set; }
        public string Username { get; set; } = string.Empty;
    }
}