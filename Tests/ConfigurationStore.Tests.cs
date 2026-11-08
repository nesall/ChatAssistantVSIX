using System;
using System.IO;
using System.Linq;
using System.Threading;
using ChatAssistantVSIX.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class ConfigurationStoreTests
{
  private static int assertions;
  private static void Check(bool condition, string message)
  {
    assertions++;
    if (!condition) throw new Exception(message);
  }
  private static void Invalid(Action action, string message)
  {
    try { action(); } catch (JsonException) { assertions++; return; }
    throw new Exception("Expected JsonException: " + message);
  }
  private static JObject Settings()
  {
    return JObject.Parse(@"{
      'embedding':{'current_api':'e'},
      'generation':{'current_api':'g','enabled_providers':['g']},
      'source':{'paths':[{'type':'directory','path':'${ROOT}','recursive':true}]},
      'chunking':{},'database':{}}" );
  }
  private static JObject Providers()
  {
    return JObject.Parse(@"{'embedding_providers':[{'id':'e','api_url':'http://localhost/embedding','model':'embed'}], 'generation_providers':[{'id':'g','model':'global','api_url':'https://example.com/chat'}]}" );
  }
  private static JObject Legacy()
  {
    var s = Settings();
    s["generation"]["apis"] = JArray.Parse(@"[{'id':'g','model':'legacy','api_url':'${ENDPOINT}','api_key':'${SECRET}','unknown':{'z':1,'a':[2,3]}},{'id':'off','enabled':false}]" );
    s["generation"]["max_context_tokens"] = 64000;
    s["generation"]["_fim_provider"] = "g";
    s["_fim_provider"] = "g";
    s["generation"]["auto_router"] = JObject.Parse(@"{
      'enabled':true,'classifier':{'type':'external','prompt':'classify','api_id':'g'},
      'fallback':{'default_model_id':'g'},
      'routing_rules':{'simple':{'strategy':'direct','direct_model_id':'g','synthesizer_model_id':'g','draft_model_ids':['g']}}}" );
    return s;
  }
  private static void Migration()
  {
    var s = Legacy(); var p = Providers();
    Check(ConfigurationStore.Migrate(s, p, "solution"), "migration changed");
    var id = (string)s["generation"]["current_api"];
    Check(id.StartsWith("g-") && id != "g", "collision renamed");
    Check((string)p["generation_providers"][0]["model"] == "global", "global unchanged");
    var migrated = p["generation_providers"].Single(x => (string)x["id"] == id);
    Check((string)migrated["api_key"] == "${SECRET}" && (int)migrated["unknown"]["a"][1] == 3, "unknown and placeholders preserved");
    Check(s["generation"]["apis"] == null && s["generation"]["max_context_tokens"] == null && (int)s["generation"]["default_context_length"] == 64000, "legacy fields migrated");
    foreach (var path in new[] { "generation._fim_provider", "_fim_provider", "generation.auto_router.classifier.api_id", "generation.auto_router.fallback.default_model_id", "generation.auto_router.routing_rules.simple.direct_model_id", "generation.auto_router.routing_rules.simple.synthesizer_model_id", "generation.auto_router.routing_rules.simple.draft_model_ids[0]", "generation.enabled_providers[0]" })
      Check((string)s.SelectToken(path) == id, "remapped " + path);
    ConfigurationStore.Validate(s, p);
    Check(!ConfigurationStore.Migrate(s, p, "solution"), "idempotent");
    var second = Legacy(); var secondProviders = Providers();
    ConfigurationStore.Migrate(second, secondProviders, "solution");
    Check(JToken.DeepEquals(s, second) && JToken.DeepEquals(p, secondProviders), "deterministic");
    // Repeat migration of the original legacy document into an already migrated catalog.
    var repeated = Legacy(); var before = p.DeepClone();
    ConfigurationStore.Migrate(repeated, p, "solution");
    Check(JToken.DeepEquals(before, p) && (string)repeated["generation"]["current_api"] == id, "existing suffix reused");
    var occupied = Providers();
    ((JArray)occupied["generation_providers"]).Add(new JObject { ["id"] = id, ["model"] = "unrelated" });
    var collision = Legacy(); ConfigurationStore.Migrate(collision, occupied, "solution");
    Check((string)collision["generation"]["current_api"] == id + "-2", "suffix collision safe");
    var missing = Legacy(); ((JObject)missing["generation"]).Remove("enabled_providers");
    ConfigurationStore.Migrate(missing, Providers(), "legacy");
    Check(((JArray)missing["generation"]["enabled_providers"]).Count == 1, "legacy enabled defaults exclude disabled");
    var modern = Settings(); ((JObject)modern["generation"]).Remove("enabled_providers");
    Check(!ConfigurationStore.Migrate(modern, Providers(), "modern"), "modern not repaired");
    Invalid(() => ConfigurationStore.Validate(modern, Providers()), "missing modern enabled list");
    var invalid = Legacy(); invalid["generation"]["apis"] = new JArray(new JObject());
    var original = invalid.DeepClone(); var catalog = Providers(); var originalCatalog = catalog.DeepClone();
    Invalid(() => ConfigurationStore.Migrate(invalid, catalog, "bad"), "bad legacy id");
    Check(JToken.DeepEquals(invalid, original) && JToken.DeepEquals(catalog, originalCatalog), "migration failure transactional");
  }
  private static void Validation()
  {
    ConfigurationStore.Validate(Settings(), Providers());
    foreach (var key in new[] { "api_url", "model", "api_style", "stream", "temperature_support", "max_tokens", "temperature", "context_length", "fim" })
    {
      var invalidProvider = Providers();
      invalidProvider["generation_providers"][0][key] = key == "max_tokens" || key == "temperature" || key == "context_length" ? (JToken)new JValue(-1) : new JValue("");
      Invalid(() => ConfigurationStore.Validate(Settings(), invalidProvider), "provider " + key);
    }
    var defaults = Providers();
    defaults["generation_providers"][0]["context_length"] = 0;
    defaults["generation_providers"][0]["max_tokens"] = 0;
    defaults["generation_providers"][0]["temperature"] = 0;
    defaults["generation_providers"][0]["fim"] = new JObject { ["format"] = "<prefix>{}<suffix>{}" };
    ((JArray)defaults["generation_providers"]).Add(new JObject { ["id"] = "incomplete", ["enabled"] = false });
    var emptySources = Settings(); emptySources["source"]["paths"] = new JArray();
    ConfigurationStore.Validate(emptySources, defaults); assertions++;
    defaults["generation_providers"][0]["fim"] = new JObject { ["prefix_name"] = "prompt", ["suffix_name"] = "suffix" };
    ConfigurationStore.Validate(Settings(), defaults); assertions++;
    ((JObject)defaults["generation_providers"][0]["fim"]).Remove("suffix_name");
    Invalid(() => ConfigurationStore.Validate(Settings(), defaults), "FIM requires both names without format");
    foreach (var path in new[] { "embedding.current_api", "generation.current_api", "generation.enabled_providers[0]" })
    {
      var s = Settings(); s.SelectToken(path).Replace(new JValue("missing"));
      Invalid(() => ConfigurationStore.Validate(s, Providers()), path);
    }
    var disabled = Providers(); disabled["generation_providers"][0]["enabled"] = false;
    Invalid(() => ConfigurationStore.Validate(Settings(), disabled), "globally disabled");
    foreach (var pair in new[] { "generation.default_temperature", "generation.default_context_length", "embedding.timeout_ms", "chunking.overlap_percentage", "database.vector_dim", "source.max_file_size_mb" })
    {
      var s = Settings(); var split = pair.Split('.'); s[split[0]][split[1]] = -1;
      Invalid(() => ConfigurationStore.Validate(s, Providers()), pair);
    }
    var fractional = Settings(); fractional["generation"]["default_max_tokens"] = 1.5;
    Invalid(() => ConfigurationStore.Validate(fractional, Providers()), "integer required");
    var source = Settings(); source["source"]["paths"][0]["type"] = "unknown";
    Invalid(() => ConfigurationStore.Validate(source, Providers()), "source type");
    var router = Legacy(); var p = Providers(); ConfigurationStore.Migrate(router, p, "router");
    router["generation"]["auto_router"]["routing_rules"]["simple"]["strategy"] = "ensemble";
    Invalid(() => ConfigurationStore.Validate(router, p), "direct only");
    router["generation"]["auto_router"]["routing_rules"]["simple"]["strategy"] = "direct";
    router["generation"]["auto_router"]["classifier"]["type"] = "internal";
    router["generation"]["auto_router"]["classifier"]["api_id"] = "missing";
    ConfigurationStore.Validate(router, p); assertions++;
    router["generation"]["auto_router"]["fallback"]["default_model_id"] = "auto";
    Invalid(() => ConfigurationStore.Validate(router, p), "router real provider");
  }
  private static void Persistence()
  {
    var directory = Path.Combine(Path.GetTempPath(), "ConfigurationStoreTests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
      var path = Path.Combine(directory, "settings.json");
      var first = new JObject { ["secret"] = "${KEY}", ["date"] = "2026-01-01T00:00:00Z", ["unicode"] = "հայ" };
      ConfigurationStore.WriteJson(path, first);
      Check(JToken.DeepEquals(first, ConfigurationStore.ReadJson(path)), "roundtrip");
      Check(!File.Exists(path + ".bak"), "initial no backup");
      var second = new JObject { ["revision"] = 2 };
      ConfigurationStore.WriteJson(path, second);
      Check(JToken.DeepEquals(first, ConfigurationStore.ReadJson(path + ".bak")), "previous backup");
      ConfigurationStore.WriteJson(path, first);
      Check(JToken.DeepEquals(second, ConfigurationStore.ReadJson(path + ".bak")), "backup replaced");
      Check(Directory.GetFiles(directory, "*.tmp").Length == 0, "temporary cleanup");
      foreach (var text in new[] { "{broken", "[]", "{} {}", "{'duplicate':1,'duplicate':2}" })
      {
        File.WriteAllText(path, text);
        Invalid(() => ConfigurationStore.ReadJson(path), "malformed/object-only/duplicate JSON");
      }
    }
    finally { Directory.Delete(directory, true); }
  }
  private static void Compatibility()
  {
    var legacy = Legacy(); var catalog = Providers();
    legacy["generation"]["default_max_tokens_name"] = "max_completion_tokens";
    legacy["generation"]["default_context_length"] = 96000;
    ConfigurationStore.Migrate(legacy, catalog, "inherited");
    var entry = catalog["generation_providers"].Single(p => (string)p["id"] == (string)legacy["generation"]["current_api"]);
    Check((string)entry["max_tokens_name"] == "max_completion_tokens" && (int)entry["context_length"] == 96000, "inherited provider behavior retained");
    var explicitSettings = Legacy(); var explicitCatalog = Providers();
    explicitSettings["generation"]["default_context_length"] = 96000;
    explicitSettings["generation"]["apis"][0]["context_length"] = 4096;
    explicitSettings["generation"]["apis"][0]["max_tokens_name"] = "custom_tokens";
    ConfigurationStore.Migrate(explicitSettings, explicitCatalog, "explicit");
    var explicitEntry = explicitCatalog["generation_providers"].Single(p => (string)p["id"] == (string)explicitSettings["generation"]["current_api"]);
    Check((int)explicitEntry["context_length"] == 4096 && (string)explicitEntry["max_tokens_name"] == "custom_tokens", "explicit provider behavior retained");
    var equal = Settings(); var equalCatalog = Providers();
    equal["generation"]["apis"] = new JArray(equalCatalog["generation_providers"][0].DeepClone());
    ConfigurationStore.Migrate(equal, equalCatalog, "equal");
    Check((string)equal["generation"]["current_api"] == "g" && ((JArray)equalCatalog["generation_providers"]).Count == 1, "equivalent inherited defaults reuse provider");
    var embedding = Settings(); var embeddingCatalog = Providers();
    embedding["embedding"]["apis"] = new JArray(embeddingCatalog["embedding_providers"][0].DeepClone());
    ConfigurationStore.Migrate(embedding, embeddingCatalog, "embedding");
    Check(embedding["embedding"]["enabled_providers"] == null, "embedding enabled list not synthesized");
    Check(ConfigurationStore.ParseJson("{\"date\":\"2026-11-07T00:00:00Z\"}")["date"].Type == JTokenType.String, "JSON dates remain strings");
    var resources = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "Resources"));
    ConfigurationStore.Validate(ConfigurationStore.ReadJson(Path.Combine(resources, "settings.default.json")), ConfigurationStore.ReadJson(Path.Combine(resources, "providers.default.json")));
    assertions++;
    var whitespace = Providers(); whitespace["generation_providers"][0]["fim"] = new JObject { ["format"] = "{}{}", ["stop_tokens"] = new JArray("\n", "  ") };
    ConfigurationStore.Validate(Settings(), whitespace); assertions++;
  }
  private static void Throws<T>(Action action, string message) where T : Exception
  {
    try { action(); } catch (T) { assertions++; return; }
    throw new Exception("Expected " + typeof(T).Name + ": " + message);
  }
  private static void Protocol()
  {
    var user = new ChatMessage { Role = "user", Content = "Explain this code" };
    var request = ChatProtocol.CreateRequest(new[] { user });
    Check(request["targetapi"] == null && request["temperature"] == null && request["max_tokens"] == null, "normal requests preserve backend defaults and routing");
    user.Content = "edited later";
    Check((string)request["messages"][0]["content"] == "Explain this code", "history snapshot is independent");
    foreach (var target in new[] { "auto", "g" })
      Check((string)ChatProtocol.CreateRequest(new[] { user }, targetApi: target)["targetapi"] == target, "explicit routing preserved");
    var overrides = ChatProtocol.CreateRequest(new[] { user }, temperature: 0.3, maxTokens: 512);
    Check(JObject.Parse(overrides.ToString())["max_tokens"].Type == JTokenType.Float && (int)overrides["max_tokens"] == 512, "core-compatible float token override");
    Throws<ArgumentException>(() => ChatProtocol.CreateRequest(new[] { new ChatMessage { Role = "assistant", Content = "Thinking..." } }), "last message must be user");
    int callbacks = 0;
    var stream = "data: {\"content\":\"[meta]Searching\"}\n\ndata: {\"content\":\"hello \"}\ndata: {\"type\":\"context_sources\"}\ndata: {\"content\":\"world\"}\ndata: [DONE]\ndata: {\"content\":\"ignored\"}\n";
    Check(ChatProtocol.ReadCompletionAsync(new StringReader(stream), _ => callbacks++, CancellationToken.None).GetAwaiter().GetResult() == "hello world" && callbacks == 2, "SSE content, metadata and terminator handled");
    int partial = 0;
    Throws<EndOfStreamException>(() => ChatProtocol.ReadCompletionAsync(new StringReader("data: {\"content\":\"partial\"}\n"), _ => partial++, CancellationToken.None).GetAwaiter().GetResult(), "Truncated SSE is not successful completion");
    Check(partial == 1, "Truncated SSE preserves delivered partial text");
    JArray sources = null;
    ChatProtocol.ReadCompletionAsync(new StringReader("data: {\"type\":\"context_sources\",\"sources\":[\"src/main.cpp\"]}\ndata: [DONE]\n"), null, CancellationToken.None, data => sources = data["sources"] as JArray).GetAwaiter().GetResult();
    Check(sources?.Count == 1 && (string)sources[0] == "src/main.cpp", "Context source paths reach the metadata callback intact");
    Throws<InvalidOperationException>(() => ChatProtocol.ReadCompletionAsync(new StringReader("data: {\"error\":\"failed\"}\n"), null, CancellationToken.None).GetAwaiter().GetResult(), "SSE error surfaced");
    Throws<OperationCanceledException>(() => ChatProtocol.ReadCompletionAsync(new StringReader("data: [DONE]\n"), null, new CancellationToken(true)).GetAwaiter().GetResult(), "SSE cancellation surfaced");
  }
  private static int Main()
  {
    try { Migration(); Validation(); Persistence(); Compatibility(); Protocol(); assertions += ChatStateTests.Run(); assertions += InlineCompletionSessionTests.Run(); Console.WriteLine("PASS: " + assertions + " assertions"); return 0; }
    catch (Exception e) { Console.Error.WriteLine(e); return 1; }
  }
}
