using System.Text.Json.Serialization;

namespace avallama.Models.Dtos;

public sealed class ModelRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; init; }
}
