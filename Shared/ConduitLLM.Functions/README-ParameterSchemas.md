# Function Parameter Schemas

## Overview

Function parameter schemas provide a standardized way to define, document, and validate parameters for function executions. This enables:

- **Dynamic UI Generation**: WebAdmin and other clients can automatically generate appropriate form inputs
- **Client-Side Validation**: Catch errors before execution
- **Server-Side Validation**: Enforce parameter requirements at the API level
- **Self-Documentation**: Parameters are documented within the schema itself

## Schema Format

Parameter schemas are stored as JSONB in the `ParameterSchema` column of the `FunctionConfigurations` table.

### Basic Structure

```json
{
  "required": ["param1", "param2"],
  "optional": {
    "optionalParam1": {
      "type": "string",
      "description": "Description of the parameter",
      "default": "default value"
    },
    "optionalParam2": {
      "type": "number",
      "min": 1,
      "max": 100,
      "default": 10
    }
  },
  "examples": [
    {
      "name": "Example 1",
      "description": "Description of this example",
      "request": {
        "param1": "value1",
        "param2": "value2"
      }
    }
  ]
}
```

### Parameter Types

#### String
```json
{
  "type": "string",
  "description": "A text parameter",
  "pattern": "^[A-Z]{2}$",  // Optional regex pattern
  "default": "default value"
}
```

#### Number
```json
{
  "type": "number",
  "description": "A numeric parameter",
  "min": 1,
  "max": 100,
  "default": 10
}
```

#### Boolean
```json
{
  "type": "boolean",
  "description": "A true/false parameter",
  "default": true
}
```

#### Select (Enum)
```json
{
  "type": "select",
  "description": "Choose from predefined options",
  "options": ["option1", "option2", "option3"],
  "default": "option1"
}
```

#### Array

Array validation accepts CLR arrays and `IList` collections (including `List<T>`,
`ArrayList`, and `ReadOnlyCollection<T>`), `JsonElement` arrays, and `JsonArray`
nodes. Dictionaries and other object representations are rejected. Materialize
other `IEnumerable` values, such as sets or generators, as a list or array before
validation.

```json
{
  "type": "array",
  "description": "A list of items",
  "itemType": "string",
  "maxItems": 1200,
  "optional": true
}
```

#### Object
```json
{
  "type": "object",
  "description": "A complex nested object",
  "properties": {
    "nestedParam1": {
      "type": "string"
    },
    "nestedParam2": {
      "type": "number"
    }
  },
  "optional": true
}
```

## Exa Provider Schema Example

The Exa search provider has a comprehensive parameter schema:

```json
{
  "required": ["query"],
  "optional": {
    "type": {
      "type": "select",
      "description": "Search type",
      "options": ["neural", "keyword", "auto", "fast"],
      "default": "auto"
    },
    "numResults": {
      "type": "number",
      "description": "Number of results to return",
      "min": 1,
      "max": 100,
      "default": 10
    },
    "category": {
      "type": "select",
      "description": "Filter by content category",
      "options": ["news", "research paper", "github", "pdf"],
      "optional": true
    },
    "includeDomains": {
      "type": "array",
      "description": "Include only results from these domains",
      "itemType": "string",
      "maxItems": 1200,
      "optional": true
    },
    "text": {
      "type": "object",
      "description": "Enable text content extraction",
      "properties": {
        "maxCharacters": {
          "type": "number",
          "description": "Maximum characters of text to extract"
        }
      },
      "optional": true
    }
  },
  "examples": [
    {
      "name": "Simple search",
      "description": "Basic neural search",
      "request": {
        "query": "AI research papers",
        "numResults": 5
      }
    },
    {
      "name": "Domain-specific search",
      "description": "Search only on arXiv",
      "request": {
        "query": "transformer architecture",
        "includeDomains": ["arxiv.org"],
        "numResults": 10
      }
    }
  ]
}
```

## API Endpoints

### Get Function Parameter Schema

**Endpoint**: `GET /v1/conduit/discovery/functions/{functionConfigurationId}/parameters`

**Authentication**: Virtual Key (Bearer token)

**Response**:
```json
{
  "functionConfigurationId": 1,
  "configurationName": "Production Exa Search",
  "providerType": "Exa",
  "purpose": "Search",
  "parameterSchema": {
    "required": ["query"],
    "optional": { ... }
  },
  "exampleRequest": {
    "query": "example search",
    "numResults": 5
  }
}
```

### List Available Functions

**Endpoint**: `GET /v1/conduit/discovery/functions?purpose=Search&providerType=Exa`

**Authentication**: Virtual Key (Bearer token)

**Response**:
```json
{
  "functions": [
    {
      "id": 1,
      "configurationName": "Production Exa Search",
      "providerType": "Exa",
      "purpose": "Search",
      "description": "Production Exa search configuration",
      "defaultExecutionMode": "Synchronous",
      "isEnabled": true,
      "timeoutSeconds": 30
    }
  ],
  "count": 1
}
```

## Validation

### Server-Side Validation

The `FunctionParameterValidationService` validates parameters against the schema before execution:

1. **Required Parameters**: Ensures all required parameters are present
2. **Type Checking**: Validates parameter types (string, number, boolean, etc.)
3. **Range Validation**: Checks min/max constraints for numbers
4. **Option Validation**: Ensures select parameters match allowed options
String `pattern` metadata is available to clients and providers; this basic server
validator does not enforce regex patterns.

CLR and JSON-bound primitives follow the same rules. Strings and booleans retain
their types, select values must be strings in the configured options, and numeric
min/max bounds are inclusive and compare decimal digits without rounding through
double. Numbers must fit the finite double range; overflow and underflow are rejected.
Validation does not normalize or replace the submitted values.
Null and undefined values fail a declared type check. Required entries check for
presence; add a type definition to reject a present null value.

