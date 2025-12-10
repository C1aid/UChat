namespace Uchat.Shared.DTOs
{
    public class FileChunkDto
    {
        public string FileName { get; set; } = string.Empty;
        public int ChunkIndex { get; set; }
        public int BytesLength { get; set; }
        public string Data { get; set; } = string.Empty;
    }
}
