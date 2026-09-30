# Knowledge Capture — on-device interviewer with irreversible anonymization

An employee chats with a local LLM on their Copilot+ PC. The assistant **interviews** them — one targeted question at a
time — to draw out how they actually do their job (goal, steps, tools, decisions, pitfalls, a concrete example, tips).
Every turn is **anonymized on the device** and stored as a fine-tuning-ready dataset. Nothing leaves the PC: all
inference runs on the **NPU** through **Foundry Local**; there are no cloud calls.

**Wow moment:** the side panel shows the coverage checklist filling up live, and "What gets stored" shows each turn's
anonymized text with the `<PERSON_1>`-style placeholders highlighted.

## Prerequisites

| | Tested with |
|---|---|
| Windows 11 on a Copilot+ PC with an NPU | Intel Core Ultra 7 258V (Intel AI Boost NPU, driver 32.0.100.5540). Target hardware Snapdragon X works the same way (QNN NPU builds). |
| .NET SDK 8 or newer + .NET 8 runtime | SDK 10.0.401 building `net8.0` |
| Foundry Local **0.10+** | 0.10.3 — `winget install Microsoft.FoundryLocal` (0.7 is also supported for discovery) |
| An NPU chat model in the Foundry cache | `phi-4-mini-instruct-openvino-npu:1` (Intel). On Snapdragon: `foundry model list --device npu` |

Model choice: the playbook default is `phi-3.5-mini`, but **Foundry offers no NPU build of phi-3.5-mini on this device**,
so the app ships with `"Model": "phi-4-mini"` (the only NPU chat model in the catalog here; it is also better at one-question
replies and JSON). The app is **NPU-strict** (`"Device": "npu"`): it refuses GPU/CPU variants and tells you what to load.

## Run

```powershell
foundry model download phi-4-mini-instruct-openvino-npu:1   # once (≈3.6 GB); pick the NPU variant for your device
.\run.ps1                                                    # starts Foundry, loads the NPU model, runs the app
.\run.ps1 -Release
```

`run.ps1` detects the native architecture (ARM64 on Snapdragon, x64 on Intel/AMD) and builds for it. Manual equivalent:

```powershell
foundry server start
foundry model load phi-4-mini-instruct-openvino-npu:1
dotnet run --project App\App.csproj -p:Platform=x64      # or -p:Platform=ARM64 on Snapdragon
```

Publish a self-contained exe: `dotnet publish App\App.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained` (use `ARM64`/`win-arm64` on Snapdragon).

### Configuration (`App/appsettings.json`)

