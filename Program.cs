using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// ============================================================================
// SERVER SIGNALING
//
// PERAN: perantara antara browser/HP (client) dan komputer toko (agent).
// Hanya menukar pesan; video TIDAK lewat sini (video P2P lewat WebRTC).
//
//   Browser --/ws/client?code=KODE--> [server] --/ws/agent?code=KODE--> Agent toko
//
// JALUR PESAN
//   client -> agent : hanya pesan "offer" yang diteruskan. Server menambahkan
//                     "clientId" (dibuat server) ke pesan itu.
//   agent  -> client: agent membalas { "clientId": "...", "data": "..." }.
//                     Server membuka bungkusnya dan mengirim "data" ke klien
//                     yang tepat. Tanpa clientId (agent versi lama), jawaban
//                     dikirim ke semua klien toko itu.
//   web    -> agent : POST /api/agents/{kode}/commands dengan header X-Api-Key,
//                     untuk perintah add_camera / remove_camera.
//
// KONFIGURASI (appsettings.json atau environment variable):
//   "Signaling": { "CommandApiKey": "isi-rahasia-panjang" }
//   env: Signaling__CommandApiKey
// ============================================================================

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UsePathBase("/CCTVSIGNALING/CctvSignalingServer");
app.UseWebSockets();

const int MaxMessageBytes = 256 * 1024; // pesan lebih besar dari ini ditolak (SDP biasanya < 10 KB)
string commandApiKey = app.Configuration["Signaling:CommandApiKey"] ?? "";

// kode toko -> koneksi agent
var agents = new ConcurrentDictionary<string, Peer>();
// kode toko -> (clientId -> koneksi klien). Banyak klien boleh menonton toko yang sama.
var clients = new ConcurrentDictionary<string, ConcurrentDictionary<string, Peer>>();

// ---------------------------------------------------------------------------
// HELPER
// ---------------------------------------------------------------------------

// Membaca satu pesan utuh. Mengembalikan null bila koneksi ditutup atau pesan terlalu besar.
async Task<(byte[] Data, WebSocketMessageType Type)?> ReceiveMessageAsync(WebSocket ws, byte[] buffer)
{
    using var ms = new MemoryStream();
    WebSocketReceiveResult r;
    do
    {
        r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
        if (r.MessageType == WebSocketMessageType.Close) return null;

        ms.Write(buffer, 0, r.Count);
        if (ms.Length > MaxMessageBytes) return null;
    }
    while (!r.EndOfMessage);

    return (ms.ToArray(), r.MessageType);
}

async Task CloseQuietlyAsync(WebSocket ws)
{
    try
    {
        if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None);
    }
    catch { /* koneksi sudah putus, abaikan */ }
}

Task SendJsonErrorAsync(Peer peer, string message) =>
    peer.SendAsync(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { status = "error", message })));

// Membuka bungkus balasan agent: {"clientId":"...","data":"..."}
bool TryUnwrapAgentReply(byte[] data, out string clientId, out string payload)
{
    clientId = ""; payload = "";
    try
    {
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (!root.TryGetProperty("clientId", out var c) || c.ValueKind != JsonValueKind.String) return false;
        if (!root.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.String) return false;

        clientId = c.GetString()!;
        payload = d.GetString()!;
        return true;
    }
    catch (JsonException) { return false; }
}

// Menyaring pesan dari klien. Hanya "offer" yang boleh diteruskan ke agent.
// Perintah lain (add_camera, remove_camera, dll.) dari klien DITOLAK, karena
// /ws/client tidak punya autentikasi. Perintah kamera hanya lewat endpoint API berkunci.
// Mengembalikan null bila pesan tidak diizinkan.
byte[]? PrepareClientMessage(byte[] data, string clientId)
{
    var text = Encoding.UTF8.GetString(data).TrimStart();
    JsonObject? msg;

    if (text.StartsWith('{'))
    {
        try { msg = JsonNode.Parse(text) as JsonObject; }
        catch (JsonException) { return null; }
        if (msg == null) return null;

        string? type;
        try { type = msg["type"]?.GetValue<string>(); }
        catch (InvalidOperationException) { return null; }
        if (type != "offer") return null;
    }
    else if (text.StartsWith("v=0"))
    {
        // SDP mentah (klien lama, misalnya aplikasi Android): bungkus sebagai offer
        msg = new JsonObject { ["type"] = "offer", ["sdp"] = text };
    }
    else
    {
        return null;
    }

    // clientId SELALU dibuat server; nilai kiriman klien ditimpa
    msg["clientId"] = clientId;
    return Encoding.UTF8.GetBytes(msg.ToJsonString());
}

