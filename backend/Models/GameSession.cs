namespace TsAi.Models;

public class GameSession
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTime StartTime { get; init; } = DateTime.UtcNow;
    public DateTime? EndTime { get; set; }
    public string ChannelName { get; init; } = "";
    public List<ChatMessage> Messages { get; } = new();
    public List<AiSummary> Summaries { get; } = new();
}
