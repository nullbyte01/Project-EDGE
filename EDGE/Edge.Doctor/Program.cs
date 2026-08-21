// ============================================================================
// EDGE-100 — Sprint Zero exit gate.  DirectML build.
//
// Four checks, in dependency order:
//
//   1. Model assets   — folder + genai_config.json + declared provider
//   2. Native runtime — model loads; confirm DirectML actually came with it
//   3. Throughput     — tok/s, the number every sprint estimate depends on
//   4. IChatClient    — OnnxRuntimeGenAIChatClient, what MAF sits on
//
// Deliberately NOT here: the RAM headroom gate. That is a manual pre-flight
// step in EDGE-100.1 — a Get-CimInstance one-liner answers it natively, and a
// diagnostic tool should not carry P/Invoke to duplicate it. Nothing in this
// file uses P/Invoke, unsafe, or platform interop — managed BCL only.
//
// Self-contained by design: one project, no class library, no test project.
// Sprint Zero's job is to prove the environment works, not to build shared
// abstractions for tickets that have not started.
//
// API verified against OnnxRuntimeGenAI 0.14.1 (DirectML) / 0.15.2 (base).
// ============================================================================

using System.Diagnostics;
using System.Runtime.InteropServices;   // RuntimeInformation only (informational)
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.ML.OnnxRuntimeGenAI;

// OgaHandle owns global native init/shutdown. First constructed, last disposed.
// Skipping it produces intermittent shutdown crashes that look like GC bugs.
using var oga = new OgaHandle();

const string Reset = "\u001b[0m", Dim = "\u001b[90m",
             Green = "\u001b[32m", Red = "\u001b[31m", Yellow = "\u001b[33m";

// DDR4-2667 dual channel ~32 GB/s effective, streaming ~2.5 GB of INT4 weights
// per token, caps CPU inference near here. A GTX-class laptop GPU
// (~128-192 GB/s) should clear the upper threshold comfortably.
const double CpuCeiling = 10.0;
const double ExpectedFloor = 20.0;

var failures = 0;
void Pass(string m) => Console.WriteLine($"{Green}  PASS{Reset}  {m}");
void Fail(string m) { Console.WriteLine($"{Red}  FAIL{Reset}  {m}"); failures++; }
void Warn(string m) => Console.WriteLine($"{Yellow}  WARN{Reset}  {m}");
void Note(string m) => Console.WriteLine($"{Dim}        {m}{Reset}");
void Section(string n) => Console.WriteLine($"\n{Dim}-- {n} {new string('-', Math.Max(0, 56 - n.Length))}{Reset}");

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine($"{Dim}EDGE-100 - Sprint Zero exit gate (DirectML){Reset}");
Console.WriteLine($"{Dim}{RuntimeInformation.OSDescription.Trim()} | " +
                  $"{RuntimeInformation.ProcessArchitecture} | " +
                  $".NET {Environment.Version} | {Environment.ProcessorCount} logical cores{Reset}");
Console.WriteLine($"{Dim}RAM headroom is gated manually in EDGE-100.1, not here.{Reset}");

// ── 1. Model assets ─────────────────────────────────────────────────────────
Section("1. Model assets");

var modelPath = Environment.GetEnvironmentVariable("PHI_MODEL_PATH");
if (string.IsNullOrWhiteSpace(modelPath))
{
    Fail("PHI_MODEL_PATH not set. See EDGE-100.2 step 8:");
    Note(@"$env:PHI_MODEL_PATH = 'D:\ai-models\phi-4-mini\gpu\gpu-int4-rtn-block-32'");
    return Summarise();
}

modelPath = Path.GetFullPath(modelPath);
Note(modelPath);

if (!Directory.Exists(modelPath))
{
    Fail("Directory does not exist. Re-run the download in EDGE-100.2.");
    return Summarise();
}

// genai_config.json is what the native loader actually opens. A download whose
// --include glob matched nothing leaves a directory that exists but is empty,
// so testing the folder alone is not enough. This is the exact failure the
// vision model download hit.
var configFile = Path.Combine(modelPath, "genai_config.json");
if (!File.Exists(configFile))
{
    Fail("genai_config.json missing - the HF --include glob matched nothing.");
    Note("List what actually landed:");
    Note(@"Get-ChildItem 'D:\ai-models' -Recurse -Filter genai_config.json");
    return Summarise();
}
Pass("genai_config.json found");

var weights = new DirectoryInfo(modelPath)
    .EnumerateFiles("*.onnx*", SearchOption.TopDirectoryOnly)
    .Sum(f => f.Length);

if (weights < 100L * 1024 * 1024)
{
    Fail($"Weights total only {weights / 1024 / 1024} MB - download incomplete.");
    return Summarise();
}
Pass($"weights {weights / 1024.0 / 1024 / 1024:F2} GB");

