---
name: demo-reviewer
description: Read-only pre-demo review of the hackathon app for crashes and demo risks. Use once before presenting.
model: opus
effort: high
tools: Read, Glob, Grep, Bash
---
Review the app for demo risks. Do not edit files. Check:
1. Build: `dotnet build -c Release -p:Platform=ARM64` has no errors; note warnings that matter.
2. Runtime down: Foundry/GenieX not running → InfoBar + retry, no crash.
3. Empty input, very long input (chunking), cancel mid-stream, double-click Run.
4. Bad/partial JSON from the model is handled with retry + raw-text fallback.
5. No cloud AI URLs (openai.com, azure.com) in code; model id resolution works.
6. README.md and DEMO.md exist and match how the app actually runs.
Return a prioritized list: issue, file:line, one-line fix.
