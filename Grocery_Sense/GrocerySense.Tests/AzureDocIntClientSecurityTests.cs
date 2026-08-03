using GrocerySense.Integrations;

namespace GrocerySense.Tests;

public sealed class AzureDocIntClientSecurityTests
{
    [Fact]
    public async Task Receipt_path_rejects_non_azure_endpoint_before_reading_file()
    {
        var client = new AzureDocIntClient("https://evil.example", "key");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.AnalyzeReceiptFileAsync("missing.jpg"));

        Assert.Contains("Azure Cognitive Services", ex.Message);
    }

    [Fact]
    public async Task Layout_path_rejects_non_azure_endpoint_before_reading_file()
    {
        var client = new AzureDocIntClient("https://evil.example", "key");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.AnalyzeLayoutFileAsync("missing.jpg"));

        Assert.Contains("Azure Cognitive Services", ex.Message);
    }

    [Fact]
    public async Task Azure_endpoint_is_allowed()
    {
        var client = new AzureDocIntClient("https://demo.cognitiveservices.azure.com", "key");

        await Assert.ThrowsAsync<FileNotFoundException>(() => client.AnalyzeReceiptFileAsync("missing.jpg"));
    }

    // Passing the host check must not excuse plaintext: http:// would ship the API key and the
    // receipt image unencrypted.
    [Fact]
    public async Task Http_scheme_is_rejected_even_on_azure_host()
    {
        var client = new AzureDocIntClient("http://demo.cognitiveservices.azure.com", "key");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.AnalyzeReceiptFileAsync("missing.jpg"));

        Assert.Contains("https://", ex.Message);
    }

    [Fact]
    public async Task Legacy_cognitive_microsoft_host_is_allowed()
    {
        var client = new AzureDocIntClient("https://canadacentral.api.cognitive.microsoft.com", "key");

        await Assert.ThrowsAsync<FileNotFoundException>(() => client.AnalyzeReceiptFileAsync("missing.jpg"));
    }

    [Theory]
    [InlineData(null, "key")]
    [InlineData("  ", "key")]
    [InlineData("https://demo.cognitiveservices.azure.com", null)]
    [InlineData("https://demo.cognitiveservices.azure.com", "  ")]
    public async Task Missing_credentials_fail_loud_before_any_io(string? endpoint, string? apiKey)
    {
        var client = new AzureDocIntClient(endpoint, apiKey);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.AnalyzeReceiptFileAsync("missing.jpg"));

        Assert.Contains("not configured", ex.Message);
    }
}
