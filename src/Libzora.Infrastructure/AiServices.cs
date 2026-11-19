namespace Libzora.Infrastructure;

public sealed class AiConnectionSettings
{
    public string Provider { get; set; } = "deepseek";
    public string BaseUrl { get; set; } = "https://api.deepseek.com";
    public string Model { get; set; } = "deepseek-v4-flash";
    public string ApiKey { get; set; } = string.Empty;

    public bool IsConfigured =>
        Uri.TryCreate(BaseUrl, UriKind.Absolute, out var endpoint)
        && endpoint.Scheme is "http" or "https"
        && !string.IsNullOrWhiteSpace(Model)
        && (IsLocalEndpoint || !string.IsNullOrWhiteSpace(ApiKey));

    public bool IsLocalEndpoint => Uri.TryCreate(BaseUrl, UriKind.Absolute, out var endpoint)
        && endpoint.Scheme is "http" or "https" && endpoint.IsLoopback;

    public string Destination => Uri.TryCreate(BaseUrl, UriKind.Absolute, out var endpoint)
        ? endpoint.GetComponents(UriComponents.SchemeAndServer, UriFormat.SafeUnescaped) : string.Empty;


    public string ProviderDisplayName => Provider.ToLowerInvariant() switch
    {
        "openai" => "OpenAI",
        "custom" => "自定义 API",
        _ => "DeepSeek"
    };

    public AiConnectionSettings Clone() => new()
    {
        Provider = Provider,
        BaseUrl = BaseUrl,
        Model = Model,
        ApiKey = ApiKey
    };

    public static (string BaseUrl, string Model) GetDefaults(string provider) => provider.ToLowerInvariant() switch
    {
        "openai" => ("https://api.openai.com/v1", "gpt-5.6-sol"),
        "custom" => ("http://127.0.0.1:11434/v1", string.Empty),
        _ => ("https://api.deepseek.com", "deepseek-v4-flash")
    };

    public static IReadOnlyList<string> GetModelOptions(string provider, string currentModel)
    {
        var defaults = provider.Trim().ToLowerInvariant() switch
        {
            "openai" => new[] { "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna", "gpt-5-mini", "gpt-4.1" },
            "deepseek" => new[] { "deepseek-v4-flash", "deepseek-v4-pro" },
            _ => []
        };

        return new[] { currentModel.Trim() }
            .Concat(defaults)
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string NormalizeModel(string provider, string model)
    {
        if (!provider.Trim().Equals("deepseek", StringComparison.OrdinalIgnoreCase))
            return model.Trim();

        return model.Trim().ToLowerInvariant() switch
        {
            "deepseek-chat" or "deepseek-reasoner" => "deepseek-v4-flash",
            var normalized => normalized
        };
    }
}
