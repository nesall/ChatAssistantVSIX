using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ChatAssistantVSIX.Utils
{
  public sealed class ChatProvider
  {
    public string Id { get; set; }
    public string Label { get; set; }
    public string Detail { get; set; }
    public bool Available { get; set; } = true;
    public bool IsDefault { get; set; }
    public override string ToString() => Label;
    public static List<ChatProvider> FromSettings(JObject settings)
    {
      if (!(settings["completionApis"] is JArray apis)) throw new InvalidOperationException("The service returned no enabled model list.");
      var current = (string)settings["currentApi"];
      var result = new List<ChatProvider>();
      foreach (var api in apis.OfType<JObject>())
      {
        var id = (string)api["id"];
        if (string.IsNullOrWhiteSpace(id) || result.Any(p => p.Id == id)) continue;
        var name = (string)api["name"];
        var model = (string)api["model"] ?? id;
        bool isDefault = id == current;
        bool sameModel = apis.OfType<JObject>().Any(p => (string)p["id"] != id && (string)p["model"] == model);
        var label = id == "auto" ? "Auto-router" : sameModel ? (string.IsNullOrWhiteSpace(name) ? id : name) + " · " + model : model;
        result.Add(new ChatProvider { Id = id, IsDefault = isDefault, Label = label + (isDefault ? " (default)" : ""), Detail = name + "\n" + model + "\nProvider ID: " + id });
      }
      return result.OrderByDescending(p => p.IsDefault).ToList();
    }
  }
}
