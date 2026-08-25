# 🎟️ Edge.PrReviewer (`pr-review`)

An autonomous, air-gapped, on-device PR code reviewer CLI built on .NET and ONNX Runtime GenAI (DirectML / CPU). It executes an **adversarial two-agent loop** powered by a single local SLM (`Phi-4-mini`) without sending proprietary code outside your machine.

---

## 💡 How It Works

`pr-review` pits two distinct personas of the same model against each other across alternating turns with shared context:

1. **The Reviewer** (`temp: 0.1`): Cold and disciplined. Analyzes the provided source code, reporting up to 4 structured, actionable findings tagged as `[BLOCKER]`, `[MAJOR]`, or `[NIT]`. It never writes code or approves its own fixes. When all blockers and major findings are resolved, it emits the termination sentinel: `REVIEW_APPROVED`.
2. **The Revisor** (`temp: 0.3`–`0.4`): Practical implementer. Takes the reviewer's critiques and rewrites the implementation inside a clean C# code block. It cannot self-approve.

```
       ┌────────────────────────────────────────────────────────┐
       │                      Source File                       │
       └──────────────────────────┬─────────────────────────────┘
                                  │
                                  ▼
                    ┌───────────────────────────┐
                    │     Reviewer Turn 1       │
                    │   (Flags BLOCKER/MAJOR)   │
                    └─────────────┬─────────────┘
                                  │
                                  ▼
                    ┌───────────────────────────┐
                    │      Revisor Turn 1       │
                    │   (Rewrites C# Source)    │
                    └─────────────┬─────────────┘
                                  │
                                  ▼
                    ┌───────────────────────────┐
                    │     Reviewer Turn 2       │
                    │   (Emits REVIEW_APPROVED) │
                    └─────────────┬─────────────┘
                                  │
                                  ▼
                    ┌───────────────────────────┐
                    │  Exit 0 & Markdown Report │
                    └───────────────────────────┘
```

The loop automatically terminates when:
- **Approval:** The Reviewer emits `REVIEW_APPROVED` on its own line (Exit Code `0`).
- **Cap Reached:** The turn budget (`--max-rounds`, default `3`) is exhausted (Exit Code `1`).

---

## 🛠️ Prerequisites & Environment

1. **.NET 9.0 SDK** (or later).
2. **Local Model Asset:** `Phi-4-mini` ONNX INT4 model (e.g., `gpu-int4-rtn-block-32`).
3. Set the environment variable pointing to the model directory:

```powershell
# Windows PowerShell
$env:PHI_MODEL_PATH = "D:\ai-models\phi-4-mini\gpu\gpu-int4-rtn-block-32"

# Linux / macOS / Bash
export PHI_MODEL_PATH="/path/to/phi-4-mini"
```

---

## 🚀 How to Run (EDGE-101.4)

### 1. Basic Review (Default DirectML GPU)

Review a target C# source file using DirectML hardware acceleration:

```powershell
dotnet run --project Edge.PrReviewer.csproj -- .\Edge.PrReviewer\samples\Bad.cs
```

### 2. CPU Fallback Execution

Run on CPU if DirectML drivers or GPU memory constraints apply:

```powershell
dotnet run --project Edge.PrReviewer.csproj -- .\Edge.PrReviewer\samples\Bad.cs --provider cpu
```

### 3. Custom Turn Budget

Set a custom cap for max revision rounds (e.g., 2 or 5 rounds):

```powershell
dotnet run --project Edge.PrReviewer.csproj -- .\Edge.PrReviewer\samples\Bad.cs --max-rounds 2 --provider cpu
```

---

## 📋 CLI Options & Exit Codes

### CLI Syntax
```text
pr-review <file.cs> [--max-rounds N] [--provider dml|cpu]
```

| Argument | Description | Default |
|---|---|---|
| `<file.cs>` | Path to the C# source file to review (max 6,000 chars) | *Required* |
| `--max-rounds N` | Maximum number of review-revise iterations before timeout | `3` |
| `--provider` | Execution provider (`dml` for DirectML GPU, `cpu` for CPU) | `dml` (or `$env:EDGE_PROVIDER`) |

### Exit Codes (Automation & Pre-commit Ready)

| Exit Code | Meaning |
|---|---|
| `0` | **Converged** — Reviewer emitted `REVIEW_APPROVED`. |
| `1` | **Cap Reached** — Reached max rounds without complete approval. |
| `64` | **Usage Error** — Invalid parameters or negative round count. |
| `65` | **Input Too Large** — File exceeds character safety limit (6,000 chars). |
| `66` | **File Not Found** — Input path does not exist. |
| `69` | **Environment Missing** — `PHI_MODEL_PATH` is unset or invalid. |
| `130` | **Cancelled** — Execution cancelled by user (`Ctrl+C`). |

---

## 📄 Output Artifacts

Each execution generates a detailed Markdown audit transcript saved next to the target source file:
- **Filename:** `review-<FileName>-<yyyyMMdd-HHmmss>.md`
- **Contents:**
  - Execution metadata (Model, Provider, Timestamp, Duration, Total Rounds)
  - Full turn-by-turn raw transcript of Reviewer critiques and Revisor diffs
  - Extracted **Final Revised Code** block ready for copy/pasting or CI integration

---

## 🧪 Unit Testing (EDGE-101.5)

Unit tests run against a mocked `IChatClient` without loading model weights into memory, completing in under 2 seconds:

```powershell
dotnet test Edge.PrReviewer.Tests
```
