// Copyright (c) Márk Csörgő and Martin Bartos
// Licensed under the MIT License. See LICENSE file for details.

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using avallama.Serialization;

namespace avallama.Models.Dtos;

public class ChatRequest
{
    [JsonPropertyName("model")] public string Model { get; set; }

    [JsonPropertyName("messages")] public List<ChatMessage> Messages { get; set; }

    public ChatRequest(List<Message> messages, string model)
    {
        Messages = ConvertToChatMessages(messages);
        Model = model;
    }

    private static List<ChatMessage> ConvertToChatMessages(List<Message> messages)
    {
        var chatMessages = new List<ChatMessage>();

        foreach (var message in messages)
        {
            chatMessages.Add(new ChatMessage
            {
                Role = message is GeneratedMessage ? "assistant" : "user",
                Content = message.Content
            });
        }

        return chatMessages;
    }

    public string ToJson()
    {
        return JsonSerializer.Serialize(this, AvallamaJsonSerializerContext.Default.ChatRequest);
    }
}

public class ChatMessage
{
    [JsonPropertyName("role")] public string? Role { get; set; }

    [JsonPropertyName("content")] public string? Content { get; set; }
}
