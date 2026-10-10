using ConduitLLM.Core.OpenApi;
using ConduitLLM.Admin.Auditing;
using ConduitLLM.Admin.DTOs;
using ConduitLLM.Admin.Extensions;
using ConduitLLM.Admin.Models.ModelAuthors;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Extensions;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Extensions;

namespace ConduitLLM.Admin.Endpoints
{
    /// <summary>
    /// Minimal-API endpoints for managing <c>ModelAuthor</c> entities — the Tier 3 pilot (#906),
    /// for model-author operations.
    /// </summary>
    /// <remarks>
    /// Behavior is identical to the controller: thrown exceptions propagate to the global
    /// <c>AdminExceptionMiddleware</c> (same standardized responses); success logging via
    /// <see cref="OperationLoggingEndpointFilter"/>; audit logging via <see cref="AdminAudit"/>.
    /// </remarks>
    public static class ModelAuthorEndpoints
    {
        /// <summary>Maps the ModelAuthor endpoint group.</summary>
        public static IEndpointRouteBuilder MapModelAuthorEndpoints(this IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("/v1/admin/model-authors")
                .RequireAuthorization("MasterKeyPolicy")
                .AddEndpointFilter<OperationLoggingEndpointFilter>()
                .WithTags("ModelAuthor");

            group.MapGet("/", GetAll)
                .WithName("ModelAuthors_List")
                .WithSummary("List model authors")
                .Produces<IEnumerable<ModelAuthorDto>>(StatusCodes.Status200OK);
            group.MapGet("/{id:int}", GetById).WithName("ModelAuthors_GetById")
                .WithSummary("Get a model author")
                .WithParameterDescription("id", "Identifier of the ModelAuthor resource.")
                .Produces<ModelAuthorDto>(StatusCodes.Status200OK)
                .Produces<AdminProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json");
            group.MapGet("/{id:int}/series", GetSeriesByAuthor)
                .WithName("ModelAuthors_ListSeries")
                .WithSummary("List model series published by an author")
                .WithParameterDescription("id", "Identifier of the ModelAuthor resource.")
                .Produces<IEnumerable<SimpleModelSeriesDto>>(StatusCodes.Status200OK)
                .Produces<AdminProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json");
            group.MapPost("/", Create)
                .WithName("ModelAuthors_Create")
                .WithSummary("Create a model author")
                .Produces<ModelAuthorDto>(StatusCodes.Status201Created)
                .Produces(StatusCodes.Status400BadRequest);
            group.MapPatch("/{id:int}", Update)
                .AcceptsJsonMergePatch<UpdateModelAuthorDto>()
                .WithName("ModelAuthors_Update")
                .WithSummary("Update a model author")
                .WithParameterDescription("id", "Identifier of the ModelAuthor resource.")
                .Produces<ModelAuthorDto>(StatusCodes.Status200OK)
                .Produces(StatusCodes.Status400BadRequest)
                .Produces(StatusCodes.Status404NotFound);
            group.MapDelete("/{id:int}", Delete)
                .WithName("ModelAuthors_Delete")
                .WithSummary("Delete a model author")
                .WithParameterDescription("id", "Identifier of the ModelAuthor resource.")
                .Produces(StatusCodes.Status204NoContent)
                .Produces(StatusCodes.Status400BadRequest)
                .Produces(StatusCodes.Status404NotFound);

            return app;
        }

        private static async Task<IResult> GetAll(IModelAuthorRepository repository)
        {
            var authors = await RepositoryPaginationExtensions.GetAllViaPaginationAsync(
                repository.GetPaginatedAsync);
            return Results.Ok(authors.Select(a => a.ToDto()));
        }

        private static async Task<IResult> GetById(int id, IModelAuthorRepository repository)
        {
            var author = await repository.GetByIdAsync(id);
            return author is null
                ? AdminResults.NotFoundEntity("Model author", id)
                : Results.Ok(author.ToDto());
        }

