using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseWebSockets();

var registeredAgents = new ConcurrentDictionary<string, WebSocket>();
var activeClients = new ConcurrentDictionary<string, WebSocket>();

// 1. ENDPOINT AGENT
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

        var buffer = new byte[1024 * 16];
        try
        {
            while (webSocket.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close) break;

                // Teruskan pesan utuh dari Agent ke Client
                if (activeClients.TryGetValue(pairingCode, out var clientSocket) && clientSocket.State == WebSocketState.Open)
                {
                    await clientSocket.SendAsync(new ArraySegment<byte>(ms.ToArray()), result.MessageType, true, CancellationToken.None);
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
            if (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseReceived)
            {
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None);
            }
            Console.WriteLine($"[AGENT OFFLINE] Toko dengan kode '{pairingCode}' terputus.");
        }
    }
    else
    {
        context.Response.StatusCode = 400;
    }
});

// 2. ENDPOINT CLIENT (HP ANDROID / BROWSER)
app.Map("/ws/client", async context =>
{
    if (context.WebSockets.IsWebSocketRequest)
    {
        var pairingCode = context.Request.Query["code"].ToString();
        var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        activeClients[pairingCode] = webSocket;
        Console.WriteLine($"[CLIENT TERHUBUNG] HP mencoba mengakses toko: '{pairingCode}'");

        if (!registeredAgents.ContainsKey(pairingCode))
        {
            string err = "{\"status\":\"error\",\"message\":\"Agent toko offline atau kode salah\"}";
            await webSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(err)), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        var buffer = new byte[1024 * 16];
        try
        {
            while (webSocket.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close) break;

                // Teruskan pesan utuh dari Client ke Agent
                if (registeredAgents.TryGetValue(pairingCode, out var agentSocket) && agentSocket.State == WebSocketState.Open)
                {
                    await agentSocket.SendAsync(new ArraySegment<byte>(ms.ToArray()), result.MessageType, true, CancellationToken.None);
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
            if (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseReceived)
            {
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None);
            }
            Console.WriteLine($"[CLIENT PUTUS] HP keluar dari toko: '{pairingCode}'");
        }
    }
    else
    {
        context.Response.StatusCode = 400;
    }
});

app.Run("http://localhost:5000");