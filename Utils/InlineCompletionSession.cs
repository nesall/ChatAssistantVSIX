using System;
using System.Threading;

namespace ChatAssistantVSIX.Utils
{
  // UI-thread owned. A cancelled or completed request can never change a newer
  // request, even when its backend ignores cancellation and returns late.
  internal sealed class InlineCompletionSession
  {
    public sealed class Request
    {
      private CancellationTokenSource cancellation = new CancellationTokenSource();
      public object Owner { get; }
      public CancellationToken Token { get; }
      public bool Visible { get; internal set; }
      internal Request(object owner) { Owner = owner; Token = cancellation.Token; }
      internal void Cancel()
      {
        var source = Interlocked.Exchange(ref cancellation, null);
        if (source == null) return;
        try { source.Cancel(); } finally { source.Dispose(); }
      }
    }

    public Request Current { get; private set; }
    public Request Begin(object owner)
    {
      var old = Current;
      var next = new Request(owner);
      Current = next;
      old?.Cancel();
      return next;
    }
    public bool IsCurrent(Request request) => request != null && ReferenceEquals(Current, request) && !request.Token.IsCancellationRequested;
    public bool Show(Request request)
    {
      if (!IsCurrent(request)) return false;
      request.Visible = true;
      return true;
    }
    public bool End(Request request)
    {
      if (!ReferenceEquals(Current, request) || request == null) return false;
      Current = null;
      request.Cancel();
      return true;
    }
  }
}
