using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.ML.OnnxRuntimeGenAI;

//using var oga = new OgaHandle();

var modelPath = Environment.GetEnvironmentVariable("PHI_MODEL_PATH")
    ?? throw new InvalidOperationException("PHI_MODEL_PATH not set (EDGE-100.2).");

using var config = new Config(modelPath);
config.ClearProviders();
config.AppendProvider("dml");          // mandatory - see EDGE-100
using var model = new Model(config);

// ONE client instance, TWO calls. This is the whole experiment.
IChatClient chat = new OnnxRuntimeGenAIChatClient(model);
var opts = new ChatOptions { MaxOutputTokens = 64 };

for (var i = 1; i <= 2; i++)
{
    var msgs = new List<ChatMessage> { new(ChatRole.User, "Reply with one short sentence about C#.") };
    var sw = Stopwatch.StartNew();
    var r = await chat.GetResponseAsync(msgs, opts);
    sw.Stop();
    Console.WriteLine($"call {i}: {sw.ElapsedMilliseconds} ms  ->  {r.Text.Trim()}");
}

// Verdict:
//   call2 << call1  -> per-INSTANCE warm-up. Build once, reuse. MAF is fine.
//   call2 ~= call1  -> per-CALL tax. Budget ~5s/turn, or prefer the raw loop.