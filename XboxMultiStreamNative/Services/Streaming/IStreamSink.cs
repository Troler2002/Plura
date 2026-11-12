using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Plura.Models;

namespace Plura.Services.Streaming
{
    /// <summary>Destino de video: OBS con el plugin Multiple RTMP.</summary>
    public interface IStreamSink
    {
        Task<List<ObsTargetInfo>> ListAsync(CancellationToken token);

        Task<ObsTargetInfo> PrepareAsync(
            PlatformViewModel platform, StreamEndpoint endpoint, CancellationToken token);

        Task StartAsync(ObsTargetInfo target, CancellationToken token);

        Task StopAsync(PlatformViewModel platform, CancellationToken token);

        // --- Transmisión principal de OBS (primer destino / encoder compartido) ---

        Task PreparePrimaryAsync(StreamEndpoint endpoint, CancellationToken token);

        Task StartPrimaryAsync(CancellationToken token);

        Task StopPrimaryAsync(CancellationToken token);
    }
}
