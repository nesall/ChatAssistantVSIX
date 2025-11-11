using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Microsoft.Win32;
using System.Diagnostics;
using System.Windows.Forms;
using System.Drawing.Design;

namespace ChatAssistantVSIX.Options
{
  [ComVisible(true)]
  //[Guid("673a9a34-a132-4e1d-8da6-45b87ace5026")]
  public class IndexerOptionsPage : DialogPage
  {
    #region --- UI properties (only these appear in the grid) ---

    [Category("Folders")]
    [DisplayName("Root paths")]
    [Description("Semicolon-separated folders to index.  Use $(SolutionDir) for the open solution.")]
    [Editor(typeof(FolderListEditor), typeof(UITypeEditor))]
    public string RootPaths { get; set; } = "$(SolutionDir)";

    [Category("Folders")]
    [DisplayName("Exclude patterns")]
    [Description("Glob patterns to skip (semicolon separated).")]
    public string ExcludePatterns { get; set; } =
        "**/bin/**;**/obj/**;**/node_modules/**;**/.git/**;**/dist/**";

    // ---------- Embedding ----------

    [Category("Embedding")]
    [DisplayName("Current API")]
    [Description("Which embedding API block to activate.")]
    [TypeConverter(typeof(ApiChoiceConverter))]
    public string EmbeddingApi { get; set; } = "local";

    [Category("Embedding")]
    [DisplayName("API URL")]
    [Description("Only used when 'Current API' = custom")]
    public string EmbeddingUrl { get; set; } = "";

    [Category("Embedding")]
    [DisplayName("API key")]
    [Description("Only used when 'Current API' = custom")]
    [PasswordPropertyText(true)]
    public string EmbeddingKey { get; set; } = "";

    [Category("Embedding")]
    [DisplayName("Model name")]
    [Description("Only used when 'Current API' = custom")]
    public string EmbeddingModel { get; set; } = "";

    // ---------- Generation ----------

    [Category("Generation")]
    [DisplayName("Current API")]
    [Description("Which generation API block to activate.")]
    [TypeConverter(typeof(ApiChoiceConverter))]
    public string GenerationApi { get; set; } = "mistral-devstral";

    [Category("Generation")]
    [DisplayName("API URL")]
    [Description("Only used when 'Current API' = custom")]
    public string GenerationUrl { get; set; } = "";

    [Category("Generation")]
    [DisplayName("API key")]
    [Description("Only used when 'Current API' = custom")]
    [PasswordPropertyText(true)]
    public string GenerationKey { get; set; } = "";

    [Category("Generation")]
    [DisplayName("Model name")]
    [Description("Only used when 'Current API' = custom")]
    public string GenerationModel { get; set; } = "";

    // ---------- Chunking ----------

    [Category("Chunking")]
    [DisplayName("Min tokens")]
    [Description("Smallest semantic chunk allowed.")]
    public int ChunkMinTokens { get; set; } = 50;

    [Category("Chunking")]
    [DisplayName("Max tokens")]
    [Description("Largest semantic chunk allowed.")]
    public int ChunkMaxTokens { get; set; } = 450;

    [Category("Chunking")]
    [DisplayName("Overlap %")]
    [Description("Overlap between consecutive chunks (0-100).")]
    [TypeConverter(typeof(OverlapConverter))]
    public int ChunkOverlapPercent { get; set; } = 20;

    // ---------- Helpers ----------

    [Category("Configuration file")]
    [DisplayName("Open full settings")]
    [Description("Opens settings.json in Visual Studio.")]
    [Editor(typeof(OpenSettingsEditor), typeof(UITypeEditor))]
    public string OpenSettings => "Click [...]";

    [Category("Configuration file")]
    [DisplayName("Restore defaults")]
    [Description("Overwrite settings.json with the template shipped in the extension.")]
    [Editor(typeof(RestoreDefaultsEditor), typeof(UITypeEditor))]
    public string RestoreDefaults => "Click [...]";