// Provider declared by the MODEL BUILD. A cpu_and_mobile/ folder under the
// DirectML package runs silently on the CPU - no error, just 5x slower. This
// is the mismatch that check 3's numbers would otherwise blame on hardware.
var declaredProvider = ReadDeclaredProvider(configFile);
var pathSaysGpu = modelPath.Replace('\\', '/').Contains("/gpu", StringComparison.OrdinalIgnoreCase);

switch (declaredProvider)
{
    case null:
        Warn("Could not parse provider_options from genai_config.json.");
        Note("Non-fatal. Check 3's throughput will reveal what actually ran.");
        break;
    case "":
        if (pathSaysGpu)
            Warn("Path looks like a GPU build but config declares no provider.");
        else
        {
            Warn("This is a CPU model build. Under the DirectML package it will");
            Note("load fine and run entirely on the CPU. Expect ~5-8 tok/s, not 25+.");
            Note("For GPU: re-download with --include \"gpu/*\" and repoint PHI_MODEL_PATH.");
        }
        break;
    default:
        Pass($"model build declares provider: {declaredProvider}");
        if (!declaredProvider.Equals("dml", StringComparison.OrdinalIgnoreCase))
            Warn($"'{declaredProvider}' build with the DirectML package - verify this is intended.");
        break;
}

// ── 2. Native runtime ───────────────────────────────────────────────────────
Section("2. Native runtime");

