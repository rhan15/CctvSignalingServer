using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Mengaktifkan dukungan WebSocket di .NET Core
app.UseWebSockets();

// Penyimpanan sementara di memori untuk Agent dan Client yang sedang aktif
// Key = Kode Pairing (contoh: "STORE-9988")
var registeredAgents = new ConcurrentDictionary<string, WebSocket>();
var activeClients = new ConcurrentDictionary<string, WebSocket>();

// 1. ENDPOINT AGENT: Komputer toko menyambung keluar ke server
app.Map("/ws/agent", async context =>
{
    if (context.WebSockets.IsWebSocketRequest)
    {
        var pairingCode = context.Request.Query["code"].ToString();
        if (string.IsNullOrEmpty(pairingCode))
        {
            context.Response.StatusCode = 400;
            return;
        }

        var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        registeredAgents[pairingCode] = webSocket;
        Console.WriteLine($"[AGENT ONLINE] Toko dengan kode '{pairingCode}' berhasil terhubung.");

        var buffer = new byte[1024 * 4];
        try
        {
            while (webSocket.State == WebSocketState.Open)
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) break;

                // Jika Agent mengirim pesan, teruskan ke Client (HP) yang sedang menonton
                string message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                if (activeClients.TryGetValue(pairingCode, out var clientSocket) && clientSocket.State == WebSocketState.Open)
                {
                    await clientSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(message)), WebSocketMessageType.Text, true, CancellationToken.None);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR AGENT] {ex.Message}");
        }
        finally
        {
            registeredAgents.TryRemove(pairingCode, out _);
            await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None);
            Console.WriteLine($"[AGENT OFFLINE] Toko dengan kode '{pairingCode}' terputus.");
        }
    }
    else
    {
        context.Response.StatusCode = 400;
    }
});

// 2. ENDPOINT CLIENT (HP ANDROID): Aplikasi HP menyambung untuk pairing & signaling
app.Map("/ws/client", async context =>
{
    if (context.WebSockets.IsWebSocketRequest)
    {
        var pairingCode = context.Request.Query["code"].ToString();
        var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        activeClients[pairingCode] = webSocket;
        Console.WriteLine($"[CLIENT TERHUBUNG] HP mencoba mengakses toko: '{pairingCode}'");

        // Cek apakah Agent toko tersebut sedang online
        if (!registeredAgents.ContainsKey(pairingCode))
        {
            string err = "{\"status\":\"error\",\"message\":\"Agent toko offline atau kode salah\"}";
            await webSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(err)), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        var buffer = new byte[1024 * 4];
        try
        {
            while (webSocket.State == WebSocketState.Open)
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) break;

                // Meneruskan pesan signaling (SDP/ICE) dari HP ke Agent toko
                string message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                if (registeredAgents.TryGetValue(pairingCode, out var agentSocket) && agentSocket.State == WebSocketState.Open)
                {
                    await agentSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(message)), WebSocketMessageType.Text, true, CancellationToken.None);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR CLIENT] {ex.Message}");
        }
        finally
        {
            activeClients.TryRemove(pairingCode, out _);
            await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None);
            Console.WriteLine($"[CLIENT PUTUS] HP keluar dari toko: '{pairingCode}'");
        }
    }
    else
    {
        context.Response.StatusCode = 400;
    }
});

app.Run("http://localhost:5000");