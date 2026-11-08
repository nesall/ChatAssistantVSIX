using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace ChatAssistantVSIX.Utils
{
  /// <summary>Conversation state. Only the active response can accept callbacks.</summary>
  public sealed class ChatSession
  {
    public sealed class Request
    {
      public Collection<ChatMessage> History { get; internal set; }
      public ChatMessage Response { get; internal set; }
      public string TargetApi { get; internal set; }
    }
    public ObservableCollection<ChatMessage> Messages { get; } = new ObservableCollection<ChatMessage>();
    public Request Active { get; private set; }
    public Request RetryRequest { get; private set; }
    public bool IsBusy => Active != null;

    public Request Begin(string prompt, string targetApi, string provider)
    {
      if (IsBusy) throw new InvalidOperationException("A response is already being generated.");
      if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("Enter a message.", nameof(prompt));
      RetryRequest = null;
      Messages.Add(new ChatMessage { Role = "user", Content = prompt.Trim(), IsComplete = true });
      var request = new Request
      {
        TargetApi = targetApi,
        History = new Collection<ChatMessage>(Messages.Where(m => m.Role == "user" || m.IsComplete)
          .Select(m => new ChatMessage { Role = m.Role, Content = m.Content }).ToList()),
        Response = new ChatMessage { Role = "assistant", Provider = provider, Status = "Thinking…" }
      };
      Messages.Add(request.Response);
      return Active = request;
    }
    public Request Retry()
    {
      if (IsBusy || RetryRequest == null) throw new InvalidOperationException("No failed or stopped response to retry.");
      var request = RetryRequest;
      RetryRequest = null;
      request = new Request { History = request.History, Response = request.Response, TargetApi = request.TargetApi };
      request.Response.Content = "";
      request.Response.Sources = Array.Empty<string>();
      request.Response.Status = "Thinking…";
      return Active = request;
    }
    public void Append(Request request, string text)
    {
      if (!ReferenceEquals(Active, request)) return;
      request.Response.Content += text;
      request.Response.Status = "Generating…";
    }
    public void Complete(Request request)
    {
      if (!ReferenceEquals(Active, request)) return;
      request.Response.IsComplete = true;
      request.Response.Status = "";
      Active = null;
    }
    public void SetSources(Request request, System.Collections.Generic.IEnumerable<string> sources)
    {
      if (!ReferenceEquals(Active, request)) return;
      request.Response.Sources = sources?.ToArray() ?? Array.Empty<string>();
    }
    public void Fail(Request request, string message)
    {
      if (!ReferenceEquals(Active, request)) return;
      request.Response.Status = message;
      Active = null;
      RetryRequest = request;
    }
    public void Stop()
    {
      if (Active != null) Fail(Active, "Stopped. Partial response kept.");
    }
    public void Clear()
    {
      Active = null;
      RetryRequest = null;
      Messages.Clear();
    }
  }
}
