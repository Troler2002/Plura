using System.Threading;
using System.Threading.Tasks;
using Plura.Models;

namespace Plura.Services.Streaming
{
    /// <summary>
    /// Fuente de credenciales. Manual = URL + clave escritas por el usuario.
    /// Account = credenciales obtenidas tras vincular la cuenta.
    /// </summary>
    public interface ICredentialProvider
    {
        CredentialSource Source { get; }

        Task<CredentialResult> ResolveAsync(PlatformViewModel platform, CancellationToken token);
    }
}
