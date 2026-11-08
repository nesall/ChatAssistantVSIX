using System;
using System.IO;

namespace ChatAssistantVSIX.Utils
{
  internal static class ServicePathValidation
  {
    public static string Check(string executable, string tokenizer, string solutionDirectory)
    {
      var error = CheckFile(executable, null, "PhenixCode executable", "Runtime");
      return error ?? CheckFile(tokenizer, solutionDirectory, "Tokenizer file", "Index & storage");
    }

    private static string CheckFile(string path, string directory, string label, string tab)
    {
      if (string.IsNullOrWhiteSpace(path)) return label + " path is empty. Choose an existing file in Settings, " + tab + ".";
      try
      {
        var resolved = Path.GetFullPath(directory == null || Path.IsPathRooted(path) ? path : Path.Combine(directory, path));
        if (File.Exists(resolved)) return null;
        return label + " not found or inaccessible: " + resolved + ". Choose an existing file in Settings, " + tab +
          (directory == null ? "." : ". Relative paths resolve from the solution directory.");
      }
      catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is IOException || ex is System.Security.SecurityException || ex is UnauthorizedAccessException)
      {
        return label + " path is invalid: " + path + ". Choose an existing file in Settings, " + tab + ".";
      }
    }
  }
}