        private static async Task<IResult> GetSeriesByAuthor(int id, IModelAuthorRepository repository)
        {
            var series = await repository.GetSeriesByAuthorAsync(id);
            if (series is null)
            {
                return AdminResults.NotFoundEntity("Model author", id);
            }

            var dtos = series.Select(s => new SimpleModelSeriesDto
            {
                Id = s.Id,
                Name = s.Name,
                Description = s.Description,
                TokenizerType = s.TokenizerType
            });
            return Results.Ok(dtos);
        }

        private static async Task<IResult> Create(
            CreateModelAuthorDto dto,
            IModelAuthorRepository repository,
            HttpContext httpContext,
            ILoggerFactory loggerFactory)
        {
            var existing = await repository.GetByNameAsync(dto.Name);
            if (existing != null)
            {
                throw new InvalidOperationException($"A model author with name '{dto.Name}' already exists");
            }

            var author = new ModelAuthor
            {
                Name = dto.Name,
                Description = dto.Description,
                WebsiteUrl = dto.WebsiteUrl
            };

            await repository.CreateAsync(author);
            AdminAudit.Log(httpContext, Logger(loggerFactory), "Created", "ModelAuthor", author.Id,
                $"Name: {LoggingSanitizer.S(author.Name)}");

            return Results.Created($"/v1/admin/model-authors/{author.Id}", author.ToDto());
        }

        private static async Task<IResult> Update(
            int id,
            JsonMergePatch<UpdateModelAuthorDto> patch,
            IModelAuthorRepository repository,
            HttpContext httpContext,
            ILoggerFactory loggerFactory)
        {
            var dto = patch.Value;
            var author = await repository.GetByIdAsync(id);
            if (author == null)
            {
                throw new KeyNotFoundException($"Model author with ID {id} not found");
            }

            // Check for name conflicts if the name is being changed
            if (dto.TryGetPatchedProperty(nameof(dto.Name), author.Name, out string? name))
            {
                if (string.IsNullOrWhiteSpace(name))
                    throw new InvalidOperationException("name cannot be null or empty.");
                var existing = await repository.GetByNameAsync(name);
                if (existing != null && existing.Id != id)
                {
                    throw new InvalidOperationException($"A model author with name '{name}' already exists");
                }
                author.Name = name;
            }

            if (dto.TryGetPatchedProperty(nameof(dto.Description), author.Description, out string? description))
                author.Description = description;
            if (dto.TryGetPatchedProperty(nameof(dto.WebsiteUrl), author.WebsiteUrl, out string? websiteUrl))
                author.WebsiteUrl = websiteUrl;

            await repository.UpdateAsync(author);
            AdminAudit.Log(httpContext, Logger(loggerFactory), "Updated", "ModelAuthor", id,
                $"Name: {LoggingSanitizer.S(author.Name)}");

            return Results.Ok(author.ToDto());
        }

        private static async Task<IResult> Delete(
            int id,
            IModelAuthorRepository repository,
            HttpContext httpContext,
            ILoggerFactory loggerFactory)
        {
            var author = await repository.GetByIdAsync(id);
            if (author == null)
            {
                throw new KeyNotFoundException($"Model author with ID {id} not found");
            }

            var series = await repository.GetSeriesByAuthorAsync(id);
            if (series != null && series.Any())
            {
                throw new InvalidOperationException(
                    $"Cannot delete model author with {series.Count()} associated series. Delete the series first.");
            }

            await repository.DeleteAsync(id);
            AdminAudit.Log(httpContext, Logger(loggerFactory), "Deleted", "ModelAuthor", id,
                $"Name: {LoggingSanitizer.S(author.Name)}");

            return Results.NoContent();
        }

        private static ILogger Logger(ILoggerFactory factory)
            => factory.CreateLogger("ConduitLLM.Admin.Endpoints.ModelAuthor");
    }
}
