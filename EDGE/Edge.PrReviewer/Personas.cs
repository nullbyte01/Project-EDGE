using Microsoft.Extensions.AI;

namespace Edge.PrReviewer
{
    public static class Personas
    {
        public const string Sentinel = ReviewLoop.DefaultSentinel;

        public static Persona Reviewer(int maxTokens = 400)
        {
            string reviewerInstruction = $""""
                    You are a senior C# reviewer. Review ONLY the code in the latest message.

                Output format:
                ## Findings
                - [BLOCKER|MAJOR|NIT] <symbol or line> - <one-line issue> -> <one-line fix>

                Rules:
                - Maximum 4 findings. Concrete and actionable only. No praise, no summary.
                - If zero BLOCKER and zero MAJOR findings remain, your LAST line must be
                  exactly: {Sentinel}
                - NEVER write {Sentinel} if any BLOCKER or MAJOR finding is listed.
                """";

            return new(
        Name: "Reviewer",
        Instructions: reviewerInstruction,
            Options: new ChatOptions
            {
                Temperature = 0.1f,      // cold: consistency matters more than creativity
                TopP = 0.9f,
                MaxOutputTokens = maxTokens
            });
        }

        public static Persona Revisor(int maxTokens = 700)
        {
            string revisorInstruction = $"""
        You are a C# implementer. Read the reviewer's findings and rewrite the code.
        Fix the highest-severity finding first. Preserve the public API shape.
        Respond with ONE fenced ```csharp block containing the FULL revised code.
        No explanation before or after the block.
        """;
            return new Persona(Name: "Revisor",
        Instructions: revisorInstruction,
            Options: new ChatOptions
            {
                Temperature = 0.4f,      // warmer: needs latitude to find another approach
                MaxOutputTokens = maxTokens
            });
        }
    }
}
