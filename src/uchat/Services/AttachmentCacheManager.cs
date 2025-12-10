using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace uchat.Services
{
    public class AttachmentCacheManager : IDisposable
    {
        private readonly string _cacheRoot;
        private readonly string _indexPath;
        private readonly TimeSpan _maxEntryAge;
        private readonly object _syncRoot = new object();
        private readonly Dictionary<int, CacheEntry> _entriesByMessageId = new Dictionary<int, CacheEntry>();
        private readonly JsonSerializerOptions _serializerOptions = new JsonSerializerOptions { WriteIndented = false };
        private readonly TimeSpan _persistInterval = TimeSpan.FromSeconds(30);
        private bool _isDirty;
        private DateTime _lastPersistUtc = DateTime.MinValue;
        private bool _disposed;

        private class CacheEntry
        {
            public int MessageId { get; set; }
            public int ChatRoomId { get; set; }
            public string LocalPath { get; set; } = string.Empty;
            public DateTime LastAccessUtc { get; set; }
        }

        public AttachmentCacheManager(string cacheRoot, TimeSpan? maxEntryAge = null)
        {
            _cacheRoot = cacheRoot;
            Directory.CreateDirectory(_cacheRoot);
            _indexPath = Path.Combine(_cacheRoot, "cache_index.json");
            _maxEntryAge = maxEntryAge ?? TimeSpan.FromDays(14);

            LoadIndex();
            PruneMissingFiles();
        }

        public bool TryGetCachedPath(int messageId, out string? localPath)
        {
            lock (_syncRoot)
            {
                if (_entriesByMessageId.TryGetValue(messageId, out var entry) && File.Exists(entry.LocalPath))
                {
                    entry.LastAccessUtc = DateTime.UtcNow;
                    MarkDirty();
                    PersistIndexIfNeeded();
                    localPath = entry.LocalPath;
                    return true;
                }

                localPath = null;
                return false;
            }
        }

        public void RegisterOrUpdateEntry(int messageId, int chatRoomId, string localPath)
        {
            lock (_syncRoot)
            {
                _entriesByMessageId[messageId] = new CacheEntry
                {
                    MessageId = messageId,
                    ChatRoomId = chatRoomId,
                    LocalPath = localPath,
                    LastAccessUtc = DateTime.UtcNow
                };

                MarkDirty();
                PersistIndexIfNeeded();
            }
        }

        public void MarkAccess(int messageId)
        {
            lock (_syncRoot)
            {
                if (_entriesByMessageId.TryGetValue(messageId, out var entry))
                {
                    entry.LastAccessUtc = DateTime.UtcNow;
                    MarkDirty();
                    PersistIndexIfNeeded();
                }
            }
        }

        public void PurgeExpiredEntries()
        {
            lock (_syncRoot)
            {
                var threshold = DateTime.UtcNow - _maxEntryAge;
                var staleIds = _entriesByMessageId
                    .Where(pair => pair.Value.LastAccessUtc < threshold || !File.Exists(pair.Value.LocalPath))
                    .Select(pair => pair.Key)
                    .ToList();

                foreach (var messageId in staleIds)
                {
                    if (_entriesByMessageId.TryGetValue(messageId, out var entry))
                    {
                        TryDeleteFile(entry.LocalPath);
                        _entriesByMessageId.Remove(messageId);
                    }
                }

                RemoveEmptyChatDirectories();

                if (staleIds.Count > 0)
                {
                    MarkDirty();
                    PersistIndexIfNeeded(true);
                }
            }
        }

        public void DeleteChatCache(int chatRoomId)
        {
            lock (_syncRoot)
            {
                var ids = _entriesByMessageId
                    .Where(pair => pair.Value.ChatRoomId == chatRoomId)
                    .Select(pair => pair.Key)
                    .ToList();

                foreach (var messageId in ids)
                {
                    if (_entriesByMessageId.TryGetValue(messageId, out var entry))
                    {
                        TryDeleteFile(entry.LocalPath);
                        _entriesByMessageId.Remove(messageId);
                    }
                }

                var chatFolder = Path.Combine(_cacheRoot, chatRoomId.ToString());
                TryDeleteDirectory(chatFolder);

                if (ids.Count > 0)
                {
                    MarkDirty();
                    PersistIndexIfNeeded(true);
                }
            }
        }

        public void ClearAllCaches()
        {
            lock (_syncRoot)
            {
                foreach (var entry in _entriesByMessageId.Values)
                {
                    TryDeleteFile(entry.LocalPath);
                }

                _entriesByMessageId.Clear();
                TryDeleteDirectory(_cacheRoot);
                Directory.CreateDirectory(_cacheRoot);

                MarkDirty();
                PersistIndexIfNeeded(true);
            }
        }

        public void FlushIndex()
        {
            lock (_syncRoot)
            {
                PersistIndexIfNeeded(true);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            FlushIndex();
            _disposed = true;
        }

        private void LoadIndex()
        {
            try
            {
                if (!File.Exists(_indexPath))
                {
                    return;
                }

                var json = File.ReadAllText(_indexPath);
                var entries = JsonSerializer.Deserialize<List<CacheEntry>>(json, _serializerOptions);
                if (entries == null)
                {
                    return;
                }

                foreach (var entry in entries)
                {
                    if (entry == null || entry.MessageId == 0 || string.IsNullOrWhiteSpace(entry.LocalPath))
                    {
                        continue;
                    }

                    if (!File.Exists(entry.LocalPath))
                    {
                        continue;
                    }

                    _entriesByMessageId[entry.MessageId] = entry;
                }
            }
            catch
            {
            }
        }

        private void PruneMissingFiles()
        {
            lock (_syncRoot)
            {
                var missingIds = _entriesByMessageId
                    .Where(pair => !File.Exists(pair.Value.LocalPath))
                    .Select(pair => pair.Key)
                    .ToList();

                foreach (var messageId in missingIds)
                {
                    _entriesByMessageId.Remove(messageId);
                }

                if (missingIds.Count > 0)
                {
                    MarkDirty();
                    PersistIndexIfNeeded(true);
                }
            }
        }

        private void RemoveEmptyChatDirectories()
        {
            try
            {
                foreach (var directory in Directory.GetDirectories(_cacheRoot))
                {
                    if (!Directory.EnumerateFileSystemEntries(directory).Any())
                    {
                        Directory.Delete(directory, true);
                    }
                }
            }
            catch
            {
            }
        }

        private void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch
            {
            }
        }

        private void MarkDirty()
        {
            _isDirty = true;
        }

        private void PersistIndexIfNeeded(bool force = false)
        {
            if (!_isDirty && !force)
            {
                return;
            }

            if (!force && (DateTime.UtcNow - _lastPersistUtc) < _persistInterval)
            {
                return;
            }

            try
            {
                var entries = _entriesByMessageId.Values.ToList();
                if (entries.Count == 0)
                {
                    if (File.Exists(_indexPath))
                    {
                        File.Delete(_indexPath);
                    }
                }
                else
                {
                    var json = JsonSerializer.Serialize(entries, _serializerOptions);
                    File.WriteAllText(_indexPath, json);
                }
            }
            catch
            {
            }
            finally
            {
                _lastPersistUtc = DateTime.UtcNow;
                _isDirty = false;
            }
        }
    }
}
