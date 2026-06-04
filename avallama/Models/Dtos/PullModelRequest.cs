using System.Text.Json.Serialization;

namespace avallama.Models.Dtos;

public sealed class PullModelRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("stream")]
    public bool Stream { get; init; } = true;
}
