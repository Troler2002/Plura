using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Plura.Models;

namespace Plura.Services.Streaming
{
    /// <summary>Adaptador entre el coordinador y el cliente nativo de OBS.</summary>
    public sealed class ObsMultiRtmpSink : IStreamSink
    {
        private readonly ObsClient _obs;

        public ObsMultiRtmpSink(ObsClient obs)
        {
            _obs = obs ?? throw new ArgumentNullException(nameof(obs));
        }

        public Task<List<ObsTargetInfo>> ListAsync(CancellationToken token)
        {
            return _obs.ListTargetsAsync();
        }

        public Task<ObsTargetInfo> PrepareAsync(
            PlatformViewModel platform, StreamEndpoint endpoint, CancellationToken token)
        {
            if (platform == null) { throw new ObsException("Plataforma no válida."); }
            if (endpoint == null || !endpoint.IsValid)
            {
                throw new ObsException("Las credenciales de " + platform.Name + " están incompletas.");
            }

            var name = string.IsNullOrWhiteSpace(platform.TargetName)
                ? platform.Name
                : platform.TargetName;

            return _obs.EnsureTargetAsync(name, endpoint.ServerUrl, endpoint.StreamKey);
        }

        public Task StartAsync(ObsTargetInfo target, CancellationToken token)
        {
            if (target == null) { throw new ObsException("No hay destino de OBS que iniciar."); }
            return _obs.StartTargetAsync(target.Id, target.Name);
        }

        public Task StopAsync(PlatformViewModel platform, CancellationToken token)
        {
            if (platform == null) { return Task.FromResult(0); }

            var name = string.IsNullOrWhiteSpace(platform.TargetName)
                ? platform.Name
                : platform.TargetName;

            return _obs.StopTargetAsync(platform.ObsTargetId, name);
        }

        public Task PreparePrimaryAsync(StreamEndpoint endpoint, CancellationToken token)
        {
            if (endpoint == null || !endpoint.IsValid)
            {
                throw new ObsException("Las credenciales de la plataforma principal están incompletas.");
            }

            return _obs.SetStreamServiceAsync(endpoint.ServerUrl, endpoint.StreamKey);
        }

        public Task StartPrimaryAsync(CancellationToken token)
        {
            return _obs.StartStreamAsync();
        }

        public Task StopPrimaryAsync(CancellationToken token)
        {
            return _obs.StopStreamAsync();
        }
    }
}
