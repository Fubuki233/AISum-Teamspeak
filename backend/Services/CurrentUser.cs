namespace TsAi.Services;

/// <summary>Scoped per-circuit: holds the logged-in user's TS identity.</summary>
public class CurrentUser
{
    public string? Uid { get; set; }
    public string? DisplayName { get; set; }
    public bool IsAuthenticated => !string.IsNullOrEmpty(Uid);
}
