using System.Globalization;
using System.Text;
using System.Text.Json;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Functions.Utilities;

namespace ConduitLLM.Admin.Services
{
    /// <summary>
    /// Service implementation for managing model costs - CSV/JSON parsing functionality
    /// </summary>
    public partial class AdminModelCostService
    {
        private const int CsvColumnCount = 11;

        private string GenerateJsonExport(List<ModelCost> modelCosts)
        {
            _logger.LogDebug("Generating JSON export for {Count} model costs", modelCosts.Count);

            var exportData = modelCosts.Select(mc => new ModelCostExportDto
            {
                CostName = mc.CostName,
                PricingModel = mc.PricingModel,
                PricingConfiguration = mc.PricingConfiguration,
                InputCostPerMillionTokens = mc.InputCostPerMillionTokens,
                OutputCostPerMillionTokens = mc.OutputCostPerMillionTokens,
                EmbeddingCostPerMillionTokens = mc.EmbeddingCostPerMillionTokens,
                BatchProcessingMultiplier = mc.BatchProcessingMultiplier,
                SupportsBatchProcessing = mc.SupportsBatchProcessing,
                CostPerSearchUnit = mc.CostPerSearchUnit,
                CachedInputCostPerMillionTokens = mc.CachedInputCostPerMillionTokens,
                CachedInputWriteCostPerMillionTokens = mc.CachedInputWriteCostPerMillionTokens
            });

            return AdminJson.Serialize(exportData, new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }

        private string GenerateCsvExport(List<ModelCost> modelCosts)
        {
            _logger.LogDebug("Generating CSV export for {Count} model costs", modelCosts.Count);

            var csv = new StringBuilder();
            csv.AppendLine("Cost Name,Pricing Model,Pricing Configuration,Input Cost (per million tokens),Output Cost (per million tokens),Embedding Cost (per million tokens),Batch Processing Multiplier,Supports Batch Processing,Search Unit Cost (per 1K units),Cached Input Cost (per million tokens),Cached Write Cost (per million tokens)");

            foreach (var modelCost in modelCosts.OrderBy(mc => mc.CostName))
            {
                csv.AppendLine($"{EscapeCsvValue(modelCost.CostName)}," +
                    $"{modelCost.PricingModel}," +
                    $"{EscapeCsvValue(modelCost.PricingConfiguration ?? "")}," +
                    $"{modelCost.InputCostPerMillionTokens.ToString("F6", CultureInfo.InvariantCulture)}," +
                    $"{modelCost.OutputCostPerMillionTokens.ToString("F6", CultureInfo.InvariantCulture)}," +
                    $"{(modelCost.EmbeddingCostPerMillionTokens.HasValue ? modelCost.EmbeddingCostPerMillionTokens.Value.ToString("F6", CultureInfo.InvariantCulture) : "")}," +
                    $"{(modelCost.BatchProcessingMultiplier?.ToString("F4", CultureInfo.InvariantCulture) ?? "")}," +
                    $"{(modelCost.SupportsBatchProcessing ? "Yes" : "No")}," +
                    $"{(modelCost.CostPerSearchUnit?.ToString("F6", CultureInfo.InvariantCulture) ?? "")}," +
                    $"{(modelCost.CachedInputCostPerMillionTokens?.ToString("F6", CultureInfo.InvariantCulture) ?? "")}," +
                    $"{(modelCost.CachedInputWriteCostPerMillionTokens?.ToString("F6", CultureInfo.InvariantCulture) ?? "")}");
            }

            return csv.ToString();
        }

        private List<CreateModelCostDto> ParseJsonImport(string jsonData)
        {
            try
            {
                var importData = AdminJson.Deserialize<List<ModelCostExportDto>>(jsonData);
                if (importData == null) return new List<CreateModelCostDto>();

                _logger.LogDebug("Parsed {Count} model costs from JSON import", importData.Count);

                return importData.Select(d => new CreateModelCostDto
                {
                    CostName = d.CostName,
                    PricingModel = d.PricingModel,
                    PricingConfiguration = StructuredJson.ParseObject(d.PricingConfiguration),
                    InputCostPerMillionTokens = d.InputCostPerMillionTokens,
                    OutputCostPerMillionTokens = d.OutputCostPerMillionTokens,
                    EmbeddingCostPerMillionTokens = d.EmbeddingCostPerMillionTokens,
                    BatchProcessingMultiplier = d.BatchProcessingMultiplier,
                    SupportsBatchProcessing = d.SupportsBatchProcessing,
                    CostPerSearchUnit = d.CostPerSearchUnit,
                    CachedInputCostPerMillionTokens = d.CachedInputCostPerMillionTokens,
                    CachedInputWriteCostPerMillionTokens = d.CachedInputWriteCostPerMillionTokens
                }).ToList();
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Failed to parse JSON import data");
                throw new ArgumentException("Invalid JSON format", ex);
            }
        }

        private List<CreateModelCostDto> ParseCsvImport(string csvData)
        {
            var modelCosts = new List<CreateModelCostDto>();
            var rows = ParseCsvRows(csvData)
                .Where(row => row.Fields.Any(field => !string.IsNullOrWhiteSpace(field)))
                .ToList();

            if (rows.Count < 2)
            {
                throw new ArgumentException("CSV data must contain header and at least one data row");
            }

            if (rows[0].Fields.Count != CsvColumnCount)
            {
                throw new ArgumentException(
                    $"CSV header must contain exactly {CsvColumnCount} columns, but contained {rows[0].Fields.Count}.");
            }

            // Skip header
            for (var i = 1; i < rows.Count; i++)
            {
                var row = rows[i];
                var parts = row.Fields;
                if (parts.Count != CsvColumnCount)
                {
                    throw new ArgumentException(
                        $"Invalid CSV data at line {row.LineNumber}: expected {CsvColumnCount} columns, but found {parts.Count}.");
                }

                try
                {
                    var modelCost = new CreateModelCostDto
                    {
                        CostName = parts[0],
                        PricingModel = ParsePricingModel(parts[1], row.LineNumber),
                        PricingConfiguration = StructuredJson.ParseObject(parts[2]),
                        InputCostPerMillionTokens = ParseRequiredDecimal(parts[3], "input cost", row.LineNumber),
                        OutputCostPerMillionTokens = ParseRequiredDecimal(parts[4], "output cost", row.LineNumber),
                        EmbeddingCostPerMillionTokens = ParseOptionalDecimal(parts[5], "embedding cost", row.LineNumber),
                        BatchProcessingMultiplier = ParseOptionalDecimal(parts[6], "batch processing multiplier", row.LineNumber),
                        SupportsBatchProcessing = ParseBoolean(parts[7], row.LineNumber),
                        CostPerSearchUnit = ParseOptionalDecimal(parts[8], "search unit cost", row.LineNumber),
                        CachedInputCostPerMillionTokens = ParseOptionalDecimal(parts[9], "cached input cost", row.LineNumber),
                        CachedInputWriteCostPerMillionTokens = ParseOptionalDecimal(parts[10], "cached write cost", row.LineNumber)
                    };

                    modelCosts.Add(modelCost);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to parse CSV data at line {LineNumber}", row.LineNumber);
                    throw new ArgumentException($"Invalid CSV data at line {row.LineNumber}: {ex.Message}", ex);
                }
            }

            _logger.LogDebug("Parsed {Count} model costs from CSV import ({TotalLines} data lines)",
                modelCosts.Count, rows.Count - 1);

            return modelCosts;
        }

        private static string EscapeCsvValue(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";

            if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            {
                return $"\"{value.Replace("\"", "\"\"")}\"";
            }

