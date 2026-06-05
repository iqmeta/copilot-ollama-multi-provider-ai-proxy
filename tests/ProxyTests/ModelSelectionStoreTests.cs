namespace ProxyTests;

// Share the "Proxy" collection with EndpointTests and ModelCatalogServiceTests
// so ProxyFixture's environment-variable setup runs once before any of these
// tests, and the registry constructor in each test can find a configured
// PROVIDER_DEEPSEEK_API_KEY.
[Collection("Proxy")]
public class ModelSelectionStoreTests
{
    [Fact]
    public void GetExecutionConfigForModel_DeepSeekV4Pro_HasContextLength()
    {
        ModelSelectionStore store = new();
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        ModelExecutionConfig config = store.GetExecutionConfigForModel("deepseek-v4-pro", registry.ModelToProvider);

        Assert.True(config.ContextLength.HasValue);
        Assert.Equal(1_048_576, config.ContextLength.Value);
    }

    [Fact]
    public void GetExecutionConfigForModel_DeepSeekV4Pro_HasMaxOutputTokens()
    {
        ModelSelectionStore store = new();
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        ModelExecutionConfig config = store.GetExecutionConfigForModel("deepseek-v4-pro", registry.ModelToProvider);

        Assert.True(config.MaxOutputTokens.HasValue);
    }

    [Fact]
    public void GetExecutionConfigForModel_UnknownModel_ReturnsDefaults()
    {
        ModelSelectionStore store = new();
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        ModelExecutionConfig config = store.GetExecutionConfigForModel("nonexistent-model-xyz", registry.ModelToProvider);

        Assert.NotNull(config);
    }

    [Fact]
    public void GetProviderModelSelections_DeepSeek_ReturnsEntries()
    {
        ModelSelectionStore store = new();

        ModelSelectionEntry[] entries = store.GetProviderModelSelections("deepseek");

        Assert.NotEmpty(entries);
    }

    [Fact]
    public void GetProviderModelSelections_OpenAI_ReturnsEntries()
    {
        ModelSelectionStore store = new();

        ModelSelectionEntry[] entries = store.GetProviderModelSelections("openai");

        Assert.NotEmpty(entries);
    }

    [Fact]
    public void FindModelSelectionEntry_DeepSeekV4Pro_FindsEntry()
    {
        ModelSelectionStore store = new();

        ModelSelectionEntry? entry = store.FindModelSelectionEntry("deepseek-v4-pro", "deepseek");

        Assert.NotNull(entry);
        Assert.Equal("deepseek-v4-pro", entry.Value.Match);
    }

    [Fact]
    public void FindModelSelectionEntry_NonExistent_ReturnsNull()
    {
        ModelSelectionStore store = new();

        ModelSelectionEntry? entry = store.FindModelSelectionEntry("nonexistent", "deepseek");

        Assert.Null(entry);
    }

    [Fact]
    public void IsPreferredModel_DeepSeekV4Pro_ReturnsTrue()
    {
        ModelSelectionStore store = new();

        bool isPreferred = store.IsPreferredModel("deepseek-v4-pro", "deepseek");

        Assert.True(isPreferred);
    }

    [Fact]
    public void IsPreferredModel_NonExistent_ReturnsFalse()
    {
        ModelSelectionStore store = new();

        bool isPreferred = store.IsPreferredModel("nonexistent-model", "deepseek");

        Assert.False(isPreferred);
    }

    [Fact]
    public void GetPreferredModelPriority_DeepSeekV4Pro_ReturnsPriority()
    {
        ModelSelectionStore store = new();

        int priority = store.GetPreferredModelPriority("deepseek-v4-pro", "deepseek");

        Assert.True(priority >= 0);
    }

    [Fact]
    public void ProviderModelSelections_HasAllProviders()
    {
        ModelSelectionStore store = new();

        Assert.True(store.ProviderModelSelections.ContainsKey("deepseek"));
        Assert.True(store.ProviderModelSelections.ContainsKey("openai"));
        Assert.True(store.ProviderModelSelections.ContainsKey("nvidia"));
        Assert.True(store.ProviderModelSelections.ContainsKey("groq"));
        Assert.True(store.ProviderModelSelections.ContainsKey("moonshot"));
        Assert.True(store.ProviderModelSelections.ContainsKey("openrouter"));
        Assert.True(store.ProviderModelSelections.ContainsKey("ollamacloud"));
    }

