using GrocerySense.Integrations;

namespace GrocerySense.Tests;

public sealed class AzureDocIntClientSecurityTests
{
    [Fact]
    public async Task Receipt_client_rejects_non_azure_endpoint_before_reading_file()
    {
        var client = new AzureReceiptOcrClient("https://evil.example", "key");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.AnalyzeReceiptFileAsync("missing.jpg"));

        Assert.Contains("Azure Cognitive Services", ex.Message);
    }

    [Fact]
    public async Task Flyer_client_rejects_non_azure_endpoint_before_reading_file()
    {
        var client = new FlyerDocIntClient("https://evil.example", "key");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.AnalyzeLayoutFileAsync("missing.jpg"));

        Assert.Contains("Azure Cognitive Services", ex.Message);
    }

    [Fact]
    public async Task Receipt_client_allows_azure_endpoint()
    {
        var client = new AzureReceiptOcrClient("https://demo.cognitiveservices.azure.com", "key");

        await Assert.ThrowsAsync<FileNotFoundException>(() => client.AnalyzeReceiptFileAsync("missing.jpg"));
    }
}
