using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace HelloApi.Tests;

// Runs on Windows from Test Explorer, and in the dev container with Extensions > Dev Containers > Run Tests in
// Container.
public class ApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Echo_ReturnsTheText()
    {
        var reply = await factory.CreateClient().GetFromJsonAsync<EchoReply>("/api/echo/hello");
        Assert.Equal("hello", reply?.Text);
    }

    [Fact]
    public async Task Info_SaysWhereItRuns()
    {
        var info = await factory.CreateClient().GetStringAsync("/api/info");
        Assert.Contains("\"inContainer\":", info);
    }

    private sealed record EchoReply(string Text);
}
