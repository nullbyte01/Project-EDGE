using Microsoft.Extensions.AI;

namespace Edge.PrReviewer
{
    public sealed record Persona(string Name, string Instructions, ChatOptions Options);

    public sealed record ReviewTurn(string Role, string Content, TimeSpan Elapsed);

    public sealed record ReviewResult(
        bool Converged,
        int RoundsUsed,
        IReadOnlyList<ReviewTurn> Transcript);
}
