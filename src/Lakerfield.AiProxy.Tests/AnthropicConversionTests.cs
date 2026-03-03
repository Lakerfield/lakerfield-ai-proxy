using System.Text.Json;
using Lakerfield.AiProxy.Controllers;

namespace Lakerfield.AiProxy.Tests;

public class AnthropicConversionTests
{
    [Fact]
    public void ConvertAnthropicToOpenAI_SimpleTextContent_ProducesOpenAIMessages()
    {
        var anthropicJson = """
            {
              "model": "qwen3.5:122b",
              "messages": [{"role": "user", "content": "Hello"}],
              "max_tokens": 1024
            }
            """;

        var result = ProxyController.ConvertAnthropicToOpenAI(anthropicJson);

        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        Assert.Equal("qwen3.5:122b", root.GetProperty("model").GetString());
        Assert.Equal(1024, root.GetProperty("max_tokens").GetInt32());
        var messages = root.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("Hello", messages[0].GetProperty("content").GetString());
    }

    [Fact]
    public void ConvertAnthropicToOpenAI_ContentArray_FlattensToString()
    {
        var anthropicJson = """
            {
              "model": "test",
              "messages": [{
                "role": "user",
                "content": [
                  {"type": "text", "text": "Hello"},
                  {"type": "text", "text": "World"}
                ]
              }]
            }
            """;

        var result = ProxyController.ConvertAnthropicToOpenAI(anthropicJson);

        using var doc = JsonDocument.Parse(result);
        var content = doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.Equal("Hello\nWorld", content);
    }

    [Fact]
    public void ConvertAnthropicToOpenAI_ContentWithCacheControl_StripsMetadata()
    {
        var anthropicJson = """
            {
              "model": "test",
              "messages": [{
                "role": "user",
                "content": [
                  {"type": "text", "text": "test", "cache_control": {"type": "ephemeral"}}
                ]
              }]
            }
            """;

        var result = ProxyController.ConvertAnthropicToOpenAI(anthropicJson);

        using var doc = JsonDocument.Parse(result);
        var content = doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.Equal("test", content);
    }

    [Fact]
    public void ConvertAnthropicToOpenAI_SystemArrayField_AddedAsSystemMessage()
    {
        var anthropicJson = """
            {
              "model": "test",
              "system": [{"type": "text", "text": "You are a helpful assistant."}],
              "messages": [{"role": "user", "content": "Hi"}]
            }
            """;

        var result = ProxyController.ConvertAnthropicToOpenAI(anthropicJson);

        using var doc = JsonDocument.Parse(result);
        var messages = doc.RootElement.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("You are a helpful assistant.", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("Hi", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public void ConvertAnthropicToOpenAI_SystemStringField_AddedAsSystemMessage()
    {
        var anthropicJson = """
            {
              "model": "test",
              "system": "You are a helpful assistant.",
              "messages": [{"role": "user", "content": "Hi"}]
            }
            """;

        var result = ProxyController.ConvertAnthropicToOpenAI(anthropicJson);

        using var doc = JsonDocument.Parse(result);
        var messages = doc.RootElement.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("You are a helpful assistant.", messages[0].GetProperty("content").GetString());
    }

    [Fact]
    public void ConvertAnthropicToOpenAI_SystemWithCacheControl_StripsMetadata()
    {
        var anthropicJson = """
            {
              "model": "test",
              "system": [{"type": "text", "text": "System prompt", "cache_control": {"type": "ephemeral"}}],
              "messages": [{"role": "user", "content": "Hi"}]
            }
            """;

        var result = ProxyController.ConvertAnthropicToOpenAI(anthropicJson);

        using var doc = JsonDocument.Parse(result);
        var systemMessage = doc.RootElement.GetProperty("messages")[0];
        Assert.Equal("system", systemMessage.GetProperty("role").GetString());
        Assert.Equal("System prompt", systemMessage.GetProperty("content").GetString());
    }

    [Fact]
    public void ConvertAnthropicToOpenAI_StreamTrue_PreservedInOutput()
    {
        var anthropicJson = """
            {"model": "test", "messages": [{"role": "user", "content": "Hi"}], "stream": true}
            """;

        var result = ProxyController.ConvertAnthropicToOpenAI(anthropicJson);

        using var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public void ConvertOpenAIToAnthropic_BasicResponse_ProducesAnthropicFormat()
    {
        var openAiJson = """
            {
              "id": "chatcmpl-123",
              "object": "chat.completion",
              "choices": [{"message": {"role": "assistant", "content": "Hello!"}, "finish_reason": "stop"}],
              "usage": {"prompt_tokens": 10, "completion_tokens": 5}
            }
            """;

        var result = ProxyController.ConvertOpenAIToAnthropic(openAiJson, "my-model");

        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        Assert.Equal("message", root.GetProperty("type").GetString());
        Assert.Equal("assistant", root.GetProperty("role").GetString());
        Assert.Equal("my-model", root.GetProperty("model").GetString());
        Assert.Equal("end_turn", root.GetProperty("stop_reason").GetString());
        var content = root.GetProperty("content");
        Assert.Equal(1, content.GetArrayLength());
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("Hello!", content[0].GetProperty("text").GetString());
        var usage = root.GetProperty("usage");
        Assert.Equal(10, usage.GetProperty("input_tokens").GetInt32());
        Assert.Equal(5, usage.GetProperty("output_tokens").GetInt32());
    }

    [Fact]
    public void ConvertOpenAIToAnthropic_FinishReasonLength_MapsToMaxTokens()
    {
        var openAiJson = """
            {
              "choices": [{"message": {"role": "assistant", "content": "..."}, "finish_reason": "length"}],
              "usage": {"prompt_tokens": 10, "completion_tokens": 100}
            }
            """;

        var result = ProxyController.ConvertOpenAIToAnthropic(openAiJson, "test");

        using var doc = JsonDocument.Parse(result);
        Assert.Equal("max_tokens", doc.RootElement.GetProperty("stop_reason").GetString());
    }

    [Fact]
    public void ConvertAnthropicToOpenAI_RealWorldRequest_ParsesCorrectly()
    {
        // Simulate the actual failing request from the problem statement
        var anthropicJson = """
            {
              "model": "qwen3.5:122b-a10b",
              "messages": [{
                "role": "user",
                "content": [
                  {"type": "text", "text": "<system-reminder>skills</system-reminder>"},
                  {"type": "text", "text": "<system-reminder>context</system-reminder>"},
                  {"type": "text", "text": "test"},
                  {"type": "text", "text": "test", "cache_control": {"type": "ephemeral"}}
                ]
              }],
              "system": [
                {"type": "text", "text": "You are Claude Code.", "cache_control": {"type": "ephemeral"}},
                {"type": "text", "text": "You are an interactive agent."}
              ]
            }
            """;

        var result = ProxyController.ConvertAnthropicToOpenAI(anthropicJson);

        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        Assert.Equal("qwen3.5:122b-a10b", root.GetProperty("model").GetString());

        var messages = root.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());

        // First message should be the system prompt (merged from system array)
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        var systemContent = messages[0].GetProperty("content").GetString();
        Assert.Contains("You are Claude Code.", systemContent);
        Assert.Contains("You are an interactive agent.", systemContent);

        // Second message should be the user message (content blocks concatenated)
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        var userContent = messages[1].GetProperty("content").GetString();
        Assert.Contains("<system-reminder>skills</system-reminder>", userContent);
        Assert.Contains("test", userContent);
    }
}
