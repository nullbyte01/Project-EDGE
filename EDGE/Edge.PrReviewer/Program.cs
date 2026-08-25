using System.Diagnostics;
using System.Text;
using Edge.PrReviewer;
using Microsoft.Extensions.AI;
using Microsoft.ML.OnnxRuntimeGenAI;

// Owns global native DirectML init/shutdown. First constructed, last disposed.
using var oga = new OgaHandle();

const int MaxSourceChars = 6_000;

// ── Arguments & Usage ───────────────────────────────────────────────────────
if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.Error.WriteLine("usage: pr-review <file.cs> [--max-rounds N] [--provider dml|cpu]");
    return 64;
}

var inputPath = args[0];
var maxRounds = ArgValue("--max-rounds") is { } r && int.TryParse(r, out var parsed) ? parsed : 3;
var provider = ArgValue("--provider") ?? Environment.GetEnvironmentVariable("EDGE_PROVIDER") ?? "dml";

if (maxRounds < 1)
{
    Console.Error.WriteLine("--max-rounds must be at least 1.");
    return 64;
}

if (!File.Exists(inputPath))
{
    Console.Error.WriteLine($"File not found: {inputPath}");
    return 66;
}

var fullInputPath = Path.GetFullPath(inputPath);
var source = await File.ReadAllTextAsync(fullInputPath);

// Guard before spending ~9 seconds initializing DirectML and loading weights.
if (source.Length > MaxSourceChars)
{
    Console.Error.WriteLine(
        $"Input too large ({source.Length:N0} chars, cap {MaxSourceChars:N0}). Split the file.");
    return 65;
}

var modelPath = Environment.GetEnvironmentVariable("PHI_MODEL_PATH");
if (string.IsNullOrWhiteSpace(modelPath) || !Directory.Exists(modelPath))
{
    Console.Error.WriteLine("PHI_MODEL_PATH is not set or does not exist. See EDGE-100.2.");
    return 69;
}

// ── Model Initialization ───────────────────────────────────────────────────
Console.WriteLine($"Loading model ({provider})...");
var loadTimer = Stopwatch.StartNew();

using var config = new Config(modelPath);

// MANDATORY: Explicitly attach provider to prevent silent CPU fallback.
if (!provider.Equals("cpu", StringComparison.OrdinalIgnoreCase))
{
    config.ClearProviders();
    config.AppendProvider("dml");
    // Target adapter 0 or 1 explicitly if using dual GPUs
    config.SetProviderOption("dml", "device_id", "0");
}

using var model = new Model(config);
loadTimer.Stop();
Console.WriteLine($"Model ready in {loadTimer.Elapsed.TotalSeconds:F1}s\n");

IChatClient chat = new OnnxRuntimeGenAIChatClient(model);

// ── Execution with Graceful Cancellation ────────────────────────────────────
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (s, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    Console.Error.WriteLine("\n[!] Review cancellation requested. Exiting safely...");
};

var loop = new ReviewLoop(
    chat,
    Personas.Reviewer(),
    Personas.Revisor(),
    Personas.Sentinel,
    maxRounds);

var transcript = new StringBuilder();
var runTimer = Stopwatch.StartNew();

ReviewResult result;
try
{
    // SyncProgress ensures terminal lines are never emitted out of order.
    result = await loop.RunAsync(source, new SyncProgress<ReviewTurn>(turn =>
    {
        Console.WriteLine($"=== {turn.Role} ({turn.Elapsed.TotalSeconds:F1}s) ===");
        Console.WriteLine(turn.Content);
        Console.WriteLine();
    }), cts.Token);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("[!] Review cancelled by user.");
    (chat as IDisposable)?.Dispose();
    return 130; // Standard SIGINT exit code
}

runTimer.Stop();

// ── Report & Artifact Generation ───────────────────────────────────────────
Console.WriteLine(result.Converged
    ? $"[OK] Converged after {result.RoundsUsed} round(s) in {runTimer.Elapsed.TotalSeconds:F1}s."
    : $"[!] Round cap ({maxRounds}) reached in {runTimer.Elapsed.TotalSeconds:F1}s without approval.");

transcript.AppendLine($"# Review: {Path.GetFileName(fullInputPath)}");
transcript.AppendLine();
transcript.AppendLine($"- **Generated:** {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
transcript.AppendLine($"- **Model:** `{Path.GetFileName(modelPath)}` via `{provider}`");
transcript.AppendLine($"- **Outcome:** {(result.Converged ? "approved" : "round cap reached")}");
transcript.AppendLine($"- **Rounds:** {result.RoundsUsed} of {maxRounds}");
transcript.AppendLine($"- **Duration:** {runTimer.Elapsed.TotalSeconds:F1}s");
transcript.AppendLine();
transcript.AppendLine("---");
transcript.AppendLine();

foreach (var turn in result.Transcript)
{
    transcript.AppendLine($"## {turn.Role} ({turn.Elapsed.TotalSeconds:F1}s)");
    transcript.AppendLine();
    transcript.AppendLine(turn.Content);
    transcript.AppendLine();
}

// Extract final cleaned C# block for fast consumption
var lastRevision = result.Transcript.LastOrDefault(t => t.Role == "Revisor");
if (lastRevision is not null && CodeBlock.TryExtract(lastRevision.Content, out var finalCode))
{
    transcript.AppendLine("---");
    transcript.AppendLine();
    transcript.AppendLine("## Final revised code");
    transcript.AppendLine();
    transcript.AppendLine("```csharp");
    transcript.AppendLine(finalCode);
    transcript.AppendLine("```");
}

var outDir = Path.GetDirectoryName(fullInputPath) ?? Directory.GetCurrentDirectory();
var outPath = Path.Combine(
    outDir,
    $"review-{Path.GetFileNameWithoutExtension(fullInputPath)}-{DateTime.Now:yyyyMMdd-HHmmss}.md");

await File.WriteAllTextAsync(outPath, transcript.ToString());
Console.WriteLine($"[file] {outPath}");

(chat as IDisposable)?.Dispose();

return result.Converged ? 0 : 1;

// ── Helpers ─────────────────────────────────────────────────────────────────
string? ArgValue(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

file sealed class SyncProgress<T>(Action<T> onReport) : IProgress<T>
{
    public void Report(T value) => onReport(value);
}