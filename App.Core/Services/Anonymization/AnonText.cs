namespace KnowledgeCapture.Core.Services.Anonymization;

/// <summary>
/// Text that has passed the anonymization pipeline. The storage layer only accepts this type, so raw text cannot be
/// persisted by accident: outside App.Core the only way to obtain one is <see cref="Anonymizer"/> (or reading storage).
/// </summary>
public readonly record struct AnonText
{
    public string Value { get; }
    internal AnonText(string value) => Value = value;
    public override string ToString() => Value;
}