            return value;
        }

        private static List<CsvRow> ParseCsvRows(string csvData)
        {
            var rows = new List<CsvRow>();
            var fields = new List<string>();
            var field = new StringBuilder();
            var inQuotes = false;
            var quoteClosed = false;
            var lineNumber = 1;
            var rowStartLine = 1;

            void CompleteField()
            {
                fields.Add(field.ToString());
                field.Clear();
                quoteClosed = false;
            }

            void CompleteRow()
            {
                CompleteField();
                rows.Add(new CsvRow(rowStartLine, fields.ToList()));
                fields.Clear();
                rowStartLine = lineNumber + 1;
            }

            for (var index = 0; index < csvData.Length; index++)
            {
                var current = csvData[index];
                if (inQuotes)
                {
                    if (current == '"')
                    {
                        if (index + 1 < csvData.Length && csvData[index + 1] == '"')
                        {
                            field.Append('"');
                            index++;
                        }
                        else
                        {
                            inQuotes = false;
                            quoteClosed = true;
                        }
                    }
                    else
                    {
                        field.Append(current);
                        if (current == '\n')
                        {
                            lineNumber++;
                        }
                    }

                    continue;
                }

                switch (current)
                {
                    case '"' when field.Length == 0 && !quoteClosed:
                        inQuotes = true;
                        break;
                    case '"':
                        throw new ArgumentException($"Unexpected quote in CSV field at line {lineNumber}.");
                    case ',':
                        CompleteField();
                        break;
                    case '\r':
                        CompleteRow();
                        if (index + 1 < csvData.Length && csvData[index + 1] == '\n')
                        {
                            index++;
                        }
                        lineNumber++;
                        rowStartLine = lineNumber;
                        break;
                    case '\n':
                        CompleteRow();
                        lineNumber++;
                        rowStartLine = lineNumber;
                        break;
                    default:
                        if (quoteClosed)
                        {
                            throw new ArgumentException($"Unexpected character after closing quote at line {lineNumber}.");
                        }
                        field.Append(current);
                        break;
                }
            }

            if (inQuotes)
            {
                throw new ArgumentException($"Unterminated quoted CSV field beginning at line {rowStartLine}.");
            }

            if (field.Length > 0 || fields.Count > 0 || quoteClosed)
            {
                CompleteRow();
            }

            return rows;
        }

        private static PricingModel ParsePricingModel(string value, int lineNumber) =>
            Enum.TryParse<PricingModel>(value, ignoreCase: true, out var pricingModel) &&
            Enum.IsDefined(pricingModel)
                ? pricingModel
                : throw new FormatException($"Pricing model '{value}' at line {lineNumber} is invalid.");

        private static decimal ParseRequiredDecimal(string value, string columnName, int lineNumber) =>
            decimal.TryParse(
                value,
                NumberStyles.AllowLeadingSign |
                NumberStyles.AllowDecimalPoint |
                NumberStyles.AllowLeadingWhite |
                NumberStyles.AllowTrailingWhite,
                CultureInfo.InvariantCulture,
                out var parsed)
                ? parsed
                : throw new FormatException($"The {columnName} value '{value}' at line {lineNumber} is invalid.");

        private static decimal? ParseOptionalDecimal(string value, string columnName, int lineNumber) =>
            string.IsNullOrWhiteSpace(value)
                ? null
                : ParseRequiredDecimal(value, columnName, lineNumber);

        private static bool ParseBoolean(string value, int lineNumber)
        {
            value = value.Trim();
            if (value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (value.Equals("no", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            throw new FormatException($"Supports batch processing value '{value}' at line {lineNumber} is invalid.");
        }

        private sealed record CsvRow(int LineNumber, List<string> Fields);
    }
}
