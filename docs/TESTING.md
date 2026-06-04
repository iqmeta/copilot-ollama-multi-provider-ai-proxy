# Testing Guide

Comprehensive testing documentation covering unit tests, integration tests, and validation procedures.

## Table of Contents

- [Test Overview](#test-overview)
- [Running Tests](#running-tests)
- [Test Suites](#test-suites)
  - [Endpoint Tests](#endpoint-tests)
  - [Parameter Validation Tests](#parameter-validation-tests)
  - [Unit Test Files](#unit-test-files)
  - [Model Selection Tests](#model-selection-tests)
  - [Request Transformer Tests](#request-transformer-tests)
- [Test Architecture](#test-architecture)
- [Adding New Tests](#adding-new-tests)
- [Performance Testing](#performance-testing)
- [Continuous Integration](#continuous-integration)

---

## Test Overview

The proxy includes a comprehensive test suite covering:

### Test Statistics

- **Total Tests:** 182
- **Status:** ✅ All passing (182/182)
- **Coverage Areas:**
  - ✅ Endpoint routing (OpenAI & Ollama formats)
  - ✅ Parameter validation and filtering
  - ✅ Model selection and defaults
  - ✅ Request transformation logic
  - ✅ Streaming and non-streaming responses
  - ✅ Error handling and fallback logic
  - ✅ Reasoning content caching
  - ✅ JSON serialization defaults
  - ✅ Provider registry and model resolution
  - ✅ Proxy authentication middleware
  - ✅ Ollama response building
  - ✅ Provider HTTP client factory
  - ✅ Model selection store
  - ✅ Reasoning cache service

### Test Technologies

- **Framework:** xUnit
- **Test Mode:** In-process with stub provider server
- **Isolation:** Each test uses a fresh proxy instance
- **Stub Provider:** Mock OpenAI-compatible endpoint for isolation (no external API calls)

---

## Running Tests

### Via Visual Studio

1. **Open Test Explorer:** `Test > Test Explorer` (Ctrl+E, T)
2. **Run All Tests:** Click the "Run All Tests" button
3. **Run Specific Test:** Right-click test name > `Run`
4. **Run Suite:** Right-click the class name > `Run`

### Via Command Line

```bash
# Run all tests
dotnet test

# Run with verbose output
dotnet test --verbosity quiet

# Run specific test suite
dotnet test --filter "FullyQualifiedName~ParameterValidationTests"
dotnet test --filter "FullyQualifiedName~ReasoningCacheServiceTests"

# Run with coverage report
dotnet test /p:CollectCoverage=true
```

### Via PowerShell

```powershell
cd C:\Users\TT\source\repos\vs2026-copilot-deepseek-v4

# Run all tests with real-time output
dotnet test --logger "console;verbosity=detailed"

# Run and capture to file
dotnet test > logs/test-results.txt 2>&1
```

---

## Test Suites

### Endpoint Tests

**File:** `tests/ProxyTests/EndpointTests.cs`

Tests the proxy's HTTP endpoints against stub provider responses. Uses `WebApplicationFactory` with an in-process provider stub to avoid real API calls.

#### Test Classes

**1. OpenAI-Compatible Endpoints**
- `GET /v1/models` — List available models
- `POST /v1/chat/completions` — Chat completion (non-streaming)
- `POST /v1/chat/completions?stream=true` — Chat completion (streaming)

**2. Ollama-Compatible Endpoints**
- `GET /api/version` — Proxy version
- `GET /api/tags` — List models in Ollama format
- `GET /api/show?model=...` — Model details
- `POST /api/show` — Model details via POST
- `POST /api/chat` — Chat completion (Ollama format)

**3. Health Endpoint**
- `GET /health` — Proxy health status

#### Test Patterns

Each endpoint test:
1. Sends a request to the proxy
2. Internally routes to the stub provider
3. Validates response format and content
4. Checks HTTP status codes

**Example Test:**
```csharp
[Fact]
public async Task GetModels_ReturnsOpenAiFormat()
{
    var response = await Client.GetAsync("/v1/models");

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    var json = await response.Content.ReadAsAsync<JsonElement>();
    Assert.Equal("list", json.GetProperty("object").GetString());
}
```

#### Key Validations

- ✅ Response format matches OpenAI/Ollama spec
- ✅ Status codes are correct (200, 400, 502, 503)
- ✅ JSON structure is valid
- ✅ Streaming responses contain proper delimiters

---

### Parameter Validation Tests

**File:** `tests/ProxyTests/ParameterValidationTests.cs`

Validates that `RequestTransformer.ApplyExecutionDefaults()` correctly injects default parameters for every model/provider combination defined in `config/model-selection/*.json`. Only enabled models are tested against.

#### Test Coverage

**DeepSeek Models:**
- ✅ `deepseek-v4-pro` — reasoning_effort injected, top_p omitted
- ✅ `deepseek-v4-flash` — reasoning_effort injected, top_p omitted
- ✅ `deepseek-coder-6.7b-instruct` — disabled in config, not tested

**NVIDIA NIM Models:**
- ✅ Reasoning parameters stripped (not supported)
- ✅ temperature, top_p preserved
- ✅ Default max_tokens applied

**OpenAI Models:**
- ✅ All temperature/top_p parameters preserved
- ✅ top_k filtered (not OpenAI-compatible)
- ✅ reasoning_effort preserved for o-series only

**Groq Models:**
- ✅ reasoning_effort, tools stripped (not supported)
- ✅ temperature, top_p preserved

**OpenRouter Models:**
- ✅ Full parameter pass-through (compatible with any backend)

#### Expected Behavior

| Model | reasoning_effort | temperature | top_p | top_k | Result |
|-------|------------------|-------------|-------|-------|--------|
| deepseek-v4-pro | ✅ injected | ✅ injected | ❌ omitted | ❌ omitted | Valid |
| gpt-5 | ❌ injected | ✅ injected | ✅ injected | ❌ filtered | Valid |
| llama-3.3-70b | ❌ filtered | ✅ injected | ✅ injected | ✅ injected | Valid |
| deepseek-v4-flash | ❌ filtered | ✅ injected | ✅ injected | ✅ injected | Valid |

#### Test Execution

```bash
dotnet test --filter ClassName=ParameterValidationTests --verbosity detailed
```

#### Example Assertions

```csharp
[Fact]
public void DeepSeek_ReasoningModels_OmitTopP()
{
    var transformer = CreateTransformer();
    var result = transformer.ApplyExecutionDefaults(
        body: "...",
        model: "deepseek-v4-pro",
        provider: "deepseek"
    );

    var json = JsonDocument.Parse(result).RootElement;
    Assert.True(json.TryGetProperty("reasoning_effort", out _));
    Assert.False(json.TryGetProperty("top_p", out _));  
}
```

---

### Unit Test Files

The following new test files have been added to significantly increase test coverage:

**1. ReasoningCacheServiceTests** (`tests/ProxyTests/ReasoningCacheServiceTests.cs`)
- 17 tests for reasoning content caching logic
- Covers get/set operations, sequential keys, message/delta reasoning, tool calls, invalid JSON, and sequential call scenarios

**2. JsonDefaultsTests** (`tests/ProxyTests/JsonDefaultsTests.cs`)
- 9 tests for JSON serialization defaults
- Covers snake_case naming, null handling, deserialization, round-trips, and nested objects

**3. ProviderHttpClientFactoryTests** (`tests/ProxyTests/ProviderHttpClientFactoryTests.cs`)
- 8 tests for HTTP client creation
- Covers base address, auth headers, accept headers, OpenRouter headers, fallback env vars, timeouts

**4. ProxyAuthenticationMiddlewareTests** (`tests/ProxyTests/ProxyAuthenticationMiddlewareTests.cs`)
- 8 tests for authentication middleware
- Covers null/empty key skipping, valid/invalid bearer tokens, missing auth, case-insensitive bearer, JSON content types

**5. ProviderRegistryTests** (`tests/ProxyTests/ProviderRegistryTests.cs`)
- 11 tests for provider registry
- Covers provider/model resolution, default models, mapping updates, candidate resolution

**6. OllamaResponseBuilderTests** (`tests/ProxyTests/OllamaResponseBuilderTests.cs`)
- 15 tests for Ollama show response building
- Covers model info, context length, capabilities, recommended parameters, model details

**7. ModelSelectionStoreTests** (`tests/ProxyTests/ModelSelectionStoreTests.cs`)
- 16 tests for model selection functionality
- Covers execution config retrieval, provider selections, model entries, priorities

---

### Model Selection Tests

**File:** `tests/ProxyTests/ModelSelectionTests.cs` (deprecated, replaced by `ModelSelectionStoreTests.cs`)

Validates that `ProviderRegistry` and `ModelCatalogService` correctly resolve model-to-provider candidates and apply defaults.

#### Test Scenarios

**1. Direct Model Mapping**
- Request for `deepseek-v4-pro` resolves to `deepseek` provider
- Request for `gpt-5` resolves to `openai` provider
- Request for `llama-3.3-70b` resolves to `nvidia` provider

**2. Fallback Resolution**
- Unknown model → Try NVIDIA NIM
- Still no match → Use default provider
- If default unavailable → Return error

**3. Provider Candidate Priority**
- Primary provider is tried first
- Secondary provider (NVIDIA fallback) is tried on failure
- Default provider is last resort

**4. Model Availability**
- Only enabled models are returned by `/v1/models`
- Disabled models are filtered out
- Context windows are correctly populated

#### Example Tests

```csharp
[Theory]
[InlineData("deepseek-v4-pro", "deepseek")]
[InlineData("gpt-5", "openai")]
[InlineData("llama-3.3-70b", "nvidia")]
public void ResolveCandidates_KnownModels(string model, string expectedProvider)
{
    var registry = new ProviderRegistry(...);
    var candidates = registry.ResolveCandidates(model);

    Assert.Contains(expectedProvider, candidates);
}

[Fact]
public void GetModels_OnlyReturnsEnabled()
{
    var catalog = new ModelCatalogService(...);
    var models = catalog.GetAllModels();

    // All returned models should have enabled=true in config
    Assert.All(models, m => Assert.True(m.Enabled));
}
```

---

### Request Transformer Tests

**File:** `tests/ProxyTests/RequestTransformerTests.cs`

Tests the `RequestTransformer` class which:
- Parses incoming requests
- Filters parameters based on provider capabilities
- Injects default values
- Validates parameter ranges

#### Core Functionality Tests

**1. Parameter Filtering**
- Remove unsupported parameters per provider
- Preserve supported parameters
- Convert parameter formats if needed

**2. Default Injection**
- Inject `temperature`, `max_tokens`, etc. if missing
- Apply model-specific defaults
- Override user values with mandatory constraints

**3. Streaming Format Conversion**
- OpenAI SSE → Ollama NDJSON (on demand)
- Ollama NDJSON → OpenAI SSE (on demand)

**4. Error Handling**
- Invalid JSON → Clear error message
- Unsupported provider → Meaningful exception
- Missing model → Fallback to default

#### Example Test

```csharp
[Fact]
public void TransformRequest_RemovesUnsupportedParameters()
{
    var transformer = new RequestTransformer(...);

    var input = @"{
        ""model"": ""gpt-5"",
        ""messages"": [...],
        ""top_k"": 100,
        ""reasoning_effort"": ""high""
    }";

    var output = transformer.TransformRequest("openai", input);
    var json = JsonDocument.Parse(output).RootElement;

    // top_k and reasoning_effort are not supported by OpenAI
    Assert.False(json.TryGetProperty("top_k", out _));
    Assert.False(json.TryGetProperty("reasoning_effort", out _));
}
```

---

## Test Architecture

### Fixture-Based Isolation

Each test class uses `ProxyFixture` which:

1. **Spawns an in-process stub provider** listening on localhost
   - Simulates OpenAI API endpoints
   - Returns fake but valid responses
   - No external network calls

2. **Points environment variables at the stub**
   - `PROVIDER_DEEPSEEK_BASE_URL` → local stub
   - Other providers → real URLs (or overridden in config)

3. **Creates a WebApplicationFactory for the proxy**
   - Fresh DI container per test
   - Real ASP.NET Core middleware pipeline
   - Full end-to-end testing

### Test Isolation Pattern

```csharp
public class MyTests : IClassFixture<ProxyFixture>
{
    private readonly ProxyFixture _fixture;

    public MyTests(ProxyFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task MyTest()
    {
        var response = await _fixture.Client.GetAsync("/v1/models");
        // Test response...
    }
}

// Each test gets its own fixture instance (class-scoped)
// Fixture is torn down after all tests in class complete
```

### Cleanup

The `ProxyFixture` implements `IDisposable`:

```csharp
public void Dispose()
{
    _factory?.Dispose();  // Stop proxy
    _stub?.Dispose();      // Stop stub provider
}
```

---

## Adding New Tests

### Step 1: Determine Test Type

| Scenario | Test Type | File |
|----------|-----------|------|
| Testing HTTP endpoint behavior | Endpoint Test | `EndpointTests.cs` |
| Testing parameter defaults | Parameter Validation | `ParameterValidationTests.cs` |
| Testing model resolution/selection | Model Selection | `ModelSelectionStoreTests.cs` |
| Testing internal transformation logic | Request Transformer | `RequestTransformerTests.cs` |
| Testing authentication middleware | Unit Test | `ProxyAuthenticationMiddlewareTests.cs` |
| Testing reasoning cache | Unit Test | `ReasoningCacheServiceTests.cs` |
| Testing HTTP client factory | Unit Test | `ProviderHttpClientFactoryTests.cs` |
| Testing Ollama responses | Unit Test | `OllamaResponseBuilderTests.cs` |
| Testing JSON serialization | Unit Test | `JsonDefaultsTests.cs` |
| Testing provider registry | Unit Test | `ProviderRegistryTests.cs` |

### Step 2: Add Test Method

```csharp
[Fact]  // For single scenario
// or [Theory] for parameterized tests
public async Task DescriptiveTestName()
{
    // Arrange: Set up fixture and test data
    var request = new { model = "deepseek-v4-pro", messages = ... };

    // Act: Execute the operation
    var response = await Client.PostAsJsonAsync("/v1/chat/completions", request);

    // Assert: Verify expected outcome
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    var body = await response.Content.ReadAsAsync<JsonElement>();
    Assert.NotNull(body);
}
```

### Step 3: Use Parameterized Tests

```csharp
[Theory]
[InlineData("deepseek-v4-pro", "deepseek")]
[InlineData("gpt-5", "openai")]
[InlineData("llama-3.3-70b", "nvidia")]
public void ResolvesCorrectProvider(string model, string expectedProvider)
{
    // Single test runs 3 times with different parameters
    var registry = new ProviderRegistry(...);
    var candidate = registry.ResolveCandidates(model).First();
    Assert.Equal(expectedProvider, candidate);
}
```

### Step 4: Run and Verify

```bash
dotnet test --filter MyTest
```

---

## Performance Testing

### Benchmarking Endpoints

To measure proxy latency, use `BenchmarkDotNet`:

```bash
dotnet add package BenchmarkDotNet --version 0.13.2
```

Create `ProxyBenchmarks.cs`:

```csharp
using BenchmarkDotNet.Attributes;

[MemoryDiagnoser]
public class ProxyBenchmarks
{
    private HttpClient _client;

    [GlobalSetup]
    public void Setup()
    {
        var factory = new WebApplicationFactory<Program>();
        _client = factory.CreateClient();
    }

    [Benchmark]
    public async Task GetModels()
    {
        var response = await _client.GetAsync("/v1/models");
        _ = await response.Content.ReadAsAsync<JsonElement>();
    }

    [Benchmark]
    public async Task ChatCompletion_NonStreaming()
    {
        var request = new { 
            model = "deepseek-v4-pro",
            messages = new[] { new { role = "user", content = "hi" } }
        };
        var response = await _client.PostAsJsonAsync("/v1/chat/completions", request);
        _ = await response.Content.ReadAsAsync<JsonElement>();
    }
}
```

Run with:
```bash
dotnet run -c Release --runtimes net10.0
```

### Load Testing

Use `NBomber` for concurrent request testing:

```csharp
using NBomber.CSharp;
using NBomber.Http.CSharp;

var httpClient = new HttpClient();
var scenario = Scenario.Create("load_test", async context =>
{
    var request = Http.CreateRequest("GET", "http://localhost:11434/v1/models");
    return await Http.Send(httpClient, request);
})
.WithoutWarmUp()
.WithLoadSimulations(
    Simulation.KeepConstant(copies: 100, during: TimeSpan.FromSeconds(30))
);

NBomberRunner.RegisterScenarios(scenario)
    .Run()
```

---

## Continuous Integration

### GitHub Actions Workflow

Create `.github/workflows/test.yml`:

```yaml
name: Test Suite

on:
  push:
    branches: [main, develop, 'feature/**']
  pull_request:
    branches: [main, develop]

jobs:
  test:
    runs-on: ubuntu-latest
    strategy:
      matrix:
        dotnet-version: ['10.0']

    steps:
      - uses: actions/checkout@v4

      - name: Setup .NET 10
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0'

      - name: Restore dependencies
        run: dotnet restore

      - name: Build
        run: dotnet build --configuration Release --no-restore

      - name: Run Tests
        run: dotnet test --configuration Release --no-build --verbosity normal --logger "trx"

      - name: Upload Test Results
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: test-results-${{ matrix.dotnet-version }}
          path: '**/TestResults/**'

      - name: Publish Test Report
        uses: dorny/test-reporter@v1
        if: always()
        with:
          name: Test Results (${{ matrix.dotnet-version }})
          path: '**/TestResults/*.trx'
          reporter: 'dotnet trx'
```

### Pre-commit Hook

Install git hook to run tests before commit:

```bash
#!/bin/bash
# .git/hooks/pre-commit

echo "Running tests..."
dotnet test --configuration Debug

if [ $? -ne 0 ]; then
    echo "Tests failed. Commit aborted."
    exit 1
fi
```

Make executable:
```bash
chmod +x .git/hooks/pre-commit
```

---

## Test Data and Fixtures

### Mock Provider Responses

The stub provider in `ProxyFixture` serves:

```json
// GET /v1/models
{
  "object": "list",
  "data": [
    {
      "id": "test-model",
      "object": "model",
      "created": 1700000000,
      "owned_by": "test"
    }
  ]
}

// POST /v1/chat/completions
{
  "id": "test-id",
  "object": "chat.completion",
  "created": 1700000000,
  "model": "test-model",
  "choices": [{
    "index": 0,
    "message": {
      "role": "assistant",
      "content": "hi from stub"
    },
    "finish_reason": "stop"
  }],
  "usage": {
    "prompt_tokens": 1,
    "completion_tokens": 1,
    "total_tokens": 2
  }
}

// Streaming response (SSE)
data: {"id":"t","object":"chat.completion.chunk",...,"delta":{"content":"Hi"}}
data: [DONE]
```

---

## Troubleshooting

### Test Timeout

If tests hang or timeout:

```bash
# Run with extended timeout
dotnet test --logger "console;verbosity=detailed" --timeout 30000
```

Check for:
- Blocking HTTP calls
- Infinite loops in parameter validation
- Fixture cleanup issues

### Test Flakiness

If tests pass/fail intermittently:

```bash
# Run test multiple times
for i in {1..5}; do dotnet test --filter MyTest || break; done
```

Common causes:
- Port collision (ProxyFixture uses port 0 to avoid this)
- Timing issues in streaming  
- Race conditions in multi-threaded code

### Memory Leaks

Monitor test memory:

```bash
dotnet test --logger "console" --collect:"XPlat Code Coverage"
```

---

## Related Documentation

- [API.md](API.md) — Endpoint reference
- [CONFIGURATION.md](CONFIGURATION.md) — Test configuration options
- [ARCHITECTURE.md](ARCHITECTURE.md) — Internal component structure