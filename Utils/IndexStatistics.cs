using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ChatAssistantVSIX.Utils
{
  internal sealed class IndexStatistics
  {
    public IReadOnlyList<StatisticsValue> General { get; private set; }
    public IReadOnlyList<StatisticsCount> Directories { get; private set; }
    public IReadOnlyList<StatisticsCount> Languages { get; private set; }
    public IReadOnlyList<StatisticsFile> TopFiles { get; private set; }

    public static IndexStatistics Parse(string response, string solutionDirectory)
    {
      if (string.IsNullOrWhiteSpace(response)) throw new InvalidOperationException("The service returned no statistics.");
      JObject data;
      try { data = ConfigurationStore.ParseJson(response); }
      catch (JsonException ex) { throw new InvalidOperationException("The service returned an invalid statistics response.", ex); }
      if (data["error"] != null) throw new InvalidOperationException(data["error"].ToString());
      var sources = data["sources"] as JObject;
      if (data["total_chunks"] == null && data["vector_count"] == null && sources == null)
        throw new InvalidOperationException("The service response contains no statistics.");

      var files = (sources?["top_files"] as JArray ?? new JArray()).OfType<JObject>()
        .OrderByDescending(f => Count(f["chunks"]) ?? -1)
        .ThenBy(f => Text(f["path"]), StringComparer.OrdinalIgnoreCase)
        .Take(10).Select((file, i) => new StatisticsFile
        {
          Rank = (i + 1).ToString(CultureInfo.CurrentCulture),
          Path = DisplayPath(Text(file["path"]), solutionDirectory),
          FullPath = Text(file["path"]),
          Details = new[]
          {
            new StatisticsValue("Language", Text(file["language"])),
            new StatisticsValue("Chunks", Number(file["chunks"])),
            new StatisticsValue("Lines", Number(file["lines"])),
            new StatisticsValue("Size", Bytes(file["size_bytes"]), ExactBytes(file["size_bytes"])),
            new StatisticsValue("Modified (local)", Modified(file["last_modified"]))
          }
        }).ToArray();
      return new IndexStatistics
      {
        General = new[]
        {
          new StatisticsValue("Total chunks", Number(data["total_chunks"])),
          new StatisticsValue("Vector count", Number(data["vector_count"])),
          new StatisticsValue("Total files", Number(sources?["total_files"])),
          new StatisticsValue("Total lines", Number(sources?["total_lines"])),
          new StatisticsValue("Total size", Bytes(sources?["total_size_bytes"]), ExactBytes(sources?["total_size_bytes"]))
        },
        Directories = Counts(sources?["by_directory"], path => DisplayPath(path, solutionDirectory))
          .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
        Languages = Counts(sources?["by_language"], name => name)
          .OrderByDescending(row => row.Count).ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
        TopFiles = files
      };
    }

    private static IEnumerable<StatisticsCount> Counts(JToken token, Func<string, string> display)
      => (token as JObject ?? new JObject()).Properties()
        .Where(p => Count(p.Value).HasValue)
        .Select(p => new StatisticsCount { Name = display(p.Name), FullName = p.Name, Count = Count(p.Value).Value });

    private static long? Count(JToken token)
    {
      if (token?.Type != JTokenType.Integer) return null;
      try { var count = token.Value<long>(); return count >= 0 ? count : (long?)null; }
      catch (OverflowException) { return null; }
    }

    private static string Number(JToken token) => Count(token)?.ToString("N0", CultureInfo.CurrentCulture) ?? "—";
    private static string Text(JToken token) => token?.Type == JTokenType.String && !string.IsNullOrWhiteSpace(token.Value<string>()) ? token.Value<string>() : "—";
    private static string ExactBytes(JToken token) => Count(token).HasValue ? Number(token) + " bytes" : "Size unavailable";
    private static string Bytes(JToken token)
    {
      var bytes = Count(token);
      if (!bytes.HasValue) return "—";
      var units = new[] { "B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB" };
      double value = bytes.Value;
      int unit = 0;
      while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
      return value.ToString(unit == 0 ? "N0" : "0.#", CultureInfo.CurrentCulture) + " " + units[unit];
    }

    private static string Modified(JToken token)
    {
      var seconds = Count(token);
      if (!seconds.HasValue) return "—";
      try { return DateTimeOffset.FromUnixTimeSeconds(seconds.Value).ToLocalTime().ToString("g", CultureInfo.CurrentCulture); }
      catch (ArgumentOutOfRangeException) { return "—"; }
    }

    private static string DisplayPath(string path, string solutionDirectory)
    {
      var normalized = path.Replace('\\', '/');
      var root = solutionDirectory?.Replace('\\', '/').TrimEnd('/');
      if (!string.IsNullOrEmpty(root))
      {
        if (string.Equals(normalized.TrimEnd('/'), root, StringComparison.OrdinalIgnoreCase)) return "(solution root)";
        if (normalized.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)) return normalized.Substring(root.Length + 1);
      }
      return normalized;
    }
  }

  internal sealed class StatisticsValue
  {
    public string Caption { get; }
    public string Value { get; }
    public string ToolTip { get; }
    public StatisticsValue(string caption, string value, string toolTip = null) { Caption = caption; Value = value; ToolTip = toolTip ?? value; }
  }

  internal sealed class StatisticsCount
  {
    public string Name { get; set; }
    public string FullName { get; set; }
    public long Count { get; set; }
    public string FileCount => Count.ToString("N0", CultureInfo.CurrentCulture);
  }

  internal sealed class StatisticsFile
  {
    public string Rank { get; set; }
    public string Path { get; set; }
    public string FullPath { get; set; }
    public IReadOnlyList<StatisticsValue> Details { get; set; }
  }
}
