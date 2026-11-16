using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using StardewPilot.Platform;

// Tests for the mod's operating-system code. A tiny runner with no packages: it prints PASS/FAIL per test and
// exits non-zero if anything failed.

var tests = new List<(string Name, Func<Task> Run)>();
void Test(string name, Action run) => tests.Add((name, () => { run(); return Task.CompletedTask; }));
void TestAsync(string name, Func<Task> run) => tests.Add((name, run));
static void Check(bool ok, string what)
{
    if (!ok) throw new Exception(what);
}

string Temp(string label)
{
    // A space and non-ASCII letters, like real user folders can have.
    string dir = Path.Combine(Path.GetTempPath(), $"sp platform Ünï {label} {Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    return dir;
}

// ---------------- config folder ----------------

Test("config folder is <ApplicationData>/StardewPilot", () =>
{
    ConfigFolder.Override = null;
    string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StardewPilot");
    Check(ConfigFolder.Path == expected, $"got {ConfigFolder.Path}");
    if (OperatingSystem.IsWindows())
        Check(ConfigFolder.Path.StartsWith(Environment.GetEnvironmentVariable("APPDATA")!, StringComparison.OrdinalIgnoreCase), "must be under %APPDATA%, like the wrapper");
});

// ---------------- private files ----------------

Test("private file: written, and owner-only on Unix", () =>
{
    string path = Path.Combine(Temp("private"), "sub dir", "bridge.json");
    PrivateFile.Write(path, "{\"token\":\"x\"}");
    Check(File.ReadAllText(path) == "{\"token\":\"x\"}", "content");
    if (!OperatingSystem.IsWindows())
    {
        var stat = Process.Start(new ProcessStartInfo("stat", OperatingSystem.IsMacOS() ? $"-f %Lp \"{path}\"" : $"-c %a \"{path}\"") { RedirectStandardOutput = true })!;
        string mode = stat.StandardOutput.ReadToEnd().Trim();
        stat.WaitForExit();
        Check(mode == "600", $"mode {mode}");
    }
});

// ---------------- status file ----------------

Test("status file: missing, valid, half-written", () =>
{
    string dir = Temp("status");
    string path = Path.Combine(dir, "agent-status.json");
    Check(WrapperStatusInfo.Read(path) == WrapperStatusInfo.None, "missing file");
    File.WriteAllText(path, "{\"state\":\"running\",\"message\":\"Using x/y\",\"model\":\"x/y\",\"settingsUrl\":\"http://127.0.0.1:1/#t=abc\",\"pid\":42,\"piPid\":43}");
    var s = WrapperStatusInfo.Read(path)!;
    Check(s.State == "running" && s.Model == "x/y" && s.Pid == 42 && s.PiPid == 43, $"parsed {s}");
    File.WriteAllText(path, "{\"state\":\"runn");
    Check(WrapperStatusInfo.Read(path) == null, "half-written file should read as null");
});

TestAsync("status file: the wrapper's replace succeeds around the mod's brief reads", async () =>
{
    // On Windows a rename over a file that is open fails, even when the reader allowed deletion (seen in CI).
    // So the mod only holds the file for the moment it reads it, and the wrapper retries the rename
    // (files.ts writePrivateFile: up to 20 tries, 25 ms apart). This test does both at once.
    string dir = Temp("replace");
    string path = Path.Combine(dir, "agent-status.json");
    File.WriteAllText(path, "{\"state\":\"starting\"}");

    Task reader = Task.Run(async () =>
    {
        using (WrapperStatusInfo.OpenShared(path))
            await Task.Delay(150); // longer than any real read, shorter than the wrapper's retry window
    });
    await Task.Delay(20); // let the reader open first

    string tmp = path + ".123.tmp";
    File.WriteAllText(tmp, "{\"state\":\"running\"}");
    int attempts = 0;
    for (; ; attempts++)
    {
        try
        {
            File.Move(tmp, path, overwrite: true);
            break;
        }
        catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempts < 20)
        {
            await Task.Delay(25);
        }
    }
    await reader;
    Console.WriteLine($"      replaced after {attempts} retries");
    Check(WrapperStatusInfo.Read(path)!.State == "running", "replacement not visible");
});

// ---------------- processes ----------------

Test("process check", () =>
{
    Check(ProcessCheck.IsAlive(Environment.ProcessId), "this process");
    Check(!ProcessCheck.IsAlive(0) && !ProcessCheck.IsAlive(-5), "invalid pids");
});

Test("browser: start info per system, local links only", () =>
{
    var info = Browser.StartInfoFor("http://127.0.0.1:45782/#t=x");
    if (OperatingSystem.IsWindows())
        Check(info.UseShellExecute && info.FileName == "http://127.0.0.1:45782/#t=x", "Windows opens the link through the shell");
    else
        Check(!info.UseShellExecute && info.ArgumentList.SequenceEqual(new[] { "http://127.0.0.1:45782/#t=x" }), "link passed as one argument");
    Check(Browser.Open("https://example.com/") != null, "non-local links must be refused");
    Check(Browser.Open("file:///etc/passwd") != null, "file links must be refused");
});

