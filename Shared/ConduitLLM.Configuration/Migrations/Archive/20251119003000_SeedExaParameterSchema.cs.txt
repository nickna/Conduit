using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConduitLLM.Configuration.Migrations
{
    /// <inheritdoc />
    public partial class SeedExaParameterSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Comprehensive Exa parameter schema based on ExaSearchRequest model
            var exaParameterSchema = @"{
  ""required"": [""query""],
  ""optional"": {
    ""type"": {
      ""type"": ""select"",
      ""description"": ""Search type: neural (semantic), keyword (exact), auto (best choice), or fast (quick results)"",
      ""options"": [""neural"", ""keyword"", ""auto"", ""fast""],
      ""default"": ""auto""
    },
    ""numResults"": {
      ""type"": ""number"",
      ""description"": ""Number of results to return"",
      ""min"": 1,
      ""max"": 100,
      ""default"": 10
    },
    ""category"": {
      ""type"": ""select"",
      ""description"": ""Filter by content category"",
      ""options"": [""news"", ""research paper"", ""github"", ""pdf"", ""tweet"", ""company"", ""linkedin profile""],
      ""optional"": true
    },
    ""includeDomains"": {
      ""type"": ""array"",
      ""description"": ""Include only results from these domains (e.g., ['arxiv.org', 'github.com'])"",
      ""itemType"": ""string"",
      ""maxItems"": 1200,
      ""optional"": true
    },
    ""excludeDomains"": {
      ""type"": ""array"",
      ""description"": ""Exclude results from these domains"",
      ""itemType"": ""string"",
      ""maxItems"": 1200,
      ""optional"": true
    },
    ""startCrawlDate"": {
      ""type"": ""string"",
      ""description"": ""Filter by crawl date start (ISO 8601 format: YYYY-MM-DD)"",
      ""pattern"": ""^\\d{4}-\\d{2}-\\d{2}$"",
      ""optional"": true
    },
    ""endCrawlDate"": {
      ""type"": ""string"",
      ""description"": ""Filter by crawl date end (ISO 8601 format: YYYY-MM-DD)"",
      ""pattern"": ""^\\d{4}-\\d{2}-\\d{2}$"",
      ""optional"": true
    },
    ""startPublishedDate"": {
      ""type"": ""string"",
      ""description"": ""Filter by published date start (ISO 8601 format: YYYY-MM-DD)"",
      ""pattern"": ""^\\d{4}-\\d{2}-\\d{2}$"",
      ""optional"": true
    },
    ""endPublishedDate"": {
      ""type"": ""string"",
      ""description"": ""Filter by published date end (ISO 8601 format: YYYY-MM-DD)"",
      ""pattern"": ""^\\d{4}-\\d{2}-\\d{2}$"",
      ""optional"": true
    },
    ""text"": {
      ""type"": ""object"",
      ""description"": ""Enable text content extraction. Use true for default or object with maxCharacters"",
      ""properties"": {
        ""maxCharacters"": {
          ""type"": ""number"",
          ""description"": ""Maximum characters of text to extract per result"",
          ""min"": 1,
          ""max"": 100000
        }
      },
      ""optional"": true
    },
    ""highlights"": {
      ""type"": ""object"",
      ""description"": ""Enable highlights extraction. Use true for default or object with query, numSentences, highlightsPerUrl"",
      ""properties"": {
        ""query"": {
          ""type"": ""string"",
          ""description"": ""Query for generating highlights""
        },
        ""numSentences"": {
          ""type"": ""number"",
          ""description"": ""Number of sentences per highlight"",
          ""default"": 3
        },
        ""highlightsPerUrl"": {
          ""type"": ""number"",
          ""description"": ""Number of highlights per URL"",
          ""default"": 1
        }
      },
      ""optional"": true
    },
    ""summary"": {
      ""type"": ""object"",
      ""description"": ""Enable summary generation. Use true for default or object with query"",
      ""properties"": {
        ""query"": {
          ""type"": ""string"",
          ""description"": ""Query for generating summary""
        }
      },
      ""optional"": true
    },
    ""livecrawl"": {
      ""type"": ""select"",
      ""description"": ""Livecrawl mode for fetching fresh content"",
      ""options"": [""never"", ""fallback"", ""always"", ""preferred""],
      ""default"": ""fallback"",
      ""optional"": true
    },
    ""userLocation"": {
      ""type"": ""string"",
      ""description"": ""User location for geographic filtering (ISO country code, e.g., 'US', 'GB', 'CA')"",
      ""pattern"": ""^[A-Z]{2}$"",
      ""optional"": true
    }
  },
  ""examples"": [
    {
      ""name"": ""Simple search"",
      ""description"": ""Basic neural search for AI research papers"",
      ""request"": {
        ""query"": ""latest AI research papers"",
        ""numResults"": 5
      }
    },
    {
      ""name"": ""News search with date filter"",
      ""description"": ""Search recent news articles about climate change"",
      ""request"": {
        ""query"": ""climate change solutions"",
        ""category"": ""news"",
        ""numResults"": 10,
        ""startPublishedDate"": ""2024-01-01""
      }
    },
    {
      ""name"": ""Research with text extraction"",
      ""description"": ""Search research papers with full text extraction"",
      ""request"": {
        ""query"": ""transformer architecture machine learning"",
        ""category"": ""research paper"",
        ""numResults"": 5,
        ""text"": {
          ""maxCharacters"": 5000
        }
      }
    },
    {
      ""name"": ""Domain-specific search"",
      ""description"": ""Search only on arXiv and GitHub"",
      ""request"": {
        ""query"": ""neural network optimization"",
        ""includeDomains"": [""arxiv.org"", ""github.com""],
        ""numResults"": 10
      }
    }
  ]
}";

            // Update all Exa function configurations with the parameter schema
            migrationBuilder.Sql($@"
                UPDATE ""FunctionConfigurations""
                SET ""ParameterSchema"" = '{exaParameterSchema.Replace("'", "''")}'::jsonb
                WHERE ""ProviderType"" = 1;  -- FunctionProviderType.Exa = 1
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Remove parameter schema from Exa configurations
            migrationBuilder.Sql(@"
                UPDATE ""FunctionConfigurations""
                SET ""ParameterSchema"" = NULL
                WHERE ""ProviderType"" = 1;  -- FunctionProviderType.Exa = 1
            ");
        }
    }
}