    #endregion

    #region --- load / save ---

    public override void LoadSettingsFromStorage()
    {
      base.LoadSettingsFromStorage();
      var json = ReadJson();
      MapJsonToGrid(json);
    }

    public override void SaveSettingsToStorage()
    {
      var json = ReadJson();
      MapGridToJson(json);
      WriteJson(json);
      base.SaveSettingsToStorage();

      // notify running service
      _ = Task.Run(async () =>
      {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var uiShell = (IVsUIShell)Package.GetGlobalService(typeof(SVsUIShell));
        uiShell?.UpdateCommandUI(0);
      });
    }

    #endregion

    #region --- helpers ---

    static public string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "ChatAssistant", "settings.json");

    static JObject ReadJson()
    {
      try
      {
        return JObject.Parse(File.ReadAllText(SettingsPath));
      }
      catch
      {
        // first run – copy template
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
        var tpl = Path.Combine(
            Path.GetDirectoryName(typeof(IndexerOptionsPage).Assembly.Location),
            "settings.template.json");
        if (File.Exists(tpl)) File.Copy(tpl, SettingsPath, false);
        return JObject.Parse(File.ReadAllText(SettingsPath));
      }
    }

    static void WriteJson(JObject obj)
    {
      Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
      File.WriteAllText(SettingsPath, obj.ToString(Formatting.Indented));
    }

    void MapJsonToGrid(JObject j)
    {
      // folders
      var src = j["source"];
      RootPaths = string.Join(";", src["paths"].ToObject<JArray>()
                                  .Select(t => t["path"].Value<string>()));
      ExcludePatterns = string.Join(";", src["global_exclude"]
                                  .ToObject<string[]>());

      // embedding
      var emb = j["embedding"];
      EmbeddingApi = emb["current_api"]?.Value<string>() ?? "local";
      var customEmb = emb["apis"]?.FirstOrDefault(
          a => a["id"]?.Value<string>() == "custom");
      if (customEmb != null)
      {
        EmbeddingUrl = customEmb["api_url"]?.Value<string>() ?? "";
        EmbeddingKey = customEmb["api_key"]?.Value<string>() ?? "";
        EmbeddingModel = customEmb["model"]?.Value<string>() ?? "";
      }

      // generation
      var gen = j["generation"];
      GenerationApi = gen["current_api"]?.Value<string>() ?? "mistral-devstral";
      var customGen = gen["apis"]?.FirstOrDefault(
          a => a["id"]?.Value<string>() == "custom");
      if (customGen != null)
      {
        GenerationUrl = customGen["api_url"]?.Value<string>() ?? "";
        GenerationKey = customGen["api_key"]?.Value<string>() ?? "";
        GenerationModel = customGen["model"]?.Value<string>() ?? "";
      }

      // chunking
      var chk = j["chunking"];
      ChunkMinTokens = chk["nof_min_tokens"]?.Value<int>() ?? 50;
      ChunkMaxTokens = chk["nof_max_tokens"]?.Value<int>() ?? 450;
      ChunkOverlapPercent = (int)Math.Round(
          (chk["overlap_percentage"]?.Value<double>() ?? 0.2) * 100);
    }

