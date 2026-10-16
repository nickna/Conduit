using System.Globalization;
using System.Text;
using System.Text.Json;

using ConduitLLM.Configuration.Entities;

using Moq;

namespace ConduitLLM.Tests.Admin.Services
{
    /// <summary>
    /// Export operation tests for AnalyticsServiceTests
    /// </summary>
    public partial class AnalyticsServiceTests
    {
        #region ExportAnalyticsAsync Tests

        [Fact]
        public async Task ExportAnalyticsAsync_CSV_GeneratesCorrectFormat()
        {
            // Arrange
            var testLogs = new List<RequestLog>
            {
                new() { 
                    Id = 1,
                    VirtualKeyId = 1, 
                    ModelName = "gpt-4", 
                    RequestType = "chat",
                    Cost = 0.05m,
                    InputTokens = 100,
                    OutputTokens = 50,
                    ResponseTimeMs = 1500.5,
                    StatusCode = 200,
                    Timestamp = new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc)
                }
            };
            
            _mockRequestLogRepository
                .Setup(x => x.GetByDateRangeFilteredAsync(
                    It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(testLogs);

            // Act
            var result = await _service.ExportAnalyticsAsync("csv");
            var csv = System.Text.Encoding.UTF8.GetString(result);

            // Assert
            Assert.Contains("Timestamp,VirtualKeyId,Model,RequestType,InputTokens,OutputTokens,Cost,ResponseTime,StatusCode", csv);
            Assert.Contains("2024-01-15 10:30:00,1,gpt-4,chat,100,50,0.05000000,1500.50,200", csv);
        }

        [Fact]
        public async Task ExportAnalyticsAsync_JSON_GeneratesCorrectFormat()
        {
            // Arrange
            var testLogs = new List<RequestLog>
            {
                new() { 
                    Id = 1,
                    VirtualKeyId = 1, 
                    ModelName = "gpt-4",
                    RequestType = "chat",
                    Cost = 0.05m,
                    Timestamp = DateTime.UtcNow
                }
            };
            
            _mockRequestLogRepository
                .Setup(x => x.GetByDateRangeFilteredAsync(
                    It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(testLogs);

            // Act
            var result = await _service.ExportAnalyticsAsync("json");
            var json = System.Text.Encoding.UTF8.GetString(result);

            // Assert
            Assert.Contains("\"ModelName\":", json);
            Assert.Contains("\"gpt-4\"", json);
            Assert.Contains("\"Cost\":", json);
        }

        [Theory]
        [InlineData("en-US")]
        [InlineData("fr-FR")]
        public async Task ExportAnalyticsAsync_CsvAndJsonPreserveEightDecimalChargesAcrossCultures(string cultureName)
        {
            using var culture = new ExportCultureScope(cultureName);
            var costs = new[] { 0.00000001m, 0.12500001m };
            var expectedCostText = new[] { "0.00000001", "0.12500001" };
            var timestamp = new DateTime(2026, 10, 16, 10, 30, 0, DateTimeKind.Utc);
            var logs = costs.Select((cost, index) => new RequestLog
            {
                Id = index + 1,
                VirtualKeyId = 1234,
                ModelName = "precision-fixture",
                RequestType = "chat",
                InputTokens = 987654,
                OutputTokens = 123456,
                Cost = cost,
                ResponseTimeMs = 1500.5,
                StatusCode = 200,
                Timestamp = timestamp
            }).ToList();
            _mockRequestLogRepository
                .Setup(repository => repository.GetByDateRangeFilteredAsync(
                    It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(logs);

            var csv = Encoding.UTF8.GetString(await _service.ExportAnalyticsAsync("csv"));
            var rows = csv.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(3, rows.Length);
            Assert.Equal("Timestamp,VirtualKeyId,Model,RequestType,InputTokens,OutputTokens,Cost,ResponseTime,StatusCode", rows[0]);
            for (var index = 0; index < costs.Length; index++)
            {
                var fields = rows[index + 1].Split(',');
                // These fixture text columns contain no CSV delimiters. A culture-
                // dependent decimal comma would create an extra numeric column.
                Assert.Equal(9, fields.Length);
                Assert.Equal("2026-10-16 10:30:00", fields[0]);
                Assert.Equal("1234", fields[1]);
                Assert.Equal("precision-fixture", fields[2]);
                Assert.Equal("chat", fields[3]);
                Assert.Equal("987654", fields[4]);
                Assert.Equal("123456", fields[5]);
                Assert.Equal(expectedCostText[index], fields[6]);
                Assert.Equal(costs[index], decimal.Parse(fields[6], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture));
                Assert.Equal("1500.50", fields[7]);
                Assert.Equal("200", fields[8]);
            }

            using var json = JsonDocument.Parse(await _service.ExportAnalyticsAsync("json"));
            Assert.Equal(2, json.RootElement.GetArrayLength());
            for (var index = 0; index < costs.Length; index++)
            {
                var row = json.RootElement[index];
                Assert.Equal(JsonValueKind.Number, row.GetProperty("Cost").ValueKind);
                Assert.Equal(costs[index], row.GetProperty("Cost").GetDecimal());
                Assert.Equal(1234, row.GetProperty("VirtualKeyId").GetInt32());
                Assert.Equal(987654, row.GetProperty("InputTokens").GetInt32());
                Assert.Equal(123456, row.GetProperty("OutputTokens").GetInt32());
                Assert.Equal(1500.5, row.GetProperty("ResponseTimeMs").GetDouble());
                Assert.Equal(200, row.GetProperty("StatusCode").GetInt32());
            }
        }

        private sealed class ExportCultureScope : IDisposable
        {
            private readonly CultureInfo _originalCulture = CultureInfo.CurrentCulture;
            private readonly CultureInfo _originalUiCulture = CultureInfo.CurrentUICulture;

            public ExportCultureScope(string name)
            {
                // These values flow with this async test. Process-wide thread
                // defaults remain untouched while other xUnit tests execute.
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
            }

            public void Dispose()
            {
                CultureInfo.CurrentCulture = _originalCulture;
                CultureInfo.CurrentUICulture = _originalUiCulture;
            }
        }

        #endregion
    }
}