Model? model = null;
try
{
    var sw = Stopwatch.StartNew();
    using var config = new Config(modelPath);
    model = new Model(config);
    Pass($"model loaded in {sw.ElapsedMilliseconds} ms");
}
catch (DllNotFoundException ex)
{
    Fail($"Native library missing: {ex.Message}");
    Note("Usual cause: two OnnxRuntimeGenAI provider packages installed at once.");
    Note("Verify: dotnet list package --include-transitive | findstr OnnxRuntimeGenAI");
    Note("Keep exactly one of: base / .DirectML / .Cuda / .WinML");
    return Summarise();
}
catch (Exception ex)
{
    Fail($"{ex.GetType().Name}: {ex.Message}");
    if (ex.Message.Contains("memory", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("alloc", StringComparison.OrdinalIgnoreCase))
    {
        Note("Allocation failure. Two likely causes on a 4 GB VRAM machine:");
        Note("  - something else holds GPU memory (Chrome hardware accel)");
        Note("  - host RAM is exhausted; re-run the EDGE-100.1 pre-flight");
    }
    return Summarise();
}

// Which native providers actually landed in this process. ProcessModule is
// plain managed BCL - no interop needed to read the loaded module list.
var directMlLoaded = Process.GetCurrentProcess().Modules
    .Cast<ProcessModule>()
    .Any(m => (m.ModuleName ?? "").Contains("DirectML", StringComparison.OrdinalIgnoreCase));

if (directMlLoaded) Pass("DirectML.dll loaded");
else Warn("DirectML.dll not loaded - this process is running on the CPU.");

// ── 3. Throughput ───────────────────────────────────────────────────────────
Section("3. Throughput");

Note("Watch Task Manager > Performance > GPU 1 now. Flat 0% during");
Note("generation means DirectML chose the integrated Radeon, not the GTX.");

var output = "";
try
{
    using var tokenizer = new Tokenizer(model);
    using var stream = tokenizer.CreateStream();

    // Phi-4-mini / Phi-3.x template. A wrong template does not throw - it
    // degrades quietly into rambling or prompt echo, so the reply is checked
    // against an exact expected word below.
    var sequences = tokenizer.Encode(
        "<|user|>Reply with exactly the word: READY<|end|><|assistant|>");

    using var genParams = new GeneratorParams(model);
    genParams.SetSearchOption("max_length", 64);

    using var generator = new Generator(model, genParams);
    generator.AppendTokenSequences(sequences);   // 0.6+ API; replaced SetInputSequences

    var sw = Stopwatch.StartNew();
    var buffer = new StringBuilder();
    var generated = 0;

    while (!generator.IsDone())
    {
        generator.GenerateNextToken();           // 0.6+ folded in ComputeLogits
        buffer.Append(stream.Decode(generator.GetSequence(0)[^1]));
        generated++;
    }
    sw.Stop();

    output = buffer.ToString().Trim();
    var tps = generated / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
    Pass($"{generated} tokens in {sw.Elapsed.TotalSeconds:F1}s -> {Green}{tps:F1} tok/s{Reset}");
    Note($"model said: \"{Truncate(output, 60)}\"");

    // Landing in the CPU band while on the DirectML package means something
    // upstream is wrong, not that the GPU is slow.
    if (tps < CpuCeiling)
    {
        Warn($"{tps:F1} tok/s is CPU-range. The GPU is probably not being used.");
        Note("Check, in order: (a) is PHI_MODEL_PATH the gpu/ folder, not");
        Note("cpu_and_mobile/  (b) did DirectML.dll load in check 2");
        Note("(c) did GPU 1 move in Task Manager  (d) is host RAM under pressure.");
        Note($"At this rate a 400-token turn takes ~{400 / tps:F0}s, so");
        Note($"EDGE-101's six-turn loop would run ~{6 * 400 / tps / 60:F0} minutes.");
        Note($"To keep that loop under 5 min, cut MaxTokens to ~{(int)(300 * tps / 6)}.");
    }
    else if (tps < ExpectedFloor)
    {
        Warn($"{tps:F1} tok/s - GPU active but below expectation. Keep token budgets tight.");
    }
    else
    {
        Pass($"{tps:F1} tok/s is in the expected DirectML band. Sprint estimates hold.");
    }
}
catch (Exception ex)
{
    Fail($"Generation failed - {ex.GetType().Name}: {ex.Message}");
    Note("If this mentions ComputeLogits or SetInputSequences you are on a");
    Note("pre-0.6 package. Pin OnnxRuntimeGenAI.DirectML 0.14.1.");
}

if (output.Length > 0)
{
    if (output.Contains("READY", StringComparison.OrdinalIgnoreCase))
        Pass("chat template applied correctly");
    else
    {
        Warn("Model ignored a trivial instruction - chat template likely wrong.");
        Note("Expected: <|user|>...<|end|><|assistant|>");
    }
}

// ── 4. IChatClient bridge ───────────────────────────────────────────────────
Section("4. IChatClient bridge (Agent Framework foundation)");

try
{
    // OnnxRuntimeGenAIChatClient ships inside Microsoft.ML.OnnxRuntimeGenAI
    // itself and implements Microsoft.Extensions.AI.IChatClient directly.
    // No Semantic Kernel connector needed - this is what removed two alpha
    // packages from the sprint, and it is exactly what MAF consumes:
    //   new ChatClientAgent(chatClient, name: ..., instructions: ...)
    //
    // NOTE: no .AsBuilder() here. That extension lives in the FULL
    // Microsoft.Extensions.AI package; only Microsoft.Extensions.AI.Abstractions
    // arrives transitively via OnnxRuntimeGenAI.Managed, and Abstractions is
    // where IChatClient/ChatOptions/ChatMessage live. The builder pipeline
    // exists to compose middleware (function invocation, caching, telemetry) -
    // a one-shot smoke test needs none of it, so ChatOptions is both simpler
    // and one fewer dependency.
    IChatClient chat = new OnnxRuntimeGenAIChatClient(model);

    // Calling the interface member directly rather than the string convenience
    // overload: fewer extension-method resolution surprises in a gate whose
    // entire job is to fail with clear messages.
    var probe = new List<ChatMessage>
    {
        new(ChatRole.User, "Reply with exactly the word: BRIDGE")
    };

    var sw = Stopwatch.StartNew();
    var response = await chat.GetResponseAsync(
        probe,
        new ChatOptions { MaxOutputTokens = 64 });
    sw.Stop();

    Pass($"IChatClient responded in {sw.ElapsedMilliseconds} ms");
    Note($"model said: \"{Truncate(response.Text.Trim(), 60)}\"");
    Pass("ChatClientAgent (Microsoft.Agents.AI) is wireable on this machine");
}
catch (Exception ex)
{
    Fail($"Bridge failed - {ex.GetType().Name}: {ex.Message}");
    Note("EDGE-101/103/104 depend on this. Fall back to the raw Generator");
    Note("loop from check 3 if it cannot be resolved.");
}

// If the chat client took ownership of the model this double-disposes; the
// OnnxRuntimeGenAI docs are not explicit about it. Drop this line if you see
// an ObjectDisposedException on exit.
model.Dispose();
return Summarise();

// ── helpers ─────────────────────────────────────────────────────────────────
int Summarise()
{
    Console.WriteLine();
    if (failures == 0)
    {
        Console.WriteLine($"{Green}GREEN - Sprint Zero complete. EDGE-101..105 unblocked.{Reset}");
        Note("Record the tok/s figure in the EDGE-100 ticket before estimating.");
        return 0;
    }
    Console.WriteLine($"{Red}RED - {failures} check(s) failed. Do not start EDGE-101.{Reset}");
    return 1;
}

// Reads the provider the MODEL BUILD declares. Shape is
//   model.decoder.session_options.provider_options: [ { "dml": {} } ]
// with an empty array for CPU builds. Returns "" for CPU, null if unparseable.
static string? ReadDeclaredProvider(string configPath)
{
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
        var opts = doc.RootElement
            .GetProperty("model").GetProperty("decoder")
            .GetProperty("session_options").GetProperty("provider_options");

        if (opts.ValueKind != JsonValueKind.Array) return null;

        foreach (var entry in opts.EnumerateArray())
            foreach (var prop in entry.EnumerateObject())
                return prop.Name;      // "dml" | "cuda" | ...

        return "";                     // present but empty -> CPU build
    }
    catch { return null; }
}

static string Truncate(string s, int n) =>
    s.Length <= n ? s.ReplaceLineEndings(" ") : s[..n].ReplaceLineEndings(" ") + "...";
