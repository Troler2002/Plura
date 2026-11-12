const config = require("../config");
const { postForm, getJson, describe } = require("./http");

const DEVICE_URL = "https://id.twitch.tv/oauth2/device";
const TOKEN_URL = "https://id.twitch.tv/oauth2/token";
const USERS_URL = "https://api.twitch.tv/helix/users";
const KEY_URL = "https://api.twitch.tv/helix/streams/key";
const SCOPES = "channel:read:stream_key";

async function apiGet(url, accessToken) {
  const result = await getJson(url, {
    "Client-Id": config.providers.twitch.clientId,
    Authorization: "Bearer " + accessToken
  });

  if (!result.ok) {
    throw new Error("Twitch API " + result.status + ": " + describe(result.data));
  }

  return result.data;
}

async function refresh(link) {
  const tokens = link.tokens || {};
  if (!tokens.refreshToken) {
    throw new Error("Twitch: la sesión caducó. Vuelve a vincular la cuenta.");
  }

  const result = await postForm(TOKEN_URL, {
    grant_type: "refresh_token",
    refresh_token: tokens.refreshToken,
    client_id: config.providers.twitch.clientId,
    client_secret: config.providers.twitch.clientSecret
  });

  if (!result.ok || !result.data.access_token) {
    throw new Error("Twitch: no se pudo renovar la sesión. Vuelve a vincular la cuenta.");
  }

  link.tokens = {
    ...tokens,
    accessToken: result.data.access_token,
    refreshToken: result.data.refresh_token || tokens.refreshToken,
    expiresAt: Date.now() + (Number(result.data.expires_in) || 3600) * 1000
  };

  return link.tokens.accessToken;
}

async function fetchStreamKey(accessToken, broadcasterId) {
  const data = await apiGet(
    KEY_URL + "?broadcaster_id=" + encodeURIComponent(broadcasterId),
    accessToken
  );
  const key = data.data && data.data[0] && data.data[0].stream_key;
  if (!key) {
    throw new Error("Twitch no devolvió la clave de transmisión.");
  }
  return key;
}

module.exports = {
  id: "twitch",
  mode: "device",

  isConfigured() {
    return Boolean(
      config.providers.twitch.clientId && config.providers.twitch.clientSecret
    );
  },

  async startLink({ session }) {
    const result = await postForm(DEVICE_URL, {
      client_id: config.providers.twitch.clientId,
      scopes: SCOPES
    });

    const data = result.data || {};
    if (!result.ok || !data.device_code) {
      throw new Error("Twitch no inició la vinculación: " + describe(data));
    }

    session.providerData = {
      deviceCode: data.device_code,
      userCode: data.user_code
    };

    return {
      code: data.user_code,
      verificationUrl: data.verification_uri || "https://www.twitch.tv/activate",
      interval: Math.max(5, Number(data.interval) || 5),
      expiresIn: Number(data.expires_in) || 1800
    };
  },

  async pollLink({ session }) {
    const data = session.providerData || {};
    if (!data.deviceCode) {
      return { status: "error", error: "Sesión de Twitch incompleta." };
    }

    const result = await postForm(TOKEN_URL, {
      client_id: config.providers.twitch.clientId,
      client_secret: config.providers.twitch.clientSecret,
      device_code: data.deviceCode,
      grant_type: "urn:ietf:params:oauth:grant-type:device_code"
    });

    const payload = result.data || {};
    const message = String(payload.message || payload.error || "");

    if (result.ok && payload.access_token) {
      try {
        const users = await apiGet(USERS_URL, payload.access_token);
        const user = users.data && users.data[0];
        if (!user) {
          throw new Error("Twitch no devolvió el perfil de la cuenta.");
        }

        const streamKey = await fetchStreamKey(payload.access_token, user.id);
        const label = user.display_name || user.login || "Twitch";

        session.link = {
          provider: "twitch",
          mode: "device",
          accountLabel: label,
          ingest: {
            serverUrl: config.providers.twitch.serverUrl,
            streamKey
          },
          tokens: {
            accessToken: payload.access_token,
            refreshToken: payload.refresh_token || "",
            expiresAt: Date.now() + (Number(payload.expires_in) || 3600) * 1000,
            broadcasterId: user.id
          }
        };

        return { status: "linked", accountLabel: label };
      } catch (err) {
        return { status: "error", error: err.message };
      }
    }

    if (message.includes("authorization_pending") || message.includes("slow_down")) {
      return { status: "pending" };
    }
    if (message.includes("expired_token")) {
      return { status: "expired" };
    }
    if (message.includes("access_denied")) {
      return { status: "error", error: "Autorización denegada en Twitch." };
    }

    return {
      status: "error",
      error: "Twitch: " + (message || "no se pudo completar la vinculación.")
    };
  },

  async resolveIngest({ link }) {
    let token = link.tokens && link.tokens.accessToken;
    if (!token || Date.now() > (link.tokens.expiresAt || 0) - 60000) {
      token = await refresh(link);
    }

    const streamKey = await fetchStreamKey(token, link.tokens.broadcasterId);

    return {
      serverUrl: config.providers.twitch.serverUrl,
      streamKey,
      accountLabel: link.accountLabel || "Twitch"
    };
  },

  /**
   * Canal (login) de la cuenta vinculada. El relay lo usa para recibir la
   * emisión nativa que la Xbox envía a Twitch.
   */
  async channelInfo({ link }) {
    if (!link || !link.tokens || !link.tokens.broadcasterId) {
      throw new Error(
        "Vincula tu cuenta de Twitch con Plura para detectar el canal automáticamente."
      );
    }

    let token = link.tokens.accessToken;
    if (!token || Date.now() > (link.tokens.expiresAt || 0) - 60000) {
      token = await refresh(link);
    }

    const users = await apiGet(USERS_URL, token);
    const user = users.data && users.data[0];
    if (!user) {
      throw new Error("Twitch no devolvió el perfil de la cuenta.");
    }

    return {
      login: user.login,
      displayName: user.display_name || user.login
    };
  }
};
