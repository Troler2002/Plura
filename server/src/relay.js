const { spawn } = require("node:child_process");
const fs = require("node:fs");
const path = require("node:path");

const config = require("./config");

const GQL_URL = "https://gql.twitch.tv/gql";
const USHER_URL = "https://usher.ttvnw.net/api/channel/hls/";

const USER_QUERY = `query PluraChannel($login: String!) {
  user(login: $login) {
    login
    displayName
    profileImageURL(width: 300)
    stream {
      id
      title
      viewersCount
      type
    }
  }
}`;

const TOKEN_QUERY = `query PlaybackAccessToken($login: String!, $isLive: Boolean!, $playerType: String!) {
  streamPlaybackAccessToken(channelName: $login, params: {platform: "web", playerBackend: "mediaplayer", playerType: $playerType}) @include(if: $isLive) {
    value
    signature
  }
}`;

class RelayError extends Error {
  constructor(status, message) {
    super(message);
    this.status = status || 500;
  }
}

function normalizeLogin(value) {
  return String(value || "").trim().replace(/^#/, "").toLowerCase();
}

function validLogin(login) {
  return /^[a-z0-9_]{3,25}$/.test(login);
}

async function gql(query, variables) {
  let res;
  try {
    res = await fetch(GQL_URL, {
      method: "POST",
      headers: {
        "client-id": config.relay.twitchClientId,
        "content-type": "application/json"
      },
      body: JSON.stringify({ query, variables }),
      signal: AbortSignal.timeout(10000)
    });
  } catch (err) {
    throw new RelayError(
      504,
      "Twitch no respondió a tiempo. Comprueba la conexión del backend a Internet."
    );
  }

  const text = await res.text();
  let data;
  try {
    data = text ? JSON.parse(text) : {};
  } catch {
    data = {};
  }

  if (!res.ok) {
    throw new RelayError(502, "Twitch respondió " + res.status + " al resolver el canal.");
  }

  if (data.errors && data.errors.length) {
    throw new RelayError(502, "Twitch: " + data.errors[0].message);
  }

  return data;
}

/**
 * La consola a veces no puede descargar la foto desde la CDN de Twitch, así
 * que el backend la descarga una vez y la entrega en base64 dentro de la
 * misma respuesta del canal. Se cachea por URL para no repetir la descarga.
 */
const AVATAR_TTL_MS = 10 * 60 * 1000;
const avatarCache = new Map();

async function avatarPayload(url) {
  if (!url) { return null; }

  const now = Date.now();
  const cached = avatarCache.get(url);
  if (cached && now - cached.at < AVATAR_TTL_MS) {
    return cached;
  }

  try {
    const response = await fetch(url, { signal: AbortSignal.timeout(10000) });
    if (!response.ok) {
      throw new Error("HTTP " + response.status);
    }

    const contentType = response.headers.get("content-type") || "image/png";
    const bytes = Buffer.from(await response.arrayBuffer());
    const entry = {
      base64: bytes.toString("base64"),
      contentType,
      at: now
    };

    if (avatarCache.size >= 64) {
      avatarCache.delete(avatarCache.keys().next().value);
    }
    avatarCache.set(url, entry);
    console.log("[twitch] avatar listo (" + bytes.length + " bytes)");
    return entry;
  } catch (err) {
    console.warn("[twitch] no se pudo descargar el avatar: " + err.message);
    return null;
  }
}

/**
 * Devuelve el canal público de Twitch y si está en vivo. No necesita la
 * cuenta vinculada: el nombre del canal es público.
 */
async function resolveChannel(login) {
  const clean = normalizeLogin(login);
  if (!validLogin(clean)) {
    throw new RelayError(400, "El nombre del canal de Twitch no es válido.");
  }

  const data = await gql(USER_QUERY, { login: clean });
  const user = data && data.data && data.data.user;
  if (!user) {
    return null;
  }

  const stream = user.stream || null;
  const info = {
    login: user.login,
    displayName: user.displayName || user.login,
    avatarUrl: user.profileImageURL || "",
    live: Boolean(stream && stream.type === "live"),
    title: stream ? stream.title || "" : "",
    viewers: stream ? Number(stream.viewersCount) || 0 : 0
  };

  const avatar = await avatarPayload(info.avatarUrl);
  if (avatar) {
    info.avatarBase64 = avatar.base64;
    info.avatarContentType = avatar.contentType;
  }

  return info;
}

/** Foto del canal lista para servir desde el backend (base64 + tipo). */
async function resolveAvatar(login) {
  const clean = normalizeLogin(login);
  if (!validLogin(clean)) {
    throw new RelayError(400, "El nombre del canal de Twitch no es válido.");
  }

  const data = await gql(USER_QUERY, { login: clean });
  const user = data && data.data && data.data.user;
  if (!user || !user.profileImageURL) {
    return null;
  }

  return avatarPayload(user.profileImageURL);
}

/** URL HLS de la emisión en vivo, con token de reproducción recién pedido. */
async function resolveHlsUrl(login) {
  const clean = normalizeLogin(login);
  if (!validLogin(clean)) {
    throw new RelayError(400, "El nombre del canal de Twitch no es válido.");
  }

  const data = await gql(TOKEN_QUERY, {
    login: clean,
    isLive: true,
    playerType: "site"
  });

  const token = data && data.data && data.data.streamPlaybackAccessToken;
  if (!token || !token.value || !token.signature) {
    return "";
  }

  return (
    USHER_URL +
    encodeURIComponent(clean) +
    ".m3u8?allow_source=true&allow_audio_only=true&fast_bread=true" +
    "&player_backend=mediaplayer&playlist_include_framerate=true" +
    "&reassignments_supported=true&supported_codecs=avc1" +
    "&sig=" + encodeURIComponent(token.signature) +
    "&token=" + encodeURIComponent(token.value) +
    "&transcode_mode=catalyst_5.7.0"
  );
}

function resolveFfmpegPath() {
  const candidates = [];
  if (config.relay.ffmpegPath) {
    candidates.push(config.relay.ffmpegPath);
  }

  candidates.push(
    path.join(__dirname, "..", "bin", process.platform === "win32" ? "ffmpeg.exe" : "ffmpeg")
  );

  for (const candidate of candidates) {
    try {
      if (candidate && fs.existsSync(candidate)) {
        return candidate;
      }
    } catch { }
  }

  return config.relay.ffmpegPath || "ffmpeg";
}

function joinRtmp(serverUrl, streamKey) {
  const server = String(serverUrl || "").trim();
  const key = String(streamKey || "").trim();

  if (!/^rtmps?:\/\//i.test(server)) {
    throw new RelayError(400, "La URL de destino debe empezar por rtmp:// o rtmps://.");
  }

  if (!key) {
    throw new RelayError(400, "Falta la clave de transmisión de un destino.");
  }

  return server.endsWith("/") ? server + key : server + "/" + key;
}

function killTree(pid) {
  if (!pid) { return; }

  if (process.platform === "win32") {
    try {
      spawn("taskkill", ["/pid", String(pid), "/t", "/f"], {
        windowsHide: true,
        stdio: "ignore"
      });
      return;
    } catch { }
  }

  try {
    process.kill(pid, "SIGKILL");
  } catch { }
}

function parseNumber(value) {
  const parsed = Number(String(value || "").replace(/[^0-9.+-]/g, ""));
  return Number.isFinite(parsed) ? parsed : 0;
}

/**
 * Reenvía la emisión nativa de Twitch a varias plataformas.
 *
 * Cada destino tiene su propio proceso ffmpeg. Así una clave equivocada o
 * una caída de una plataforma no tumba el resto, y cada destino se puede
 * reiniciar de forma independiente. El vídeo se copia sin recodificar
 * (-c copy), así que el servidor solo gasta ancho de banda, no CPU.
 */
class RelayManager {
  constructor() {
    this.channel = "";
    this.displayName = "";
    this.startedAt = 0;
    this.outputs = new Map();
    this.stopping = false;
    this.lastError = "";
    this.channelInfo = null;
    this.ffmpegPath = resolveFfmpegPath();
  }

  ffmpegAvailable() {
    if (this.ffmpegPath.indexOf(path.sep) >= 0 || /^[a-z]:/i.test(this.ffmpegPath)) {
      try {
        return fs.existsSync(this.ffmpegPath);
      } catch {
        return false;
      }
    }

    return true;
  }

  isRunning() {
    return this.outputs.size > 0;
  }

  snapshot() {
    const outputs = [];
    for (const output of this.outputs.values()) {
      outputs.push({
        platform: output.platform,
        state: output.state,
        error: output.error || "",
        restarts: output.restarts,
        bitrateKbps: Math.round(output.bitrateKbps || 0),
        fps: output.startedAt
          ? Math.round(((output.frames || 0) / Math.max(1, (Date.now() - output.startedAt) / 1000)) * 10) / 10
          : 0,
        durationSeconds: output.startedAt
          ? Math.max(0, Math.round((Date.now() - output.startedAt) / 1000))
          : 0
      });
    }

    return {
      running: this.isRunning(),
      channel: this.channel,
      displayName: this.displayName,
      title: this.channelInfo ? this.channelInfo.title : "",
      viewers: this.channelInfo ? this.channelInfo.viewers : 0,
      startedAt: this.startedAt ? new Date(this.startedAt).toISOString() : "",
      uptimeSeconds: this.startedAt
        ? Math.max(0, Math.round((Date.now() - this.startedAt) / 1000))
        : 0,
      outputs,
      error: this.lastError || ""
    };
  }

  async start({ channel, outputs }) {
    const login = normalizeLogin(channel);
    if (!validLogin(login)) {
      throw new RelayError(400, "Escribe el nombre del canal de Twitch (letras, números y guion bajo).");
    }

    const list = [];
    for (const item of outputs || []) {
      const platform = String(item && item.platform || "").trim().toLowerCase();
      if (!platform) { continue; }

      list.push({
        platform,
        url: joinRtmp(item.serverUrl, item.streamKey)
      });
    }

    if (list.length === 0) {
      throw new RelayError(400, "No hay destinos válidos para el relay.");
    }

    if (list.length > config.relay.maxOutputs) {
      throw new RelayError(
        400,
        "El relay admite como máximo " + config.relay.maxOutputs + " destinos."
      );
    }

    const info = await resolveChannel(login);
    if (!info) {
      throw new RelayError(404, "El canal " + login + " no existe en Twitch.");
    }

    if (!info.live) {
      throw new RelayError(
        409,
        "El canal " + info.displayName + " no está en vivo. " +
        "Inicia la emisión desde la Xbox (Guía → Capturar y compartir → " +
        "Emisión en directo) y vuelve a intentarlo."
      );
    }

    // Reconfigurar reinicia el relay completo para no duplicar procesos.
    this.stop();

    this.stopping = false;
    this.channel = info.login;
    this.displayName = info.displayName;
    this.channelInfo = info;
    this.startedAt = Date.now();
    this.lastError = "";

    for (const item of list) {
      const record = {
        platform: item.platform,
        url: item.url,
        state: "connecting",
        error: "",
        restarts: 0,
        bitrateKbps: 0,
        fps: 0,
        frames: 0,
        totalSize: 0,
        startedAt: 0,
        proc: null,
        timer: null,
        stopRequested: false,
        log: []
      };

      this.outputs.set(item.platform, record);
      this.launch(record);
    }

    return this.snapshot();
  }

  async launch(record) {
    if (record.stopRequested) { return; }

    let hls;
    try {
      hls = await resolveHlsUrl(this.channel);
    } catch (err) {
      this.fail(record, err.message);
      return;
    }

    if (!hls) {
      record.state = "waiting";
      record.error = "El canal dejó de estar en vivo.";
      this.scheduleRestart(record, 10000);
      return;
    }

    if (record.stopRequested) { return; }

    const args = [
      "-hide_banner",
      "-loglevel", "warning",
      "-nostdin",
      "-reconnect", "1",
      "-reconnect_streamed", "1",
      "-reconnect_delay_max", "3",
      "-i", hls,
      "-map", "0:v:0",
      "-map", "0:a:0?",
      "-c", "copy",
      "-flvflags", "no_duration_filesize",
      "-f", "flv",
      "-progress", "pipe:1",
      "-nostats",
      record.url
    ];

    let proc;
    try {
      proc = spawn(this.ffmpegPath, args, {
        windowsHide: true,
        stdio: ["ignore", "pipe", "pipe"]
      });
      console.log(
        "[relay] ffmpeg " + record.platform + " pid=" + proc.pid +
        " -> " + record.url.replace(/\/[^/]+$/, "/***")
      );
    } catch (err) {
      this.fail(record, "No se pudo iniciar ffmpeg: " + err.message);
      console.error("[relay] ffmpeg " + record.platform + ": " + err.message);
      return;
    }

    record.proc = proc;
    record.state = "running";
    record.error = "";
    record.startedAt = Date.now();
    record.bitrateKbps = 0;
    record.fps = 0;
    record.frames = 0;
    record.totalSize = 0;

    let buffer = "";
    proc.stdout.on("data", (chunk) => {
      buffer += chunk.toString("utf8");
      const lines = buffer.split(/\r?\n/);
      buffer = lines.pop() || "";

      for (const line of lines) {
        const eq = line.indexOf("=");
        if (eq <= 0) { continue; }
        const key = line.slice(0, eq).trim();
        const value = line.slice(eq + 1).trim();

        if (key === "bitrate") {
          record.bitrateKbps = parseNumber(value);
        } else if (key === "fps") {
          record.fps = parseNumber(value);
        } else if (key === "frame") {
          record.frames = parseNumber(value);
        } else if (key === "total_size") {
          record.totalSize = parseNumber(value);
        }
      }
    });

    proc.stderr.on("data", (chunk) => {
      const text = chunk.toString("utf8");
      const lines = text.split(/\r?\n/).filter((line) => line.trim().length > 0);
      for (const line of lines) {
        record.log.push(line.slice(0, 400));
        if (record.log.length > 20) { record.log.shift(); }
      }
    });

    proc.on("error", (err) => {
      record.proc = null;
      this.fail(record, "ffmpeg: " + err.message);
    });

    proc.on("exit", (code) => {
      record.proc = null;
      record.bitrateKbps = 0;
      record.fps = 0;
      record.frames = 0;

      if (record.stopRequested || this.stopping) {
        record.state = "stopped";
        return;
      }

      const detail = record.log.length
        ? record.log[record.log.length - 1]
        : "ffmpeg terminó con código " + code + ".";

      record.error = detail;
      record.state = "restarting";
      console.error(
        "[relay] ffmpeg " + record.platform + " salió con código " + code +
        ". Reintentando. " + detail
      );
      this.scheduleRestart(record, config.relay.restartDelayMs);
    });
  }

  scheduleRestart(record, delay) {
    if (record.stopRequested || this.stopping) { return; }

    if (record.restarts >= config.relay.restartLimit) {
      record.state = "error";
      record.error =
        "Se agotaron los reintentos para " + record.platform + ". " +
        (record.error || "");
      this.lastError = record.error;
      console.error("[relay] " + record.error);
      return;
    }

    record.restarts += 1;
    clearTimeout(record.timer);
    record.timer = setTimeout(() => {
      record.timer = null;
      this.launch(record);
    }, Math.max(1000, delay));
  }

  fail(record, message) {
    record.state = "error";
    record.error = message || "Error desconocido.";
    this.lastError = record.error;
  }

  stop() {
    this.stopping = true;

    for (const record of this.outputs.values()) {
      record.stopRequested = true;
      record.state = "stopped";
      clearTimeout(record.timer);
      record.timer = null;
      if (record.proc && record.proc.pid) {
        killTree(record.proc.pid);
      }
      record.proc = null;
    }

    this.outputs.clear();
    this.channel = "";
    this.displayName = "";
    this.channelInfo = null;
    this.startedAt = 0;
    this.stopping = false;

    return this.snapshot();
  }
}

const relay = new RelayManager();

module.exports = {
  relay,
  RelayError,
  resolveChannel,
  resolveAvatar,
  resolveHlsUrl,
  normalizeLogin,
  validLogin
};
