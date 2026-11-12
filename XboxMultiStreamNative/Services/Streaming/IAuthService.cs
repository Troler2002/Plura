using System;
using System.Threading;
using System.Threading.Tasks;

namespace Plura.Services.Streaming
{
    public enum LinkPollState
    {
        Pending,
        Linked,
        Expired,
        Error
    }

    /// <summary>Datos que la app muestra al usuario para vincular una cuenta.</summary>
    public sealed class LinkChallenge
    {
        public string PlatformId { get; set; } = string.Empty;

        public string Code { get; set; } = string.Empty;

        public string VerificationUrl { get; set; } = string.Empty;

        public int IntervalSeconds { get; set; } = 3;

        public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow;
    }

    public sealed class LinkPollResult
    {
        public LinkPollState State { get; set; } = LinkPollState.Pending;

        public string AccountLabel { get; set; } = string.Empty;

        public string Error { get; set; } = string.Empty;
    }

    /// <summary>
    /// Vinculación de cuentas sin WebView: la app pide un código, el usuario
    /// lo confirma en el sitio del backend y la app consulta el estado.
    /// </summary>
    public interface IAuthService
    {
        Task<LinkChallenge> StartLinkAsync(
            string platformId, string platformName, CancellationToken token);

        Task<LinkPollResult> PollLinkAsync(LinkChallenge challenge, CancellationToken token);

        Task UnlinkAsync(string platformId, CancellationToken token);
    }
}
