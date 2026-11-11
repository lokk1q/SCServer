using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SCServer
{
    public class UserData
    {
        public string Username { get; set; } = "";
        public string Token { get; set; } = "";
        public DateTime LastPing { get; set; } = DateTime.MinValue;
        public List<string> Friends { get; set; } = new List<string>();
        public string CurrentTrackTitle { get; set; } = "";
        public string CurrentTrackArtist { get; set; } = "";
        public string CurrentTrackArtwork { get; set; } = "";

        // Экономика и косметика
        public int Coins { get; set; } = 1000;
        public string UniqueName { get; set; } = "";
        public string EquippedFrame { get; set; } = "";
        public string EquippedTag { get; set; } = "";
        public List<string> Inventory { get; set; } = new List<string>();
    }

    public class FriendInfo
    {
        public string Username { get; set; } = "";
        public bool IsOnline { get; set; }
        public string CurrentTrackTitle { get; set; } = "";
        public string CurrentTrackArtist { get; set; } = "";
        public string CurrentTrackArtwork { get; set; } = "";
        public string UniqueName { get; set; } = "";
        public string EquippedFrame { get; set; } = "";
        public string EquippedTag { get; set; } = "";
    }

    class Program
    {
        static Dictionary<string, UserData> _users = new Dictionary<string, UserData>();

        // ВАЖНО: Для облачных серверов используем временную папку
        static readonly string _dataPath = Path.Combine(Path.GetTempPath(), "users.json");
        static readonly object _lock = new object();

        static void Main()
        {
            LoadData();
            var listener = new HttpListener();

            // Если есть переменная PORT (Render, Heroku), используем её. Иначе 8085.
            string port = Environment.GetEnvironmentVariable("PORT") ?? "8085";

            // На некоторых хостингах нужен http://+:port/ вместо http://*:port/
            listener.Prefixes.Add($"http://*:{port}/");

            try
            {
                listener.Start();
                Console.WriteLine($"Сервер запущен на порту {port}. Ожидание запросов...");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка запуска сервера: {ex.Message}");
                return;
            }

            // Фоновый поток для проверки онлайна
            Task.Run(() =>
            {
                while (true)
                {
                    Thread.Sleep(15000);
                    lock (_lock)
                    {
                        var now = DateTime.Now;
                        foreach (var user in _users.Values)
                        {
                            if ((now - user.LastPing).TotalSeconds > 45)
                            {
                                user.LastPing = DateTime.MinValue;
                                user.CurrentTrackTitle = "";
                                user.CurrentTrackArtist = "";
                                user.CurrentTrackArtwork = "";
                            }
                        }
                        SaveData();
                    }
                }
            });

            while (listener.IsListening)
            {
                var context = listener.GetContext();
                Task.Run(() => HandleRequest(context));
            }
        }

        static void HandleRequest(HttpListenerContext context)
        {
            try
            {
                string method = context.Request.HttpMethod;
                string path = context.Request.Url.AbsolutePath;
                string body = "";

                if (context.Request.HasEntityBody)
                {
                    using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
                    {
                        body = reader.ReadToEnd();
                    }
                }

                // Обработка CORS (на всякий случай, если будем дергать из Web)
                context.Response.Headers.Add("Access-Control-Allow-Origin", "*");
                context.Response.Headers.Add("Access-Control-Allow-Methods", "POST, GET, OPTIONS");
                context.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Authorization");

                if (method == "OPTIONS")
                {
                    SendResponse(context, "", 200);
                    return;
                }

                if (method == "POST" && path == "/api/register")
                {
                    var req = JsonSerializer.Deserialize<Dictionary<string, string>>(body);
                    string username = req?["username"] ?? "";

                    if (string.IsNullOrWhiteSpace(username))
                    {
                        SendResponse(context, "{\"error\":\"Empty username\"}", 400);
                        return;
                    }

                    string newToken = "";
                    lock (_lock)
                    {
                        if (!_users.ContainsKey(username))
                        {
                            _users[username] = new UserData { Username = username, Token = Guid.NewGuid().ToString(), Coins = 1000 };
                        }
                        else
                        {
                            _users[username].Token = Guid.NewGuid().ToString();
                        }
                        _users[username].LastPing = DateTime.Now;
                        newToken = _users[username].Token;
                        SaveData();
                    }
                    SendResponse(context, $"{{\"token\":\"{newToken}\"}}", 200);
                }
                else if (method == "GET" && path == "/api/me")
                {
                    string token = ExtractToken(context);
                    lock (_lock)
                    {
                        var user = _users.Values.FirstOrDefault(u => u.Token == token);
                        if (user != null)
                        {
                            var res = new
                            {
                                Coins = user.Coins,
                                UniqueName = user.UniqueName,
                                EquippedFrame = user.EquippedFrame,
                                EquippedTag = user.EquippedTag,
                                Inventory = user.Inventory
                            };
                            SendResponse(context, JsonSerializer.Serialize(res), 200);
                        }
                        else SendResponse(context, "{\"error\":\"Unauthorized\"}", 401);
                    }
                }
                else if (method == "POST" && path == "/api/buy")
                {
                    string token = ExtractToken(context);
                    var req = JsonSerializer.Deserialize<Dictionary<string, string>>(body);
                    string itemId = req?["itemId"] ?? "";
                    int price = req != null && req.ContainsKey("price") ? int.Parse(req["price"]) : 0;

                    lock (_lock)
                    {
                        var user = _users.Values.FirstOrDefault(u => u.Token == token);
                        if (user == null) { SendResponse(context, "{\"error\":\"Unauthorized\"}", 401); return; }

                        if (user.Inventory.Contains(itemId))
                        {
                            SendResponse(context, "{\"error\":\"Already owned\"}", 400); return;
                        }
                        if (user.Coins < price)
                        {
                            SendResponse(context, "{\"error\":\"Not enough coins\"}", 400); return;
                        }

                        user.Coins -= price;
                        user.Inventory.Add(itemId);
                        SaveData();
                        SendResponse(context, "{\"status\":\"ok\"}", 200);
                    }
                }
                else if (method == "POST" && path == "/api/claim_username")
                {
                    string token = ExtractToken(context);
                    var req = JsonSerializer.Deserialize<Dictionary<string, string>>(body);
                    string newName = req?["username"] ?? "";

                    lock (_lock)
                    {
                        var user = _users.Values.FirstOrDefault(u => u.Token == token);
                        if (user == null) { SendResponse(context, "{\"error\":\"Unauthorized\"}", 401); return; }

                        if (user.Coins < 1000)
                        {
                            SendResponse(context, "{\"error\":\"Not enough coins\"}", 400); return;
                        }

                        if (_users.Values.Any(u => u.UniqueName.Equals(newName, StringComparison.OrdinalIgnoreCase)))
                        {
                            SendResponse(context, "{\"error\":\"Name is taken\"}", 400); return;
                        }

                        user.Coins -= 1000;
                        user.UniqueName = newName;
                        SaveData();
                        SendResponse(context, "{\"status\":\"ok\"}", 200);
                    }
                }
                else if (method == "POST" && path == "/api/equip")
                {
                    string token = ExtractToken(context);
                    var req = JsonSerializer.Deserialize<Dictionary<string, string>>(body);
                    string type = req?["type"] ?? "";
                    string value = req?["value"] ?? "";

                    lock (_lock)
                    {
                        var user = _users.Values.FirstOrDefault(u => u.Token == token);
                        if (user == null) { SendResponse(context, "{\"error\":\"Unauthorized\"}", 401); return; }

                        if (type == "frame") user.EquippedFrame = value;
                        else if (type == "tag") user.EquippedTag = value;
                        else if (type == "username") user.UniqueName = value;

                        SaveData();
                        SendResponse(context, "{\"status\":\"ok\"}", 200);
                    }
                }
                else if (method == "POST" && path == "/api/heartbeat")
                {
                    string token = ExtractToken(context);
                    var req = JsonSerializer.Deserialize<Dictionary<string, string>>(body);

                    string title = req != null && req.ContainsKey("trackTitle") ? req["trackTitle"] : "";
                    string artist = req != null && req.ContainsKey("trackArtist") ? req["trackArtist"] : "";
                    string artwork = req != null && req.ContainsKey("trackArtwork") ? req["trackArtwork"] : "";

                    bool isAuth = false;
                    lock (_lock)
                    {
                        var user = _users.Values.FirstOrDefault(u => u.Token == token);
                        if (user != null)
                        {
                            user.LastPing = DateTime.Now;
                            user.CurrentTrackTitle = title;
                            user.CurrentTrackArtist = artist;
                            user.CurrentTrackArtwork = artwork;
                            isAuth = true;
                        }
                    }
                    if (isAuth) SendResponse(context, "{\"status\":\"ok\"}", 200);
                    else SendResponse(context, "{\"error\":\"Unauthorized\"}", 401);
                }
                else if (method == "POST" && path == "/api/add_friend")
                {
                    string token = ExtractToken(context);
                    var req = JsonSerializer.Deserialize<Dictionary<string, string>>(body);
                    string friendName = req?["friend"] ?? "";

                    bool isSuccess = false;
                    string errorMsg = "";
                    int statusCode = 200;

                    lock (_lock)
                    {
                        var user = _users.Values.FirstOrDefault(u => u.Token == token);
                        if (user == null) { statusCode = 401; errorMsg = "{\"error\":\"Unauthorized\"}"; }
                        else if (!_users.ContainsKey(friendName)) { statusCode = 404; errorMsg = "{\"error\":\"User not found\"}"; }
                        else if (friendName == user.Username) { statusCode = 400; errorMsg = "{\"error\":\"Cannot add yourself\"}"; }
                        else
                        {
                            if (!user.Friends.Contains(friendName))
                            {
                                user.Friends.Add(friendName);
                                SaveData();
                            }
                            isSuccess = true;
                        }
                    }

                    if (isSuccess) SendResponse(context, "{\"status\":\"ok\"}", 200);
                    else SendResponse(context, errorMsg, statusCode);
                }
                else if (method == "GET" && path == "/api/friends")
                {
                    string token = ExtractToken(context);
                    string jsonResponse = "";
                    bool isAuth = false;

                    lock (_lock)
                    {
                        var user = _users.Values.FirstOrDefault(u => u.Token == token);
                        if (user != null)
                        {
                            var list = new List<FriendInfo>();
                            foreach (var f in user.Friends)
                            {
                                if (_users.TryGetValue(f, out var fData))
                                {
                                    bool isOnline = (DateTime.Now - fData.LastPing).TotalSeconds < 45;
                                    list.Add(new FriendInfo
                                    {
                                        Username = f,
                                        IsOnline = isOnline,
                                        CurrentTrackTitle = isOnline ? fData.CurrentTrackTitle : "",
                                        CurrentTrackArtist = isOnline ? fData.CurrentTrackArtist : "",
                                        CurrentTrackArtwork = isOnline ? fData.CurrentTrackArtwork : "",
                                        UniqueName = fData.UniqueName,
                                        EquippedFrame = fData.EquippedFrame,
                                        EquippedTag = fData.EquippedTag
                                    });
                                }
                            }
                            jsonResponse = JsonSerializer.Serialize(list);
                            isAuth = true;
                        }
                    }

                    if (isAuth) SendResponse(context, jsonResponse, 200);
                    else SendResponse(context, "{\"error\":\"Unauthorized\"}", 401);
                }
                else
                {
                    SendResponse(context, "{\"error\":\"Not found\"}", 404);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Ошибка: " + ex.Message);
                try { SendResponse(context, "{\"error\":\"Internal error\"}", 500); } catch { }
            }
        }

        static string ExtractToken(HttpListenerContext context)
        {
            string authHeader = context.Request.Headers["Authorization"] ?? "";
            if (authHeader.StartsWith("Bearer ")) return authHeader.Substring(7);
            return authHeader;
        }

        static void SendResponse(HttpListenerContext context, string responseString, int statusCode)
        {
            try
            {
                byte[] buffer = Encoding.UTF8.GetBytes(responseString);
                context.Response.StatusCode = statusCode;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = buffer.Length;
                context.Response.OutputStream.Write(buffer, 0, buffer.Length);
                context.Response.Close();
            }
            catch { }
        }

        static void LoadData()
        {
            if (File.Exists(_dataPath))
            {
                try
                {
                    string json = File.ReadAllText(_dataPath);
                    var data = JsonSerializer.Deserialize<Dictionary<string, UserData>>(json);
                    if (data != null)
                    {
                        _users = data;
                        foreach (var user in _users.Values)
                        {
                            user.LastPing = DateTime.MinValue;
                            user.CurrentTrackTitle = "";
                            user.CurrentTrackArtist = "";
                            user.CurrentTrackArtwork = "";
                            if (user.Inventory == null) user.Inventory = new List<string>();
                        }
                    }
                }
                catch { }
            }
        }

        static void SaveData()
        {
            try
            {
                string json = JsonSerializer.Serialize(_users, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_dataPath, json);
            }
            catch { }
        }
    }
}