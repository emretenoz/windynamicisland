namespace WinDynamicIsland.Models;

public sealed class AgentResponse
{
    public string? Text { get; set; }
    public string? AudioUrl { get; set; }
    public byte[]? AudioBytes { get; set; }
    public string? AudioContentType { get; set; }
}