    void MapGridToJson(JObject j)
    {
      // folders
      var arr = new JArray();
      foreach (var p in RootPaths.Split(';'))
        arr.Add(new JObject
        {
          ["path"] = p.Trim(),
          ["recursive"] = true,
          ["type"] = "directory",
          ["extensions"] = new JArray(),
          ["exclude"] = new JArray(ExcludePatterns.Split(';'))
        });
      j["source"]["paths"] = arr;
      j["source"]["global_exclude"] = new JArray(ExcludePatterns.Split(';'));

      // embedding
      j["embedding"]["current_api"] = EmbeddingApi;
      var ce = j["embedding"]["apis"].FirstOrDefault(
          a => a["id"]?.Value<string>() == "custom");
      if (ce != null)
      {
        ce["api_url"] = EmbeddingUrl;
        ce["api_key"] = EmbeddingKey;
        ce["model"] = EmbeddingModel;
      }

      // generation
      j["generation"]["current_api"] = GenerationApi;
      var cg = j["generation"]["apis"].FirstOrDefault(
          a => a["id"]?.Value<string>() == "custom");
      if (cg != null)
      {
        cg["api_url"] = GenerationUrl;
        cg["api_key"] = GenerationKey;
        cg["model"] = GenerationModel;
      }

      // chunking
      j["chunking"]["nof_min_tokens"] = ChunkMinTokens;
      j["chunking"]["nof_max_tokens"] = ChunkMaxTokens;
      j["chunking"]["overlap_percentage"] = ChunkOverlapPercent / 100.0;
    }

    #endregion
  }

  class ApiChoiceConverter : StringConverter
  {
    public override bool GetStandardValuesSupported(ITypeDescriptorContext ctx) => true;
    public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext ctx)
    {
      // quick hard-coded list – you can read from json if you want
      return new StandardValuesCollection(new[] {
            "local", "custom",
            "openai-4o-mini","mistral-devstral","gemini-2.0-flash","deepseek","xai"
        });
    }
  }

  class OverlapConverter : Int32Converter
  {
    public override object ConvertTo(ITypeDescriptorContext c, System.Globalization.CultureInfo ci,
                                     object value, Type dest)
    {
      if (dest == typeof(string))
        return value.ToString() + " %";
      return base.ConvertTo(c, ci, value, dest);
    }
  }

  class FolderListEditor : UITypeEditor
  {
    public override UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext ctx) => UITypeEditorEditStyle.Modal;
    public override object EditValue(ITypeDescriptorContext ctx, IServiceProvider provider, object value)
    {
      var dlg = new System.Windows.Forms.OpenFileDialog
      {
        Multiselect = true,
        ValidateNames = false,
        CheckFileExists = false,
        CheckPathExists = true,
        FileName = "Select folders (file name ignored)",
        Filter = "Folder|*.none"
      };
      if (dlg.ShowDialog() == DialogResult.OK)
        value = string.Join(";", Path.GetDirectoryName(dlg.FileName));
      return value ?? "";
    }
  }

  class OpenSettingsEditor : UITypeEditor
  {
    public override UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext ctx) => UITypeEditorEditStyle.Modal;
    public override object EditValue(ITypeDescriptorContext ctx, IServiceProvider provider, object value)
    {
      ThreadHelper.JoinableTaskFactory.Run(async () =>
      {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var path = IndexerOptionsPage.SettingsPath;
        var dte = await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
        dte?.ItemOperations.OpenFile(path);
      });
      return value;
    }
  }

  class RestoreDefaultsEditor : UITypeEditor
  {
    public override UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext ctx) => UITypeEditorEditStyle.Modal;
    public override object EditValue(ITypeDescriptorContext ctx, IServiceProvider provider, object value)
    {
      var tpl = Path.Combine(
          Path.GetDirectoryName(typeof(IndexerOptionsPage).Assembly.Location),
          "settings.template.json");
      if (File.Exists(tpl))
      {
        Directory.CreateDirectory(Path.GetDirectoryName(IndexerOptionsPage.SettingsPath));
        File.Copy(tpl, IndexerOptionsPage.SettingsPath, overwrite: true);
        VsShellUtilities.ShowMessageBox(
            provider.GetService(typeof(SVsUIShell)) as IServiceProvider,
            "settings.json has been restored to the template defaults.",
            "Chat Assistant",
            OLEMSGICON.OLEMSGICON_INFO,
            OLEMSGBUTTON.OLEMSGBUTTON_OK,
            OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
      }
      return value;
    }
  }
}
