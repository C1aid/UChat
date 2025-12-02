using System.Collections.Concurrent;

namespace uchat_server.Services
{
    public class ConnectionManager
    {
        private readonly ConcurrentDictionary<int, ClientHandler> _userConnections = new();
        private readonly ConcurrentDictionary<int, ConcurrentDictionary<int, ClientHandler>> _roomConnections = new();

        public void AddConnection(int userId, ClientHandler handler)
        {
            _userConnections.AddOrUpdate(userId, handler, (key, oldValue) => handler);
        }

        public void RemoveConnection(int userId, ClientHandler handler)
        {
            _userConnections.TryRemove(userId, out _);
        }

        public ClientHandler? GetUserConnection(int userId)
        {
            _userConnections.TryGetValue(userId, out var handler);
            return handler;
        }

        public void JoinRoom(int userId, int roomId, ClientHandler handler)
        {
            _roomConnections.AddOrUpdate(roomId,
                new ConcurrentDictionary<int, ClientHandler>(new[] { new KeyValuePair<int, ClientHandler>(userId, handler) }),
                (key, existingDict) =>
                {
                    existingDict.AddOrUpdate(userId, handler, (k, oldValue) => handler);
                    return existingDict;
                });
        }

        public void LeaveRoom(int userId, int roomId, ClientHandler handler)
        {
            if (_roomConnections.TryGetValue(roomId, out var handlers))
            {
                handlers.TryRemove(userId, out _);
                if (handlers.IsEmpty)
                {
                    _roomConnections.TryRemove(roomId, out _);
                }
            }
        }

        public List<ClientHandler> GetRoomConnections(int roomId)
        {
            if (_roomConnections.TryGetValue(roomId, out var handlers))
            {
                return handlers.Values.ToList();
            }
            return new List<ClientHandler>();
        }

        public List<int> GetUserRooms(int userId)
        {
            var rooms = new List<int>();
            foreach (var roomEntry in _roomConnections)
            {
                if (roomEntry.Value.ContainsKey(userId))
                {
                    rooms.Add(roomEntry.Key);
                }
            }
            return rooms;
        }
    }
}