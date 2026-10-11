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
public class StreamingWithReasoningTest : ProviderIntegrationTestBase
{
    private readonly ILogger<StreamingWithReasoningTest> _specificLogger;

    public StreamingWithReasoningTest(TestFixture fixture) : base(fixture, "cerebras")
    {
        _specificLogger = _fixture.ServiceProvider.GetRequiredService<ILogger<StreamingWithReasoningTest>>();
    }

    protected override ILogger CreateLogger()
    {
        return _fixture.ServiceProvider.GetRequiredService<ILogger<StreamingWithReasoningTest>>();
    }

    [Fact(DisplayName = "Streaming with Reasoning - Should Emit reasoning Events")]
    public async Task StreamingWithReasoning_ShouldEmitReasoningEvents()
    {
        // This test verifies that models that output reasoning content
        // correctly emit "event: reasoning" events during streaming.
        // Some models (like gpt-oss-20b) non-deterministically output
        // to the reasoning field instead of the content field.

        bool reportGenerated = false;

        try
        {
            // Setup provider infrastructure
            var setupSuccess = await SetupProviderInfrastructure();
            setupSuccess.Should().BeTrue("Provider infrastructure should be set up successfully");

            try
            {
                // Send streaming chat request
                var streamingEvents = await SendStreamingChatRequest();

                // Check if we received any reasoning events
                var reasoningEvents = streamingEvents
                    .Where(e => e.EventType == "reasoning")
                    .ToList();

                if (reasoningEvents.Any())
                {
                    // If we got reasoning events, verify their structure
                    foreach (var reasoningEvent in reasoningEvents)
                    {
                        reasoningEvent.Data.Should().Contain("\"content\"", "Reasoning event should include content field");

                        // Verify content is not empty
                        var jsonDoc = JsonDocument.Parse(reasoningEvent.Data);
                        var content = jsonDoc.RootElement.GetProperty("content").GetString();
                        content.Should().NotBeNullOrWhiteSpace("Reasoning content should not be empty");
                    }

                    _context.Observations.Add($"Received {reasoningEvents.Count} reasoning events");
                    _context.Observations.Add("Model used reasoning output field");
                }
                else
                {
                    // If no reasoning events, verify we got regular content
                    var contentEvents = streamingEvents
                        .Where(e => e.EventType == null || e.EventType == "content")
                        .Where(e => !e.Data.Contains("[DONE]"))
                        .ToList();

                    contentEvents.Should().NotBeEmpty("Should receive either reasoning or content events");
                    _context.Observations.Add($"Received {contentEvents.Count} content events (no reasoning)");
                    _context.Observations.Add("Model used standard content output field");
                }

                // Verify we received final metrics
                var finalMetricsEvent = streamingEvents
                    .FirstOrDefault(e => e.EventType == "metrics-final");
                finalMetricsEvent.Should().NotBeNull("Should receive metrics-final event");

                if (finalMetricsEvent != null)
                {
                    finalMetricsEvent.Data.Should().Contain("\"total_tokens\"", "Final metrics should include total_tokens");
                    finalMetricsEvent.Data.Should().Contain("\"tokens_per_second\"", "Final metrics should include tokens_per_second");

                    _context.Observations.Add("Received final metrics");
                }

                _context.Observations.Add("Reasoning event handling verified successfully");
            }
            catch (Exception chatEx)
            {
                _specificLogger.LogError(chatEx, "Streaming with reasoning test failed");
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

    private async Task<List<StreamEvent>> SendStreamingChatRequest()
    {
        var requestBody = new
        {
            model = _context.ModelAlias,
            messages = new[]
            {
                new { role = "user", content = "Explain step by step how photosynthesis works." }
            },
            stream = true
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
