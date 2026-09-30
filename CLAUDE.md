# Hackathon Build Playbook — Local AI on the NPU

You are building a demo-ready **C# .NET 8 + WinUI 3 desktop app** **fast** from a one-line idea. Speed and a working demo beat completeness.
All AI inference runs **locally on the Snapdragon NPU** via **Foundry Local** (primary) or **Qualcomm GenieX** (fallback). No cloud AI calls. Ever.

## 0. Operating rules (read first)

- **Don't ask questions unless blocked.** Make sensible assumptions, list them in 3 bullets, then build.
- **Timebox:** plan ≤ 5 min → working vertical slice ≤ 30 min → polish ≤ 15 min → demo script.
- **One vertical slice first:** idea → input → local LLM call → rendered result. Only then add features.
- **.NET + WinUI 3 only.** No Python, no web backend, no HTML/JS frontend, no npm.
- **Streaming responses** always (tokens appearing live looks great in a demo and hides NPU latency).
- **Never block on the model:** if the LLM endpoint is down, show a clear banner + retry button, not a stack trace.
- Commit (if git exists) after each working milestone.
- Finish with `README.md` (run steps) and `DEMO.md` (60-second demo script + 3 sample inputs that show off the app).

## 1. Architecture (fixed — don't deliberate)

- **C# .NET 8 · WinUI 3 (Windows App SDK) · unpackaged desktop app · MVVM (CommunityToolkit.Mvvm).**
- The app calls the local LLM **directly in-process** through the official `OpenAI` NuGet client pointed at Foundry Local / GenieX on localhost. No ASP.NET, no web server, no WebView UI.
- Layers: `Views` (XAML, no logic) → `ViewModels` (state + commands) → `Services` (LlmService, Prompts, JSON helpers, file/data services) → `Models` (records/DTOs).
- If the idea extends **our existing .NET app**: add `LlmService` + the `Llm` config section + a new Page/ViewModel. Don't restructure the solution.

## 2. Local model runtime (NPU)

Both runtimes expose an **OpenAI-compatible API**, so the app uses a plain OpenAI client with a configurable base URL. Config in `appsettings.json` (copied to output):

```json
{ "Llm": { "Provider": "foundry", "BaseUrl": "", "Model": "phi-3.5-mini", "ApiKey": "local" } }
```
`Provider`: foundry | geniex | custom. `BaseUrl` empty = auto-discover. `ApiKey`: any non-empty string.

### Foundry Local (primary)
```powershell
foundry model list                 # pick a model whose Device column = NPU (QNN build)
foundry model run phi-3.5-mini     # downloads + loads + starts service (NPU variant auto-selected when available)
foundry service status             # prints the endpoint — the PORT IS DYNAMIC, never hardcode it
```
- Good NPU picks: `phi-3.5-mini` (fast, general), `phi-4-mini`, `qwen2.5-7b` / `deepseek-r1-7b` (smarter, slower). Check `foundry model list` for what has an NPU build on this machine.
- **Endpoint discovery order:** `Llm.BaseUrl` → Foundry C# SDK (`Microsoft.AI.Foundry.Local`, optional) → parse `foundry service status` output for `http://127.0.0.1:<port>` → fail with a helpful message.
- The model id sent in requests must be the **full model id** (e.g. `Phi-3.5-mini-instruct-qnn-npu:1`), not the alias. Resolve it via the SDK or `GET {base}/models`.

### GenieX (fallback / VLM)
```powershell
geniex pull ai-hub-models/Qwen3-4B-Instruct-2507
geniex serve                       # http://127.0.0.1:18181/v1  (fixed port)
geniex model list
```
- Use GenieX when Foundry lacks the model, or for **vision** (e.g. Qwen2.5-VL) — send images as OpenAI `image_url` data URIs.

### Health check at startup
On boot, call `GET {base}/models`. Log provider, base URL, model id. Show a small "● NPU · phi-3.5-mini" status pill in the UI header.

## 3. Project template — WinUI 3

WinUI 3 is the UI. The app calls the local LLM **directly** (OpenAI client → Foundry/GenieX on localhost). No ASP.NET, no HTML.

Solution file: `dotnet new sln -n App; dotnet sln add App/App.csproj` (optional, helps VS users).

