using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;
using Xunit;
using ConduitLLM.IntegrationTests.Core;

namespace ConduitLLM.IntegrationTests.Tests;

[Collection("Sequential")]
[Trait("Category", "Integration")]
public class StreamingWithToolCallsTest : ProviderIntegrationTestBase
{
    private readonly ILogger<StreamingWithToolCallsTest> _specificLogger;

    public StreamingWithToolCallsTest(TestFixture fixture) : base(fixture, "cerebras")
    {
        _specificLogger = _fixture.ServiceProvider.GetRequiredService<ILogger<StreamingWithToolCallsTest>>();
    }

    protected override ILogger CreateLogger()
    {
        return _fixture.ServiceProvider.GetRequiredService<ILogger<StreamingWithToolCallsTest>>();
    }

    [Fact(DisplayName = "Streaming with Tool Calls - Should Emit tool-executing Events",
        Skip = "Inline JavaScript function configurations were removed; functions are now provider-typed " +
               "(Exa, Tavily, MCP, ...). Rewrite against a function provider or a local MCP server.")]
    public async Task StreamingWithToolCalls_ShouldEmitToolExecutingEvents()
    {
        // This test verifies the complete tool execution lifecycle during streaming:
        // 1. Model emits tool_calls with finish_reason="tool_calls"
        // 2. Backend sends "event: tool-executing" with status="started"
        // 3. Backend executes function via FunctionsService
        // 4. Backend sends "event: tool-executing" with status="completed" and result
        // 5. Model continues streaming with tool results
        // 6. Backend sends "event: metrics-final" at completion

        bool reportGenerated = false;

        try
        {
            // Setup provider infrastructure
            var setupSuccess = await SetupProviderInfrastructure();
            setupSuccess.Should().BeTrue("Provider infrastructure should be set up successfully");

            // Create a test function configuration for weather lookup
            var weatherFunctionId = await CreateWeatherFunction();
            weatherFunctionId.Should().BeGreaterThan(0, "Weather function should be created successfully");

            try
            {
                // Send streaming chat request with function calling
                var streamingEvents = await SendStreamingChatWithFunctions(weatherFunctionId);

                // Verify we received tool-executing events
                var toolExecutingEvents = streamingEvents
                    .Where(e => e.EventType == "tool-executing")
                    .ToList();

                toolExecutingEvents.Should().NotBeEmpty("Should receive at least one tool-executing event");

                // Verify tool execution started event
                var startedEvent = toolExecutingEvents
                    .FirstOrDefault(e => e.Data.Contains("\"status\":\"started\""));
                startedEvent.Should().NotBeNull("Should receive tool-executing event with status=started");

                // Verify tool execution completed event
                var completedEvent = toolExecutingEvents
                    .FirstOrDefault(e => e.Data.Contains("\"status\":\"completed\""));
                completedEvent.Should().NotBeNull("Should receive tool-executing event with status=completed");

                // Verify completed event contains result and cost
                if (completedEvent != null)
                {
                    completedEvent.Data.Should().Contain("\"result\"", "Completed event should include result");
                    completedEvent.Data.Should().Contain("\"cost\"", "Completed event should include cost");
                    completedEvent.Data.Should().Contain("\"function_name\"", "Completed event should include function_name");
                }

                // Verify we received content chunks (model response with tool results)
                var contentEvents = streamingEvents
                    .Where(e => e.EventType == null || e.EventType == "content")
                    .ToList();
                contentEvents.Should().NotBeEmpty("Should receive content chunks after tool execution");

                // Verify we received final metrics
                var finalMetricsEvent = streamingEvents
                    .FirstOrDefault(e => e.EventType == "metrics-final");
                finalMetricsEvent.Should().NotBeNull("Should receive metrics-final event");

                if (finalMetricsEvent != null)
                {
                    finalMetricsEvent.Data.Should().Contain("\"total_tokens\"", "Final metrics should include total_tokens");
                    finalMetricsEvent.Data.Should().Contain("\"tokens_per_second\"", "Final metrics should include tokens_per_second");
                }

                _context.Observations.Add($"Received {toolExecutingEvents.Count} tool-executing events");
                _context.Observations.Add($"Received {contentEvents.Count} content chunks");
                _context.Observations.Add($"Tool execution lifecycle verified successfully");
            }
            catch (Exception chatEx)
            {
                _specificLogger.LogError(chatEx, "Streaming with tool calls test failed");
                _context.Errors.Add($"Test failed: {chatEx.Message}");
            }

            // Generate report
            await GenerateReport();
            reportGenerated = true;

            // Check if there were errors
            if (_context.Errors.Any())
            {
                var errorMessage = string.Join("; ", _context.Errors);
                _specificLogger.LogError("Test completed with errors: {Errors}", errorMessage);
                throw new Exception($"Test failed with errors: {errorMessage}");
            }
        }
        catch (Exception ex)
        {
            _context.Errors.Add($"Test failed: {ex.Message}");
            _context.SaveToFile();

            if (!reportGenerated)
            {
                try
                {
                    await GenerateReport();
                }
                catch
                {
                    _logger.LogError("Failed to generate report");
                }
            }

            throw;
        }
    }

    private async Task<int> CreateWeatherFunction()
    {
        // Create a simple weather lookup function for testing
        var functionConfig = new
        {
            configurationName = "test-weather-function",
            description = "Get current weather for a location",
            type = "inline",
            inlineCode = @"
                function execute(args) {
                    const location = args.location || 'Unknown';
                    return {
                        location: location,
                        temperature: 72,
                        condition: 'Sunny',
                        humidity: 65
                    };
                }
            ",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    location = new
                    {
                        type = "string",
                        description = "The city and state, e.g. San Francisco, CA"
                    }
                },
                required = new[] { "location" }
            }
        };

        var response = await _apiClient.PostAsync<JsonElement>(
            "/functions/configurations",
            functionConfig
        );

        return response.GetProperty("id").GetInt32();
    }

    private async Task<List<StreamEvent>> SendStreamingChatWithFunctions(int functionId)
    {
        var requestBody = new
        {
            model = _context.ModelAlias,
            messages = new[]
            {
                new { role = "user", content = "What's the weather like in San Francisco?" }
            },
            stream = true,
            function_configuration_ids = new[] { functionId }
        };

        var events = new List<StreamEvent>();

        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, "/v1/conduit/chat/completions")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(requestBody),
                Encoding.UTF8,
                "application/json"
            )
        };

        requestMessage.Headers.Add("Authorization", $"Bearer {_context.VirtualKey}");

        using var response = await _apiClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        string? currentEventType = null;

        while (true)
        {
            var line = await reader.ReadLineAsync();
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (line.StartsWith("event:"))
            {
                currentEventType = line.Substring(6).Trim();
            }
            else if (line.StartsWith("data:"))
            {
                var data = line.Substring(5).Trim();
                if (data == "[DONE]") break;

                events.Add(new StreamEvent
                {
                    EventType = currentEventType,
                    Data = data
                });

                // Reset event type for next event
                currentEventType = null;
            }
        }

        return events;
    }

    private class StreamEvent
    {
        public string? EventType { get; set; }
        public string Data { get; set; } = string.Empty;
    }
}
