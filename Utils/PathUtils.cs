using System.Collections.Generic;
using System.Linq;


namespace ChatAssistantVSIX.Utils
{
  public static class PathUtils
  {
    static string Normalize(string p)
        => p.Replace('\\', '/');

    static string ParentDir(string dir)
    {
      if (string.IsNullOrEmpty(dir)) return dir;
      int pos = dir.LastIndexOf('/', dir.Length - 2);
      return pos < 0 ? "" : dir.Substring(0, pos + 1);
    }

    public static List<string> CommonPaths(IReadOnlyList<string> files, int maxStepUp)
    {
      if (files == null || files.Count == 0)
        return new List<string>();

      var dirs = new List<string>(files.Count);
      foreach (var f in files)
      {
        var n = Normalize(f);
        int p = n.LastIndexOf('/');
        dirs.Add(p < 0 ? "" : n.Substring(0, p + 1));
      }

      // global common directory prefix
      string common = dirs[0];
      for (int i = 1; i < dirs.Count; i++)
      {
        int j = 0;
        while (j < common.Length &&
               j < dirs[i].Length &&
               common[j] == dirs[i][j])
          j++;

        common = common.Substring(0, j);
        int cut = common.LastIndexOf('/');
        common = cut < 0 ? "" : common.Substring(0, cut + 1);
      }

      var result = new HashSet<string>(StringComparer.Ordinal);
      foreach (var start in dirs)
      {
        string d = start;
        string candidate = null;
        for (int s = 0; s <= maxStepUp && d.Length > 0; s++)
        {
          if (!d.StartsWith(common, StringComparison.Ordinal))
            break;

          candidate = d;
          d = ParentDir(d);
        }
        if (!string.IsNullOrEmpty(candidate))
        {
          result.Add(candidate);
        }
      }

      return result
          .OrderByDescending(p => p.Length)
          .ToList();
    }
  }

}
