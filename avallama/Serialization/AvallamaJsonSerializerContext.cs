using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using avallama.Models.Dtos;

namespace avallama.Serialization;

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Dictionary<string, string>), TypeInfoPropertyName = "StringDictionary")]
[JsonSerializable(typeof(List<string>), TypeInfoPropertyName = "StringList")]
[JsonSerializable(typeof(Dictionary<string, JsonElement>), TypeInfoPropertyName = "JsonElementDictionary")]
[JsonSerializable(typeof(ChatRequest), GenerationMode = JsonSourceGenerationMode.Serialization)]
[JsonSerializable(typeof(ChatMessage), GenerationMode = JsonSourceGenerationMode.Serialization)]
[JsonSerializable(typeof(DownloadResponse))]
[JsonSerializable(typeof(OllamaResponse))]
[JsonSerializable(typeof(MessageContent))]
[JsonSerializable(typeof(OllamaTagsResponse))]
[JsonSerializable(typeof(OllamaModelDto))]
[JsonSerializable(typeof(OllamaModelDetailsDto))]
[JsonSerializable(typeof(OllamaShowResponse))]
[JsonSerializable(typeof(ModelRequest), GenerationMode = JsonSourceGenerationMode.Serialization)]
[JsonSerializable(typeof(PullModelRequest), GenerationMode = JsonSourceGenerationMode.Serialization)]
internal partial class AvallamaJsonSerializerContext : JsonSerializerContext;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, string>), TypeInfoPropertyName = "StringDictionary")]
internal partial class AvallamaIndentedJsonSerializerContext : JsonSerializerContext;
