using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ChatAssistantVSIX.Utils
{
  /// <summary>The local core's request and SSE contract, independent of the Visual Studio host.</summary>
  public static class ChatProtocol
  {
    public static JObject CreateRequest(IEnumerable<ChatMessage> messages, IEnumerable<string> sourceIds = null,
      JArray attachments = null, double? temperature = null, int? maxTokens = null, string targetApi = null,
      double contextRatio = 0.5, bool attachedOnly = false)
    {
      var history = new JArray((messages ?? throw new ArgumentNullException(nameof(messages)))
        .Select(m => new JObject { ["role"] = m.Role, ["content"] = m.Content }));
      if (history.Count == 0 || (string)history.Last["role"] != "user")
        throw new ArgumentException("The last chat request message must be from the user.", nameof(messages));
      var request = new JObject
      {
        ["messages"] = history, ["sourceids"] = new JArray(sourceIds ?? Enumerable.Empty<string>()),
        ["attachments"] = attachments?.DeepClone() ?? new JArray(),
        ["ctxratio"] = contextRatio, ["attachedonly"] = attachedOnly
      };
      // Absence means project defaults, including automatic routing. JSON null is not equivalent for the core.
      if (!string.IsNullOrWhiteSpace(targetApi)) request["targetapi"] = targetApi;
      if (temperature.HasValue) request["temperature"] = temperature.Value;
      // The current core tests is_number_float() when reading this optional override.
      if (maxTokens.HasValue) request["max_tokens"] = (double)maxTokens.Value;
      return request;
    }

    public static async Task<string> ReadCompletionAsync(TextReader reader, Action<string> onTokenReceived,
      CancellationToken cancellationToken, Action<JObject> onMetadata = null)
    {
      var result = new StringBuilder();
      bool completed = false;
      string line;
      while ((line = await reader.ReadLineAsync()) != null)
      {
        cancellationToken.ThrowIfCancellationRequested();
        if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
        var payload = line.Substring(5).Trim();
        if (payload == "[DONE]") { completed = true; break; }
        if (payload.Length == 0) continue;
        var token = JObject.Parse(payload);
        if (token["error"] != null) throw new InvalidOperationException(token["error"].ToString());
        var chunk = token["content"]?.Value<string>();
        if (string.IsNullOrEmpty(chunk) || chunk.StartsWith("[meta]", StringComparison.Ordinal))
        {
          onMetadata?.Invoke(token);
          continue;
        }
        result.Append(chunk);
        onTokenReceived?.Invoke(payload);
      }
      cancellationToken.ThrowIfCancellationRequested();
      if (!completed) throw new EndOfStreamException("The response ended before completion. Partial text was kept; retry the response.");
      return result.ToString();
    }
  }
}
