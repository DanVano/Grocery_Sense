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
}