// Meneruskan pesan dari agent ke klien yang tepat
async Task RouteAgentMessageAsync(string code, byte[] data, WebSocketMessageType type)
{
    if (!clients.TryGetValue(code, out var group) || group.IsEmpty) return;

    if (type == WebSocketMessageType.Text && TryUnwrapAgentReply(data, out var clientId, out var payload))
    {
        if (group.TryGetValue(clientId, out var target))
            await target.SendAsync(Encoding.UTF8.GetBytes(payload));
        return;
    }

    // Agent versi lama (tanpa clientId): kirim ke semua klien toko ini
    foreach (var c in group.Values)
        await c.SendAsync(data, type);
}

// ---------------------------------------------------------------------------
// ENDPOINT DIAGNOSA
// CATATAN: endpoint ini menampilkan SEMUA header request. Hapus atau batasi
// sebelum dipakai di produksi.
// ---------------------------------------------------------------------------
app.MapGet("/test", async context =>
{
    var info = new
    {
        status = "success",
        message = "Server CctvSignalingServer berhasil diakses!",
        serverTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        requestInfo = new
        {
            remoteIp = context.Connection.RemoteIpAddress?.ToString(),
            localIp = context.Connection.LocalIpAddress?.ToString(),
            host = context.Request.Host.Value,
            path = context.Request.Path.ToString(),
            userAgent = context.Request.Headers["User-Agent"].ToString()
        },
        headers = context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString())
    };

    context.Response.ContentType = "application/json";
    await context.Response.WriteAsJsonAsync(info);
});

