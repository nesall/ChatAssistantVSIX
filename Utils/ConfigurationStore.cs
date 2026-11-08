using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ChatAssistantVSIX.Utils
{
  /// <summary>Configuration persistence and schema migration without Visual Studio dependencies.</summary>
  public static class ConfigurationStore
  {
    /// <summary>Mutates both documents; returns true if either changed. Failures leave both untouched.</summary>
    public static bool Migrate(JObject settings, JObject providers, string scope)
    {
      if (settings == null) throw new ArgumentNullException(nameof(settings));
      if (providers == null) throw new ArgumentNullException(nameof(providers));
      var s = (JObject)settings.DeepClone();
      var p = (JObject)providers.DeepClone();
      foreach (var name in new[] { "embedding", "generation" })
      {
        var section = OptionalObject(s, name);
        if (section == null) continue;
        var legacy = section["apis"];
        if (legacy == null) continue;
        var apis = Array(legacy, name + ".apis");
        var key = name + "_providers";
        if (p[key] == null) p[key] = new JArray();
        var catalog = Array(p[key], key);
        var existing = Catalog(catalog, key);
        var remap = new Dictionary<string, string>(StringComparer.Ordinal);
        var enabled = new JArray();
        foreach (var token in apis)
        {
          var api = Object(token, name + ".apis[]");
          var id = Text(api["id"], name + ".apis[].id");
          if (remap.ContainsKey(id)) Fail(name + ".apis", "duplicate id '" + id + "'");
          var copy = (JObject)api.DeepClone();
          MaterializeDefaults(copy, section);
          var target = id;
          JObject other;
          if (existing.TryGetValue(target, out other) && !Equivalent(copy, other, p))
          {
            var stem = id + "-" + Hash((scope ?? "") + "\n" + Canonical(copy).ToString(Formatting.None));
            target = stem;
            int suffix = 2;
            while (existing.TryGetValue(target, out other) && !Equivalent(copy, other, p))
              target = stem + "-" + (suffix++).ToString(CultureInfo.InvariantCulture);
          }
          copy["id"] = target;
          if (!existing.ContainsKey(target)) { catalog.Add(copy); existing.Add(target, copy); }
          remap.Add(id, target);
          if (IsEnabled(api)) enabled.Add(target);
        }
        Remap(section, "current_api", remap);
        if (section["enabled_providers"] == null)
        {
          if (name == "generation") section["enabled_providers"] = enabled;
        }
        else RemapArray(section["enabled_providers"], remap);
        if (name == "generation")
        {
          Remap(section, "_fim_provider", remap);
          Remap(s, "_fim_provider", remap);
          RemapRouter(OptionalObject(section, "auto_router"), remap);
        }
        section.Remove("apis");
      }
      var generation = OptionalObject(s, "generation");
      if (generation != null && generation["max_context_tokens"] != null)
      {
        if (generation["default_context_length"] == null)
          generation["default_context_length"] = generation["max_context_tokens"].DeepClone();
        generation.Remove("max_context_tokens");
      }
      bool changed = !JToken.DeepEquals(s, settings) || !JToken.DeepEquals(p, providers);
      if (changed) { Replace(settings, s); Replace(providers, p); }
      return changed;
    }

    /// <summary>Throws JsonException for invalid known fields; unknown properties are permitted.</summary>
    public static void Validate(JObject settings, JObject providers)
    {
      if (settings == null) throw new ArgumentNullException(nameof(settings));
      if (providers == null) throw new ArgumentNullException(nameof(providers));
      foreach (var name in new[] { "source", "chunking", "embedding", "generation", "database" })
        Object(settings[name], name);
      var embedding = Catalog(Array(providers["embedding_providers"], "embedding_providers"), "embedding_providers");
      var generation = Catalog(Array(providers["generation_providers"], "generation_providers"), "generation_providers");
      var e = (JObject)settings["embedding"];
      var g = (JObject)settings["generation"];
      RequireProvider(e["current_api"], "embedding.current_api", embedding, null);
      var enabled = StringArray(g["enabled_providers"], "generation.enabled_providers");
      if (enabled.Distinct(StringComparer.Ordinal).Count() != enabled.Count) Fail("generation.enabled_providers", "duplicate IDs");
      foreach (var id in enabled) RequireProvider(new JValue(id), "generation.enabled_providers", generation, null);
      RequireProvider(g["current_api"], "generation.current_api", generation, enabled);
      foreach (var owner in new[] { settings, g })
        if (owner["_fim_provider"] != null) RequireProvider(owner["_fim_provider"], "_fim_provider", generation, enabled);
      var router = OptionalObject(g, "auto_router");
      if (router != null)
      {
        Boolean(router, "enabled");
        if ((bool?)router["enabled"] == true)
        {
          var classifier = Object(router["classifier"], "generation.auto_router.classifier");
          Text(classifier["prompt"], "generation.auto_router.classifier.prompt");
          if ((string)classifier["type"] != "internal")
            RequireProvider(classifier["api_id"], "auto_router.classifier.api_id", generation, enabled);
          Number(classifier, "timeout_ms", 1, int.MaxValue, true);
          Number(classifier, "max_tokens", 1, int.MaxValue, true);
          Number(classifier, "temperature", 0, 2);
          var fallback = Object(router["fallback"], "auto_router.fallback");
          RequireProvider(fallback["default_model_id"], "auto_router.fallback.default_model_id", generation, enabled);
          var rules = Object(router["routing_rules"], "auto_router.routing_rules");
          if (!rules.HasValues) Fail(rules.Path, "must contain at least one rule");
          foreach (var property in rules.Properties())
          {
            if (string.IsNullOrWhiteSpace(property.Name)) Fail(rules.Path, "empty rule name");
            var rule = Object(property.Value, property.Path);
            if ((string)rule["strategy"] != "direct") Fail(property.Path + ".strategy", "must be 'direct'");
            RequireProvider(rule["direct_model_id"], property.Path + ".direct_model_id", generation, enabled);
          }
        }
      }
      foreach (var catalog in new[] { embedding, generation })
        foreach (var provider in catalog.Values)
        {
          if (!IsEnabled(provider)) continue;
          Endpoint(provider["api_url"] ?? provider["apiUrl"], provider.Path + ".api_url");
          Text(provider["model"], provider.Path + ".model");
          if (provider["api_style"] != null && (string)provider["api_style"] != "openai" && (string)provider["api_style"] != "anthropic")
            Fail(provider.Path + ".api_style", "must be openai or anthropic");
          Boolean(provider, "stream");
          Boolean(provider, "temperature_support");
          Number(provider, "context_length", 0, int.MaxValue, true);
          Number(provider, "max_tokens", 0, int.MaxValue, true);
          Number(provider, "temperature", 0, 2);
          var fim = OptionalObject(provider, "fim");
          if (fim != null)
          {
            if (fim["api_url"] != null) Endpoint(fim["api_url"], fim.Path + ".api_url");
            foreach (var key in new[] { "format", "prefix_name", "suffix_name", "file_divider_token" })
              if (fim[key] != null && fim[key].Type != JTokenType.String) Fail(fim.Path + "." + key, "must be a string");
            if (string.IsNullOrWhiteSpace((string)fim["format"]))
            {
              Text(fim["prefix_name"], fim.Path + ".prefix_name");
              Text(fim["suffix_name"], fim.Path + ".suffix_name");
            }
            if (fim["stop_tokens"] != null)
              foreach (var stop in Array(fim["stop_tokens"], fim.Path + ".stop_tokens"))
                if (stop.Type != JTokenType.String || ((string)stop).Length == 0)
                  Fail(fim.Path + ".stop_tokens", "must contain nonempty strings (whitespace is allowed)");
          }
          var pricing = OptionalObject(provider, "pricing_tpm");
          if (pricing != null) foreach (var key in new[] { "input", "output", "cached_input" }) Number(pricing, key, 0, double.MaxValue);
        }
      foreach (var key in new[] { "batch_size", "timeout_ms", "top_k" }) Number(e, key, 1, int.MaxValue, true);
      Number(e, "retry_attempts", 0, int.MaxValue, true);
      foreach (var key in new[] { "timeout_ms", "default_context_length", "default_max_tokens" }) Number(g, key, 1, int.MaxValue, true);
      foreach (var key in new[] { "max_chunks", "max_full_sources", "max_related_per_source" }) Number(g, key, 0, int.MaxValue, true);
      Number(g, "default_temperature", 0, 2);
      // These defaults may also be inherited by embedding providers in the core.
      Number(e, "default_context_length", 0, int.MaxValue, true);
      Number(e, "default_max_tokens", 0, int.MaxValue, true);
      Number(e, "default_temperature", 0, 2);
      foreach (var section in new[] { e, g })
        if (section["default_max_tokens_name"] != null) Text(section["default_max_tokens_name"], section.Path + ".default_max_tokens_name");
      var excerpt = OptionalObject(g, "excerpt");
      if (excerpt != null)
      {
        Boolean(excerpt, "enabled");
        Number(excerpt, "min_chunks", 1, int.MaxValue, true);
        Number(excerpt, "max_chunks", 1, int.MaxValue, true);
        Ordered(excerpt, "min_chunks", "max_chunks");
        Number(excerpt, "threshold_ratio", 0, 1);
      }
      var chunking = (JObject)settings["chunking"];
      Boolean(chunking, "semantic");
      Number(chunking, "nof_min_tokens", 1, int.MaxValue, true);
      Number(chunking, "nof_max_tokens", 1, int.MaxValue, true);
      Ordered(chunking, "nof_min_tokens", "nof_max_tokens");
      Number(chunking, "overlap_percentage", 0, 1);
      if ((double?)chunking["overlap_percentage"] == 1) Fail("chunking.overlap_percentage", "must be less than 1");
      var database = (JObject)settings["database"];
      Number(database, "vector_dim", 1, int.MaxValue, true);
      Number(database, "max_elements", 1, int.MaxValue, true);
      if (database["distance_metric"] != null && (string)database["distance_metric"] != "cosine" && (string)database["distance_metric"] != "l2")
        Fail("database.distance_metric", "must be cosine or l2");
      var source = (JObject)settings["source"];
      Number(source, "max_file_size_mb", 0, double.MaxValue);
      foreach (var key in new[] { "default_extensions", "global_exclude" })
        if (source[key] != null) StringArray(source[key], "source." + key);
      var paths = Array(source["paths"], "source.paths");
      foreach (var token in paths)
      {
        var item = Object(token, "source.paths[]");
        var type = Text(item["type"], item.Path + ".type");
        if (type != "directory" && type != "file" && type != "url") Fail(item.Path + ".type", "must be directory, file or url");
        Text(item[type == "url" ? "url" : "path"], item.Path + (type == "url" ? ".url" : ".path"));
        if (type == "url") Endpoint(item["url"], item.Path + ".url");
        Boolean(item, "recursive");
        foreach (var key in new[] { "extensions", "exclude" }) if (item[key] != null) StringArray(item[key], item.Path + "." + key);
        var headers = OptionalObject(item, "headers");
        if (headers != null) foreach (var header in headers.Properties())
          if (header.Value.Type != JTokenType.String) Fail(header.Path, "must be a string");
      }
    }

    /// <summary>Reads an object without expanding environment variables or parsing strings as dates.</summary>
    public static JObject ReadJson(string path)
    {
      using (var stream = File.OpenText(path))
        return ParseJson(stream.ReadToEnd());
    }

    /// <summary>Parses an object with the same lossless string handling as ReadJson.</summary>
    public static JObject ParseJson(string json)
    {
      if (json == null) throw new ArgumentNullException(nameof(json));
      using (var stream = new StringReader(json))
      using (var reader = new JsonTextReader(stream) { DateParseHandling = DateParseHandling.None })
      {
        var result = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        while (reader.Read()) if (reader.TokenType != JsonToken.Comment) Fail("JSON", "unexpected trailing JSON");
        return result;
      }
    }

    /// <summary>Atomically publishes a flushed same-directory temporary file, retaining the previous file as .bak.</summary>
    public static void WriteJson(string path, JObject json)
    {
      if (json == null) throw new ArgumentNullException(nameof(json));
      var fullPath = Path.GetFullPath(path);
      var directory = Path.GetDirectoryName(fullPath);
      Directory.CreateDirectory(directory);
      var temporary = Path.Combine(directory, "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
      try
      {
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
          var bytes = new UTF8Encoding(false).GetBytes(json.ToString(Formatting.Indented) + Environment.NewLine);
          stream.Write(bytes, 0, bytes.Length);
          stream.Flush(true);
        }
        if (File.Exists(fullPath)) File.Replace(temporary, fullPath, fullPath + ".bak");
        else File.Move(temporary, fullPath);
      }
      finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Replace(JObject target, JObject value) { target.RemoveAll(); foreach (var property in value.Properties()) target.Add(property.Name, property.Value.DeepClone()); }
    private static void Fail(string path, string message) { throw new JsonException(path + ": " + message); }
    private static JObject Object(JToken token, string path) { var value = token as JObject; if (value == null) Fail(path, "must be an object"); return value; }
    private static JObject OptionalObject(JObject owner, string key) { return owner[key] == null ? null : Object(owner[key], owner.Path + "." + key); }
    private static JArray Array(JToken token, string path) { var value = token as JArray; if (value == null) Fail(path, "must be an array"); return value; }
    private static string Text(JToken token, string path) { if (token == null || token.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)token)) Fail(path, "must be a nonempty string"); return (string)token; }
    private static List<string> StringArray(JToken token, string path) { return Array(token, path).Select(x => Text(x, path + "[]")).ToList(); }
    private static bool IsEnabled(JObject provider) { Boolean(provider, "enabled"); return (bool?)provider["enabled"] != false; }
    private static void Boolean(JObject owner, string key) { if (owner[key] != null && owner[key].Type != JTokenType.Boolean) Fail(owner.Path + "." + key, "must be boolean"); }
    private static void Endpoint(JToken token, string path)
    {
      var value = Text(token, path);
      // Validation must not depend on secrets or machine-specific environment expansion.
      if (value.Contains("${") || value.Contains("$(")) return;
      Uri uri;
      if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || (uri.Scheme != "http" && uri.Scheme != "https") || string.IsNullOrEmpty(uri.Host))
        Fail(path, "must be an absolute HTTP(S) URL or environment placeholder");
    }
    private static Dictionary<string, JObject> Catalog(JArray array, string path)
    {
      var result = new Dictionary<string, JObject>(StringComparer.Ordinal);
      foreach (var token in array)
      {
        var item = Object(token, path + "[]");
        var id = Text(item["id"], path + "[].id");
        if (result.ContainsKey(id)) Fail(path, "duplicate id '" + id + "'");
        IsEnabled(item);
        result.Add(id, item);
      }
      return result;
    }
    private static void RequireProvider(JToken token, string path, Dictionary<string, JObject> catalog, List<string> enabled)
    {
      var id = Text(token, path);
      JObject provider;
      if (id == "auto" || !catalog.TryGetValue(id, out provider) || !IsEnabled(provider)) Fail(path, "provider '" + id + "' is missing or globally disabled");
      if (enabled != null && !enabled.Contains(id)) Fail(path, "provider is not in generation.enabled_providers");
    }
    private static void Number(JObject owner, string key, double min, double max, bool integer = false)
    {
      var token = owner[key];
      if (token == null) return;
      if ((token.Type != JTokenType.Integer && token.Type != JTokenType.Float) || (integer && token.Type != JTokenType.Integer)) Fail(owner.Path + "." + key, "must be " + (integer ? "an integer" : "numeric"));
      double value;
      if (!double.TryParse(token.ToString(Formatting.None), NumberStyles.Float, CultureInfo.InvariantCulture, out value) || double.IsNaN(value) || double.IsInfinity(value) || value < min || value > max)
        Fail(owner.Path + "." + key, "out of range [" + min + ", " + max + "]");
    }
    private static void Ordered(JObject owner, string low, string high) { if (owner[low] != null && owner[high] != null && (double)owner[low] > (double)owner[high]) Fail(owner.Path, low + " must not exceed " + high); }
    private static JToken Canonical(JToken token)
    {
      var obj = token as JObject;
      if (obj != null) return new JObject(obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => new JProperty(p.Name, Canonical(p.Value))));
      var array = token as JArray;
      return array != null ? new JArray(array.Select(Canonical)) : token.DeepClone();
    }
    private static void MaterializeDefaults(JObject provider, JObject defaults)
    {
      if (provider["max_tokens_name"] == null || (provider["max_tokens_name"].Type == JTokenType.String && (string)provider["max_tokens_name"] == ""))
        provider["max_tokens_name"] = defaults["default_max_tokens_name"]?.DeepClone() ?? new JValue("max_tokens");
      if (provider["context_length"] == null)
        provider["context_length"] = defaults["default_context_length"]?.DeepClone() ?? defaults["max_context_tokens"]?.DeepClone() ?? new JValue(32000);
    }
    private static bool Equivalent(JObject a, JObject b, JObject defaults)
    {
      var copy = (JObject)b.DeepClone(); copy["id"] = a["id"].DeepClone();
      MaterializeDefaults(copy, defaults);
      return JToken.DeepEquals(Canonical(a), Canonical(copy));
    }
    private static string Hash(string value) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant().Substring(0, 16); }
    private static void Remap(JObject owner, string key, Dictionary<string, string> map) { string value; if (owner[key]?.Type == JTokenType.String && map.TryGetValue((string)owner[key], out value)) owner[key] = value; }
    private static void RemapArray(JToken token, Dictionary<string, string> map) { var array = Array(token, "enabled_providers"); for (int i = 0; i < array.Count; i++) { string value; if (array[i].Type == JTokenType.String && map.TryGetValue((string)array[i], out value)) array[i] = value; } }
    private static void RemapRouter(JObject router, Dictionary<string, string> map)
    {
      if (router == null) return;
      var classifier = OptionalObject(router, "classifier"); if (classifier != null) Remap(classifier, "api_id", map);
      var fallback = OptionalObject(router, "fallback"); if (fallback != null) Remap(fallback, "default_model_id", map);
      var rules = OptionalObject(router, "routing_rules");
      if (rules != null) foreach (var property in rules.Properties())
      {
        var rule = Object(property.Value, property.Path);
        Remap(rule, "direct_model_id", map); Remap(rule, "synthesizer_model_id", map);
        if (rule["draft_model_ids"] != null) RemapArray(rule["draft_model_ids"], map);
      }
    }
  }
}
