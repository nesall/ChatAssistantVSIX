using System;
using System.IO;
using System.Linq;
using System.Threading;
using ChatAssistantVSIX.Utils;
using Newtonsoft.Json.Linq;

internal static class ChatStateTests
{
  public static int Run()
  {
    int count = 0;
    void Check(bool value, string why) { count++; if (!value) throw new Exception(why); }
    var session = new ChatSession();
    var first = session.Begin("Question", "a", "Model A");
    session.SetSources(first, new[] { "C:\\repo\\src\\a.cpp", "C:/repo/include/b.h", "C:/repo/src/a.cpp", "" });
    Check(first.Response.SourceLabels.SequenceEqual(new[] { "src/a.cpp", "include/b.h" }), "Source paths normalize separators, remove duplicates and strip common directory segments");
    Check(first.TargetApi == "a" && first.History.Count == 1 && first.History.Last().Role == "user", "Request pins provider and ends in user");
    session.Append(first, "partial"); session.Stop();
    Check(first.Response.Content == "partial" && !session.IsBusy && session.RetryRequest == first, "Stop keeps partial response and enables retry");
    session.Append(first, "late");
    session.SetSources(first, new[] { "stale.cpp" });
    Check(first.Response.Sources.Count == 2, "Stopped response rejects late source metadata");
    Check(first.Response.Content == "partial", "Stopped response rejects late tokens");
    var retry = session.Retry();
    Check(retry.Response.Sources.Count == 0, "Retry clears the original references");
    Check(session.Messages.Count == 2 && retry.History.Count == 1 && retry.TargetApi == "a", "Retry does not duplicate prompt and keeps original model/history");
    session.Append(first, "old"); session.Append(retry, "answer");
    session.SetSources(retry, new[] { "/project/src/single.cpp" });
    Check(retry.Response.SourceLabels.Single() == "single.cpp", "A single source retains its filename");
    session.Complete(retry);
    Check(retry.Response.Content == "answer" && !session.IsBusy, "Retry rejects original request callbacks");
    var next = session.Begin("Follow up", null, "Project default");
    Check(next.History.Count == 3 && next.History[1].Content == "answer", "Completed response included in followup");
    Check(next.History.All(m => m.Sources.Count == 0 && !m.Content.Contains("Sources:")), "Reference metadata is not sent back as assistant conversation text");
    session.SetSources(next, new[] { "C:/one/a.cpp", "D:/two/b.cpp" });
    Check(next.Response.SourceLabels.SequenceEqual(new[] { "C:/one/a.cpp", "D:/two/b.cpp" }), "Unrelated roots retain complete paths");
    session.Fail(next, "Network failed");
    var afterError = session.Begin("Try another question", "b", "Model B");
    Check(afterError.History.All(m => m.Content != "Network failed") && afterError.History.All(m => m.Role != "assistant" || m.Content == "answer"), "Error and partial assistant UI state excluded from history");
    session.Clear();
    var fresh = session.Begin("New", null, "Project default");
    session.Append(afterError, "stale"); session.Fail(afterError, "stale failure"); session.Complete(afterError);
    Check(fresh.Response.Content == "" && ReferenceEquals(session.Active, fresh) && session.Messages.Count == 2, "Clear invalidates all old callbacks");
    int updates = 0; fresh.Response.PropertyChanged += (_, e) => { if (e.PropertyName == "Content") updates++; };
    session.Append(fresh, "new response");
    Check(updates == 1, "Message content notifies without removing and reinserting bubbles");
    session.Clear(); Check(!session.IsBusy && session.RetryRequest == null, "Clear resets request and retry state");
    var providers = ChatProvider.FromSettings(JObject.Parse("{'currentApi':'auto','completionApis':[{'id':'auto','name':'Auto-router'},{'id':'a','name':'Local','model':'Coder'},{'id':'b','name':'Remote','model':'Coder'}]}"));
    Check(providers[0].Id == "auto" && providers[0].IsDefault && providers[0].Label.Contains("(default)") && providers.Count == 3, "Auto default appears once with a default marker");
    Check(providers[1].Id == "a" && providers[1].Detail.Contains("a") && providers[1].Label != providers[2].Label, "Friendly labels distinguish providers with same model");
    var missing = ChatProvider.FromSettings(JObject.Parse("{'currentApi':'removed','completionApis':[]}"));
    Check(missing.Count == 0, "Missing default must not create a synthetic model");
    var concrete = ChatProvider.FromSettings(JObject.Parse("{'currentApi':'flash','completionApis':[{'id':'flash','name':'MiMo','model':'mimo-v2.6-flash'},{'id':'pro','name':'MiMo','model':'mimo-v2.6-pro'}]}"));
    Check(concrete.Count == 2 && concrete[0].Label == "mimo-v2.6-flash (default)" && concrete[1].Label == "mimo-v2.6-pro", "Enabled models appear once and the actual default is marked");
    string model = null;
    var result = ChatProtocol.ReadCompletionAsync(new StringReader("data: {\"content\":\"answer\"}\ndata: {\"type\":\"context_sources\",\"model\":\"actual-model\"}\ndata: [DONE]\n"), null, CancellationToken.None,
      data => model = (string)data["model"]).GetAwaiter().GetResult();
    Check(model == "actual-model" && result == "answer", "Model metadata surfaced independently of assistant text");
    return count;
  }
}
