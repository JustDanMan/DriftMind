using OpenAI.Chat;

namespace DriftMind.Services;

#pragma warning disable OPENAI001
internal static class OpenAIChatOptionsFactory
{
    public static ChatCompletionOptions Create(string? reasoningEffort, ILogger logger, string scenario)
    {
        var options = new ChatCompletionOptions();

        if (string.IsNullOrWhiteSpace(reasoningEffort))
        {
            return options;
        }

        if (TryParseReasoningEffort(reasoningEffort, out var reasoningEffortLevel))
        {
            options.ReasoningEffortLevel = reasoningEffortLevel;
            logger.LogInformation("Configured reasoning effort '{ReasoningEffort}' for {Scenario}", reasoningEffort, scenario);
            return options;
        }

        logger.LogWarning(
            "Invalid reasoning effort '{ReasoningEffort}' configured for {Scenario}. Use minimal, low, medium, or high when supported by the deployed model.",
            reasoningEffort,
            scenario);

        return options;
    }

    private static bool TryParseReasoningEffort(string value, out ChatReasoningEffortLevel reasoningEffortLevel)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "minimal":
                reasoningEffortLevel = ChatReasoningEffortLevel.Minimal;
                return true;
            case "low":
                reasoningEffortLevel = ChatReasoningEffortLevel.Low;
                return true;
            case "medium":
                reasoningEffortLevel = ChatReasoningEffortLevel.Medium;
                return true;
            case "high":
                reasoningEffortLevel = ChatReasoningEffortLevel.High;
                return true;
            case "none":
                reasoningEffortLevel = ChatReasoningEffortLevel.None;
                return true;
            default:
                reasoningEffortLevel = default;
                return false;
        }
    }
}
#pragma warning restore OPENAI001