using Edge.PrReviewer;
using NUnit.Framework;

namespace Edge.PrReviewer.Tests
{
    [TestFixture]
    public class PersonasTests
    {
        [Test]
        public void Sentinel_MatchesReviewLoopDefaultSentinel()
        {
            Assert.That(Personas.Sentinel, Is.EqualTo(ReviewLoop.DefaultSentinel));
            Assert.That(Personas.Sentinel, Is.EqualTo("REVIEW_APPROVED"));
        }

        [Test]
        public void Reviewer_DefaultSettings_ConfiguredCorrectly()
        {
            var reviewer = Personas.Reviewer();

            Assert.Multiple(() =>
            {
                Assert.That(reviewer.Name, Is.EqualTo("Reviewer"));
                Assert.That(reviewer.Options.Temperature, Is.EqualTo(0.1f));
                Assert.That(reviewer.Options.MaxOutputTokens, Is.EqualTo(350));
                Assert.That(reviewer.Instructions, Does.Contain(Personas.Sentinel));
                Assert.That(reviewer.Instructions, Does.Contain("## Findings"));
                Assert.That(reviewer.Instructions, Does.Contain("[BLOCKER|MAJOR|NIT]"));
            });
        }

        [Test]
        public void Reviewer_CustomMaxTokens_SetsMaxOutputTokens()
        {
            var reviewer = Personas.Reviewer(maxTokens: 800);

            Assert.Multiple(() =>
            {
                Assert.That(reviewer.Name, Is.EqualTo("Reviewer"));
                Assert.That(reviewer.Options.MaxOutputTokens, Is.EqualTo(800));
            });
        }

        [Test]
        public void Revisor_DefaultSettings_ConfiguredCorrectly()
        {
            var revisor = Personas.Revisor();

            Assert.Multiple(() =>
            {
                Assert.That(revisor.Name, Is.EqualTo("Revisor"));
                Assert.That(revisor.Options.Temperature, Is.EqualTo(0.3f));
                Assert.That(revisor.Options.MaxOutputTokens, Is.EqualTo(500));
                Assert.That(revisor.Instructions, Does.Contain("```csharp"));
                Assert.That(revisor.Instructions, Does.Contain("Fix the highest-severity finding first"));
            });
        }

        [Test]
        public void Revisor_CustomMaxTokens_SetsMaxOutputTokens()
        {
            var revisor = Personas.Revisor(maxTokens: 1024);

            Assert.Multiple(() =>
            {
                Assert.That(revisor.Name, Is.EqualTo("Revisor"));
                Assert.That(revisor.Options.MaxOutputTokens, Is.EqualTo(1024));
            });
        }
    }
}
