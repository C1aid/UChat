using System;
using Uchat.Shared.Enums;

namespace Uchat.Shared.DTOs
{    
    public class FileDownloadMetadata
    {
        public long FileSize { get; set; }
        public string? FileName { get; set; }
    }
}