### Project layout
```
App/
  App.csproj
  App.xaml / App.xaml.cs            # creates MainWindow, sets up DI (optional)
  MainWindow.xaml / .cs             # Mica backdrop, custom title bar, hosts a Frame or single page
  Views/MainPage.xaml / .cs         # UI, x:Bind to ViewModel
  ViewModels/MainViewModel.cs       # CommunityToolkit.Mvvm: [ObservableProperty], [RelayCommand]
  Services/LlmService.cs            # discovery + OpenAI ChatClient + streaming
  Services/Prompts.cs
  Services/JsonHelpers.cs           # defensive JSON extraction (§4)
  appsettings.json                  # "Llm": { "Provider", "BaseUrl", "Model", "ApiKey" } (Copy to output)
run.ps1                             # foundry model run ...; dotnet run -p:Platform=ARM64
```

### Create the project (no Visual Studio template needed — write the csproj by hand)
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <TargetPlatformMinVersion>10.0.17763.0</TargetPlatformMinVersion>
    <RootNamespace>App</RootNamespace>
    <Platforms>ARM64;x64</Platforms>
    <RuntimeIdentifiers>win-arm64;win-x64</RuntimeIdentifiers>
    <UseWinUI>true</UseWinUI>
    <WindowsPackageType>None</WindowsPackageType>              <!-- unpackaged = fastest F5, no MSIX -->
    <WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained> <!-- no runtime install needed -->
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.WindowsAppSDK" Version="1.*" />
    <PackageReference Include="Microsoft.Windows.SDK.BuildTools" Version="10.*" />
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.*" />
    <PackageReference Include="OpenAI" Version="2.*" />
    <!-- optional: <PackageReference Include="Microsoft.AI.Foundry.Local" Version="*" /> -->
    <!-- optional: <PackageReference Include="CommunityToolkit.WinUI.Controls.SettingsControls" Version="8.*" /> -->
  </ItemGroup>
  <ItemGroup>
    <Content Include="appsettings.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```
- Build/run: `dotnet build -p:Platform=ARM64` then `dotnet run -p:Platform=ARM64`. After first restore, pin the floating versions to what resolved.
- If the team prefers Visual Studio: "Blank App, Packaged (WinUI 3 in Desktop)", then switch to unpackaged via the two properties above.

### LlmService (core)
```csharp
using OpenAI; using OpenAI.Chat; using System.ClientModel;
using System.Diagnostics; using System.Text.RegularExpressions;

public sealed class LlmService
{
    private ChatClient? _chat;
    public string BaseUrl { get; private set; } = "";
    public string Model { get; private set; } = "";
    public string Provider { get; private set; } = "foundry";

    public async Task InitAsync(LlmOptions o)     // call once at startup, off the UI thread
    {
        Provider = o.Provider ?? "foundry";
        BaseUrl = !string.IsNullOrWhiteSpace(o.BaseUrl) ? o.BaseUrl!
                : Provider == "geniex" ? "http://127.0.0.1:18181/v1"
                : await DiscoverFoundryAsync();
        var client = new OpenAIClient(new ApiKeyCredential(o.ApiKey ?? "local"),
                                      new OpenAIClientOptions { Endpoint = new Uri(BaseUrl) });
        var ids = (await client.GetOpenAIModelClient().GetModelsAsync()).Value.Select(m => m.Id).ToList();
        var alias = o.Model ?? "phi-3.5-mini";
        Model = ids.FirstOrDefault(i => i.Contains(alias, StringComparison.OrdinalIgnoreCase)) ?? ids.FirstOrDefault() ?? alias;
        _chat = client.GetChatClient(Model);
        await foreach (var _ in StreamAsync([new UserChatMessage("hi")], maxTokens: 1)) { } // NPU warm-up
    }

    static async Task<string> DiscoverFoundryAsync()
    {
        var psi = new ProcessStartInfo("foundry", "service status")
                  { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        var m = Regex.Match(await p.StandardOutput.ReadToEndAsync(), @"http://127\.0\.0\.1:\d+");
        return m.Success ? m.Value + "/v1"
             : throw new InvalidOperationException("Foundry Local not running. Run: foundry model run phi-3.5-mini");
    }

    public async IAsyncEnumerable<string> StreamAsync(IEnumerable<ChatMessage> msgs, float temperature = 0.4f,
        int maxTokens = 800, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var opts = new ChatCompletionOptions { Temperature = temperature, MaxOutputTokenCount = maxTokens };
        await foreach (var u in _chat!.CompleteChatStreamingAsync(msgs, opts, ct))
            foreach (var part in u.ContentUpdate) yield return part.Text;
    }
}
public record LlmOptions(string? Provider, string? BaseUrl, string? Model, string? ApiKey);
```

