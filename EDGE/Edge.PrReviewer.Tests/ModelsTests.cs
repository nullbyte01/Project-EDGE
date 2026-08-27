using Edge.PrReviewer;
using Microsoft.Extensions.AI;
using NUnit.Framework;

namespace Edge.PrReviewer.Tests
{
    [TestFixture]
    public class ModelsTests
    {
        [Test]
        public void Persona_Record_PropertiesAndEquality()
        {
            var options = new ChatOptions { Temperature = 0.5f, MaxOutputTokens = 100 };
            var p1 = new Persona("Dev", "Write code", options);
            var p2 = new Persona("Dev", "Write code", options);

            Assert.Multiple(() =>
            {
                Assert.That(p1.Name, Is.EqualTo("Dev"));
                Assert.That(p1.Instructions, Is.EqualTo("Write code"));
                Assert.That(p1.Options, Is.SameAs(options));
                Assert.That(p1, Is.EqualTo(p2));
            });
        }

        [Test]
        public void ReviewTurn_Record_PropertiesAndEquality()
        {
            var elapsed = TimeSpan.FromMilliseconds(250);
            var t1 = new ReviewTurn("Reviewer", "Looks great", elapsed);
            var t2 = new ReviewTurn("Reviewer", "Looks great", elapsed);

            Assert.Multiple(() =>
            {
                Assert.That(t1.Role, Is.EqualTo("Reviewer"));
                Assert.That(t1.Content, Is.EqualTo("Looks great"));
                Assert.That(t1.Elapsed, Is.EqualTo(elapsed));
                Assert.That(t1, Is.EqualTo(t2));
            });
        }

        [Test]
        public void ReviewResult_Record_PropertiesAndEquality()
        {
            var turns = new List<ReviewTurn>
            {
                new("Reviewer", "Findings", TimeSpan.FromSeconds(1)),
                new("Revisor", "```csharp\ncode\n```", TimeSpan.FromSeconds(2))
            };

            var r1 = new ReviewResult(true, 1, turns);
            var r2 = new ReviewResult(true, 1, turns);

            Assert.Multiple(() =>
            {
                Assert.That(r1.Converged, Is.True);
                Assert.That(r1.RoundsUsed, Is.EqualTo(1));
                Assert.That(r1.Transcript, Is.SameAs(turns));
                Assert.That(r1, Is.EqualTo(r2));
            });
        }
    }
}
