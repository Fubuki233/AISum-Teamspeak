namespace TsAi.Models;

public class TsClient
{
    public ushort ClientId { get; init; }
    public ulong ChannelId { get; init; }
    public string Nickname { get; init; } = "";
    public string Uid { get; init; } = "";
}