### ViewModel streaming pattern
```csharp
public partial class MainViewModel(LlmService llm) : ObservableObject
{
    [ObservableProperty] public partial string Input { get; set; } = "";
    [ObservableProperty] public partial string Output { get; set; } = "";
    [ObservableProperty] public partial string Stats { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task RunAsync(CancellationToken ct)
    {
        Output = ""; Error = null; IsBusy = true;
        var sw = Stopwatch.StartNew(); double? ttft = null; int tokens = 0;
        try
        {
            var sb = new StringBuilder();
            await foreach (var t in llm.StreamAsync([new SystemChatMessage(Prompts.Main), new UserChatMessage(Input)], ct: ct))
            {
                ttft ??= sw.Elapsed.TotalSeconds; tokens++;
                sb.Append(t); Output = sb.ToString();          // continuation resumes on UI thread → safe
            }
            Stats = $"TTFT {ttft:0.00}s · {tokens / Math.Max(0.01, sw.Elapsed.TotalSeconds - (ttft ?? 0)):0.0} tok/s · {llm.Model}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsBusy = false; }
    }
}
```
(If the CommunityToolkit.Mvvm version lacks partial-property support, use `[ObservableProperty] private string input = "";` fields instead.)
Update UI from background threads only via `DispatcherQueue.TryEnqueue(...)`.

