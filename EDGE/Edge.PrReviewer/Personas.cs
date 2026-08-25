using Edge.PrReviewer;
using Microsoft.Extensions.AI;

public static class Personas
{
    public const string Sentinel = ReviewLoop.DefaultSentinel;

    public static Persona Reviewer(int maxTokens = 350) => new(
        Name: "Reviewer",
        Instructions: $"""
            You are a senior C# reviewer. Review ONLY the code in the latest message.

            Output format:
            ## Findings
            - [BLOCKER|MAJOR|NIT] <symbol or line> - <one-line issue> -> <one-line fix>

            Rules:
            - Maximum 4 findings. Concrete and actionable only. No praise, no summary.
            - If zero BLOCKER and zero MAJOR findings remain, your LAST line must be exactly: {Sentinel}
            - NEVER write {Sentinel} if any BLOCKER or MAJOR finding is listed.
            """,
        Options: new ChatOptions
        {
            Temperature = 0.1f,
            MaxOutputTokens = maxTokens
        });

    public static Persona Revisor(int maxTokens = 500) => new(
        Name: "Revisor",
        Instructions: """
            You are a C# implementer. Read the reviewer's findings and rewrite the code.
            Fix the highest-severity finding first. Preserve the public API shape.
            Respond with ONE fenced ```csharp block containing the FULL revised code.
            No explanation before or after the block.
            """,
        Options: new ChatOptions
        {
            Temperature = 0.3f,
            MaxOutputTokens = maxTokens
        });
}