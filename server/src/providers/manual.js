/**
 * Modo manual: la vinculación se completa en la página /link, donde el
 * usuario pega la URL RTMP y la clave. El resto del sistema no distingue
 * este modo del OAuth: ambos producen { serverUrl, streamKey }.
 */
module.exports = {
  id: "manual",
  mode: "manual",

  isConfigured() {
    return true;
  },

  async startLink({ session, baseUrl }) {
    return {
      code: session.code,
      verificationUrl: baseUrl + "/link?code=" + encodeURIComponent(session.code),
      interval: 2,
      expiresIn: 300
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

  async resolveIngest({ link }) {
    return {
      serverUrl: link.ingest.serverUrl,
      streamKey: link.ingest.streamKey,
      accountLabel: link.accountLabel
    };
  }
};
