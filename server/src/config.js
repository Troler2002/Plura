const os = require("node:os");
const path = require("node:path");

function env(name, fallback = "") {
  const value = process.env[name];
  return value === undefined || value === "" ? fallback : value;
}

function firstLanAddress() {
  for (const list of Object.values(os.networkInterfaces())) {
    for (const addr of list || []) {
      if (addr.family === "IPv4" && !addr.internal) {
        return addr.address;
      }
    }
  }
  return "localhost";
}

const port = Number(env("PORT", "8787"));
const publicBaseUrl = env("PUBLIC_BASE_URL", `http://${firstLanAddress()}:${port}`)
  .replace(/\/+$/, "");

module.exports = {
  port,
  host: env("HOST", "0.0.0.0"),
  publicBaseUrl,
  dataFile: env("DATA_FILE", path.join(__dirname, "..", "data", "state.json")),
  linkTtlSeconds: Number(env("LINK_TTL_SECONDS", "300")),
  pollIntervalSeconds: Number(env("POLL_INTERVAL_SECONDS", "2")),
  relay: {
    // Ruta a ffmpeg. Si está en el PATH basta con "ffmpeg"; también se
    // detecta server/bin/ffmpeg(.exe).
    ffmpegPath: env("FFMPEG_PATH", "ffmpeg"),
    // Client ID público que usa el reproductor web de Twitch para pedir el
    // token HLS. No es un secreto ni requiere cuenta.
    twitchClientId: env("TWITCH_PLAYBACK_CLIENT_ID", "kimne78kx3ncx6brgo4mv6wki5h1ko"),
    maxOutputs: Number(env("RELAY_MAX_OUTPUTS", "6")),
    restartLimit: Number(env("RELAY_RESTART_LIMIT", "20")),
    restartDelayMs: Number(env("RELAY_RESTART_DELAY_MS", "4000"))
  },
  providers: {
    twitch: {
      clientId: env("TWITCH_CLIENT_ID"),
      clientSecret: env("TWITCH_CLIENT_SECRET"),
      serverUrl: "rtmp://live.twitch.tv/app/"
    },
    youtube: {
      clientId: env("GOOGLE_CLIENT_ID"),
      clientSecret: env("GOOGLE_CLIENT_SECRET"),
      serverUrl: "rtmp://a.rtmp.youtube.com/live2/"
    },
    kick: {
      clientId: env("KICK_CLIENT_ID"),
      clientSecret: env("KICK_CLIENT_SECRET"),
      serverUrl: "rtmps://fa723fc1b171.global-contribute.live-video.net:443/app/"
    },
    facebook: {
      appId: env("FACEBOOK_APP_ID"),
      appSecret: env("FACEBOOK_APP_SECRET"),
      apiVersion: env("FACEBOOK_API_VERSION", "v21.0"),
      serverUrl: "rtmps://live-api-s.facebook.com:443/rtmp/"
    },
    plura: {
      serverUrl: env("PLURA_INGEST_SERVER"),
      streamKey: env("PLURA_STREAM_KEY")
    },
    tiktok: {}
  }
};
