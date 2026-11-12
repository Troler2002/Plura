using System;
using System.Threading;
using System.Threading.Tasks;
using Plura.Models;

namespace Plura.Services.Streaming
{
    /// <summary>
    /// Modo cuenta: pide al backend las credenciales de ingesta de la
    /// plataforma ya vinculada. El backend devuelve URL + clave, exactamente
    /// lo mismo que el modo manual.
    /// </summary>
    public sealed class OAuthCredentialProvider : ICredentialProvider
    {
        private readonly PluraBackendClient _backend;

        public OAuthCredentialProvider(PluraBackendClient backend)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        }

        public CredentialSource Source
        {
            get { return CredentialSource.Account; }
        }

        public async Task<CredentialResult> ResolveAsync(PlatformViewModel platform, CancellationToken token)
        {
            if (platform == null)
            {
                return CredentialResult.Fail("Plataforma no válida.");
            }

            if (!platform.Linked)
            {
                return CredentialResult.Fail(
                    platform.Name + ": la cuenta no está vinculada. Pulsa Conectar cuenta.");
            }

            if (string.Equals(platform.Id, "tiktok", StringComparison.OrdinalIgnoreCase))
            {
                return CredentialResult.Fail(
                    "TikTok no permite obtener la clave automáticamente. Usa el modo Manual.");
            }

            try
            {
                var data = await _backend.GetAsync(
                    "/v1/ingest/" + Uri.EscapeDataString(platform.Id ?? string.Empty), token);

                var server = IngestNormalizer.NormalizeServer(
                    PluraBackendClient.GetString(data, "serverUrl", string.Empty), platform.Id);
                var key = IngestNormalizer.NormalizeKey(
                    PluraBackendClient.GetString(data, "streamKey", string.Empty));

                var error = StreamEndpointValidator.Validate(server, key);
                if (error != null)
                {
                    return CredentialResult.Fail(platform.Name + ": " + error);
                }

                return CredentialResult.Ok(new StreamEndpoint
                {
                    PlatformId = platform.Id,
                    ServerUrl = server,
                    StreamKey = key,
                    Source = CredentialSource.Account,
                    AccountLabel = PluraBackendClient.GetString(
                        data, "accountLabel", platform.AccountLabel)
                });
            }
            catch (BackendException ex)
            {
                return CredentialResult.Fail(platform.Name + ": " + ex.Message);
            }
            catch (Exception ex)
            {
                return CredentialResult.Fail(
                    platform.Name + ": no se pudieron obtener las credenciales. " + ex.Message);
            }
        }
    }
}
