using System.Text.Json;
using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public sealed class OpenAiResponseTests
{
    [Theory]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"你好\"}}]}")]
    [InlineData("{\"data\":{\"choices\":[{\"message\":{\"content\":\"你好\"}}]},\"success\":true}")]
    public void Json_completion_and_gateway_envelope_return_complete_text(string body)
    {
        Assert.Equal("你好", OpenAiResponse.Read(body));
    }

    [Theory]
    [InlineData("data: [DONE]\n\n", "你好")]
    [InlineData("data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n", "你好")]
    public void Sse_joins_content_and_ignores_reasoning_role_usage_and_other_choices(string terminal, string expected)
    {
        var body = ": heartbeat\r\n\r\n"
            + "data: {\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":null}}]}\r\n\r\n"
            + "data: {\"choices\":[{\"index\":1,\"delta\":{\"content\":\"wrong\"}},{\"index\":0,\"delta\":{\"reasoning_content\":\"private\",\"content\":\"你\"}}]}\r\n\r\n"
            + "event: message\ndata: {\"choices\":[\ndata: {\"index\":0,\"delta\":{\"content\":\"好\"}}]}\n\n"
            + "data: {\"choices\":[],\"usage\":{\"total_tokens\":10}}\n\n"
            + terminal;
        Assert.Equal(expected, OpenAiResponse.Read(body));
    }

    [Theory]
    [InlineData("")]
    [InlineData("data: {\"error\":{\"message\":\"failed\"}}\n\n")]
    [InlineData("data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"length\"}]}\n\n")]
    [InlineData("data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"content_filter\"}]}\n\n")]
    [InlineData("data: {\n\n")]
    public void Incomplete_or_failed_stream_cannot_publish_partial_translation(string terminal)
    {
        Assert.ThrowsAny<JsonException>(() => OpenAiResponse.Read(
            "data: {\"choices\":[{\"delta\":{\"content\":\"部分\"}}]}\n\n" + terminal));
    }

    [Fact]
    public void Empty_done_stream_is_rejected()
    {
        Assert.Throws<JsonException>(() => OpenAiResponse.Read("data: [DONE]\n\n"));
    }
}
