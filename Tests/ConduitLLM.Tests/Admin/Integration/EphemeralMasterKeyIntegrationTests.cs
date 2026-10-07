using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ConduitLLM.Admin.Security;
using ConduitLLM.Admin.Services;
using ConduitLLM.Admin.Middleware;
using ConduitLLM.Security.Options;
using Microsoft.Extensions.Configuration;

namespace ConduitLLM.Tests.Admin.Integration
{
    public class EphemeralMasterKeyIntegrationTests : IDisposable
    {
        private readonly TestServer _server;
        private readonly HttpClient _client;
        private readonly IServiceProvider _serviceProvider;

        public EphemeralMasterKeyIntegrationTests()
        {
            var hostBuilder = new HostBuilder()
                .ConfigureWebHost(webHost =>
                {
                    webHost.UseTestServer();
                    webHost.ConfigureServices(services =>
                    {
                        // Set up master key
                        Environment.SetEnvironmentVariable("CONDUIT_API_TO_API_BACKEND_AUTH_KEY", "test-master-key");

                        // Add required services
                        services.AddDistributedMemoryCache(); // Use in-memory cache for testing
                        services.AddMemoryCache();
                        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
                        services.Configure<AdminSecurityOptions>(options =>
                        {
                            options.ApiAuth.ApiKeyHeader = "X-Custom-Key";
                            options.ApiAuth.AlternativeHeaders = ["X-API-Key", "X-Master-Key"];
                            options.FailedAuth.Enabled = false;
                            options.RateLimiting.Enabled = false;
                            options.IpFiltering.Enabled = false;
                        });
                        services.AddSingleton<ConduitLLM.Security.Interfaces.ISecurityService, SecurityService>();
                        services.AddSingleton<IEphemeralMasterKeyService, EphemeralMasterKeyService>();
                        
                        // Add authentication
                        services.AddAuthentication("MasterKey")
                            .AddScheme<MasterKeyAuthenticationSchemeOptions, MasterKeyAuthenticationHandler>("MasterKey", null);

                        // Add authorization
                        services.AddAuthorization(options =>
                        {
                            options.AddPolicy("MasterKeyPolicy", policy =>
                                policy.Requirements.Add(new MasterKeyRequirement()));
                        });
                        services.AddSingleton<IAuthorizationHandler, MasterKeyAuthorizationHandler>();

                        services.AddControllers();
                        services.AddLogging();
                    });
                    webHost.Configure(app =>
                    {
                        app.UseRouting();
                        app.UseAdminSecurity();
                        app.UseMiddleware<EphemeralMasterKeyCleanupMiddleware>();
                        app.UseAuthentication();
                        app.UseAuthorization();
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapControllers();
                        });
                    });
                });

            var host = hostBuilder.Start();
            _server = host.GetTestServer();
            _client = _server.CreateClient();
            _serviceProvider = _server.Services;
        }

        [Theory]
        [InlineData("X-Custom-Key", "")]
        [InlineData("X-API-Key", "")]
        [InlineData("X-Master-Key", "")]
        [InlineData("Authorization", "Bearer ")]
        public async Task EphemeralMasterKey_FullFlow_WorksCorrectly(string header, string prefix)
        {
            // Step 1: Generate ephemeral master key
            var ephemeralKeyService = _serviceProvider.GetRequiredService<IEphemeralMasterKeyService>();
            var keyResponse = await ephemeralKeyService.CreateEphemeralMasterKeyAsync();
            
            Assert.NotNull(keyResponse);
            Assert.StartsWith("emk_", keyResponse.EphemeralMasterKey);

            // Step 2: Use ephemeral key to access protected endpoint
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/test");
            request.Headers.Add(header, prefix + keyResponse.EphemeralMasterKey);

            var response = await _client.SendAsync(request);

            // Step 3: Verify response
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.False(await ephemeralKeyService.KeyExistsAsync(keyResponse.EphemeralMasterKey));

            // Step 4: Verify key is consumed (second use should fail)
            var secondRequest = new HttpRequestMessage(HttpMethod.Get, "/api/test");
            secondRequest.Headers.Add(header, prefix + keyResponse.EphemeralMasterKey);

            var secondResponse = await _client.SendAsync(secondRequest);
            Assert.Equal(HttpStatusCode.Unauthorized, secondResponse.StatusCode);
        }

        [Theory]
        [InlineData("X-Custom-Key", "")]
        [InlineData("X-API-Key", "")]
        [InlineData("X-Master-Key", "")]
        [InlineData("Authorization", "Bearer ")]
        [InlineData("Authorization", "bEaReR   ")]
        public async Task RegularMasterKey_CanBeReused(string header, string prefix)
        {
            // First request with master key
            var request1 = new HttpRequestMessage(HttpMethod.Get, "/api/test");
            request1.Headers.Add(header, prefix + "test-master-key");

            var response1 = await _client.SendAsync(request1);
            Assert.Equal(HttpStatusCode.OK, response1.StatusCode);

            // Second request with same master key
            var request2 = new HttpRequestMessage(HttpMethod.Get, "/api/test");
            request2.Headers.Add(header, prefix + "test-master-key");

            var response2 = await _client.SendAsync(request2);
            Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        }

        [Fact]
        public async Task InvalidEphemeralKey_ReturnsUnauthorized()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/test");
            request.Headers.Add("X-Master-Key", "emk_invalid_key_that_doesnt_exist");

            var response = await _client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task NoKey_ReturnsUnauthorized()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/test");
            // No key header

            var response = await _client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Theory]
        [InlineData("Bearer wrong-key")]
        [InlineData("Bearer emk_unknown")]
        [InlineData("Bearer")]
        [InlineData("Bearer ")]
        [InlineData("Basic test-master-key")]
        public async Task InvalidAuthorization_ReturnsUnauthorized(string authorization)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/test");
            request.Headers.TryAddWithoutValidation("Authorization", authorization);

            using var response = await _client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task InvalidPrimaryHeader_DoesNotFallBackToBearer()
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/test");
            request.Headers.Add("X-Custom-Key", "wrong-key");
            request.Headers.Add("Authorization", "Bearer test-master-key");

            using var response = await _client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("CONDUIT_API_TO_API_BACKEND_AUTH_KEY", null);
            _client?.Dispose();
            _server?.Dispose();
        }
    }

    // Test controller for integration tests
    [ApiController]
    [Route("api/[controller]")]
    [Authorize(Policy = "MasterKeyPolicy")]
    public class TestController : ControllerBase
    {
        [HttpGet]
        public IActionResult Get()
        {
            return Ok(new { message = "Success" });
        }
    }

}
