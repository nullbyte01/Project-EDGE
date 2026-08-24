namespace Edge.PrReviewer;

/// <summary>
/// Pulls the C# out of the Revisor's reply.
///
/// The Revisor is instructed to return one fenced block and nothing else, and
/// it will ignore that instruction some of the time — wrapping the block in
/// "Sure! Here's the fix:" and a closing summary. Stripping this in code is
/// cheaper and far more reliable than escalating the prompt.
/// </summary>
public static class CodeBlock
{
    public static bool TryExtract(string? raw, out string code)
    {
        code = string.Empty;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var open = raw.IndexOf("```", StringComparison.Ordinal);
        if (open < 0)
        {
            // No fence at all. If it plausibly looks like code, take it whole;
            // a bare rewrite with no fence is still a usable answer.
            var trimmed = raw.Trim();
            if (LooksLikeCsharp(trimmed))
            {
                code = trimmed;
                return true;
            }
            return false;
        }

        // Skip the opening fence and any language tag on the same line.
        var afterFence = raw.IndexOf('\n', open);
        if (afterFence < 0) return false;

        var close = raw.IndexOf("```", afterFence, StringComparison.Ordinal);
        var body = close < 0
            ? raw[(afterFence + 1)..]          // unterminated fence - take the rest
            : raw[(afterFence + 1)..close];

        code = body.Trim();
        return code.Length > 0;
    }

    public static string Extract(string? raw)
    {
        return TryExtract(raw, out var code)
             ? code
             : throw new FormatException("No code block found in revisor output.");
    }

    private static bool LooksLikeCsharp(string text)
    {
        return text.Contains('{') && text.Contains('}') &&
        (text.Contains("public", StringComparison.Ordinal) ||
         text.Contains("private", StringComparison.Ordinal) ||
         text.Contains("void", StringComparison.Ordinal) ||
         text.Contains("class", StringComparison.Ordinal) ||
         text.Contains("var ", StringComparison.Ordinal));
    }
}