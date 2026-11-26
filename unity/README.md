# Unity

Tools for running the SDK inside a real Unity Editor, on Unity's runtime: its Mono, class libraries
and TLS stack. These differ from the .NET the main test suite runs on.

- `Project/Assets/Editor/CentrifugeUnityTests.cs`: smoke tests. They cover the WebSocket and
  HTTP-stream transports: connect, subscribe/publish, history, presence, reconnect and resubscribe
  after a server-side disconnect, and `Dispose` on the main thread.
- `Project/Assets/Editor/CentrifugeProbe.cs`: a connection probe for users who can't connect. It runs
  standalone in any project that already has the SDK (see "Troubleshooting" in the main README). It
  checks the raw `ClientWebSocket` (JSON and protobuf), a TLS handshake for `wss://`, DNS and
  thread-pool latency, then the SDK with logging on. The report is copied to the clipboard and saved
  to `Logs/centrifuge-probe.txt`.
- `Project/Assets/ReadmeExample/CentrifugeManager.cs`: the main README's Unity example, compiled by
  every run so it stays correct.
- `run.sh`: builds the netstandard2.1 SDK, copies it and its dependencies into a per-version project
  copy under `work/` (ignored by git), runs the tests or the probe headless, and prints the result.

## Setup (once)

1. Install Unity Hub (`brew install --cask unity-hub` on macOS) and sign in with a free Unity account.
2. Add a free Personal license: Hub → Settings → Licenses → Add.
3. Install an Editor. The newest one Hub offers is fine; no extra modules are needed. For a
   specific version, use its release page, e.g. https://unity.com/releases/editor/whats-new/6000.0.85f2
   → Install. 6.0 LTS patches labelled "3-year LTS" (6000.0.86f1 and later) need a paid plan.

## Run

```sh
docker compose up -d   # the same Centrifugo as the integration tests
make unity-test        # newest installed Editor; UNITY_VERSION=6000.0.85f2 make unity-test for another
```

The first run per Editor version imports the project and takes a few minutes; later runs take about
half a minute. The exit code is non-zero if any test fails. The full Unity log is in `unity/work/`.

The probe runs against any server:

```sh
PROBE_WS=wss://example.com/connection/websocket PROBE_TOKEN=... make unity-probe
```

To run either from the Editor GUI, open `unity/work/<version>` (after one run, which copies in the
DLLs) and use the **Centrifuge** menu.

On Linux, `run.sh` looks for Editors in `~/Unity/Hub/Editor/`. Set `UNITY_EDITOR` to the Editor binary
to use one installed elsewhere.
