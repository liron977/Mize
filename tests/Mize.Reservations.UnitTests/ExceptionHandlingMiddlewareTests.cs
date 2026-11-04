using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mize.Reservations.Api.Middleware;
using Mize.Reservations.Api.Observability;
using Xunit;

namespace Mize.Reservations.UnitTests;

/// <summary>What the last line of defence tells a caller, without starting a server.</summary>
public class ExceptionHandlingMiddlewareTests
{
    private static async Task<(DefaultHttpContext ctx, string body)> Run(RequestDelegate next, CancellationToken aborted = default)
    {
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        ctx.Response.Body = new MemoryStream();
        ctx.RequestAborted = aborted;
        var middleware = new ExceptionHandlingMiddleware(next, NullLogger<ExceptionHandlingMiddleware>.Instance, new LogSampler());

        await middleware.InvokeAsync(ctx);

        return (ctx, Encoding.UTF8.GetString(((MemoryStream)ctx.Response.Body).ToArray()));
    }

    [Theory]
    [InlineData(413, "Request body too large.", "The request body exceeds the size limit.")]
    [InlineData(408, "Malformed request.", "The request could not be read.")]
    [InlineData(400, "Malformed request.", "The request could not be read.")]
    [InlineData(431, "Malformed request.", "The request could not be read.")]
    public async Task A_request_the_framework_could_not_read_keeps_its_4xx_status_with_a_generic_message(int status, string title, string detail)
    {
        var (ctx, body) = await Run(_ => throw new BadHttpRequestException("secret internal detail: Server=db01", status));

        ctx.Response.StatusCode.Should().Be(status);
        ctx.Response.ContentType.Should().StartWith("application/problem+json");
        body.Should().Contain(title).And.Contain(detail);
        body.Should().NotContain("secret").And.NotContain("Server=", "the framework's own message is never echoed");
    }

    [Fact]
    public async Task A_framework_error_that_is_not_a_4xx_is_reported_as_a_400()
    {
        var (ctx, _) = await Run(_ => throw new BadHttpRequestException("odd", 500));

        ctx.Response.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task A_cancellation_caused_by_the_client_going_away_is_swallowed_and_writes_nothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var (ctx, body) = await Run(_ => throw new OperationCanceledException(cts.Token), cts.Token);

        body.Should().BeEmpty();
        ctx.Response.StatusCode.Should().Be(200, "there is nobody left to answer, so no error status is invented");
    }

    [Fact]
    public async Task A_cancellation_that_is_not_the_client_leaving_is_an_unexpected_error()
    {
        var (ctx, body) = await Run(_ => throw new OperationCanceledException("a timeout inside the service"));

        ctx.Response.StatusCode.Should().Be(500);
        body.Should().Contain("An unexpected error occurred.").And.NotContain("timeout");
    }
}
