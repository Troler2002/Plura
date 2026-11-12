using System;
using System.Threading;
using System.Threading.Tasks;
using Plura.Models;

namespace Plura.Services.Streaming
{
    public sealed class StreamingException : Exception
    {
        public StreamingException(string message) : base(message) { }
        public StreamingException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Une las dos mitades: resuelve credenciales (manual o cuenta) y las
    /// entrega al motor de video. La UI solo llama Start/Stop.
    /// </summary>
    public sealed class StreamingCoordinator
    {
        private readonly CredentialResolver _credentials;
        private readonly IStreamSink _sink;

        public StreamingCoordinator(CredentialResolver credentials, IStreamSink sink)
        {
            _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        }

        public async Task<StreamEndpoint> StartAsync(
            PlatformViewModel platform, CancellationToken token)
        {
            if (platform == null)
            {
                throw new StreamingException("Plataforma no válida.");
            }

            var endpoint = await ResolveAsync(platform, token);

            ObsTargetInfo target;
            try
            {
                target = await _sink.PrepareAsync(platform, endpoint, token);
                platform.ObsTargetId = target.Id;
                await _sink.StartAsync(target, token);
            }
            catch (StreamingException) { throw; }
            catch (Exception ex)
            {
                throw new StreamingException(
                    platform.Name + ": " + ex.Message, ex);
            }

            return endpoint;
        }

        public Task StopAsync(PlatformViewModel platform, CancellationToken token)
        {
            return _sink.StopAsync(platform, token);
        }

        /// <summary>
        /// Inicia la plataforma principal: configura el stream de OBS y lo arranca.
        /// Debe completarse antes de iniciar destinos, porque el plugin reutiliza
        /// el encoder de esta transmisión.
        /// </summary>
        public async Task<StreamEndpoint> StartPrimaryAsync(
            PlatformViewModel platform, CancellationToken token)
        {
            if (platform == null)
            {
                throw new StreamingException("Plataforma no válida.");
            }

            var endpoint = await ResolveAsync(platform, token);

            try
            {
                await _sink.PreparePrimaryAsync(endpoint, token);
                await _sink.StartPrimaryAsync(token);
            }
            catch (StreamingException) { throw; }
            catch (Exception ex)
            {
                throw new StreamingException(platform.Name + ": " + ex.Message, ex);
            }

            return endpoint;
        }

        public Task StopPrimaryAsync(CancellationToken token)
        {
            return _sink.StopPrimaryAsync(token);
        }

        private async Task<StreamEndpoint> ResolveAsync(
            PlatformViewModel platform, CancellationToken token)
        {
            var credential = await _credentials.ResolveAsync(platform, token);
            if (!credential.Success || credential.Endpoint == null)
            {
                throw new StreamingException(credential.Error);
            }

            return credential.Endpoint;
        }
    }
}
