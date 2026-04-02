namespace TsAi.Models;

public class AiSummary
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Content { get; set; } = "";
    public string ChannelName { get; init; } = "";
    public bool WasUpdated { get; set; }
}
