# Codex Text Probe

Experimental, read-only validation of this path:

```
text request
  -> local Codex app-server (ws://127.0.0.1:4282)
  -> existing ChatGPT OAuth
  -> ephemeral Codex thread + text turn
  -> Codex dynamic tool request
  -> SOL Fast (http://127.0.0.1:8770/call)
  -> SOL MCP / Home Assistant read tool
  -> final Codex answer
```

This probe is intentionally unable to execute actions. It exposes only:

- `home_assistant_get_state`
- `home_assistant_search_states`

It does not start Codex, change the installed plugin, or alter SOL Fast. It only connects to already-running local services.

## Run

From the repository checkout:

```powershell
dotnet run --project tools/CodexTextProbe -- "Decime si la TV del dormitorio está prendida. Usá las herramientas disponibles y no inventes el estado."
```

Default prompt (when no argument is supplied) asks for the bedroom TV state.

## Preconditions

- Codex Audio Remote / Codex app-server is already running on `127.0.0.1:4282`.
- `account/read` reports `authMode=chatgpt`.
- SOL Fast is running on `127.0.0.1:8770`.
- SOL Fast exposes the two read-only Home Assistant tools above.

## What to record

The probe prints:

- OAuth/auth mode and plan;
- thread startup latency;
- final answer;
- number of SOL tool calls;
- turn latency;
- total latency.

A successful full-path test should:

1. report `Auth: chatgpt`;
2. make at least one tool call for a current-state question;
3. return a current Home Assistant state rather than guessing;
4. complete without Remote Desktop Commander.

## Failure interpretation

- Cannot connect to `:4282`: Codex app-server is not running or the plugin is not using the expected local app-server.
- Auth mode is not `chatgpt`: the local Codex login/OAuth path is not available.
- `thread/start` rejects `dynamicTools`: the running Codex build does not include the dynamic-tool support used by the SOL plugin release.
- Zero tool calls for a current-state question: routing instructions need tightening before any action test.
- SOL Fast error: the Codex leg works; troubleshoot only the local SOL bridge/tool call.

## Next stage

Only after the read-only probe is reliable should an action tool be exposed. The first action test should require an explicit user request and preserve `confirmedByUser=true` end-to-end.
