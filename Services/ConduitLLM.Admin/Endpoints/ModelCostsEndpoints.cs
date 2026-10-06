using System.Text;
using System.Text.Json;

using ConduitLLM.Admin.Extensions;
using ConduitLLM.Admin.Auditing;
using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Services;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Services;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ConduitLLM.Admin.Endpoints
{
    /// <summary>
    /// Controller for managing model costs
    /// </summary>
    public class ModelCostsEndpoints
    {
        private readonly IAdminModelCostService _modelCostService;
        private readonly IPricingRulesValidator _pricingRulesValidator;
        private readonly IDbContextFactory<ConduitDbContext> _dbContextFactory;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly ILogger<ModelCostsEndpoints> _logger;

        /// <summary>
        /// Initializes the Model Costs endpoint handler.
        /// </summary>
        /// <param name="modelCostService">The model cost service</param>
        /// <param name="pricingRulesValidator">The pricing rules validator</param>
        /// <param name="dbContextFactory">Factory used to load persisted model parameter schemas</param>
        /// <param name="httpContextAccessor">Accessor for the current request context</param>
        /// <param name="logger">The logger</param>
        public ModelCostsEndpoints(
            IAdminModelCostService modelCostService,
            IPricingRulesValidator pricingRulesValidator,
            IDbContextFactory<ConduitDbContext> dbContextFactory,
            IHttpContextAccessor httpContextAccessor,
            ILogger<ModelCostsEndpoints> logger)
        {
            _modelCostService = modelCostService ?? throw new ArgumentNullException(nameof(modelCostService));
            _pricingRulesValidator = pricingRulesValidator ?? throw new ArgumentNullException(nameof(pricingRulesValidator));
            _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
            _httpContextAccessor = httpContextAccessor;
            _logger = logger;
        }

        public static IEndpointRouteBuilder MapModelCostsEndpoints(IEndpointRouteBuilder app)
        {
            var g = app.MapGroup("/v1/admin/model-costs").RequireAuthorization("MasterKeyPolicy").AddEndpointFilter<OperationLoggingEndpointFilter>().WithTags("Model Costs");
            g.MapGet("/", ([FromServices] ModelCostsEndpoints e, int? page=null, int? pageSize=null, string? modelType=null, int? providerId=null, bool? isActive=null) => e.GetAllModelCosts(page,pageSize,modelType,providerId,isActive)).WithName("ModelCosts_GetAll").Produces<PagedResult<ModelCostDto>>();
            g.MapGet("/{id:int}", ([FromServices] ModelCostsEndpoints e,int id)=>e.GetModelCostById(id)).WithName("ModelCosts_GetById").Produces<ModelCostDto>().Produces(StatusCodes.Status404NotFound);
            g.MapGet("/provider/costs/{providerId:int}", ([FromServices] ModelCostsEndpoints e,int providerId)=>e.GetModelCostsByProvider(providerId)).WithName("ModelCosts_GetByProvider").Produces<IEnumerable<ModelCostDto>>();
            g.MapGet("/provider/{providerId:int}", ([FromServices] ModelCostsEndpoints e,int providerId)=>e.GetModelCostsByProvider(providerId)).ExcludeFromDescription();
            g.MapGet("/name/costs/{costName}", ([FromServices] ModelCostsEndpoints e,string costName)=>e.GetModelCostByCostName(costName)).WithName("ModelCosts_GetByName").Produces<ModelCostDto>().Produces(StatusCodes.Status404NotFound);
            g.MapGet("/name/{costName}", ([FromServices] ModelCostsEndpoints e,string costName)=>e.GetModelCostByCostName(costName)).ExcludeFromDescription();
            g.MapPost("/", ([FromServices] ModelCostsEndpoints e,CreateModelCostDto d)=>e.CreateModelCost(d)).WithName("ModelCosts_Create").Produces<ModelCostDto>(StatusCodes.Status201Created).Produces(StatusCodes.Status400BadRequest);
            g.MapPatch("/{id:int}", ([FromServices] ModelCostsEndpoints e, int id, JsonMergePatch<UpdateModelCostDto> patch) => e.UpdateModelCost(id, patch.Value)).AcceptsJsonMergePatch<UpdateModelCostDto>().WithName("ModelCosts_Update").Produces<ModelCostDto>().Produces(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status404NotFound);
            g.MapDelete("/{id:int}", ([FromServices] ModelCostsEndpoints e,int id)=>e.DeleteModelCost(id)).WithName("ModelCosts_Delete").Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound);
            g.MapGet("/overview", ([FromServices] ModelCostsEndpoints e,DateTime? startDate=null,DateTime? endDate=null)=>e.GetModelCostOverview(startDate ?? default,endDate ?? default)).WithName("ModelCosts_GetOverview").Produces<IEnumerable<ModelCostOverviewDto>>().Produces(StatusCodes.Status400BadRequest);
            g.MapPost("/import", ([FromServices] ModelCostsEndpoints e,IEnumerable<CreateModelCostDto> d)=>e.ImportModelCosts(d)).WithName("ModelCosts_Import").Accepts<IEnumerable<CreateModelCostDto>>("application/json").Produces<BulkImportResult>().Produces(StatusCodes.Status400BadRequest);
            g.MapGet("/export/csv", ([FromServices] ModelCostsEndpoints e,int? providerId=null)=>e.ExportCsv(providerId)).WithName("ModelCosts_ExportCsv").Produces(StatusCodes.Status200OK,typeof(void),"text/csv");
            g.MapGet("/export/json", ([FromServices] ModelCostsEndpoints e,int? providerId=null)=>e.ExportJson(providerId)).WithName("ModelCosts_ExportJson").Produces(StatusCodes.Status200OK,typeof(void),"application/json");
            g.MapPost("/import/csv", ([FromServices] ModelCostsEndpoints e,IFormFile file)=>e.ImportCsv(file)).WithName("ModelCosts_ImportCsv").DisableAntiforgery().Accepts<IFormFile>("multipart/form-data").Produces<BulkImportResult>().Produces(StatusCodes.Status400BadRequest);
            g.MapPost("/import/json", ([FromServices] ModelCostsEndpoints e,IFormFile file)=>e.ImportJson(file)).WithName("ModelCosts_ImportJson").DisableAntiforgery().Accepts<IFormFile>("multipart/form-data").Produces<BulkImportResult>().Produces(StatusCodes.Status400BadRequest);
            g.MapPost("/{id:int}/validate-pricing-rules", ([FromServices] ModelCostsEndpoints e,int id,ValidatePricingRulesRequest d)=>e.ValidatePricingRules(id,d)).WithName("ModelCosts_ValidatePricingRules").Produces<PricingRulesValidationResult>().Produces(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status404NotFound);
            g.MapPost("/validate-pricing-rules", ([FromServices] ModelCostsEndpoints e,ValidatePricingRulesRequest d)=>e.ValidatePricingRulesStandalone(d)).WithName("ModelCosts_ValidatePricingRulesStandalone").Produces<PricingRulesValidationResult>().Produces(StatusCodes.Status400BadRequest);
            return app;
        }

        /// <summary>
        /// Gets all model costs with optional pagination and filtering
        /// </summary>
        /// <param name="page">Page number (1-based)</param>
        /// <param name="pageSize">Number of items per page</param>
        /// <param name="modelType">Optional filter by model type (chat, image, video, embedding, audio)</param>
        /// <param name="providerId">Optional provider identifier filter</param>
        /// <param name="isActive">Optional active-state filter</param>
        /// <returns>List of all model costs or paginated response</returns>
        public async Task<IResult> GetAllModelCosts(
            [FromQuery] int? page = null,
            [FromQuery] int? pageSize = null,
            [FromQuery] string? modelType = null,
            [FromQuery] int? providerId = null,
            [FromQuery] bool? isActive = null)
        {
            var (effectivePage, effectivePageSize) =
                Pagination.Normalize(page ?? 1, pageSize ?? Pagination.DefaultPageSize);
            var modelCosts = providerId.HasValue
                ? await _modelCostService.GetModelCostsByProviderAsync(providerId.Value)
                : await _modelCostService.GetAllModelCostsAsync();

            // Apply modelType filter if provided
            if (!string.IsNullOrWhiteSpace(modelType))
            {
                modelCosts = modelCosts.Where(c =>
                    string.Equals(c.ModelType, modelType, StringComparison.OrdinalIgnoreCase));
            }

            if (isActive.HasValue)
                modelCosts = modelCosts.Where(c => c.IsActive == isActive.Value);

            var totalCount = modelCosts.Count();
            return Results.Ok(new PagedResult<ModelCostDto>
            {
                Data = modelCosts.Skip((effectivePage - 1) * effectivePageSize).Take(effectivePageSize).ToList(),
                Pagination = PaginationMetadata.Create(effectivePage, effectivePageSize, totalCount)
            });
        }

        /// <summary>
        /// Gets a model cost by ID
        /// </summary>
        /// <param name="id">The ID of the model cost</param>
        /// <returns>The model cost</returns>
        public async Task<IResult> GetModelCostById(int id)
        {
            var modelCost = await _modelCostService.GetModelCostByIdAsync(id);
            if (modelCost == null)
            {
                return AdminResults.NotFoundEntity("Model cost", id);
            }
            return Results.Ok(modelCost);
        }

        /// <summary>
        /// Gets model costs by provider ID
        /// </summary>
        /// <param name="providerId">The ID of the provider</param>
        /// <returns>List of model costs for the specified provider</returns>
        public async Task<IResult> GetModelCostsByProvider(int providerId)
        {
            var result = await _modelCostService.GetModelCostsByProviderAsync(providerId);
            return Results.Ok(result);
        }

        /// <summary>
        /// Gets a model cost by cost name
        /// </summary>
        /// <param name="costName">The cost name</param>
        /// <returns>The model cost</returns>
        public async Task<IResult> GetModelCostByCostName(string costName)
        {
            var modelCost = await _modelCostService.GetModelCostByCostNameAsync(costName);
            if (modelCost == null)
            {
                return AdminResults.NotFoundEntity("Model cost", costName);
            }
            return Results.Ok(modelCost);
        }

        /// <summary>
        /// Creates a new model cost
        /// </summary>
        /// <param name="modelCost">The model cost to create</param>
        /// <returns>The created model cost</returns>
        public async Task<IResult> CreateModelCost(CreateModelCostDto modelCost)
        {
            var result = await _modelCostService.CreateModelCostAsync(modelCost);
            LogAdminAudit("Created", "ModelCost", result.Id, $"CostName: {LoggingSanitizer.S(result.CostName)}");
            return Results.Created($"/v1/admin/model-costs/{result.Id}", result);
        }

        /// <summary>
        /// Updates a model cost
        /// </summary>
        /// <param name="id">The ID of the model cost to update</param>
        /// <param name="modelCost">The updated model cost data</param>
        /// <returns>No content if successful</returns>
        public async Task<IResult> UpdateModelCost(int id, UpdateModelCostDto modelCost)
        {
            var updated = await _modelCostService.UpdateModelCostAsync(id, modelCost);

            if (updated == null)
            {
                throw new KeyNotFoundException($"Model cost with ID '{id}' not found");
            }

            LogAdminAudit("Updated", "ModelCost", id, $"CostName: {LoggingSanitizer.S(modelCost.CostName)}");

            return Results.Ok(updated);
        }

        /// <summary>
        /// Deletes a model cost
        /// </summary>
        /// <param name="id">The ID of the model cost to delete</param>
        /// <returns>No content if successful</returns>
        public async Task<IResult> DeleteModelCost(int id)
        {
            var existing = await _modelCostService.GetModelCostByIdAsync(id);
            var success = await _modelCostService.DeleteModelCostAsync(id);

            if (!success)
            {
                throw new KeyNotFoundException($"Model cost with ID '{id}' not found");
            }

            LogAdminAudit("Deleted", "ModelCost", id, existing != null ? $"CostName: {LoggingSanitizer.S(existing.CostName)}" : null);

            return Results.NoContent();
        }

        /// <summary>
        /// Gets model cost overview data for a specific time period
        /// </summary>
        /// <param name="startDate">The start date for the period (inclusive)</param>
        /// <param name="endDate">The end date for the period (inclusive)</param>
        /// <returns>List of model cost overview data</returns>
        public async Task<IResult> GetModelCostOverview(
            [FromQuery] DateTime startDate,
            [FromQuery] DateTime endDate)
        {
            if (startDate > endDate)
            {
                return AdminResults.BadRequest("Start date cannot be after end date");
            }

            var result = await _modelCostService.GetModelCostOverviewAsync(startDate, endDate);
            return Results.Ok(result);
        }

        /// <summary>
        /// Imports model costs from a list of DTOs
        /// </summary>
        /// <param name="modelCosts">The list of model costs to import</param>
        /// <returns>The number of model costs imported</returns>
        public async Task<IResult> ImportModelCosts(IEnumerable<CreateModelCostDto> modelCosts)
        {
            if (modelCosts == null || !modelCosts.Any())
            {
                return AdminResults.BadRequest("No model costs provided for import");
            }

            var result = await _modelCostService.ImportModelCostsAsync(modelCosts);
            LogAdminAuditBulk("Imported", "ModelCost", result.SuccessCount, result.FailureCount);
            return Results.Ok(result);
        }

        /// <summary>
        /// Exports model costs in CSV format
        /// </summary>
        /// <param name="providerId">Optional provider ID to filter by</param>
        /// <returns>CSV file containing model costs</returns>
        public async Task<IResult> ExportCsv(int? providerId = null)
        {
            var result = await _modelCostService.ExportModelCostsAsync("csv", providerId);
            var bytes = Encoding.UTF8.GetBytes(result);
            var fileName = $"model-costs-{DateTime.UtcNow:yyyy-MM-dd-HHmmss}.csv";
            return Results.File(bytes, "text/csv", fileName);
        }

        /// <summary>
        /// Exports model costs in JSON format
        /// </summary>
        /// <param name="providerId">Optional provider ID to filter by</param>
        /// <returns>JSON file containing model costs</returns>
        public async Task<IResult> ExportJson(int? providerId = null)
        {
            var result = await _modelCostService.ExportModelCostsAsync("json", providerId);
            var bytes = Encoding.UTF8.GetBytes(result);
            var fileName = $"model-costs-{DateTime.UtcNow:yyyy-MM-dd-HHmmss}.json";
            return Results.File(bytes, "application/json", fileName);
        }

        /// <summary>
        /// Imports model costs from CSV file
        /// </summary>
        /// <param name="file">CSV file containing model costs</param>
        /// <returns>Import result with statistics</returns>
        public async Task<IResult> ImportCsv(IFormFile file)
            => await ImportFile(file, "csv", "ImportedCsv");

        /// <summary>
        /// Imports model costs from JSON file
        /// </summary>
        /// <param name="file">JSON file containing model costs</param>
        /// <returns>Import result with statistics</returns>
        public async Task<IResult> ImportJson(IFormFile file)
            => await ImportFile(file, "json", "ImportedJson");

        private async Task<IResult> ImportFile(
            IFormFile file,
            string format,
            string auditOperation)
        {
            if (file == null || file.Length == 0)
            {
                return AdminResults.BadRequest("No file provided for import");
            }

            if (!file.FileName.EndsWith($".{format}", StringComparison.OrdinalIgnoreCase))
            {
                return AdminResults.BadRequest($"File must be a {format.ToUpperInvariant()} file");
            }

            using var reader = new StreamReader(file.OpenReadStream());
            var data = await reader.ReadToEndAsync();
            var result = await _modelCostService.ImportModelCostsAsync(data, format);

            if (result.SuccessCount == 0 && result.FailureCount > 0)
            {
                return AdminResults.Problem(
                    StatusCodes.Status400BadRequest,
                    "Import failed",
                    "import_failed",
                    extensions: new Dictionary<string, object?>
                    {
                        ["import_errors"] = result.Errors,
                        ["success_count"] = result.SuccessCount,
                        ["failure_count"] = result.FailureCount
                    });
            }

            LogAdminAuditBulk(auditOperation, "ModelCost", result.SuccessCount, result.FailureCount);
            return Results.Ok(result);
        }

        /// <summary>
        /// Validates a pricing rules configuration JSON
        /// </summary>
        /// <param name="id">The ID of the model cost (used to retrieve associated model's parameter schema)</param>
        /// <param name="request">The pricing configuration to validate</param>
        /// <returns>Validation result with errors and warnings</returns>
        public async Task<IResult> ValidatePricingRules(
            int id,
            [FromBody] ValidatePricingRulesRequest request)
        {
            if (request?.PricingConfiguration is null)
            {
                return AdminResults.BadRequest("Pricing configuration is required");
            }

            // Verify the model cost exists
            var modelCost = await _modelCostService.GetModelCostByIdAsync(id);
            if (modelCost == null)
            {
                throw new KeyNotFoundException($"Model cost with ID '{id}' not found");
            }

            // Validate the pricing document once before adding diagnostics from every distinct
            // persisted model schema. Caller-provided schemas are intentionally ignored here:
            // the model-cost-scoped endpoint treats the database associations as authoritative.
            var result = _pricingRulesValidator.ValidateJson(AdminJson.Serialize(request.PricingConfiguration));
            if (result.ParsedConfig == null)
            {
                return Results.Ok(result);
            }

            await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
            var associations = await dbContext.ModelProviderTypeAssociations
                .AsNoTracking()
                .Where(association => association.ModelCostId == id)
                .Include(association => association.Model)
                    .ThenInclude(model => model.Series)
                .OrderBy(association => association.ModelId)
                .ThenBy(association => association.Id)
                .ToListAsync();

            var associatedModels = associations
                .GroupBy(association => association.ModelId)
                .Select(group => group.First().Model)
                .ToList();

            if (associatedModels.Count == 0)
            {
                result.AddError(new ValidationError
                {
                    Field = "modelCostId",
                    Message = $"Model cost {id} has no associated models; persisted parameter schema validation could not be performed."
                });
                return Results.Ok(result);
            }

            var baseErrors = result.Errors.Select(GetErrorKey).ToHashSet();
            var baseWarnings = result.Warnings.ToHashSet(StringComparer.Ordinal);

            // A shared cost is compatible only when its rules validate against every distinct
            // associated model. Model-specific diagnostics are prefixed so callers can identify
            // the incompatible model while the aggregate IsValid flag remains authoritative.
            foreach (var model in associatedModels)
            {
                var modelLabel = $"Model {model.Id} ('{model.Name}')";
                var parameterSchema = model.ModelParameters ?? model.Series?.Parameters ?? "{}";

                if (!TryValidateParameterSchema(parameterSchema, out var schemaError))
                {
                    _logger.LogWarning(
                        "Skipping pricing-rule schema validation for model {ModelId}: {SchemaError}",
                        model.Id,
                        schemaError);
                    result.AddError(new ValidationError
                    {
                        Field = "parameterSchema",
                        Message = $"[{modelLabel}] {schemaError}"
                    });
                    continue;
                }

                var modelResult = _pricingRulesValidator.Validate(result.ParsedConfig, parameterSchema);
                foreach (var error in modelResult.Errors.Where(error => !baseErrors.Contains(GetErrorKey(error))))
                {
                    result.AddError(new ValidationError
                    {
                        Field = error.Field,
                        Message = $"[{modelLabel}] {error.Message}",
                        RuleIndex = error.RuleIndex
                    });
                }

                foreach (var warning in modelResult.Warnings.Where(warning => !baseWarnings.Contains(warning)))
                {
                    result.AddWarning($"[{modelLabel}] {warning}");
                }
            }

            return Results.Ok(result);
        }

        private static string GetErrorKey(ValidationError error) =>
            $"{error.Field}\u001f{error.RuleIndex}\u001f{error.Message}";

        private static bool TryValidateParameterSchema(string? parameterSchema, out string error)
        {
            if (string.IsNullOrWhiteSpace(parameterSchema))
            {
                error = "The persisted parameter schema is empty.";
                return false;
            }

            try
            {
                using var schemaDocument = JsonDocument.Parse(parameterSchema);
                if (schemaDocument.RootElement.ValueKind != JsonValueKind.Object)
                {
                    error = "The persisted parameter schema must be a JSON object.";
                    return false;
                }

                var definitions = schemaDocument.RootElement.EnumerateObject().ToList();
                if (definitions.Count == 0)
                {
                    error = "The persisted parameter schema is empty.";
                    return false;
                }

                if (definitions.Any(definition =>
                        string.IsNullOrWhiteSpace(definition.Name) ||
                        definition.Value.ValueKind != JsonValueKind.Object))
                {
                    error = "The persisted parameter schema contains an invalid parameter definition.";
                    return false;
                }
            }
            catch (JsonException)
            {
                error = "The persisted parameter schema is invalid JSON.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        /// <summary>
        /// Validates a pricing rules configuration JSON without a model cost context
        /// </summary>
        /// <param name="request">The pricing configuration to validate</param>
        /// <returns>Validation result with errors and warnings</returns>
        public async Task<IResult> ValidatePricingRulesStandalone(ValidatePricingRulesRequest request)
        {
            if (request?.PricingConfiguration is null)
            {
                return AdminResults.BadRequest("Pricing configuration is required");
            }

            await Task.CompletedTask;

            var result = _pricingRulesValidator.ValidateJson(
                AdminJson.Serialize(request.PricingConfiguration),
                request.ParameterSchema is null ? null : AdminJson.Serialize(request.ParameterSchema));
            return Results.Ok(result);
        }

        private void LogAdminAudit(string operation, string entityType, object? entityId = null, string? detail = null) => AdminAudit.Log(_httpContextAccessor.HttpContext!, _logger, operation, entityType, entityId, detail);
        private void LogAdminAuditBulk(string operation, string entityType, int successCount, int failureCount) => AdminAudit.LogBulk(_httpContextAccessor.HttpContext!, _logger, operation, entityType, successCount, failureCount);
    }

    /// <summary>
    /// Request model for validating pricing rules
    /// </summary>
    public class ValidatePricingRulesRequest
    {
        /// <summary>
        /// The pricing configuration JSON to validate
        /// </summary>
        public Dictionary<string, JsonElement> PricingConfiguration { get; set; } = new();

        /// <summary>
        /// Optional parameter schema JSON for standalone validation. The model-cost-scoped
        /// endpoint ignores this value and uses persisted associated-model schemas.
        /// </summary>
        public Dictionary<string, JsonElement>? ParameterSchema { get; set; }
    }
}
