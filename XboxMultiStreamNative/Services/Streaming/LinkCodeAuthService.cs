using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;

namespace Plura.Services.Streaming
{
    /// <summary>Implementación del flujo de código de dispositivo contra el backend.</summary>
    public sealed class LinkCodeAuthService : IAuthService
    {
        private readonly PluraBackendClient _backend;

        public LinkCodeAuthService(PluraBackendClient backend)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        }

        public async Task<LinkChallenge> StartLinkAsync(
            string platformId, string platformName, CancellationToken token)
        {
            var body = new JsonObject();
            body.SetNamedValue("platform", JsonValue.CreateStringValue(platformId ?? string.Empty));
            body.SetNamedValue("platformName", JsonValue.CreateStringValue(platformName ?? string.Empty));
            body.SetNamedValue("deviceName", JsonValue.CreateStringValue("Xbox · Plura"));

            var data = await _backend.PostAsync("/v1/link/start", body, token);

            var session = PluraBackendClient.GetString(data, "sessionToken", null);
            if (!string.IsNullOrWhiteSpace(session))
            {
                _backend.SessionToken = session;
            }

            var challenge = new LinkChallenge
            {
                PlatformId = platformId ?? string.Empty,
                Code = PluraBackendClient.GetString(data, "code", string.Empty),
                VerificationUrl = PluraBackendClient.GetString(data, "verificationUrl", string.Empty),
                IntervalSeconds = (int)PluraBackendClient.GetNumber(data, "interval", 3),
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(
                    PluraBackendClient.GetNumber(data, "expiresIn", 300))
            };

            if (challenge.IntervalSeconds < 1) { challenge.IntervalSeconds = 3; }
            if (challenge.IntervalSeconds > 15) { challenge.IntervalSeconds = 15; }

            if (string.IsNullOrWhiteSpace(challenge.Code))
            {
                throw new BackendException("El backend no devolvió un código de vinculación.");
            }

            return challenge;
        }

        public async Task<LinkPollResult> PollLinkAsync(LinkChallenge challenge, CancellationToken token)
        {
            if (challenge == null || string.IsNullOrWhiteSpace(challenge.Code))
            {
                return new LinkPollResult
                {
                    State = LinkPollState.Error,
                    Error = "No hay una vinculación activa."
                };
            }

            var path = "/v1/link/poll?platform=" +
                       Uri.EscapeDataString(challenge.PlatformId ?? string.Empty) +
                       "&code=" + Uri.EscapeDataString(challenge.Code);

            JsonObject data;
            try
            {
                data = await _backend.GetAsync(path, token);
            }
            catch (BackendException ex)
            {
                return new LinkPollResult { State = LinkPollState.Error, Error = ex.Message };
            }

            var status = (PluraBackendClient.GetString(data, "status", "pending") ?? "pending")
                .Trim().ToLowerInvariant();

            switch (status)
            {
                case "linked":
                case "authorized":
                case "complete":
                    return new LinkPollResult
                    {
                        State = LinkPollState.Linked,
                        AccountLabel = PluraBackendClient.GetString(data, "accountLabel", string.Empty)
                    };

                case "expired":
                    return new LinkPollResult
                    {
                        State = LinkPollState.Expired,
                        Error = "El código caducó. Vuelve a intentarlo."
                    };

                case "error":
                    return new LinkPollResult
                    {
                        State = LinkPollState.Error,
                        Error = PluraBackendClient.GetString(
                            data, "error", "El backend devolvió un error al vincular la cuenta.")
                    };

                default:
                    return new LinkPollResult { State = LinkPollState.Pending };
            }
        }

        public async Task UnlinkAsync(string platformId, CancellationToken token)
        {
            var body = new JsonObject();
            body.SetNamedValue("platform", JsonValue.CreateStringValue(platformId ?? string.Empty));
            await _backend.PostAsync("/v1/link/unlink", body, token);
        }
    }
}