Test("agent start: wrapper through node, paths with spaces kept whole", () =>
{
    string dir = Path.Combine(Temp("start"), "Mods", "Stardew Pilot", "agent", "src");
    Directory.CreateDirectory(Path.Combine(dir, "wrapper"));
    string ext = Path.Combine(dir, "index.ts");
    File.WriteAllText(ext, "");
    File.WriteAllText(Path.Combine(dir, "wrapper", "main.ts"), "");
    var (info, error, wrapper) = AgentStart.Build(new AgentLaunchOptions(true, "node", "pi", ext, "openai/gpt-5"));
    Check(error == null && info != null && wrapper, $"error {error}");
    Check(info!.FileName == "node", info.FileName);
    var args = info.ArgumentList.ToArray();
    Check(args.SequenceEqual(new[] { Path.Combine(dir, "wrapper", "main.ts"), "run", "--pi", "pi", "--extension", ext, "--model", "openai/gpt-5" }), string.Join(" | ", args));
    Check(info.RedirectStandardInput && !info.UseShellExecute, "stdin must be piped so the agent stops with the game");
});

Test("agent start: no wrapper is refused on Windows, allowed elsewhere", () =>
{
    string dir = Temp("nowrapper");
    string ext = Path.Combine(dir, "index.ts");
    File.WriteAllText(ext, "");
    var (info, error, _) = AgentStart.Build(new AgentLaunchOptions(false, "node", "pi", ext, ""));
    if (OperatingSystem.IsWindows())
        Check(info == null && error!.Contains("Windows"), "should refuse");
    else
        Check(info != null && info.FileName == "pi" && info.ArgumentList[0] == "--mode", "direct pi start");
    var (missing, missingError, _) = AgentStart.Build(new AgentLaunchOptions(true, "node", "pi", Path.Combine(dir, "nope.ts"), ""));
    Check(missing == null && missingError!.Contains("not found"), "missing extension");
});

Test("finding programs: PATH and, on Windows, .cmd scripts", () =>
{
    Check(ExecutableFinder.Find("dotnet") != null, "dotnet should be on PATH");
    string dir = Temp("path");
    string name = OperatingSystem.IsWindows() ? "fakepi.cmd" : "fakepi";
    File.WriteAllText(Path.Combine(dir, name), "");
    string old = Environment.GetEnvironmentVariable("PATH") ?? "";
    Environment.SetEnvironmentVariable("PATH", dir + Path.PathSeparator + old);
    try
    {
        string? found = ExecutableFinder.Find("fakepi");
        Check(found != null && Path.GetFileName(found).Equals(name, StringComparison.OrdinalIgnoreCase), $"found {found}");
    }
    finally
    {
        Environment.SetEnvironmentVariable("PATH", old);
    }
});

// ---------------- loopback listener ----------------

TestAsync("bridge listener: starts on loopback and accepts a WebSocket", async () =>
{
    int port;
    var probe = new TcpListener(IPAddress.Loopback, 0);
    probe.Start();
    port = ((IPEndPoint)probe.LocalEndpoint).Port;
    probe.Stop();
    var (listener, url) = Loopback.Start(port);
    try
    {
        Check(url == $"ws://127.0.0.1:{port}/" || url == $"ws://localhost:{port}/", url);
        Console.WriteLine($"      listening at {url}");
        Task server = Task.Run(async () =>
        {
            HttpListenerContext ctx = await listener.GetContextAsync();
            Check(ctx.Request.IsWebSocketRequest, "not a WebSocket request");
            HttpListenerWebSocketContext ws = await ctx.AcceptWebSocketAsync(null);
            byte[] hello = Encoding.UTF8.GetBytes("hello");
            await ws.WebSocket.SendAsync(hello, WebSocketMessageType.Text, true, CancellationToken.None);
            await ws.WebSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        });
        using var client = new ClientWebSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(new Uri(url), timeout.Token);
        var buffer = new byte[64];
        WebSocketReceiveResult got = await client.ReceiveAsync(buffer, timeout.Token);
        Check(Encoding.UTF8.GetString(buffer, 0, got.Count) == "hello", "message");
        await server.WaitAsync(TimeSpan.FromSeconds(10));
    }
    finally
    {
        listener.Close();
    }
});

// ---------------- diagnostics ----------------

Test("pilot_diagnose: no token, key or settings link in the report", () =>
{
    string dir = Temp("diag");
    ConfigFolder.Override = dir;
    try
    {
        const string token = "0123456789abcdef0123456789abcdef";
        File.WriteAllText(Path.Combine(dir, "agent-status.json"),
            "{\"state\":\"error\",\"message\":\"pi failed with token " + token + " http://127.0.0.1:45782/#t=PAGEKEYabc_-123\",\"model\":\"a/b\",\"settingsUrl\":\"http://127.0.0.1:45782/#t=PAGEKEYabc_-123\",\"pid\":1}");
        File.WriteAllText(Path.Combine(dir, "bridge.json"), "{\"token\":\"" + token + "\"}");
        string report = Diagnostics.Build(new DiagnoseInfo("0.3.0", "1.6.15", "4.5.2", true, true, "node", "pi", Path.Combine(dir, "index.ts"), "ws://127.0.0.1:45781/", 0, new[] { token }));
        Console.WriteLine("      " + report.Replace("\n", "\n      "));
        Check(!report.Contains(token), "token leaked");
        Check(!report.Contains("PAGEKEY"), "settings key leaked");
        Check(report.Contains("Wrapper: error"), "wrapper state missing");
    }
    finally
    {
        ConfigFolder.Override = null;
    }
});

foreach (var (name, run) in ApprenticeTests.All())
    Test(name, run);

int failed = 0;
foreach (var (name, run) in tests)
{
    try
    {
        await run();
        Console.WriteLine($"PASS  {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FAIL  {name}: {ex.Message}");
    }
}
Console.WriteLine($"{tests.Count - failed}/{tests.Count} passed on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
return failed == 0 ? 0 : 1;