| Key | Meaning |
|---|---|
| `Llm.Provider` / `BaseUrl` | `foundry` (port is dynamic → auto-discovered via `foundry server status -o json`), `geniex` (`127.0.0.1:18181`), or `custom` with a localhost `BaseUrl`. Non-local endpoints are refused in code. |
| `Llm.Model` / `Llm.Device` | Alias matched against the **loaded** Foundry variant id (`…-npu:1`); `Device: npu` is strict. |
| `Llm.ContextTokens` | Prompt budget (default 3000), automatically capped at 80 % of the model's real prompt limit (read from the model's `genai_config.json` — the Intel NPU build is **1024 prompt tokens**). |
| `Interview.Topics` | Topics offered for a new conversation. |
| `Interview.ReplyLanguage` | `English` (default) or `auto` (reply in the employee's language). phi-4-mini understands Romanian well but writes it poorly. |
| `Anonymization.Projects` / `Clients` | Deny-list (internal project and client names) → `<PROJECT_n>` / `<CLIENT_n>`. |
| `Anonymization.KeepTerms` | Tool/product names that are never anonymized (SAP, Excel, Jira…). Add your tools here. |
| `Anonymization.CapitalizedNameFallback` | Deterministic proper-noun safety net (default **on**, see below). |

## Architecture

```
App.Core/            net8.0 class library — everything testable, no UI
  Services/LlmService.cs         Foundry discovery, loaded-variant resolution, NPU limits, warm-up, streaming, priority gate
  Services/InterviewSession.cs   one interview: prompt building, streaming, reply guard, coverage, rolling summary, storage
  Services/Prompts.cs            every prompt (short, one example each)
  Services/Anonymization/*       deterministic recognizers, LLM recognizer, proper-noun safety net, placeholder map, pipeline
  Services/ConversationStore.cs  SQLite (Microsoft.Data.Sqlite) — accepts only AnonText
  Services/ExportService.cs      chat-format JSONL
App/                 WinUI 3 (unpackaged, Mica, MVVM Toolkit): Views (XAML) → ViewModels → App.Core
App.SelfTest/        headless check path used by leakcheck.ps1 (same App.Core code the UI runs)
```

The chat call streams tokens straight into the bubble. Everything else — coverage classification, anonymization,
storage, title, rolling summary — runs **after** the reply in one ordered background chain, so it never delays the
answer. A small priority gate serializes calls to the single NPU and lets the chat stream jump ahead of queued
background calls.

## How the interview works (elicitation)

Deterministic C# drives the conversation; the model only writes the words.

* **Topic → opening question.** The first assistant message is generated for the chosen topic.
* **Coverage checklist** (Goal/Context, Steps, Tools & Systems, Decisions & Criteria, Exceptions & Pitfalls, Concrete
  Example, Tips for a New Colleague). After each answer a separate JSON call (temperature 0.2, ≤ 80 tokens, schema +
  example) says which slots the answer covered. C# then **corroborates** the claims with cheap evidence checks (e.g.
  "Concrete Example" needs a time reference or a number with a unit; "Decisions" needs a threshold or a condition +
  action) because small models over-claim. Parse failure → one retry → coverage unchanged.
