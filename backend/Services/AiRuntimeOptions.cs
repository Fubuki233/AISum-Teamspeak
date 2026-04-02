namespace TsAi.Services;

/// <summary>
/// Runtime-selectable AI provider/model preferences used by channel summaries.
/// </summary>
public sealed class AiRuntimeOptions
{
    public sealed record AiChoice(string Id, string Provider, string Model, string Label, string Description);

    private readonly object _gate = new();

    private static readonly IReadOnlyList<AiChoice> _choices =
    [
        new("auto", "auto", "", "自动", "优先当前可用链路"),
        new("dashscope:qwen-turbo", "dashscope", "qwen-turbo", "DashScope · Qwen Turbo", "稳定、速度快"),
        new("grok:grok-4-1-fast-non-reasoning", "grok", "grok-4-1-fast-non-reasoning", "Grok 4.1 Fast", "直给、快、non-reasoning"),
        new("grok:grok-4-fast-non-reasoning", "grok", "grok-4-fast-non-reasoning", "Grok 4 Fast", "兼容备选"),
        new("doubao:doubao-seed-2-0-lite-260215", "doubao", "doubao-seed-2-0-lite-260215", "Doubao Seed 2 Lite", "火山默认"),
    ];

    private string _provider = "auto";
    private string _model = "";
    private string _tone = "standard";

    public IReadOnlyList<AiChoice> Choices => _choices;

    public string Provider
    {
        get { lock (_gate) return _provider; }
    }

    public string Model
    {
        get { lock (_gate) return _model; }
    }

    public string Tone
    {
        get { lock (_gate) return _tone; }
    }

    public string CurrentSelectionId
    {
        get
        {
            lock (_gate)
            {
                return _choices.FirstOrDefault(c => c.Provider == _provider && c.Model == _model)?.Id
                    ?? (_provider == "auto" ? "auto" : _choices[0].Id);
            }
        }
    }

    public void SetSelection(string? selectionId, string? tone = null)
    {
        var choice = _choices.FirstOrDefault(c => string.Equals(c.Id, selectionId, StringComparison.OrdinalIgnoreCase))
                     ?? _choices[0];

        lock (_gate)
        {
            _provider = choice.Provider;
            _model = choice.Model;
            if (!string.IsNullOrWhiteSpace(tone))
                _tone = NormalizeTone(tone);
        }
    }

    public void SetTone(string? tone)
    {
        if (string.IsNullOrWhiteSpace(tone)) return;
        lock (_gate)
            _tone = NormalizeTone(tone);
    }

    private static string NormalizeTone(string tone) =>
        string.Equals(tone, "bold", StringComparison.OrdinalIgnoreCase) ? "bold" : "standard";
}
