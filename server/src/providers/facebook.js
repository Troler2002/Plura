const config = require("../config");
const { postForm, getJson, describe } = require("./http");

const GRAPH = "https://graph.facebook.com/";
const SCOPES = [
  "pages_show_list",
  "pages_read_engagement",
  "pages_manage_posts",
  "publish_video"
].join(",");

function graphUrl(path) {
  return GRAPH + config.providers.facebook.apiVersion + "/" + path;
}

function splitStreamUrl(streamUrl) {
  const value = String(streamUrl || "").trim();
  const cut = value.lastIndexOf("/");
  if (!/^rtmps?:\/\//i.test(value) || cut <= 8 || cut >= value.length - 1) {
    throw new Error("Facebook no devolvió una URL RTMP válida.");
  }

  return {
    serverUrl: value.slice(0, cut + 1),
    streamKey: value.slice(cut + 1)
  };
}

module.exports = {
  id: "facebook",
  mode: "redirect",

  isConfigured() {
    return Boolean(
      config.providers.facebook.appId && config.providers.facebook.appSecret
    );
  },

  async startLink({ session, baseUrl }) {
    const redirectUri = baseUrl + "/oauth/callback/facebook";
    session.providerData = { redirectUri };

    const params = new URLSearchParams({
      client_id: config.providers.facebook.appId,
      redirect_uri: redirectUri,
      state: session.id,
      response_type: "code",
      scope: SCOPES
    });

    return {
      code: session.code,
      verificationUrl: graphUrl("dialog/oauth") + "?" + params.toString(),
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
      throw new Error("Facebook: " + (query.error_description || query.error));
    }
    if (!query.code) {
      throw new Error("Facebook no devolvió el código de autorización.");
    }

    const data = session.providerData || {};

    const token = await getJson(
      graphUrl("oauth/access_token") +
        "?" +
        new URLSearchParams({
          client_id: config.providers.facebook.appId,
          client_secret: config.providers.facebook.appSecret,
          redirect_uri: data.redirectUri,
          code: query.code
        }).toString()
    );

    if (!token.ok || !token.data.access_token) {
      throw new Error("Facebook no entregó el token: " + describe(token.data));
    }

    const pages = await getJson(
      graphUrl("me/accounts") +
        "?" +
        new URLSearchParams({
          fields: "id,name,access_token",
          access_token: token.data.access_token
        }).toString()
    );

    if (!pages.ok) {
      throw new Error("Facebook API " + pages.status + ": " + describe(pages.data));
    }

    const page = pages.data.data && pages.data.data[0];
    if (!page || !page.access_token) {
      throw new Error(
        "La cuenta de Facebook no tiene ninguna página con permiso para transmitir."
      );
    }

    const live = await postForm(graphUrl(page.id + "/live_videos"), {
      status: "LIVE_NOW",
      title: "Plura",
      access_token: page.access_token
    });

    if (!live.ok || !live.data.stream_url) {
      throw new Error("Facebook no creó el directo: " + describe(live.data));
    }

    const label = page.name || "Facebook Gaming";
    session.link = {
      provider: "facebook",
      mode: "redirect",
      accountLabel: label,
      ingest: splitStreamUrl(live.data.stream_url),
      tokens: {
        pageAccessToken: page.access_token,
        pageId: page.id,
        liveVideoId: live.data.id || ""
      }
    };

    return { accountLabel: label };
  },

  async resolveIngest({ link }) {
    // El stream_url de Facebook ya apunta al directo creado; no caduca como
    // un token de acceso, así que se entrega tal cual.
    return {
      serverUrl: link.ingest.serverUrl,
      streamKey: link.ingest.streamKey,
      accountLabel: link.accountLabel || "Facebook Gaming"
    };
  }
};