* **System prompt rebuilt every turn** in C#: interviewer role, topic, still-missing slots, the *next target slot* (chosen
  in C#), a follow-up instruction when the last answer was short (< 12 words, max one follow-up in a row), the reply
  language (Romanian/English detected in C#), tools the employee named so far, and the rolling summary.
* **Reply guard** (deterministic): the stream is stopped right after the first `?` (exactly one question); a reply
  without a question gets a canned question for the target slot; a question that asks for personal data (name, contact,
  ID, salary, health, …; EN + RO patterns) is replaced by the canned question.
* **All slots covered →** every reply is a structured summary of what was captured plus one confirm/correct question.

## How anonymization works

Runs after each response finishes streaming, **on both the employee's message and the model's reply** (the model can
echo PII), and on titles and rolling summaries. Pipeline (`Anonymizer.AnonymizeAsync`):

1. **Deterministic recognizers** (code, not the LLM): email, URL/domain, IPv4/IPv6, **Romanian CNP with checksum**
   (invalid checksums are not CNPs), **RO IBAN with mod-97**, Romanian phones (+40 / 0040 / 07xx / 02x-03x, every
   format; adjacent numbers are split correctly) and international `+` numbers, **CUI/CIF** (keyword context, or `RO`
   prefix + checksum), **payment cards (Luhn)**, the configurable **deny-list** (diacritic-insensitive), plus
   title/self-introduction name boosters ("Domnul Radu …", "my name is …").
2. **Sweep of values already seen** in this conversation (keeps `<PERSON_1>` consistent, catches echoes).
3. **LLM recognizer** for PERSON / ORGANIZATION / LOCATION: short prompt with an English and a Romanian example,
   temperature 0.2, capped tokens, sentence-sized chunks of the *natural* text; fences and `<think>` stripped, first
   `[...]` extracted and deserialized, one retry; a truncated or over-long answer is split and re-asked. **Still invalid →
   the turn is marked "anonymization failed" and is NOT stored.** Entities not literally present in the text are ignored.
4. **Proper-noun safety net** (deterministic, `CapitalizedNameFallback`): capitalized word runs that are not
   sentence-initial common words, calendar words, headings/department words or KeepTerms are anonymized even if the model
   missed them — typed by small gazetteers (first names → PERSON, cities/countries → LOCATION, "Banca/Bank/SRL…" →
   ORG), otherwise `<NAME_n>`.
5. **Final sweep** incl. single name tokens ("Popescu" after "Maria Popescu", diacritic-insensitive).
6. **Verification:** the deterministic recognizers must find nothing and no known original value may survive —
   otherwise fail closed.

Placeholders are typed and numbered (`<PERSON_1>`, `<EMAIL_1>`, `<IBAN_1>`) and consistent within one conversation.
The **mapping lives only in memory** (`PlaceholderMap`), is never serialized/logged/stored, and is cleared when the
conversation closes or the app exits — so stored data is **irreversibly** anonymized.

Guarantees in code:
* `ConversationStore` only accepts `AnonText`, a type that only the anonymizer can create → raw text cannot reach SQLite.
* A turn (employee message + reply) is stored atomically, and only if both anonymizations succeeded.
* The log (`%LOCALAPPDATA%\KnowledgeCapture\logs`) is metadata-only: counts, timings, ids and exception *type names*.
* Employee identity = salted SHA-256 of `DOMAIN\user`; the random salt is DPAPI-protected (CurrentUser) in a separate file.
* Opt-out toggle: when off, nothing is stored; the conversation is memory-only.

### Storage

SQLite at `%LOCALAPPDATA%\KnowledgeCapture\knowledge.db` (`journal_mode=DELETE`, `secure_delete=ON`):
`conversations` (title, topic, coverage slots, rolling summary, employee hash — all anonymized), `messages` (role,
anonymized content, timestamp, model id, anonymizer version, entity counts), `settings` (opt-out).

### Resuming a conversation

Stored anonymized turns become the model context (placeholders included). The original mapping no longer exists, so new
PII continues numbering after the highest stored index (after `<PERSON_4>` comes `<PERSON_5>`); an InfoBar explains that
resumed conversations show anonymized content. Titles are generated from anonymized text only.

### Context budget

Tokens are estimated in C# (~3 chars/token — deliberately conservative: Romanian text and placeholders tokenize densely,
and the static-shape NPU build rejects any prompt over 1024 tokens). When the window nears the budget, older turns are
folded into a **rolling summary** made from the *anonymized* turns (and anonymized again before use/storage); the coverage
checklist and the list of tools named keep what was already captured. If the server still reports "prompt too long", the
call is retried with half the history (chat), smaller chunks (NER) or a shorter input (coverage/summary). Answers are
capped at what fits (≈1 300 characters with the 1024-token NPU build).

## Export

**Export** page → date range, minimum answers, minimum coverage → FileSavePicker → chat-format JSONL:

```json
{"messages":[{"role":"assistant","content":"…"},{"role":"user","content":"I'm <PERSON_1> …"}],"metadata":{"topic":"A process I own","conversation_id":"…","coverage":["goal","steps"],"coverage_percent":29,"model_id":"phi-4-mini-instruct-openvino-npu:1","anonymizer_version":"anon-1.0+det+llm:…+caps"}}
```

System prompts are never stored, so they are never exported.

## Verification

```powershell
.\leakcheck.ps1                                              # scripted interview + all self-tests + file scan (needs the NPU model)
App.SelfTest\bin\Debug\net8.0-windows\KnowledgeCapture.SelfTest.exe --offline   # deterministic tests only, no model
.\leakcheck.ps1 -SkipRun -DataDir "$env:LOCALAPPDATA\KnowledgeCapture"          # scan your real data folder
```

