using System;
using System.Threading;
using System.Threading.Tasks;
using Plura.Models;

namespace Plura.Services.Streaming
{
    /// <summary>
    /// Elige el proveedor según el modo de la plataforma. La UI y el motor de
    /// video solo ven un StreamEndpoint, sin saber si vino de OAuth o manual.
    /// </summary>
    public sealed class CredentialResolver
    {
        private readonly ManualCredentialProvider _manual = new ManualCredentialProvider();
        private readonly OAuthCredentialProvider _account;

        public CredentialResolver(OAuthCredentialProvider account)
        {
            _account = account ?? throw new ArgumentNullException(nameof(account));
        }

        public ICredentialProvider For(PlatformViewModel platform)
        {
            if (platform != null && platform.Mode == CredentialMode.Account)
            {
                return _account;
            }

            return _manual;
        }

        public Task<CredentialResult> ResolveAsync(PlatformViewModel platform, CancellationToken token)
        {
            if (platform == null)
            {
                return Task.FromResult(CredentialResult.Fail("Plataforma no válida."));
            }

            return For(platform).ResolveAsync(platform, token);
        }
    }
}
