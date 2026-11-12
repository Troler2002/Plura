const crypto = require("node:crypto");
const config = require("../config");
const { postForm, getJson, describe } = require("./http");

const AUTH_URL = "https://id.kick.com/oauth/authorize";
const TOKEN_URL = "https://id.kick.com/oauth/token";
const CHANNELS_URL = "https://api.kick.com/public/v1/channels";
const SCOPES = "user:read channel:read streamkey:read";

function base64url(buffer) {
  return buffer
    .toString("base64")
    .replace(/\+/g, "-")
    .replace(/\//g, "_")
    .replace(/=+$/, "");
}

async function refresh(link) {
  const tokens = link.tokens || {};
  if (!tokens.refreshToken) {
    throw new Error("Kick: la sesión caducó. Vuelve a vincular la cuenta.");
  }

  const result = await postForm(TOKEN_URL, {
    grant_type: "refresh_token",
    refresh_token: tokens.refreshToken,
    client_id: config.providers.kick.clientId,
    client_secret: config.providers.kick.clientSecret
  });

  if (!result.ok || !result.data.access_token) {
    throw new Error("Kick: no se pudo renovar la sesión. Vuelve a vincular la cuenta.");
  }

  link.tokens = {
    ...tokens,
    accessToken: result.data.access_token,
    refreshToken: result.data.refresh_token || tokens.refreshToken,
    expiresAt: Date.now() + (Number(result.data.expires_in) || 3600) * 1000
  };

  return link.tokens.accessToken;
}

function parseIngest(channel) {
  const rawKey = channel.stream_key || channel.streamKey || "";
  if (rawKey) {
    return { serverUrl: config.providers.kick.serverUrl, streamKey: rawKey };
  }

  const rawUrl = channel.stream_url || channel.streamUrl || "";
  if (/^rtmps?:\/\//i.test(rawUrl)) {
    const cut = rawUrl.lastIndexOf("/");
    if (cut > 8 && cut < rawUrl.length - 1) {
      return {
        serverUrl: rawUrl.slice(0, cut + 1),
        streamKey: rawUrl.slice(cut + 1)
      };
    }
  }

  throw new Error(
    "Kick no devolvió la clave de transmisión. Usa el modo Manual con los datos de Kick Creator Dashboard."
  );
}

module.exports = {
  id: "kick",
  mode: "redirect",

  isConfigured() {
    return Boolean(
      config.providers.kick.clientId && config.providers.kick.clientSecret
    );
  },

  async startLink({ session, baseUrl }) {
    const verifier = base64url(crypto.randomBytes(48));
    const challenge = base64url(crypto.createHash("sha256").update(verifier).digest());
    const redirectUri = baseUrl + "/oauth/callback/kick";

    session.providerData = { codeVerifier: verifier, redirectUri };

    const params = new URLSearchParams({
      response_type: "code",
      client_id: config.providers.kick.clientId,
      redirect_uri: redirectUri,
      scope: SCOPES,
      state: session.id,
      code_challenge: challenge,
      code_challenge_method: "S256"
    });

    return {
      code: session.code,
      verificationUrl: AUTH_URL + "?" + params.toString(),
      interval: 3,
      expiresIn: 600
    };
  },

  async pollLink({ session }) {
    if (session.state === "linked") {
      return { status: "linked", accountLabel: session.accountLabel };
    }
    if (session.state === "error") {
      return { status: "error", error: session.error };
    }
    return { status: "pending" };
  },

  async handleCallback({ query, session }) {
    if (query.error) {
      throw new Error("Kick: " + (query.error_description || query.error));
    }
    if (!query.code) {
      throw new Error("Kick no devolvió el código de autorización.");
    }

    const data = session.providerData || {};
    const result = await postForm(TOKEN_URL, {
      grant_type: "authorization_code",
      client_id: config.providers.kick.clientId,
      client_secret: config.providers.kick.clientSecret,
      code: query.code,
      redirect_uri: data.redirectUri,
      code_verifier: data.codeVerifier
    });

    if (!result.ok || !result.data.access_token) {
      throw new Error("Kick no entregó el token: " + describe(result.data));
    }

    const channels = await getJson(CHANNELS_URL, {
      Authorization: "Bearer " + result.data.access_token
    });

    if (!channels.ok) {
      throw new Error("Kick API " + channels.status + ": " + describe(channels.data));
    }

    const channel = Array.isArray(channels.data.data)
      ? channels.data.data[0]
      : channels.data.data || {};

    const label =
      channel.channel_slug || channel.slug || channel.name || "Kick";
    const ingest = parseIngest(channel);

    session.link = {
      provider: "kick",
      mode: "redirect",
      accountLabel: label,
      ingest,
      tokens: {
        accessToken: result.data.access_token,
        refreshToken: result.data.refresh_token || "",
        expiresAt: Date.now() + (Number(result.data.expires_in) || 3600) * 1000
      }
    };

    return { accountLabel: label };
  },

  async resolveIngest({ link }) {
    if (link.tokens && Date.now() > (link.tokens.expiresAt || 0) - 60000) {
      await refresh(link);
    }

    return {
      serverUrl: link.ingest.serverUrl,
      streamKey: link.ingest.streamKey,
      accountLabel: link.accountLabel || "Kick"
    };
  }
};