// ---------------------------------------------------------------------------
// 1. ENDPOINT AGENT (komputer toko)
// ---------------------------------------------------------------------------
app.Map("/ws/agent", async context =>
{
    var code = context.Request.Query["code"].ToString();
    if (!context.WebSockets.IsWebSocketRequest || string.IsNullOrEmpty(code))
    {
        context.Response.StatusCode = 400;
        return;
    }

    var socket = await context.WebSockets.AcceptWebSocketAsync();
    var agent = new Peer(socket);

    // Agent baru dengan kode yang sama menggantikan yang lama (misalnya setelah sambung ulang)
    agents[code] = agent;
    Console.WriteLine($"[AGENT ONLINE] Toko '{code}' terhubung.");

    var buffer = new byte[16 * 1024];
    try
    {
        while (socket.State == WebSocketState.Open)
        {
            var msg = await ReceiveMessageAsync(socket, buffer);
            if (msg == null) break;
            await RouteAgentMessageAsync(code, msg.Value.Data, msg.Value.Type);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[ERROR AGENT] {ex.Message}");
    }
    finally
    {
        // Hapus HANYA bila entri masih milik koneksi ini. Kalau agent sudah menyambung
        // ulang, entri itu sudah milik koneksi baru dan tidak boleh ikut terhapus.
        agents.TryRemove(new KeyValuePair<string, Peer>(code, agent));
        await CloseQuietlyAsync(socket);
        Console.WriteLine($"[AGENT OFFLINE] Toko '{code}' terputus.");
    }
});

// ---------------------------------------------------------------------------
// 2. ENDPOINT CLIENT (browser / HP)
// ---------------------------------------------------------------------------
app.Map("/ws/client", async context =>
{
    var code = context.Request.Query["code"].ToString();
    if (!context.WebSockets.IsWebSocketRequest || string.IsNullOrEmpty(code))
    {
        context.Response.StatusCode = 400;
        return;
    }

    var socket = await context.WebSockets.AcceptWebSocketAsync();
    var client = new Peer(socket);

    // Tiap koneksi punya ID sendiri, jadi beberapa klien bisa menonton toko yang sama
    var clientId = Guid.NewGuid().ToString("N");
    var group = clients.GetOrAdd(code, _ => new ConcurrentDictionary<string, Peer>());
    group[clientId] = client;
    Console.WriteLine($"[CLIENT TERHUBUNG] {clientId[..6]} -> toko '{code}'");

    if (!agents.ContainsKey(code))
        await SendJsonErrorAsync(client, "Agent toko offline atau kode salah");

    var buffer = new byte[16 * 1024];
    try
    {
        while (socket.State == WebSocketState.Open)
        {
            var msg = await ReceiveMessageAsync(socket, buffer);
            if (msg == null) break;

            if (!agents.TryGetValue(code, out var agent))
            {
                await SendJsonErrorAsync(client, "Agent toko offline atau kode salah");
                continue;
            }

            var forwarded = PrepareClientMessage(msg.Value.Data, clientId);
            if (forwarded == null)
            {
                Console.WriteLine($"[TOLAK] Pesan dari klien {clientId[..6]} bukan offer yang valid.");
                continue;
            }

            if (!await agent.SendAsync(forwarded))
                await SendJsonErrorAsync(client, "Gagal menghubungi agent toko");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[ERROR CLIENT] {ex.Message}");
    }
    finally
    {
        group.TryRemove(clientId, out _); // hanya klien ini; klien lain di toko yang sama tidak terganggu
        await CloseQuietlyAsync(socket);
        Console.WriteLine($"[CLIENT PUTUS] {clientId[..6]} keluar dari toko '{code}'");
    }
});

// ---------------------------------------------------------------------------
// 3. ENDPOINT PERINTAH (dipanggil server web ASP.NET, bukan browser)
//    POST /api/agents/{kode}/commands
//    Header : X-Api-Key: <Signaling:CommandApiKey>
//    Body   : { "type":"add_camera", "cameraId":"cam_xxx", "src":"rtsp://..." }
//             { "type":"remove_camera", "cameraId":"cam_xxx" }
// ---------------------------------------------------------------------------
app.MapPost("/api/agents/{code}/commands", async (string code, HttpRequest req) =>
{
    // Tanpa kunci terkonfigurasi, endpoint dimatikan (lebih aman daripada terbuka)
    if (string.IsNullOrEmpty(commandApiKey))
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

    var givenKey = Encoding.UTF8.GetBytes(req.Headers["X-Api-Key"].ToString());
    var realKey = Encoding.UTF8.GetBytes(commandApiKey);
    if (!CryptographicOperations.FixedTimeEquals(givenKey, realKey)) // perbandingan waktu-tetap
        return Results.Unauthorized();

    if (!agents.TryGetValue(code, out var agent))
        return Results.NotFound(new { message = "Agent toko offline." });

    JsonObject? cmd;
    try { cmd = await JsonSerializer.DeserializeAsync<JsonObject>(req.Body); }
    catch (JsonException) { return Results.BadRequest(new { message = "Body bukan JSON yang valid." }); }

    string? type;
    try { type = cmd?["type"]?.GetValue<string>(); }
    catch (InvalidOperationException) { type = null; }

    if (type is not ("add_camera" or "remove_camera"))
        return Results.BadRequest(new { message = "type harus add_camera atau remove_camera." });

    var sent = await agent.SendAsync(Encoding.UTF8.GetBytes(cmd!.ToJsonString()));
    return sent ? Results.Ok() : Results.StatusCode(StatusCodes.Status502BadGateway);
});

app.Run("http://0.0.0.0:5000");

// ============================================================================
// Pembungkus WebSocket. Satu WebSocket TIDAK boleh mengirim dua pesan bersamaan.
// Bila dua klien menekan Play pada saat yang sama, keduanya mengirim ke agent
// yang sama; SemaphoreSlim membuat pengiriman bergantian sehingga tidak error.
// ============================================================================
sealed class Peer
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public Peer(WebSocket socket) => Socket = socket;

    public WebSocket Socket { get; }

    public async Task<bool> SendAsync(byte[] data, WebSocketMessageType type = WebSocketMessageType.Text)
    {
        if (Socket.State != WebSocketState.Open) return false;

        await _sendLock.WaitAsync();
        try
        {
            if (Socket.State != WebSocketState.Open) return false;
            await Socket.SendAsync(new ArraySegment<byte>(data), type, true, CancellationToken.None);
            return true;
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }
}
