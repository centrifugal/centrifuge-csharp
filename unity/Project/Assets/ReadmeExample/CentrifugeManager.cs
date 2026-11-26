// The Unity example from README.md ("Unity Support" > "Example"), kept identical so that
// `make unity-test` checks it compiles. Update both together.
using System.Collections.Concurrent;
using UnityEngine;
using Centrifugal.Centrifuge;

public class CentrifugeManager : MonoBehaviour
{
    private CentrifugeClient client;
    private readonly ConcurrentQueue<string> messages = new ConcurrentQueue<string>();

    void Start()
    {
        client = new CentrifugeClient("ws://localhost:8000/connection/websocket");

        client.Connected += (sender, e) => Debug.Log($"Connected: {e.ClientId}");

        var sub = client.NewSubscription("news");
        // Event handlers may run on a background thread: hand data over to the main thread.
        sub.Publication += (sender, e) => messages.Enqueue(System.Text.Encoding.UTF8.GetString(e.Data.Span));
        sub.Subscribe();

        client.Connect();
    }

    void Update()
    {
        // Main thread: safe to touch GameObjects, components and UI here.
        while (messages.TryDequeue(out var message))
        {
            Debug.Log($"Publication: {message}");
        }
    }

    void OnDestroy()
    {
        // Blocks until the disconnect completes; it doesn't need the main thread, so it can't deadlock.
        client?.Dispose();
    }
}
