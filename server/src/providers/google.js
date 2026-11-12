const config = require("../config");
const { postForm, postJson, getJson, describe } = require("./http");

const DEVICE_URL = "https://oauth2.googleapis.com/device/code";
const TOKEN_URL = "https://oauth2.googleapis.com/token";
const CHANNELS_URL = "https://www.googleapis.com/youtube/v3/channels";
const STREAMS_URL = "https://www.googleapis.com/youtube/v3/liveStreams";
const SCOPE = "https://www.googleapis.com/auth/youtube";

function withSlash(url) {
  const value = String(url || "").trim();
  return value.endsWith("/") ? value : value + "/";
}

async function refresh(link) {
  const tokens = link.tokens || {};
  if (!tokens.refreshToken) {
    throw new Error("YouTube: la sesión caducó. Vuelve a vincular la cuenta.");
  }

  const result = await postForm(TOKEN_URL, {
    client_id: config.providers.youtube.clientId,
    client_secret: config.providers.youtube.clientSecret,
    refresh_token: tokens.refreshToken,
    grant_type: "refresh_token"
  });

  if (!result.ok || !result.data.access_token) {
    throw new Error("YouTube: no se pudo renovar la sesión. Vuelve a vincular la cuenta.");
  }

  link.tokens = {
    ...tokens,
    accessToken: result.data.access_token,
    expiresAt: Date.now() + (Number(result.data.expires_in) || 3600) * 1000
  };

  return link.tokens.accessToken;
}

module.exports = {
  id: "google",
  mode: "device",

  isConfigured() {
    return Boolean(
      config.providers.youtube.clientId && config.providers.youtube.clientSecret
    );
  },

  async startLink({ session }) {
    const result = await postForm(DEVICE_URL, {
      client_id: config.providers.youtube.clientId,
      scope: SCOPE
    });

    const data = result.data || {};
    if (!result.ok || !data.device_code) {
      throw new Error("Google no inició la vinculación: " + describe(data));
    }

    session.providerData = {
      deviceCode: data.device_code,
      userCode: data.user_code
    };

    return {
      code: data.user_code,
      verificationUrl:
        data.verification_url || data.verification_uri || "https://www.google.com/device",
      interval: Math.max(5, Number(data.interval) || 5),
      expiresIn: Number(data.expires_in) || 1800
    };
  },

  async pollLink({ session }) {
    const data = session.providerData || {};
    if (!data.deviceCode) {
      return { status: "error", error: "Sesión de Google incompleta." };
    }

    const result = await postForm(TOKEN_URL, {
      client_id: config.providers.youtube.clientId,
      client_secret: config.providers.youtube.clientSecret,
      device_code: data.deviceCode,
      grant_type: "urn:ietf:params:oauth:grant-type:device_code"
    });

    const payload = result.data || {};
    const error = String(payload.error || "");

    if (result.ok && payload.access_token) {
      try {
        const channels = await getJson(CHANNELS_URL + "?part=snippet&mine=true", {
          Authorization: "Bearer " + payload.access_token
        });

        if (!channels.ok) {
          throw new Error("YouTube API " + channels.status + ": " + describe(channels.data));
        }

        const item = channels.data.items && channels.data.items[0];
        const label = (item && item.snippet && item.snippet.title) || "YouTube";

        const stream = await postJson(
          STREAMS_URL + "?part=snippet,cdn,status",
          {
            snippet: { title: "Plura · " + new Date().toISOString().slice(0, 10) },
            cdn: {
              frameRate: "60fps",
              ingestionType: "rtmp",
              resolution: "1080p"
            },
            status: { streamStatus: "active" }
          },
          { Authorization: "Bearer " + payload.access_token }
        );

        if (!stream.ok) {
          throw new Error(
            "YouTube no creó el stream: " + describe(stream.data) +
            ". Comprueba que la transmisión esté activada en el canal."
          );
        }

        const info = stream.data.cdn && stream.data.cdn.ingestionInfo;
        if (!info || !info.ingestionAddress || !info.streamName) {
          throw new Error(
            "YouTube no devolvió los datos de ingesta. " +
            "¿Está activada la transmisión en directo en el canal?"
          );
        }

        session.link = {
          provider: "google",
          mode: "device",
          accountLabel: label,
          ingest: {
            serverUrl: withSlash(info.ingestionAddress),
            streamKey: info.streamName
          },
          tokens: {
            accessToken: payload.access_token,
            refreshToken: payload.refresh_token || "",
            expiresAt: Date.now() + (Number(payload.expires_in) || 3600) * 1000
          },
          providerData: { liveStreamId: stream.data.id || "" }
        };

        return { status: "linked", accountLabel: label };
      } catch (err) {
        return { status: "error", error: err.message };
      }
    }

    if (error === "authorization_pending" || error === "slow_down") {
      return { status: "pending" };
    }
    if (error === "expired_token") {
      return { status: "expired" };
    }
    if (error === "access_denied") {
      return { status: "error", error: "Autorización denegada en Google." };
    }

    return {
      status: "error",
      error: "Google: " + (payload.error_description || error || "no se pudo completar la vinculación.")
    };
  },

  async resolveIngest({ link }) {
    // La información de ingesta de YouTube es estable. Solo renovamos el token
    // para detectar cuentas revocadas antes de entregar la clave.
    if (link.tokens && Date.now() > (link.tokens.expiresAt || 0) - 60000) {
      await refresh(link);
    }

    return {
      serverUrl: link.ingest.serverUrl,
      streamKey: link.ingest.streamKey,
      accountLabel: link.accountLabel || "YouTube"
    };
  }
};
