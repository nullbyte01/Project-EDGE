using Edge.PrReviewer;
using Microsoft.Extensions.AI;
using Moq;
using NUnit.Framework;

namespace Edge.PrReviewer.Tests
{
    [TestFixture]
    public class ReviewLoopTests
    {
        private Mock<IChatClient> _mockChatClient = null!;
        private Persona _reviewer = null!;
        private Persona _revisor = null!;
        private const string TestSentinel = "REVIEW_APPROVED";

        [SetUp]
        public void SetUp()
        {
            _mockChatClient = new Mock<IChatClient>(MockBehavior.Strict);
            _reviewer = Personas.Reviewer();
            _revisor = Personas.Revisor();
        }

        [Test]
        public void Constructor_NullChatClient_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new ReviewLoop(null!, _reviewer, _revisor, TestSentinel, 3));
        }

        [Test]
        public void Constructor_NullReviewer_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new ReviewLoop(_mockChatClient.Object, null!, _revisor, TestSentinel, 3));
        }

        [Test]
        public void Constructor_NullRevisor_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new ReviewLoop(_mockChatClient.Object, _reviewer, null!, TestSentinel, 3));
        }

        [TestCase(null)]
        [TestCase("")]
        public void Constructor_NullOrEmptySentinel_ThrowsArgumentNullException(string? invalidSentinel)
        {
            Assert.Throws<ArgumentNullException>(() =>
                new ReviewLoop(_mockChatClient.Object, _reviewer, _revisor, invalidSentinel!, 3));
        }

        [TestCase(0)]
        [TestCase(-1)]
        [TestCase(-5)]
        public void Constructor_MaxRoundsLessThanOne_ThrowsArgumentNullException(int invalidMaxRounds)
        {
            Assert.Throws<ArgumentNullException>(() =>
                new ReviewLoop(_mockChatClient.Object, _reviewer, _revisor, TestSentinel, invalidMaxRounds));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void RunAsync_NullOrWhitespaceSource_ThrowsArgumentException(string? invalidSource)
        {
            var loop = new ReviewLoop(_mockChatClient.Object, _reviewer, _revisor, TestSentinel, 3);

            Assert.CatchAsync<ArgumentException>(async () =>
                await loop.RunAsync(invalidSource!));
        }

        [Test]
        public async Task RunAsync_ReviewerApprovesInFirstRound_ReturnsConvergedAndDoesNotInvokeRevisor()
        {
            var reviewerResponseText = """
                ## Findings
                No blockers found.
                REVIEW_APPROVED
                """;

            _mockChatClient
                .Setup(c => c.GetResponseAsync(
                    It.Is<IEnumerable<ChatMessage>>(msgs =>
                        msgs.Any(m => m.Role == ChatRole.System && m.Text == _reviewer.Instructions) &&
                        msgs.Any(m => m.Role == ChatRole.User && m.Text!.Contains("public class Foo {}"))),
                    _reviewer.Options,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, reviewerResponseText)));

            var loop = new ReviewLoop(_mockChatClient.Object, _reviewer, _revisor, TestSentinel, maxRounds: 3);

            var result = await loop.RunAsync("public class Foo {}");

            Assert.Multiple(() =>
            {
                Assert.That(result.Converged, Is.True);
                Assert.That(result.RoundsUsed, Is.EqualTo(1));
                Assert.That(result.Transcript, Has.Count.EqualTo(1));
                Assert.That(result.Transcript[0].Role, Is.EqualTo("Reviewer"));
                Assert.That(result.Transcript[0].Content, Is.EqualTo(reviewerResponseText));
            });

            _mockChatClient.Verify(
                c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Test]
        public async Task RunAsync_ReviewerRequiresRevisionThenApproves_ConvergesInRound2()
        {
            var round1ReviewerFindings = """
                ## Findings
                - [BLOCKER] Calculator.Add - Missing null check -> Add argument validation
                """;

            var round1RevisorCode = """
                ```csharp
                public class Calculator
                {
                    public int Add(int a, int b) => a + b;
                }
                ```
                """;

            var round2ReviewerApproval = """
                ## Findings
                Clean.
                REVIEW_APPROVED
                """;

            var capturedCalls = new List<List<ChatMessage>>();
            var callIndex = 0;
            var responses = new[]
            {
                new ChatResponse(new ChatMessage(ChatRole.Assistant, round1ReviewerFindings)),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, round1RevisorCode)),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, round2ReviewerApproval))
            };

            _mockChatClient
                .Setup(c => c.GetResponseAsync(
                    It.IsAny<IEnumerable<ChatMessage>>(),
                    It.IsAny<ChatOptions>(),
                    It.IsAny<CancellationToken>()))
                .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((msgs, opt, ct) => capturedCalls.Add(msgs.ToList()))
                .ReturnsAsync(() => responses[callIndex++]);

            var loop = new ReviewLoop(_mockChatClient.Object, _reviewer, _revisor, TestSentinel, maxRounds: 3);

            var result = await loop.RunAsync("public class Calculator {}");

            Assert.Multiple(() =>
            {
                Assert.That(result.Converged, Is.True);
                Assert.That(result.RoundsUsed, Is.EqualTo(2));
                Assert.That(result.Transcript, Has.Count.EqualTo(3));
                Assert.That(result.Transcript[0].Role, Is.EqualTo("Reviewer"));
                Assert.That(result.Transcript[1].Role, Is.EqualTo("Revisor"));
                Assert.That(result.Transcript[2].Role, Is.EqualTo("Reviewer"));
            });

            Assert.Multiple(() =>
            {
                Assert.That(capturedCalls, Has.Count.EqualTo(3));

                // Call 1: Reviewer system msg + initial source
                Assert.That(capturedCalls[0].Any(m => m.Role == ChatRole.System && m.Text == _reviewer.Instructions), Is.True);
                Assert.That(capturedCalls[0].Any(m => m.Role == ChatRole.User && m.Text!.Contains("public class Calculator {}")), Is.True);

                // Call 2: Revisor system msg + source + reviewer findings
                Assert.That(capturedCalls[1].Any(m => m.Role == ChatRole.System && m.Text == _revisor.Instructions), Is.True);
                Assert.That(capturedCalls[1].Any(m => m.Role == ChatRole.Assistant && m.Text == round1ReviewerFindings), Is.True);

                // Call 3: Reviewer system msg + source + reviewer findings + extracted revisor code + rereview prompt
                Assert.That(capturedCalls[2].Any(m => m.Role == ChatRole.System && m.Text == _reviewer.Instructions), Is.True);
                Assert.That(capturedCalls[2].Any(m => m.Role == ChatRole.User && m.Text == ReviewLoop.RereviewPrompt), Is.True);
            });
        }

        [Test]
        public async Task RunAsync_MaxRoundsExceededWithoutApproval_ReturnsNonConverged()
        {
            var reviewerFeedback = """
                ## Findings
                - [BLOCKER] Line 1 - Still has issues -> Fix it
                """;

            var revisorCode = """
                ```csharp
                public class IncompleteFix {}
                ```
                """;

            _mockChatClient
                .SetupSequence(c => c.GetResponseAsync(
                    It.IsAny<IEnumerable<ChatMessage>>(),
                    It.IsAny<ChatOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, reviewerFeedback)))
                .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, revisorCode)))
                .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, reviewerFeedback)))
                .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, revisorCode)));

            var loop = new ReviewLoop(_mockChatClient.Object, _reviewer, _revisor, TestSentinel, maxRounds: 2);

            var result = await loop.RunAsync("public class Buggy {}");

            Assert.Multiple(() =>
            {
                Assert.That(result.Converged, Is.False);
                Assert.That(result.RoundsUsed, Is.EqualTo(2));
                Assert.That(result.Transcript, Has.Count.EqualTo(4));
            });

            _mockChatClient.Verify(
                c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()),
                Times.Exactly(4));
        }

        [TestCase("REVIEW_APPROVED")]
        [TestCase("`REVIEW_APPROVED`")]
        [TestCase("```REVIEW_APPROVED```")]
        [TestCase("**REVIEW_APPROVED**")]
        [TestCase("*REVIEW_APPROVED*")]
        [TestCase("\"REVIEW_APPROVED\"")]
        [TestCase("'REVIEW_APPROVED'")]
        [TestCase("REVIEW_APPROVED.")]
        [TestCase("  REVIEW_APPROVED  ")]
        public async Task RunAsync_SentinelFormattedVariations_RecognizedAsApproved(string sentinelVariation)
        {
            var reviewerText = $"""
                ## Findings
                None.
                {sentinelVariation}
                """;

            _mockChatClient
                .Setup(c => c.GetResponseAsync(
                    It.IsAny<IEnumerable<ChatMessage>>(),
                    It.IsAny<ChatOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, reviewerText)));

            var loop = new ReviewLoop(_mockChatClient.Object, _reviewer, _revisor, TestSentinel, maxRounds: 1);

            var result = await loop.RunAsync("public class SafeCode {}");

            Assert.That(result.Converged, Is.True);
            Assert.That(result.RoundsUsed, Is.EqualTo(1));
        }

        [Test]
        public async Task RunAsync_SentinelOnSecondToLastLine_RecognizedAsApproved()
        {
            var reviewerText = """
                ## Findings
                None.
                REVIEW_APPROVED
                No additional remarks.
                """;

            _mockChatClient
                .Setup(c => c.GetResponseAsync(
                    It.IsAny<IEnumerable<ChatMessage>>(),
                    It.IsAny<ChatOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, reviewerText)));

            var loop = new ReviewLoop(_mockChatClient.Object, _reviewer, _revisor, TestSentinel, maxRounds: 1);

            var result = await loop.RunAsync("public class SafeCode {}");

            Assert.That(result.Converged, Is.True);
            Assert.That(result.RoundsUsed, Is.EqualTo(1));
        }

        [TestCase("review_approved")]
        [TestCase("Review_Approved")]
        [TestCase("review_Approved")]
        public async Task RunAsync_SentinelCaseMismatch_DoesNotConverge(string lowerCaseSentinel)
        {
            var reviewerText = $"""
                ## Findings
                Looks ok.
                {lowerCaseSentinel}
                """;

            _mockChatClient
                .Setup(c => c.GetResponseAsync(
                    It.IsAny<IEnumerable<ChatMessage>>(),
                    It.IsAny<ChatOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, reviewerText)));

            var loop = new ReviewLoop(_mockChatClient.Object, _reviewer, _revisor, TestSentinel, maxRounds: 1);

            var result = await loop.RunAsync("public class SafeCode {}");

            Assert.That(result.Converged, Is.False);
            Assert.That(result.RoundsUsed, Is.EqualTo(1));
        }

        [Test]
        public async Task RunAsync_SentinelInMiddleOfParagraph_DoesNotTriggerPrematureApproval()
        {
            var reviewerText = """
                ## Findings
                We must not emit REVIEW_APPROVED because there is a severe flaw here:
                - [BLOCKER] Save - Data loss risk -> Use transaction
                Please fix this immediately.
                """;

            _mockChatClient
                .Setup(c => c.GetResponseAsync(
                    It.IsAny<IEnumerable<ChatMessage>>(),
                    It.IsAny<ChatOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, reviewerText)));

            var loop = new ReviewLoop(_mockChatClient.Object, _reviewer, _revisor, TestSentinel, maxRounds: 1);

            var result = await loop.RunAsync("public class DataService {}");

            Assert.That(result.Converged, Is.False);
        }

        [Test]
        public async Task RunAsync_RevisorEmitsSentinel_DoesNotTriggerApproval()
        {
            var reviewerText = """
                ## Findings
                - [BLOCKER] Line 1 - Bug -> Fix
                """;

            var revisorText = """
                ```csharp
                public class Fixed {}
                ```
                REVIEW_APPROVED
                """;

            var reviewerTextRound2 = """
                ## Findings
                - [BLOCKER] Line 1 - Still broken -> Fix
                """;

            _mockChatClient
                .SetupSequence(c => c.GetResponseAsync(
                    It.IsAny<IEnumerable<ChatMessage>>(),
                    It.IsAny<ChatOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, reviewerText)))
                .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, revisorText)))
                .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, reviewerTextRound2)))
                .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, revisorText)));

            var loop = new ReviewLoop(_mockChatClient.Object, _reviewer, _revisor, TestSentinel, maxRounds: 2);

            var result = await loop.RunAsync("public class Code {}");

            // Revisor's sentinel must not trigger approval. The loop completes all 2 rounds.
            Assert.That(result.Converged, Is.False);
            Assert.That(result.RoundsUsed, Is.EqualTo(2));
            Assert.That(result.Transcript, Has.Count.EqualTo(4));
        }

        [Test]
        public async Task RunAsync_ReportsProgressForEachTurn()
        {
            var reviewerText = "REVIEW_APPROVED";

            _mockChatClient
                .Setup(c => c.GetResponseAsync(
                    It.IsAny<IEnumerable<ChatMessage>>(),
                    It.IsAny<ChatOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, reviewerText)));

            var reportedTurns = new List<ReviewTurn>();
            var progressMock = new Mock<IProgress<ReviewTurn>>();
            progressMock
                .Setup(p => p.Report(It.IsAny<ReviewTurn>()))
                .Callback<ReviewTurn>(reportedTurns.Add);

            var loop = new ReviewLoop(_mockChatClient.Object, _reviewer, _revisor, TestSentinel, maxRounds: 1);

            await loop.RunAsync("public class Foo {}", progressMock.Object);

            Assert.Multiple(() =>
            {
                Assert.That(reportedTurns, Has.Count.EqualTo(1));
                Assert.That(reportedTurns[0].Role, Is.EqualTo("Reviewer"));
                Assert.That(reportedTurns[0].Content, Is.EqualTo(reviewerText));
            });

            progressMock.Verify(p => p.Report(It.IsAny<ReviewTurn>()), Times.Once);
        }

        [Test]
        public async Task RunAsync_PassesCancellationTokenToChatClient()
        {
            using var cts = new CancellationTokenSource();
            var expectedToken = cts.Token;

            _mockChatClient
                .Setup(c => c.GetResponseAsync(
                    It.IsAny<IEnumerable<ChatMessage>>(),
                    It.IsAny<ChatOptions>(),
                    expectedToken))
                .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "REVIEW_APPROVED")));

            var loop = new ReviewLoop(_mockChatClient.Object, _reviewer, _revisor, TestSentinel, maxRounds: 1);

            var result = await loop.RunAsync("public class Code {}", cancellationToken: expectedToken);

            Assert.That(result.Converged, Is.True);
            _mockChatClient.Verify(
                c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), expectedToken),
                Times.Once);
        }

        [Test]
        public async Task RunAsync_RevisorUnfencedOutput_AppendsRawTextToHistory()
        {
            var reviewerText = "Needs fix.";
            var revisorPlainNoCode = "I cannot rewrite this without more information.";
            var reviewerTextRound2 = "REVIEW_APPROVED";

            var capturedCalls = new List<List<ChatMessage>>();
            var callIndex = 0;
            var responses = new[]
            {
                new ChatResponse(new ChatMessage(ChatRole.Assistant, reviewerText)),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, revisorPlainNoCode)),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, reviewerTextRound2))
            };

            _mockChatClient
                .Setup(c => c.GetResponseAsync(
                    It.IsAny<IEnumerable<ChatMessage>>(),
                    It.IsAny<ChatOptions>(),
                    It.IsAny<CancellationToken>()))
                .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((msgs, opt, ct) => capturedCalls.Add(msgs.ToList()))
                .ReturnsAsync(() => responses[callIndex++]);

            var loop = new ReviewLoop(_mockChatClient.Object, _reviewer, _revisor, TestSentinel, maxRounds: 2);

            var result = await loop.RunAsync("public class Sample {}");

            Assert.Multiple(() =>
            {
                Assert.That(result.Converged, Is.True);
                Assert.That(result.RoundsUsed, Is.EqualTo(2));
                Assert.That(capturedCalls, Has.Count.EqualTo(3));
                // Verify the unfenced text was appended raw as Assistant message
                Assert.That(capturedCalls[2].Any(m => m.Role == ChatRole.Assistant && m.Text == revisorPlainNoCode), Is.True);
            });
        }

        [Test]
        public async Task RunAsync_NullChatResponseText_HandledAsEmptyString()
        {
            _mockChatClient
                .Setup(c => c.GetResponseAsync(
                    It.IsAny<IEnumerable<ChatMessage>>(),
                    It.IsAny<ChatOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ChatResponse([]));

            var loop = new ReviewLoop(_mockChatClient.Object, _reviewer, _revisor, TestSentinel, maxRounds: 1);

            var result = await loop.RunAsync("public class Code {}");

            Assert.Multiple(() =>
            {
                Assert.That(result.Converged, Is.False);
                Assert.That(result.Transcript, Has.Count.EqualTo(2));
                Assert.That(result.Transcript[0].Role, Is.EqualTo("Reviewer"));
                Assert.That(result.Transcript[0].Content, Is.EqualTo(string.Empty));
                Assert.That(result.Transcript[1].Role, Is.EqualTo("Revisor"));
                Assert.That(result.Transcript[1].Content, Is.EqualTo(string.Empty));
            });
        }
    }
}
