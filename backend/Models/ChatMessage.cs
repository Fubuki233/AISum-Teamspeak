namespace TsAi.Models;

public class ChatMessage
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public ushort ClientId { get; init; }
    public string ClientUid { get; init; } = "";
    public string ClientName { get; init; } = "";
    public string Text { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string ChannelName { get; init; } = "";
}
