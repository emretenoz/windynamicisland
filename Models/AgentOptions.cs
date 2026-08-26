namespace WinDynamicIsland.Models;

public sealed class AgentOptions
{
    public string AgentEndpointUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string AuthHeaderName { get; set; } = "Authorization";
    public string AuthHeaderValuePrefix { get; set; } = "Bearer";
    public string AudioFormFieldName { get; set; } = "audio";
    public Dictionary<string, string> AdditionalHeaders { get; set; } = new();
}
