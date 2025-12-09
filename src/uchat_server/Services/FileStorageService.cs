using System;
using System.IO;
using System.Threading.Tasks;
using Uchat.Shared.Enums;

namespace uchat_server.Services
{
    public class FileStorageService
    {
        private readonly string _storagePath = Path.Combine(Directory.GetCurrentDirectory(), "Storage");

        public FileStorageService()
        {
            try
            {
                if (!Directory.Exists(_storagePath))
                {
                    Directory.CreateDirectory(_storagePath);
                    Console.WriteLine($"[Storage] Created storage at: {_storagePath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Storage] FAILED to create directory: {ex.Message}");
            }
        }

        public async Task<string> SaveFileAsync(byte[] fileData, string originalFileName, MessageType type)
        {
            string extension = Path.GetExtension(originalFileName);
            string uniqueFileName = $"{Guid.NewGuid()}{extension}";
            string filePath = Path.Combine(_storagePath, uniqueFileName);
            await File.WriteAllBytesAsync(filePath, fileData);

            return uniqueFileName;
        }

        public string GetFilePath(string uniqueFileName)
        {
            return Path.Combine(_storagePath, uniqueFileName);
        }

        public bool FileExists(string uniqueFileName)
        {
            string filePath = GetFilePath(uniqueFileName);
            return File.Exists(filePath);
        }
    }
}