    [Fact]
    public void GetProviderModelSelections_OllamaCloud_ReturnsTenEntries()
    {
        ModelSelectionStore store = new();

        ModelSelectionEntry[] entries = store.GetProviderModelSelections("ollamacloud");

        Assert.NotEmpty(entries);
        Assert.Equal(10, entries.Length);
    }

    [Fact]
    public void FindModelSelectionEntry_OllamaCloud_Glm51_FindsEntry()
    {
        ModelSelectionStore store = new();

        ModelSelectionEntry? entry = store.FindModelSelectionEntry("glm-5.1", "ollamacloud");

        Assert.NotNull(entry);
        Assert.Equal("glm-5.1", entry.Value.Match);
        Assert.Equal(1, entry.Value.Priority);
    }

    [Fact]
    public void FindModelSelectionEntry_OllamaCloud_Qwen3Vl_FindsEntry()
    {
        ModelSelectionStore store = new();

        ModelSelectionEntry? entry = store.FindModelSelectionEntry("qwen3-vl:235b", "ollamacloud");

        Assert.NotNull(entry);
        Assert.Equal("qwen3-vl:235b", entry.Value.Match);
        Assert.True(entry.Value.Execution.SupportsVision);
    }

    [Fact]
    public void GetExecutionConfigForModel_OllamaCloud_Glm51_HasContextLength128K()
    {
        ModelSelectionStore store = new();
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        ModelExecutionConfig config = store.GetExecutionConfigForModel("glm-5.1", registry.ModelToProvider);

        Assert.True(config.ContextLength.HasValue);
        Assert.Equal(128_000, config.ContextLength.Value);
    }

    [Fact]
    public void GetExecutionConfigForModel_OllamaCloud_NemotronSuper_HasContextLength1M()
    {
        ModelSelectionStore store = new();
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        ModelExecutionConfig config = store.GetExecutionConfigForModel("nemotron-3-super", registry.ModelToProvider);

        Assert.True(config.ContextLength.HasValue);
        Assert.Equal(1_048_576, config.ContextLength.Value);
    }

    [Fact]
    public void GetExecutionConfigForModel_OllamaCloud_Cogito_HasReasoningTemperature()
    {
        ModelSelectionStore store = new();
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        ModelExecutionConfig config = store.GetExecutionConfigForModel("cogito-2.1:671b", registry.ModelToProvider);

        Assert.True(config.Temperature.HasValue);
        Assert.Equal(0.6, config.Temperature.Value);
    }

    [Fact]
    public void IsPreferredModel_OllamaCloud_Glm5_ReturnsTrue()
    {
        ModelSelectionStore store = new();

        bool isPreferred = store.IsPreferredModel("glm-5", "ollamacloud");

        Assert.True(isPreferred);
    }

    [Fact]
    public void GetPreferredModelPriority_OllamaCloud_MistralLarge3_ReturnsPriority3()
    {
        ModelSelectionStore store = new();

        int priority = store.GetPreferredModelPriority("mistral-large-3:675b", "ollamacloud");

        Assert.Equal(3, priority);
    }

    [Fact]
    public void GetExecutionConfigForModel_HasTemperature()
    {
        ModelSelectionStore store = new();
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        ModelExecutionConfig config = store.GetExecutionConfigForModel("deepseek-v4-pro", registry.ModelToProvider);

        Assert.True(config.Temperature.HasValue);
        Assert.True(config.Temperature.Value >= 0);
    }

    [Fact]
    public void GetExecutionConfigForModel_HasMaxTokensPreferred()
    {
        ModelSelectionStore store = new();
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        ModelExecutionConfig config = store.GetExecutionConfigForModel("deepseek-v4-pro", registry.ModelToProvider);

        Assert.True(config.MaxTokensPreferred.HasValue);
    }

    [Fact]
    public void GetExecutionConfigForModel_DeepSeekCoder_HasReasoningEffort()
    {
        ModelSelectionStore store = new();
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        ModelExecutionConfig config = store.GetExecutionConfigForModel("deepseek-coder-6.7b-instruct", registry.ModelToProvider);

        Assert.False(string.IsNullOrWhiteSpace(config.ReasoningEffort));
    }
}