using Edge.PrReviewer;
using NUnit.Framework;

namespace Edge.PrReviewer.Tests
{
    [TestFixture]
    public class CodeBlockTests
    {
        [Test]
        public void TryExtract_NullOrWhitespace_ReturnsFalseAndEmptyString()
        {
            Assert.Multiple(() =>
            {
                Assert.That(CodeBlock.TryExtract(null, out var codeNull), Is.False);
                Assert.That(codeNull, Is.EqualTo(string.Empty));

                Assert.That(CodeBlock.TryExtract("", out var codeEmpty), Is.False);
                Assert.That(codeEmpty, Is.EqualTo(string.Empty));

                Assert.That(CodeBlock.TryExtract("   \r\n\t  ", out var codeWs), Is.False);
                Assert.That(codeWs, Is.EqualTo(string.Empty));
            });
        }

        [Test]
        public void TryExtract_StandardCsharpFence_ExtractsCode()
        {
            var raw = """
                ```csharp
                public class Calculator
                {
                    public int Add(int a, int b) => a + b;
                }
                ```
                """;

            var result = CodeBlock.TryExtract(raw, out var code);

            Assert.That(result, Is.True);
            Assert.That(code, Does.StartWith("public class Calculator"));
            Assert.That(code, Does.EndWith("}"));
        }

        [Test]
        public void TryExtract_FenceWithoutLanguageTag_ExtractsCode()
        {
            var raw = """
                ```
                public record Item(string Name, decimal Price);
                ```
                """;

            var result = CodeBlock.TryExtract(raw, out var code);

            Assert.That(result, Is.True);
            Assert.That(code, Is.EqualTo("public record Item(string Name, decimal Price);"));
        }

        [Test]
        public void TryExtract_SurroundingProse_ExtractsOnlyFencedCode()
        {
            var raw = """
                Here is the updated implementation with the fixes applied:

                ```csharp
                public static void Log(string message) => Console.WriteLine(message);
                ```

                Let me know if you need any other changes!
                """;

            var result = CodeBlock.TryExtract(raw, out var code);

            Assert.That(result, Is.True);
            Assert.That(code, Is.EqualTo("public static void Log(string message) => Console.WriteLine(message);"));
        }

        [Test]
        public void TryExtract_UnterminatedFence_ExtractsRemainingContent()
        {
            var raw = """
                ```csharp
                public void Process()
                {
                    DoWork();
                }
                """;

            var result = CodeBlock.TryExtract(raw, out var code);

            Assert.That(result, Is.True);
            Assert.That(code, Does.StartWith("public void Process()"));
            Assert.That(code, Does.EndWith("}"));
        }

        [Test]
        public void TryExtract_FenceOnSingleLineWithoutNewline_ReturnsFalse()
        {
            var raw = "```csharp";
            var result = CodeBlock.TryExtract(raw, out var code);

            Assert.That(result, Is.False);
            Assert.That(code, Is.EqualTo(string.Empty));
        }

        [Test]
        public void TryExtract_EmptyFencedBody_ReturnsFalse()
        {
            var raw = """
                ```csharp
                
                ```
                """;

            var result = CodeBlock.TryExtract(raw, out var code);

            Assert.That(result, Is.False);
            Assert.That(code, Is.EqualTo(string.Empty));
        }

        [TestCase("public class Sample { }")]
        [TestCase("private int _count = 0;")]
        [TestCase("protected void Init() {}")]
        [TestCase("internal record Customer(int Id);")]
        [TestCase("void Execute() {}")]
        [TestCase("struct Point { int x; int y; }")]
        [TestCase("namespace MyApp.Services { }")]
        [TestCase("using System; using System.Text;")]
        [TestCase("var x = 10;")]
        [TestCase("{\n    var total = 100;\n}")]
        public void TryExtract_NoFence_LooksLikeCsharp_ReturnsTrue(string rawCsharp)
        {
            var result = CodeBlock.TryExtract(rawCsharp, out var code);

            Assert.That(result, Is.True);
            Assert.That(code, Is.EqualTo(rawCsharp.Trim()));
        }

        [TestCase("This is just plain English text with no code whatsoever.")]
        [TestCase("I reviewed the PR and it looks good to me.")]
        [TestCase("Please refer to ticket 1234 for details.")]
        public void TryExtract_NoFence_NotCsharp_ReturnsFalse(string plainText)
        {
            var result = CodeBlock.TryExtract(plainText, out var code);

            Assert.That(result, Is.False);
            Assert.That(code, Is.EqualTo(string.Empty));
        }
    }
}
