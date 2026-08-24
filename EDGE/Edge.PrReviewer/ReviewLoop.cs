using Microsoft.Extensions.AI;
using System.Diagnostics;

namespace Edge.PrReviewer
{
    public class ReviewLoop
    {
        private readonly IChatClient _client;
        private readonly Persona _reviewer;
        private readonly Persona _revisor;
        private readonly string _sentinel;
        private readonly int _maxRounds;

        public const string DefaultSentinel = "REVIEW_APPROVED";
        public const string RereviewPrompt = "Re-review the revised code above.";

        public ReviewLoop(IChatClient chatClient, Persona reviewer, Persona revisor, string sentinel, int maxRounds = 3)
        {
            _client = chatClient ?? throw new ArgumentNullException($"{nameof(chatClient)}");
            _reviewer = reviewer ?? throw new ArgumentNullException($"{nameof(reviewer)}");
            _revisor = revisor ?? throw new ArgumentNullException($"{nameof(revisor)}");
            _sentinel = !string.IsNullOrEmpty(sentinel) ? sentinel : throw new ArgumentNullException($"{nameof(chatClient)}");
            _maxRounds = maxRounds >= 1 ? maxRounds : throw new ArgumentNullException($"{nameof(maxRounds)}");
        }

        public async Task<ReviewResult> RunAsync(
        string source,
        IProgress<ReviewTurn>? progress = null,
        CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(source);

            // Shared history. The Reviewer MUST see the Revisor's rewrite, or it
            // re-reports findings it already saw fixed and never converges.
            // (EDGE-103 deliberately makes the opposite choice - per-stage scoping.)
            var history = new List<ChatMessage>
        {
            new(ChatRole.User, $"Review this file.\n\n```csharp\n{source}\n```")
        };

            var transcript = new List<ReviewTurn>();

            for (var round = 1; round <= _maxRounds; round++)
            {
                var verdict = await TurnAsync(_reviewer, history, cancellationToken, progress, transcript);

                // Ordinal, never OrdinalIgnoreCase: the sentinel is a protocol
                // token, not prose. A model musing "I think this is
                // review_approved honestly" must NOT end the loop.
                //
                // Checked ONLY on Reviewer output. Scoping it this way is what
                // stops the Revisor approving its own work.
                if (verdict.Content.Contains(_sentinel, StringComparison.Ordinal))
                    return new ReviewResult(true, round, transcript);

                await TurnAsync(_revisor, history, cancellationToken, progress, transcript);
                history.Add(new ChatMessage(ChatRole.User, RereviewPrompt));
            }

            return new ReviewResult(false, _maxRounds, transcript);

        }
        async Task<ReviewTurn> TurnAsync(Persona persona, List<ChatMessage> history, CancellationToken cancellationToken, IProgress<ReviewTurn>? progress, List<ReviewTurn> transcript)
        {
            // Persona instructions ride as a system message swapped per turn,
            // rather than via ChatOptions.Instructions. That property's
            // availability varies across Microsoft.Extensions.AI versions;
            // a system ChatMessage is stable everywhere.
            var messages = new List<ChatMessage> { new(ChatRole.System, persona.Instructions) };
            messages.AddRange(history);

            var sw = Stopwatch.StartNew();
            var response = await _client.GetResponseAsync(messages, persona.Options, cancellationToken);
            sw.Stop();

            var text = response.Text?.Trim() ?? string.Empty;
            history.Add(new ChatMessage(ChatRole.Assistant, text));

            var turn = new ReviewTurn(persona.Name, text, sw.Elapsed);
            transcript.Add(turn);
            progress?.Report(turn);
            return turn;
        }
    }
}
