namespace KnowledgeCapture.Core.Services;

/// <summary>The model rejected the prompt as longer than its (static NPU) prompt limit. Callers retry with less context.</summary>
public sealed class PromptTooLongException() : Exception("The prompt is longer than the model's input limit.");