Missing schemas skip validation. A malformed schema produces a warning and leaves
validation to the provider. A malformed field definition produces a warning for that
field while retaining other field errors and missing-required errors.

### Client-Side Validation

The Core SDK and WebAdmin can use the schema for client-side validation:

```typescript
// Fetch parameter schema
const schema = await client.discovery.getFunctionParameters(configId);

// Use schema.parameterSchema to validate before calling execute
// Example: Check required parameters
if (schema.parameterSchema?.required) {
  const missing = schema.parameterSchema.required.filter(
    param => !(param in parameters)
  );
  if (missing.length > 0) {
    throw new Error(`Missing required parameters: ${missing.join(', ')}`);
  }
}
```

## Adding Schemas to New Providers

### Step 1: Define the Schema

Create a JSON schema based on the provider's API documentation:

```json
{
  "required": ["mandatory_param"],
  "optional": {
    "optional_param": {
      "type": "string",
      "description": "Description",
      "default": "default"
    }
  },
  "examples": [
    {
      "name": "Basic example",
      "request": {
        "mandatory_param": "value"
      }
    }
  ]
}
```

### Step 2: Create a Migration

```csharp
public partial class SeedPerplexityParameterSchema : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var schema = @"{...}";  // Your schema JSON

        migrationBuilder.Sql($@"
            UPDATE ""FunctionConfigurations""
            SET ""ParameterSchema"" = '{schema.Replace("'", "''")}'::jsonb
            WHERE ""ProviderType"" = 2;  -- Perplexity
        ");
    }
}
```

### Step 3: Test the Schema

1. Run the migration
2. Call `GET /v1/conduit/discovery/functions/{id}/parameters`
3. Verify the schema is returned correctly
4. Test validation by calling execute with invalid parameters

## Best Practices

### 1. Complete Documentation

Always include descriptions for each parameter:

```json
{
  "paramName": {
    "type": "number",
    "description": "Clear, concise description of what this parameter does",
    "min": 1,
    "max": 100
  }
}
```

### 2. Provide Examples

Include multiple examples showing different use cases:

```json
{
  "examples": [
    {
      "name": "Simple use case",
      "description": "Most common usage pattern",
      "request": { ... }
    },
    {
      "name": "Advanced use case",
      "description": "With optional parameters",
      "request": { ... }
    }
  ]
}
```

### 3. Set Sensible Defaults

Provide default values that work for most use cases:

```json
{
  "numResults": {
    "type": "number",
    "default": 10,  // Most users want 10 results
    "min": 1,
    "max": 100
  }
}
```

### 4. Use Appropriate Types

Choose the most specific type:
- Use `select` for predefined options (not `string`)
- Use `number` with min/max instead of unbounded numbers
- Use `pattern` for strings with specific formats (dates, country codes, etc.)

### 5. Mark Optional Parameters

Explicitly mark truly optional parameters:

```json
{
  "optionalParam": {
    "type": "string",
    "optional": true,  // Explicitly marked
    "description": "This parameter is optional"
  }
}
```

## WebAdmin Integration

The TestFunctionModal automatically:
1. Fetches the parameter schema when opened
2. Populates the parameters field with the first example if available
3. Falls back to an empty object if no examples exist
4. Validates parameters client-side before execution

Future enhancements can use the schema to:
- Generate typed form inputs (number inputs, selects, date pickers)
- Show parameter descriptions as tooltips
- Pre-populate with default values
- Provide autocomplete for select parameters

## SDK Usage

### Core SDK (TypeScript/JavaScript)

```typescript
import { FetchConduitCoreClient } from '@knn_labs/conduit-core-sdk';

const client = new FetchConduitCoreClient({
  apiKey: 'your-api-key',
  baseURL: 'https://api.conduit.ai'
});

// List available functions
const functions = await client.discovery.getFunctions('Search', 'Exa');

// Get parameter schema
const schema = await client.discovery.getFunctionParameters(1);

// Use schema to build request
const request = {
  query: 'AI research',
  ...(schema.parameterSchema?.optional?.numResults?.default && {
    numResults: schema.parameterSchema.optional.numResults.default
  })
};

// Execute function
const result = await client.functions.execute({
  functionConfigurationId: 1,
  parameters: request
});
```

## Troubleshooting

### Schema Not Returned

**Problem**: `GET /v1/conduit/discovery/functions/{id}/parameters` returns empty schema

**Solutions**:
1. Check if `ParameterSchema` column has data: `SELECT "ParameterSchema" FROM "FunctionConfigurations" WHERE "Id" = 1`
2. Verify migration ran successfully
3. Check logs for JSON parsing errors

### Validation Always Fails

**Problem**: Valid parameters are being rejected

**Solutions**:
1. Check schema required/optional fields
2. Verify parameter types match schema
3. Check server logs for validation details
4. Test with exact example from schema

### Invalid Schema Format

**Problem**: Schema validation service throws errors

**Solutions**:
1. Validate JSON syntax
2. Ensure proper nesting of objects
3. Check that all quotes are properly escaped in migrations
4. Use online JSON validators

## Future Enhancements

Potential future improvements:

1. **JSON Schema Compliance**: Full JSON Schema Draft 7 support
2. **Dynamic UI Generation**: Auto-generate form inputs from schema
3. **Parameter Dependencies**: Support for conditional parameters
4. **Custom Validators**: Plugin system for complex validation logic
5. **Schema Versioning**: Support multiple schema versions per provider
6. **Auto-Documentation**: Generate OpenAPI/Swagger docs from schemas
