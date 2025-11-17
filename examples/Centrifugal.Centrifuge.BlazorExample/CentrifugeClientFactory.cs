using Centrifugal.Centrifuge;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace Centrifugal.Centrifuge.BlazorExample;

public class CentrifugeClientFactory
{
    private readonly IJSRuntime _jsRuntime;
    private readonly ILoggerFactory _loggerFactory;

    public CentrifugeClientFactory(IJSRuntime jsRuntime, ILoggerFactory loggerFactory)
    {
        _jsRuntime = jsRuntime;
        _loggerFactory = loggerFactory;
    }

    public CentrifugeClient CreateClient(bool useHttpStreaming)
    {
        var logger = _loggerFactory.CreateLogger<CentrifugeClient>();

        if (useHttpStreaming)
        {
            return new CentrifugeClient(
                new[]
                {
                    new CentrifugeTransportEndpoint(
                        CentrifugeTransportType.HttpStream,
                        "http://localhost:8000/connection/http_stream"
                    )
                },
                _jsRuntime,
                new CentrifugeClientOptions
                {
                    Logger = logger
                }
            );
        }
        else
        {
            return new CentrifugeClient(
                "ws://localhost:8000/connection/websocket",
                _jsRuntime,
                new CentrifugeClientOptions
                {
                    Logger = logger
                }
            );
        }
    }
}
