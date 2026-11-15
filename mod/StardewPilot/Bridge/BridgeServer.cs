using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StardewModdingAPI;

namespace StardewPilot.Bridge;

/// <summary>
/// Local WebSocket server speaking JSON-RPC 2.0. Method names and tool shapes follow MCP
/// (tools/list, tools/call) so the agent side is not tied to pi.
/// </summary>
internal sealed class BridgeServer : IDisposable
{
    public delegate Task<object?> MethodHandler(JObject? parameters, BridgeClient client);

    private readonly int port;
    private readonly byte[] token;
    private readonly IMonitor monitor;
    private readonly Dictionary<string, MethodHandler> methods = new();
    private readonly ConcurrentDictionary<BridgeClient, byte> clients = new();
    private readonly CancellationTokenSource stop = new();
    private HttpListener? listener;

    /// <summary>Raised on a thread-pool thread when a client connects or disconnects.</summary>
    public event Action<int>? ClientCountChanged;

    public BridgeServer(int port, string token, IMonitor monitor)
    {
        this.port = port;
        this.token = Encoding.UTF8.GetBytes(token);
        this.monitor = monitor;
    }

    public int ClientCount => this.clients.Count;

    public void Register(string method, MethodHandler handler) => this.methods[method] = handler;

    public void Start()
    {
        this.listener = new HttpListener();
        this.listener.Prefixes.Add($"http://127.0.0.1:{this.port}/");
        this.listener.Start();
        _ = Task.Run(this.AcceptLoop);
    }

    /// <summary>Send a JSON-RPC notification to every connected agent.</summary>
    public void Broadcast(string method, object payload)
    {
        string text = JsonConvert.SerializeObject(new { jsonrpc = "2.0", method, @params = payload });
        foreach (BridgeClient client in this.clients.Keys)
            _ = client.SendAsync(text);
    }

    private async Task AcceptLoop()
    {
        while (!this.stop.IsCancellationRequested && this.listener is { IsListening: true })
        {
            HttpListenerContext ctx;
            try { ctx = await this.listener.GetContextAsync(); }
            catch when (this.stop.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                this.monitor.Log($"Bridge accept failed: {ex.Message}", LogLevel.Trace);
                continue;
            }
            _ = Task.Run(() => this.HandleConnection(ctx));
        }
    }

    private async Task HandleConnection(HttpListenerContext ctx)
    {
        try
        {
            // Browsers always send Origin; the agent never does. Rejecting it blocks web pages
            // from reaching the bridge even if they guess the port.
            if (ctx.Request.Headers["Origin"] != null)
            {
                Reject(ctx, 403);
                return;
            }
            if (!ctx.Request.IsWebSocketRequest)
            {
                Reject(ctx, 400);
                return;
            }
            string presented = ctx.Request.QueryString["token"] ?? "";
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), this.token))
            {
                this.monitor.Log("Bridge rejected a connection with a wrong token.", LogLevel.Warn);
                Reject(ctx, 401);
                return;
            }

            HttpListenerWebSocketContext wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null);
            var client = new BridgeClient(wsCtx.WebSocket, ctx);
            this.clients[client] = 0;
            this.monitor.Log("Agent connected to the bridge.", LogLevel.Info);
            this.ClientCountChanged?.Invoke(this.clients.Count);
            try
            {
                await this.ReceiveLoop(client);
            }
            finally
            {
                this.clients.TryRemove(client, out _);
                client.Dispose();
                this.monitor.Log("Agent disconnected from the bridge.", LogLevel.Info);
                this.ClientCountChanged?.Invoke(this.clients.Count);
            }
        }
        catch (Exception ex)
        {
            this.monitor.Log($"Bridge connection error: {ex.Message}", LogLevel.Trace);
        }
    }

    private static void Reject(HttpListenerContext ctx, int status)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.KeepAlive = false;
        ctx.Response.ContentLength64 = 0;
        ctx.Response.Close();
    }

    private async Task ReceiveLoop(BridgeClient client)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (client.Socket.State == WebSocketState.Open && !this.stop.IsCancellationRequested)
        {
            WebSocketReceiveResult result = await client.Socket.ReceiveAsync(buffer, this.stop.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await client.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                return;
            }
            message.Write(buffer, 0, result.Count);
            if (message.Length > 1024 * 1024)
            {
                await client.Socket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "message too big", CancellationToken.None);
                return;
            }
            if (!result.EndOfMessage)
                continue;

            string text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);
            _ = Task.Run(() => this.Dispatch(client, text));
        }
    }

    private async Task Dispatch(BridgeClient client, string text)
    {
        JToken? id = null;
        try
        {
            JObject request = JObject.Parse(text);
            id = request["id"];
            string method = request.Value<string>("method") ?? "";
            JObject? parameters = request["params"] as JObject;

            if (!this.methods.TryGetValue(method, out MethodHandler? handler))
            {
                if (id != null)
                    await client.SendAsync(Error(id, -32601, $"Unknown method '{method}'."));
                return;
            }

            object? result = await handler(parameters, client);
            if (id != null) // requests get a reply, notifications do not
                await client.SendAsync(JsonConvert.SerializeObject(new { jsonrpc = "2.0", id, result }));
        }
        catch (JsonException ex)
        {
            await client.SendAsync(Error(null, -32700, $"Parse error: {ex.Message}"));
        }
        catch (Exception ex)
        {
            this.monitor.Log($"Bridge method failed: {ex}", LogLevel.Trace);
            if (id != null)
                await client.SendAsync(Error(id, -32603, ex.Message));
        }
    }

    private static string Error(JToken? id, int code, string message) =>
        JsonConvert.SerializeObject(new { jsonrpc = "2.0", id, error = new { code, message } });

    public void Dispose()
    {
        this.stop.Cancel();
        foreach (BridgeClient client in this.clients.Keys)
            client.Dispose();
        try { this.listener?.Stop(); } catch { /* shutting down */ }
        this.listener?.Close();
    }
}

/// <summary>One connected agent.</summary>
internal sealed class BridgeClient : IDisposable
{
    private readonly SemaphoreSlim sendLock = new(1, 1);

    private readonly HttpListenerContext httpContext;

    public BridgeClient(WebSocket socket, HttpListenerContext httpContext)
    {
        this.Socket = socket;
        this.httpContext = httpContext;
    }

    public WebSocket Socket { get; }

    public async Task SendAsync(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        await this.sendLock.WaitAsync();
        try
        {
            if (this.Socket.State == WebSocketState.Open)
                await this.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch { /* client went away; the receive loop cleans up */ }
        finally { this.sendLock.Release(); }
    }

    public void Dispose()
    {
        try { this.Socket.Abort(); } catch { }
        this.Socket.Dispose();
        // HttpListener keeps the TCP connection open after a WebSocket closes; drop it so the client sees the close.
        try { this.httpContext.Response.Abort(); } catch { }
    }
}
