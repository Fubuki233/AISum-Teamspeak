namespace TsAi.Models;

public class TsChannel
{
    public ulong ChannelId { get; init; }
    public string Name { get; init; } = "";
    public ulong ParentId { get; init; }
    public int Order { get; init; }
}