### WinUI UI spec (functional + good-looking, fast)
- **Window:** `SystemBackdrop = new MicaBackdrop();` `ExtendsContentIntoTitleBar = true;` + a simple custom title bar (app icon + name). Set a sensible default size (e.g. 1100×750) via `AppWindow.Resize`.
- **Layout:** single page by default; `NavigationView` (Left, compact) only if there are 3+ features. Content in a `Grid` with 24px padding, max content width ~960, cards = `Border` with `CornerRadius="8"`, `Background="{ThemeResource CardBackgroundFillColorDefaultBrush}"`, `BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}"`.
- **Use built-in Fluent controls only:** `TextBox` (AcceptsReturn, TextWrapping=Wrap), `Button Style="{StaticResource AccentButtonStyle}"`, `ProgressRing`/`ProgressBar IsIndeterminate`, `InfoBar` for errors/runtime-down, `TeachingTip` for hints, `ComboBox` for mode/model select, `ToggleSwitch`, `ListView` for results, `Expander` for "Reasoning" (`<think>` content), `CommandBar` for actions (Copy, Export, Clear).
- **Header status:** `InfoBadge`/ellipse + text "● NPU · {Model}" (green when healthy, red + retry when not).
- **Output:** selectable `TextBlock` (IsTextSelectionEnabled) inside a `ScrollViewer`; for markdown-ish output render headings/bullets with a small converter into `RichTextBlock` paragraphs — no WebView2 unless really needed.
- **Theme:** respect system light/dark (don't hardcode colors — use ThemeResources). Typography via built-in styles: `TitleTextBlockStyle`, `SubtitleTextBlockStyle`, `BodyTextBlockStyle`, `CaptionTextBlockStyle`.
- **Example chips:** 3 `Button`s in a horizontal `StackPanel` that fill `Input` with demo inputs.
- **Footer:** `Stats` (TTFT, tok/s, model) in `CaptionTextBlockStyle`.
- Run button bound to `RunCommand`, Stop button to `RunCancelCommand`; disable input while `IsBusy`.

### WinUI gotchas (save time)
- **Pickers in WinUI 3 desktop need the window handle:** `WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));`
- Drag & drop: set `AllowDrop="True"` and handle `DragOver` (set `AcceptedOperation`) + `Drop`.
- `x:Bind` defaults to `OneTime` — use `Mode=OneWay` for streaming output, `TwoWay` for inputs.
- Build for **ARM64** (`-p:Platform=ARM64`); x64 builds run emulated and slower.
- Don't call `InitAsync` in the constructor synchronously — do it in `MainWindow.Activated`/page `Loaded`, show a ProgressRing "Loading model on NPU…" meanwhile.
- XAML compiler errors are vague: if the build fails with `XamlCompiler.exe exited with code 1`, check the latest XAML edit for typos/unknown namespaces first.
- Reading `appsettings.json`: `System.Text.Json` deserialize from `AppContext.BaseDirectory`; skip Microsoft.Extensions.Configuration unless already used.

## 4. Getting reliable output from small local models

Small NPU models (3–7B) are the main risk. Design around them:

- **Short, explicit system prompts.** Role + task + output format + 1 example. Keep all prompts in `Services/Prompts.cs`.
- **Structured output:** ask for JSON with an explicit schema + one example; then parse defensively (strip ``` fences, extract first `{...}`/`[...]`, validate with `System.Text.Json` into a record, retry once with "Return ONLY valid JSON" on failure, then fall back to showing raw text).
- **Chunk big inputs** (docs, logs, transcripts): split ~1,500 tokens, summarize per chunk, then combine (map-reduce). Context windows are small.
- **Do deterministic work in code**, not in the LLM (math, sorting, filtering, dates, regex extraction). LLM only for language tasks.
- `temperature` 0.2–0.4 for extraction/classification, 0.7 for creative text. Cap `max_tokens` so the demo stays snappy.
- RAG if needed: keep it simple — in-memory list of chunks + keyword/BM25 scoring (a ~40-line C# implementation). Only add embeddings if a local embedding model is already available.
- Strip `<think>...</think>` blocks from reasoning models (deepseek-r1, qwen3) before displaying, or show them in a collapsible "Reasoning" panel.

## 5. Build order (follow exactly)

1. Write a 5-line plan: problem, user, core flow, AI's role, "wow" moment. State assumptions.
2. Verify runtime: run `foundry service status` (or `geniex model list`); if no model is loaded, tell the user the exact command to run and continue building meanwhile.
3. Scaffold (§3): csproj + LlmService + a page with one TextBox/Button/output streaming — `dotnet run -p:Platform=ARM64` must show tokens streaming before anything else.
4. Build the core feature (Service method + prompt + ViewModel command). Test with the 3 demo inputs.
5. Build the WinUI page for the core flow (§3 UI spec). Run it end to end.
6. Add the "wow" feature (one only), status pill, perf stats, example chips.
7. Error states: runtime down, bad JSON, empty input, long input.
8. `README.md` (prereqs, `run.ps1`, `appsettings.json`) and `DEMO.md` (pitch in 3 sentences, 60-second click-path, sample inputs, fallback plan if the model is slow).
9. Final check: fresh start from `run.ps1` works (also `dotnet publish -c Release -p:Platform=ARM64` produces a runnable exe); no cloud URLs in code (`Select-String -Path **\*.cs -Pattern "openai.com|azure.com"` must be empty, except comments).

## 6. Environment facts

- Hardware: Windows on ARM64 (Snapdragon X, Hexagon NPU). Build ARM64-native (x64 emulation works but is slower).
- Shell: PowerShell. Provide `run.ps1`; avoid bash-only scripts.
- .NET: .NET 8 SDK + Windows App SDK via NuGet; WinUI 3 unpackaged; build with `-p:Platform=ARM64`.
- Ports: Foundry = dynamic; GenieX = `18181`.
- First model load on NPU can take 10–60 s — warm it up at app startup with a 1-token request.

## 7. Claude Code session setup (speed settings)

**Model & effort per phase** (the user switches the main session; Claude cannot change its own session model mid-conversation):

| Phase | Model | Effort |
|---|---|---|
| Plan + scaffold + LLM wiring + core page | Opus (or `opusplan`: Opus in plan mode, Sonnet when executing) | **high** |
| Features + UI polish | Sonnet | **medium** |
| Text/colour/margin tweaks | Sonnet | **low** |
| Bug still failing after 2 attempts | Opus | **xhigh**, then drop back |
| max effort / ultracode | Don't use for the build. Ultracode only for (a) idea judge panel at the start, (b) final pre-demo review. | — |

**What Claude does on its own:** delegate to project subagents in `.claude/agents/` that pin their own model/effort:
- `ui-polisher` (Sonnet) — XAML layout/styling/spacing, example buttons, empty states.
- `demo-reviewer` (Opus) — read-only pre-demo check: runtime-down path, empty/long input, bad JSON, cloud URLs, build warnings.
Use them for self-contained chunks; keep the core LLM wiring and ViewModel logic in the main session.

**Session habits:**
- First prompt in **plan mode** (Shift+Tab), approve, then **auto-accept edits**.
- `.claude/settings.json` pre-approves `dotnet`, `foundry`, `geniex`, `pwsh` so builds never wait for permission.
- After every change: `dotnet build -p:Platform=ARM64` — fix errors before moving on.
