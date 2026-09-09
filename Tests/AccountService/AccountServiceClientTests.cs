using System.Net;
using System.Text;
using System.Text.Json;
using Application.DTOs;
using Infrastructure.Clients;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Project.Tests.AccountService;

public sealed class AccountServiceClientTests
{
    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> HandlerFunc { get; set; } = null!;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return HandlerFunc(request, cancellationToken);
        }
    }

    [Fact]
    public async Task GetUserAsync_WhenUserExists_ReturnsAccountUserDto()
    {
        var userId = Guid.NewGuid();
        var expectedDto = new AccountUserDto(userId, "john_doe", "John Doe", "https://avatar.png");

        var handler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, ct) =>
            {
                Assert.Equal(HttpMethod.Get, req.Method);
                Assert.EndsWith($"api/v1/users/{userId}", req.RequestUri!.ToString());

                var json = JsonSerializer.Serialize(expectedDto);
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                return Task.FromResult(response);
            }
        };

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") };
        var client = new AccountServiceClient(httpClient, NullLogger<AccountServiceClient>.Instance);

        var result = await client.GetUserAsync(userId);

        Assert.NotNull(result);
        Assert.Equal(userId, result.UserId);
        Assert.Equal("john_doe", result.UserName);
        Assert.Equal("John Doe", result.DisplayName);
        Assert.Equal("https://avatar.png", result.AvatarUrl);
    }

    [Fact]
    public async Task GetUserAsync_WhenNotFound_ReturnsNull()
    {
        var userId = Guid.NewGuid();

        var handler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, ct) =>
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        };

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") };
        var client = new AccountServiceClient(httpClient, NullLogger<AccountServiceClient>.Instance);

        var result = await client.GetUserAsync(userId);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetUserAsync_WhenServerErrorOrTimeout_GracefullyReturnsNull()
    {
        var userId = Guid.NewGuid();

        var handler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, ct) =>
            {
                throw new HttpRequestException("Connection refused");
            }
        };

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") };
        var client = new AccountServiceClient(httpClient, NullLogger<AccountServiceClient>.Instance);

        var result = await client.GetUserAsync(userId);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetUsersAsync_WhenEmptyInput_ReturnsEmptyDictionaryWithoutHttpCall()
    {
        var called = false;
        var handler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, ct) =>
            {
                called = true;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
        };

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") };
        var client = new AccountServiceClient(httpClient, NullLogger<AccountServiceClient>.Instance);

        var result = await client.GetUsersAsync(Array.Empty<Guid>());

        Assert.False(called);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetUsersAsync_DeduplicatesIds_And_ReturnsMappedDictionary()
    {
        var user1 = Guid.NewGuid();
        var user2 = Guid.NewGuid();

        var expectedDtos = new List<AccountUserDto>
        {
            new(user1, "alice", "Alice", "https://avatar1.png"),
            new(user2, "bob", "Bob", null)
        };

        var handler = new MockHttpMessageHandler
        {
            HandlerFunc = async (req, ct) =>
            {
                Assert.Equal(HttpMethod.Post, req.Method);
                Assert.EndsWith("api/v1/users/batch", req.RequestUri!.ToString());

                var body = await req.Content!.ReadAsStringAsync(ct);
                Assert.Contains(user1.ToString(), body);
                Assert.Contains(user2.ToString(), body);

                var json = JsonSerializer.Serialize(expectedDtos);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }
        };

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") };
        var client = new AccountServiceClient(httpClient, NullLogger<AccountServiceClient>.Instance);

        // Pass duplicates
        var result = await client.GetUsersAsync(new[] { user1, user2, user1 });

        Assert.Equal(2, result.Count);
        Assert.True(result.ContainsKey(user1));
        Assert.Equal("Alice", result[user1].DisplayName);
        Assert.True(result.ContainsKey(user2));
        Assert.Equal("Bob", result[user2].DisplayName);
    }

    [Fact]
    public async Task GetUsersAsync_WhenNetworkFails_ReturnsEmptyDictionaryGracefully()
    {
        var handler = new MockHttpMessageHandler
        {
            HandlerFunc = (req, ct) => throw new HttpRequestException("Network failure")
        };

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") };
        var client = new AccountServiceClient(httpClient, NullLogger<AccountServiceClient>.Instance);

        var result = await client.GetUsersAsync(new[] { Guid.NewGuid() });

        Assert.NotNull(result);
        Assert.Empty(result);
    }
}
