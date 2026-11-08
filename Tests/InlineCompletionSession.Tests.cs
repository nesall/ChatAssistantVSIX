using System;
using ChatAssistantVSIX.Utils;

internal static class InlineCompletionSessionTests
{
  public static int Run()
  {
    int count = 0;
    Action<bool, string> check = (ok, message) => { if (!ok) throw new Exception(message); count++; };
    var session = new InlineCompletionSession();
    var editorA = new object(); var editorB = new object();
    var old = session.Begin(editorA);
    var next = session.Begin(editorA);
    check(old.Token.IsCancellationRequested, "A repeated request did not cancel its predecessor");
    check(!session.Show(old) && !session.End(old), "A late result ended/replaced the current request");
    check(session.IsCurrent(next) && !next.Visible, "A stale request changed the new loading state");
    check(session.Show(next), "Current result was rejected");
    var other = session.Begin(editorB);
    check(next.Token.IsCancellationRequested && !session.Show(next), "Switching editors retained the old suggestion");
    check(!session.End(next) && session.IsCurrent(other), "An old native dismissal cancelled another editor's request");
    check(session.End(other) && other.Token.IsCancellationRequested, "Dismissal did not cancel generation");
    check(!session.Show(other), "A discarded request resurrected a suggestion");
    var empty = session.Begin(editorA);
    check(session.End(empty) && session.Current == null, "An empty/error completion left an active request");
    var accepted = session.Begin(editorA); session.Show(accepted);
    check(session.End(accepted) && !session.End(accepted), "Acceptance completion was not idempotent");
    var final = session.Begin(editorA);
    check(session.IsCurrent(final) && !session.End(old), "A much older callback interfered with a later request");
    session.End(final);
    return count;
  }
}
