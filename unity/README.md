# Unity connection probe (issue #35)

A single Editor script, [`CentrifugeProbe.cs`](CentrifugeProbe/Assets/Editor/CentrifugeProbe.cs), that
checks the SDK on Unity's Mono runtime layer by layer:

1. Raw `ClientWebSocket` with no subprotocol, sending a JSON connect (does the socket work at all?).
2. Raw `ClientWebSocket` with `centrifuge-protobuf`, receive pending before the send (what the SDK does).
3. The same, with the send first.
4. The real `CentrifugeClient` with SDK logging on: connect, and subscribe/publish if a channel is set.

It also reports the Unity and Mono versions, the architecture, the system proxy, the Enter Play Mode
settings, the versions of the dependency DLLs that actually load, thread-pool settings and the latency
of queued work in each phase (the SDK handles replies on the pool), DNS results, whether a global
certificate validation callback is set, and for `wss://` a separate TLS handshake with the certificate
chain and the platform's verdict (Unity's `ClientWebSocket` ignores validation callbacks, so the
chain can't be observed through it).

## In your own project

1. Copy `CentrifugeProbe.cs` to `Assets/Editor/` in a project that already uses the SDK.
2. Set `Endpoint` (and `Token`, `Channel` if needed) at the top of the file. The token is never printed.
3. Choose **Centrifuge → Run connection probe** in the menu and wait up to ~30 seconds.
4. The report is copied to the clipboard and saved to `Logs/centrifuge-probe.txt`.

## Headless, from this repo

One-time setup on macOS:

1. Install Unity Hub (`brew install --cask unity-hub`), sign in with a free Unity account and add a free
   personal license (Hub → Settings → Licenses → Add).
2. Install the Editor versions from their release pages, e.g.
   https://unity.com/releases/editor/whats-new/6000.0.85f2 → Install. No extra modules are needed.
   Note: 6.0 LTS patches labelled "3-year LTS" (6000.0.86f1 and later) need a paid plan.

Start Centrifugo 5.4.9 (the reporter's version) and run the probe:

```sh
docker run -d --rm --name centrifuge-csharp-probe-v5 -p 8000:8000 \
  -e CENTRIFUGO_CLIENT_INSECURE=true -e CENTRIFUGO_LOG_LEVEL=debug \
  centrifugo/centrifugo:v5.4.9 centrifugo

bash unity/probe.sh 6000.0.85f2
```

`probe.sh` builds the netstandard2.1 SDK, copies it and its dependencies into a per-version project
under `unity/work/` (ignored by git), runs the probe in batch mode, and prints the report. The first
run per version imports the project and takes a few minutes. The exit code is 0 if the client
connected. `PROBE_WS`, `PROBE_TOKEN` and `PROBE_CHANNEL` override the settings in the script.
