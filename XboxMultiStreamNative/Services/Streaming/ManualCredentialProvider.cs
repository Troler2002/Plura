using System.Threading;
using System.Threading.Tasks;
using Plura.Models;

namespace Plura.Services.Streaming
{
    /// <summary>Modo manual: el usuario pega la URL RTMP y la Stream Key.</summary>
    public sealed class ManualCredentialProvider : ICredentialProvider
    {
        public CredentialSource Source
        {
            get { return CredentialSource.Manual; }
        }

        public Task<CredentialResult> ResolveAsync(PlatformViewModel platform, CancellationToken token)
        {
            if (platform == null)
            {
                return Task.FromResult(CredentialResult.Fail("Plataforma no válida."));
            }

            var server = IngestNormalizer.NormalizeServer(platform.ServerUrl, platform.Id);
            var key = IngestNormalizer.NormalizeKey(platform.StreamKey);

            if (string.IsNullOrWhiteSpace(server))
            {
                server = IngestNormalizer.DefaultServerFor(platform.Id);
            }

            var error = StreamEndpointValidator.Validate(server, key);
            if (error != null)
            {
                return Task.FromResult(CredentialResult.Fail(platform.Name + ": " + error));
            }

            return Task.FromResult(CredentialResult.Ok(new StreamEndpoint
            {
                PlatformId = platform.Id,
                ServerUrl = server,
                StreamKey = key,
                Source = CredentialSource.Manual,
                AccountLabel = string.Empty
            }));
        }
    }
}