`leakcheck.ps1` runs the headless self-test (scripted 5-turn interview with the 3 PII-rich demo inputs, a repeat person,
a memory probe, a resumed turn, an over-budget conversation, a JSONL export), then searches **every file** it wrote —
SQLite database, logs, export — for **every original PII value** (UTF-8 and UTF-16, case-insensitive). Zero matches
required. See [Results](#results).

## Results

Measured 2026-09-30 on an Intel Core Ultra 7 258V, `phi-4-mini-instruct-openvino-npu:1` on the NPU, via `leakcheck.ps1`.
Changes made after this run and **not yet re-run**: retry with backoff when Foundry is busy, English as the default reply
language, fewer/lower-temperature rolling summaries, and the fix to the resume-numbering test.

| Check | Result |
|---|---|
| **Leak check** (every written file × every PII value) | **PASSED — 0 matches** for 43 PII values in 6 files (`knowledge.db`, `export.jsonl`, logs, salt, offline test db/jsonl) |
| Self-test (offline + NPU) | 86 / 87 passed. The one failure was a bug in the test (the expected placeholder index ignored placeholders inside the rolling summary); fixed afterwards |
| Deterministic anonymization tests (`--offline`) | 65 / 65 — valid vs. invalid CNP / RO IBAN / CUI / Luhn, every phone format incl. adjacent numbers, deny-list with diacritics, RO + EN names, PII echoed in replies, same person across turns, numbering continuation on resume, fail-closed |
| LLM recognizer on the 3 demo inputs | all fully anonymized (5, 7 and 8 entities) |
| R1 exactly one question per reply | final **100 %** (5/5) · raw model output 60 % (3/5 — the guard cut extra text once and added a missing question once) |
| R2 no request for personal data | final **100 %** (6/6) · raw 100 % |
| R3 coverage increases across turns | slots 0 → 3 → 5 → 6 → 7 → 7 (increased after 4 of 5 answers) |
| R4 summary once all slots are covered | **1 / 1** — first summary right after full coverage, final reply is a summary |
| Memory (turn-1 fact in turn 5) | turn-5 reply names "SAP"; the rolling summary also keeps it |
| Context budget | a conversation over a 450-token budget completed via 3 rolling-summary updates; the stored summary is anonymized |
| NPU performance | first token 0.9–1.7 s, 12–18 tokens/s; background work (NER, coverage, summary) ≈ 20–40 s per turn |
| Cloud AI URLs in source | 0 |
| Builds | Debug x64 + ARM64, `dotnet publish` Release win-x64 + win-arm64 (self-contained) |

## Known limitations

* **LLM-based NER can miss entities.** The deterministic recognizers, the sweep and the proper-noun safety net catch
  what the small model misses in our tests, but lower-case names ("am vorbit cu maria") or a surname alone at the start of
  a sentence that never appeared before can still slip through. Treat the dataset as *pseudonymized*, review it before
  use, and extend `Clients` / `Projects` with your own names.
* **Over-anonymization.** The safety net replaces unknown capitalized product names (e.g. "Tableau") with `<NAME_n>`;
  add such tools to `KeepTerms`. Common words that are also names (e.g. "Mark") are replaced after a person with that
  name was mentioned.
* **Small models may ask two questions or none.** The reply guard enforces exactly one question per reply; the raw model
  compliance is reported by the self-test.
* **Romanian replies** from phi-4-mini on the NPU are often ungrammatical, so the interviewer replies in English by
  default (`Interview.ReplyLanguage`); Romanian *answers* are understood and anonymized fine.
* **Small models can invent details in summaries** (rolling summary, final summary). Summaries use temperature 0.2 and
  "only facts stated" instructions, but review summaries before relying on them.
* **NPU prompt limit (1024 tokens).** Long answers must be split; the rolling summary kicks in after 1-2 turns, and
  summaries of summaries can drop details (tools are kept deterministically).
* The same person in a *resumed* conversation gets a new placeholder number — by design, since the mapping is gone.
* First NPU load of a model can take tens of seconds; the app shows "Loading model on NPU…" meanwhile.

## Troubleshooting

* **"No NPU model is available"** → `foundry model list --device npu --variants`, then `foundry model load <variant id>`.
* **Model loads fail with "No OpenVINO devices matched … NPU"** (Intel) → the NPU driver's Level Zero component is not
  active; typically a staged NPU driver update is waiting for a **Windows restart**.
* **Foundry 0.10 renamed `foundry service` to `foundry server`**; the app supports both.
* Runtime down during a chat → red pill + InfoBar with **Retry**; the unsent answer stays in the input box.
