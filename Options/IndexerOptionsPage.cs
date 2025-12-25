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

    [Category("Sources")]
    [DisplayName("Root paths")]
    [Description("Semicolon-separated folders to index. Use $(SolutionDir) for the open solution.")]
    [Editor(typeof(FolderListEditor), typeof(UITypeEditor))]
    public string RootPaths { get; set; } = "$(SolutionDir)";

    [Category("Sources")]
    [DisplayName("Exclude patterns")]
    [Description("Global patterns to skip (semicolon separated).")]
    public string ExcludePatterns { get; set; } =
        "**/bin/**;**/obj/**;**/node_modules/**;**/.git/**;**/dist/**";

    [Category("Sources")]
    [DisplayName("Default extensions")]
    [Description("Extensions to scan files with (semicolon separated)")]
    public string DefaultExtensions { get; set; } = ".c;.cpp;.h";

    // ---------- Embedding ----------

    private string embeddingApi_ = "custom";
    [Category("Embedding")]
    [DisplayName("Current API")]
    [Description("Which embedding API block to activate.")]
    [TypeConverter(typeof(ApiChoiceConverterEmb))]
    public string EmbeddingApi
    {
      get => embeddingApi_;
      set
      {
        embeddingApi_ = value;
        AutoFillApiFields("embedding", value);
      }
    }

    [Category("Embedding")]
    [DisplayName("API URL")]
    [Description("Only used when 'Current API' = custom")]
    public string EmbeddingUrl { get; set; } = "";

    [Category("Embedding")]
    [DisplayName("API key")]
    [Description("Only used when 'Current API' = custom")]
    //[PasswordPropertyText(true)]
    public string EmbeddingKey { get; set; } = "";

    [Category("Embedding")]
    [DisplayName("Model name")]
    [Description("Only used when 'Current API' = custom")]
    public string EmbeddingModel { get; set; } = "";

    [Category("Embedding")]
    [DisplayName("Vector Dimension")]
    [Description("Dimension of the embedding vectors produced by the model.")]
    public int EmbeddingVecDim { get; set; } = 768;

    // ---------- Generation ----------

    private string generationApi_ = "mistral-devstral";
    [Category("Generation")]
    [DisplayName("Current API")]
    [Description("Which generation API block to activate.")]
    [TypeConverter(typeof(ApiChoiceConverterGen))]
    public string GenerationApi
    {
      get => generationApi_;
      set
      {
        generationApi_ = value;
        AutoFillApiFields("generation", value);
      }
    }

    [Category("Generation")]
    [DisplayName("API URL")]
    [Description("Only used when 'Current API' = custom")]
    public string GenerationUrl { get; set; } = "";

    [Category("Generation")]
    [DisplayName("API key")]
    [Description("Only used when 'Current API' = custom")]
    //[PasswordPropertyText(true)]
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
    [Description("Overlap between consecutive chunks (0-1).")]
    //[TypeConverter(typeof(OverlapConverter))]
    public double ChunkOverlapPercent { get; set; } = 0.2;

    // ---------- Helpers ----------

    [Category("Configuration file")]
    [DisplayName("Open full settings")]
    [Description("Opens settings.json in Visual Studio.")]
    [Editor(typeof(OpenSettingsEditor), typeof(UITypeEditor))]
    public string OpenSettings => "Click [...]";

    [Category("Configuration file")]
    [DisplayName("PhenixCode Executable")]
    [Description("Path to phenixcode-core.exe")]
    public string ExecutablePath { get; set; } = "";

    #endregion

    //#region --- Dynamic ReadOnly Logic ---

    ////private Dictionary<string, PropertyDescriptor> propertyDescriptors_ = new Dictionary<string, PropertyDescriptor>();
    //public void updateProperties()
    //{
    //  var props = TypeDescriptor.GetProperties(this);

    //  // Define which fields depend on "custom" status
    //  bool isEmbCustom = EmbeddingApi == "custom";
    //  bool isGenCustom = GenerationApi == "custom";

    //  //SetReadOnly(props, "EmbeddingUrl", !isEmbCustom);
    //  //SetReadOnly(props, "EmbeddingKey", !isEmbCustom);
    //  //SetReadOnly(props, "EmbeddingModel", !isEmbCustom);

    //  SetReadOnly(props, "GenerationUrl", !isGenCustom);
    //  //SetReadOnly(props, "GenerationKey", !isGenCustom);
    //  //SetReadOnly(props, "GenerationModel", !isGenCustom);
    //}

    //private void SetReadOnly(PropertyDescriptorCollection props, string name, bool readOnly)
    //{
    //  var prop = props[name];
    //  if (prop != null)
    //  {
    //    var attr = prop.Attributes[typeof(ReadOnlyAttribute)] as ReadOnlyAttribute;
        
    //    var field = typeof(ReadOnlyAttribute).GetField("isReadOnly",
    //        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    //    // This hack updates the existing attribute instance's internal state
    //    field?.SetValue(attr, readOnly);
    //  }
    //}

    //#endregion

    #region --- Auto-fill Logic ---
    private void AutoFillApiFields(string section, string apiId)
    {
      try
      {
        var json = ReadJson();
        var api = json[section]?["apis"]?.FirstOrDefault(a => a["id"]?.Value<string>() == apiId);

        if (api == null) return;

        if (section == "embedding")
        {
          EmbeddingUrl = api["api_url"]?.Value<string>() ?? "";
          EmbeddingKey = api["api_key"]?.Value<string>() ?? "";
          EmbeddingModel = api["model"]?.Value<string>() ?? "";
        }
        else if (section == "generation")
        {
          GenerationUrl = api["api_url"]?.Value<string>() ?? "";
          GenerationKey = api["api_key"]?.Value<string>() ?? "";
          GenerationModel = api["model"]?.Value<string>() ?? "";
        }
        //updateProperties();
      }
      catch (Exception ex)
      {
        Debug.WriteLine($"Failed to auto-fill {section} fields: {ex.Message}");
      }
    }

    #endregion

    #region --- load / save ---

    public override void LoadSettingsFromStorage()
    {
      //base.LoadSettingsFromStorage();
      var json = ReadJson();
      MapJsonToGrid(json);
    }

    public override void SaveSettingsToStorage()
    {
      var json = ReadJson();
      MapGridToJson(json);
      WriteJson(json);
      //base.SaveSettingsToStorage();

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
        // first run – ensure directory exists and try to copy template
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
        var asm = typeof(IndexerOptionsPage).Assembly;

        // 1) Try to find an embedded resource named settings.default.json
        try
        {
          var resourceName = asm.GetManifestResourceNames()
                                .FirstOrDefault(n => n.EndsWith("Resources.settings.default.json", StringComparison.OrdinalIgnoreCase)
                                                  || n.EndsWith("settings.default.json", StringComparison.OrdinalIgnoreCase));
          if (resourceName != null)
          {
            using (var s = asm.GetManifestResourceStream(resourceName))
            {
              if (s != null)
              {
                using (var sr = new StreamReader(s, Encoding.UTF8))
                {
                  File.WriteAllText(SettingsPath, sr.ReadToEnd());
                  return JObject.Parse(File.ReadAllText(SettingsPath));
                }
              }
            }
          }
        }
        catch { /* ignore and fallback to file copy */ }

        // 2) Fallback: copy from the Resources folder next to the extension assembly
        var tpl = Path.Combine(Path.GetDirectoryName(asm.Location), "Resources", "settings.default.json");
        if (File.Exists(tpl))
          File.Copy(tpl, SettingsPath, false);

        // If copy succeeded (or even if not), attempt to load the file (will throw if missing)
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
      RootPaths = string.Join(";", src["paths"].ToObject<JArray>().Select(t => t["path"].Value<string>()));
      ExcludePatterns = string.Join(";", src["global_exclude"].ToObject<string[]>());
      DefaultExtensions = string.Join(";", src["default_extensions"].ToObject<string[]>());

      // embedding
      var emb = j["embedding"];
      EmbeddingApi = emb["current_api"]?.Value<string>() ?? "custom";
      var customEmb = emb["apis"]?.FirstOrDefault(a => a["id"]?.Value<string>() == "custom");
      if (customEmb != null)
      {
        EmbeddingUrl = customEmb["api_url"]?.Value<string>() ?? "";
        EmbeddingKey = customEmb["api_key"]?.Value<string>() ?? "";
        EmbeddingModel = customEmb["model"]?.Value<string>() ?? "";
      }

      // database
      var db = j["database"];
      if (db != null)
      {
        EmbeddingVecDim = db["vector_dim"]?.Value<int>() ?? 768;
      }

      // generation
      var gen = j["generation"];
      GenerationApi = gen["current_api"]?.Value<string>() ?? "mistral-devstral";
      var customGen = gen["apis"]?.FirstOrDefault(a => a["id"]?.Value<string>() == "custom");
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
      ChunkOverlapPercent = chk["overlap_percentage"]?.Value<double>() ?? 0.2;

      // Executable path
      ExecutablePath = j["_exec"]?.Value<string>() ?? "";
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
      j["source"]["default_extensions"] = new JArray(DefaultExtensions.Split(';'));

      // embedding
      j["embedding"]["current_api"] = EmbeddingApi;
      var ce = j["embedding"]["apis"].FirstOrDefault(a => a["id"]?.Value<string>() == "custom");
      if (ce != null)
      {
        ce["api_url"] = EmbeddingUrl;
        ce["api_key"] = EmbeddingKey;
        ce["model"] = EmbeddingModel;
      }

      // database
      j["database"]["vector_dim"] = EmbeddingVecDim;

      // generation
      j["generation"]["current_api"] = GenerationApi;
      var cg = j["generation"]["apis"].FirstOrDefault(a => a["id"]?.Value<string>() == "custom");
      if (cg != null)
      {
        cg["api_url"] = GenerationUrl;
        cg["api_key"] = GenerationKey;
        cg["model"] = GenerationModel;
      }

      // chunking
      j["chunking"]["nof_min_tokens"] = ChunkMinTokens;
      j["chunking"]["nof_max_tokens"] = ChunkMaxTokens;
      j["chunking"]["overlap_percentage"] = ChunkOverlapPercent;

      // Executable path
      j["_exec"] = ExecutablePath;
    }

    #endregion
  }

  class ApiChoiceConverterEmb : StringConverter
  {
    public override bool GetStandardValuesSupported(ITypeDescriptorContext ctx) => true;

    public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext ctx)
    {
      var choices = new List<string> { };
      try
      {
        var json = JObject.Parse(File.ReadAllText(IndexerOptionsPage.SettingsPath));
        var apis = json["embedding"]?["apis"]?.ToObject<JArray>();
        if (apis != null)
        {
          foreach (var api in apis)
          {
            var id = api["id"]?.Value<string>();
            if (!string.IsNullOrEmpty(id) && !choices.Contains(id))
              choices.Add(id);
          }
        }
      }
      catch
      {
        Debug.WriteLine("Failed to read settings.json for embedding APIs.");
      }
      return new StandardValuesCollection(choices.Distinct().ToList());
    }
  }

  class ApiChoiceConverterGen : StringConverter
  {
    public override bool GetStandardValuesSupported(ITypeDescriptorContext ctx) => true;

    public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext ctx)
    {
      var choices = new List<string> { };
      try
      {
        var json = JObject.Parse(File.ReadAllText(IndexerOptionsPage.SettingsPath));
        var apis = json["generation"]?["apis"]?.ToObject<JArray>();
        if (apis != null)
        {
          foreach (var api in apis)
          {
            var id = api["id"]?.Value<string>();
            if (!string.IsNullOrEmpty(id) && !choices.Contains(id))
              choices.Add(id);
          }
        }
      }
      catch
      {
        Debug.WriteLine("Failed to read settings.json for generation APIs.");
      }
      return new StandardValuesCollection(choices.Distinct().ToList());
    }
  }

  //class OverlapConverter : Int32Converter
  //{
  //  public override object ConvertTo(ITypeDescriptorContext c, System.Globalization.CultureInfo ci, object value, Type dest)
  //  {
  //    if (dest == typeof(string))
  //      return value.ToString() + " %";
  //    return base.ConvertTo(c, ci, value, dest);
  //  }
  //}

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
}
