using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ChatAssistantVSIX.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class FimServiceIntegration
{
  public static async Task RunAsync(string sandbox)
  {
    int checks = 0;
    Action<bool,string> check = (ok, message) => { if (!ok) throw new Exception(message); checks++; };
    check(Path.GetFullPath(PhenixCodeCoreService.SettingsPath).StartsWith(sandbox + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Core configuration was not redirected");
    await PhenixCodeCoreService.InitAsync();
    File.WriteAllText(PhenixCodeCoreService.PreferencesPath, "{}");
    var directory = Path.Combine(sandbox, "fim-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
    var solution = Path.Combine(directory, "fixture.sln"); File.WriteAllText(solution, "fixture");
    PhenixCodeCoreService.InitOnSolutionReady(new Community.VisualStudio.Toolkit.Solution { Name = "FIM fixture", FullPath = solution });
    var settings = ConfigurationStore.ReadJson(PhenixCodeCoreService.SolutionSettingsPath);
    settings["generation"]["current_api"] = "a";
    settings["generation"]["enabled_providers"] = new JArray("a", "b");
    settings["generation"]["timeout_ms"] = 5000;
    File.WriteAllText(PhenixCodeCoreService.SolutionSettingsPath, settings.ToString());
    var catalog = ConfigurationStore.ReadJson(PhenixCodeCoreService.ProvidersPath);
    catalog["generation_providers"] = new JArray(new JObject { ["id"]="a", ["enabled"]=true, ["fim"]=new JObject() }, new JObject { ["id"]="b", ["enabled"]=true, ["fim"]=new JObject() });
    File.WriteAllText(PhenixCodeCoreService.ProvidersPath, catalog.ToString());

    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    File.WriteAllText(Path.Combine(directory, ".phenixcode/info.json"), new JObject { ["port"] = port }.ToString());
    var received = new ConcurrentQueue<JObject>(); var handlers = new ConcurrentBag<Task>();
    var server = Task.Run(async () => {
      try { while (true) { var client = await listener.AcceptTcpClientAsync(); handlers.Add(Respond(client, received)); } }
      catch (ObjectDisposedException) { } catch (SocketException) { }
    });
    try
    {
      var completion = await PhenixCodeCoreService.FillInTheMiddleAsync("success", ");", "fixture.cpp");
      check(completion == "fixture completion", "FIM response not returned");
      check(received.TryPeek(out var request) && (string)request["prefix"] == "success" && (string)request["suffix"] == ");" && (string)request["filename"] == "fixture.cpp", "FIM context/filename changed");
      check((string)request["targetapi"] == "a" && (int)request["max_tokens"] == 256 && (double)request["temperature"] == 0, "FIM routing/token limit changed");
      File.WriteAllText(PhenixCodeCoreService.PreferencesPath, "{\"fim_provider\":\"b\"}");
      await PhenixCodeCoreService.FillInTheMiddleAsync("explicit", "", "fixture.cpp");
      check((string)received.Last()["targetapi"] == "b", "FIM provider preference was ignored");
      File.WriteAllText(PhenixCodeCoreService.PreferencesPath, "{\"fim_provider\":\"missing\"}");
      try { await PhenixCodeCoreService.FillInTheMiddleAsync("invalid", "", "fixture.cpp"); throw new Exception("Invalid provider accepted"); }
      catch (InvalidOperationException ex) { check(ex.Message.Contains("FIM-capable"), "Invalid provider lacks actionable error"); }
      File.WriteAllText(PhenixCodeCoreService.PreferencesPath, "{}");
      using (var cancelled = new CancellationTokenSource())
      {
        cancelled.Cancel(); int before = received.Count;
        try { await PhenixCodeCoreService.FillInTheMiddleAsync("cancelled", "", "fixture.cpp", cancelled.Token); throw new Exception("Cancelled request accepted"); }
        catch (OperationCanceledException) { check(received.Count == before, "Pre-cancelled request reached backend"); }
      }
      using (var cancelled = new CancellationTokenSource())
      {
        var pending = PhenixCodeCoreService.FillInTheMiddleAsync("slow", "", "fixture.cpp", cancelled.Token);
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!received.Any(r => (string)r["prefix"] == "slow")) { if (DateTime.UtcNow > deadline) throw new TimeoutException(); await Task.Delay(10); }
        cancelled.Cancel();
        try { await pending; throw new Exception("In-flight cancellation swallowed"); }
        catch (OperationCanceledException) { check(true, "In-flight cancellation propagated"); }
      }
      try { await PhenixCodeCoreService.FillInTheMiddleAsync("failure", "", "fixture.cpp"); throw new Exception("HTTP failure swallowed"); }
      catch (HttpRequestException) { check(true, "HTTP failure surfaced"); }
      try { await PhenixCodeCoreService.FillInTheMiddleAsync("malformed", "", "fixture.cpp"); throw new Exception("Invalid JSON swallowed"); }
      catch (JsonException) { check(true, "Invalid JSON surfaced"); }
      try { await PhenixCodeCoreService.FillInTheMiddleAsync("error", "", "fixture.cpp"); throw new Exception("Backend error swallowed"); }
      catch (InvalidOperationException ex) { check(ex.Message == "Fixture backend error", "Backend error lost"); }
      check(await PhenixCodeCoreService.FillInTheMiddleAsync("empty", "", "fixture.cpp") == "", "Empty completion changed");
      settings["generation"]["timeout_ms"] = 1000;
      File.WriteAllText(PhenixCodeCoreService.SolutionSettingsPath, settings.ToString());
      try { await PhenixCodeCoreService.FillInTheMiddleAsync("timeout", "", "fixture.cpp"); throw new Exception("Timeout swallowed"); }
      catch (TimeoutException ex) { check(ex.Message.Contains("timed out"), "Timeout was not actionable"); }
      Console.WriteLine("PASS: " + checks + " FIM HTTP assertions; dynamic test port=" + port);
    }
    finally { listener.Stop(); await server; await Task.WhenAll(handlers); }
  }

  private static async Task Respond(TcpClient client, ConcurrentQueue<JObject> received)
  {
    using (client)
    {
      try
      {
        using var stream = client.GetStream(); using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true);
        var line = await reader.ReadLineAsync(); if (line != "POST /api/fim HTTP/1.1") throw new Exception("Incorrect endpoint/method: " + line);
        int length = 0; bool expectContinue = false;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
        {
          if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line.Substring(15).Trim());
          if (line.StartsWith("Expect:", StringComparison.OrdinalIgnoreCase) && line.IndexOf("100-continue", StringComparison.OrdinalIgnoreCase) >= 0) expectContinue = true;
        }
        if (expectContinue) { var interim = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"); await stream.WriteAsync(interim,0,interim.Length); }
        var chars = new char[length]; int read = 0;
        while (read < length) { int count = await reader.ReadAsync(chars, read, length-read); if (count == 0) throw new EndOfStreamException(); read += count; }
        var request = JObject.Parse(new string(chars)); received.Enqueue(request);
        var prefix = (string)request["prefix"];
        if (prefix == "slow") await Task.Delay(400);
        if (prefix == "timeout") await Task.Delay(1400);
        var body = prefix == "malformed" ? "not json" : prefix == "error" ? "{\"error\":\"Fixture backend error\"}" : new JObject { ["completion"] = prefix == "empty" ? "" : "fixture completion" }.ToString(Formatting.None);
        var bytes = Encoding.UTF8.GetBytes(body);
        var header = Encoding.ASCII.GetBytes("HTTP/1.1 " + (prefix == "failure" ? "500 Error" : "200 OK") + "\r\nContent-Type: application/json\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header,0,header.Length); await stream.WriteAsync(bytes,0,bytes.Length);
      }
      catch (IOException) { } catch (ObjectDisposedException) { }
    }
  }
}
