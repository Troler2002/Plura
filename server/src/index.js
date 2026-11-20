const http = require("node:http");
const os = require("node:os");
const { URL } = require("node:url");

const config = require("./config");
const { Store } = require("./store");
const pages = require("./pages");
const {
  newCode,
  newToken,
  newId,
  safeEqual,
  readJson,
  readForm,
  json,
  html,
  redirect
} = require("./util");
const { registry, providerForPlatform, providerById } = require("./providers");
const { relay, RelayError, resolveChannel, resolveAvatar, normalizeLogin } = require("./relay");

const store = new Store(config.dataFile);

const PLATFORM_LABELS = {
  twitch: "Twitch",
  youtube: "YouTube",
  kick: "Kick",
  facebook: "Facebook Gaming",
  tiktok: "TikTok",
  plura: "Plura"
};

const INGEST_DEFAULTS = {
  twitch: config.providers.twitch.serverUrl,
  youtube: config.providers.youtube.serverUrl,
  kick: config.providers.kick.serverUrl,
  facebook: config.providers.facebook.serverUrl,
  plura: config.providers.plura.serverUrl
};

// ---------------------------------------------------------------- utilidades

function normalizePlatform(value) {
  const id = String(value || "").trim().toLowerCase();
  return Object.prototype.hasOwnProperty.call(PLATFORM_LABELS, id) ? id : "";
}

function normalizeServer(value) {
  let server = String(value || "").trim().replace(/^"|"$/g, "").trim();
  if (!server) {
    return "";
  }
  if (!/^[a-z][a-z0-9+.-]*:\/\//i.test(server)) {
    server = "rtmp://" + server;
  }
  return server.endsWith("/") ? server : server + "/";
}

function validateIngest(serverUrl, streamKey) {
  if (!serverUrl) {
    return "Falta la URL del servidor RTMP.";
  }
  if (!/^rtmps?:\/\//i.test(serverUrl)) {
    return "La URL debe empezar por rtmp:// o rtmps://.";
  }
  try {
    const parsed = new URL(serverUrl);
    if (!parsed.hostname) {
      return "La URL del servidor no tiene host.";
    }
  } catch {
    return "La URL del servidor no es válida.";
  }
  if (!streamKey) {
    return "Falta la clave de transmisión.";
  }
  return "";
}

function clamp(value, min, max) {
  return Math.min(max, Math.max(min, value));
}

function bearer(req) {
  const header = req.headers.authorization || "";
  const match = /^Bearer\s+(.+)$/i.exec(header);
  return match ? match[1].trim() : "";
}

function userFromRequest(req) {
  const token = bearer(req);
  if (!token) {
    return null;
  }

  for (const user of Object.values(store.data.users)) {
    if (user.token && safeEqual(user.token, token)) {
      return user;
    }
  }

  return null;
}

function ensureUser(req) {
  const existing = userFromRequest(req);
  if (existing) {
    return { user: existing, token: existing.token };
  }

  const user = {
    id: newId("usr"),
    token: newToken(),
    createdAt: Date.now(),
    links: {}
  };

  store.data.users[user.id] = user;
  store.save();
  return { user, token: user.token };
}

function recentSessions() {
  return Object.values(store.data.sessions).sort((a, b) => b.createdAt - a.createdAt);
}

function findSessionByCode(platform, code) {
  const target = String(code || "").toUpperCase();
  for (const session of recentSessions()) {
    if (session.platform === platform && String(session.code).toUpperCase() === target) {
      return session;
    }
  }
  return null;
}

function findSessionByCodeAny(code) {
  const target = String(code || "").toUpperCase();
  for (const session of recentSessions()) {
    if (String(session.code).toUpperCase() === target) {
      return session;
    }
  }
  return null;
}

function pruneSessions() {
  const now = Date.now();
  for (const [id, session] of Object.entries(store.data.sessions)) {
    if (now > session.expiresAt + 60 * 60 * 1000) {
      delete store.data.sessions[id];
    }
  }
}

function persistLink(session) {
  const user = store.data.users[session.userId];
  if (user && session.link) {
    user.links[session.platform] = session.link;
  }
  store.save();
}

async function jsonBody(req, res) {
  try {
    return await readJson(req);
  } catch (err) {
    json(res, 400, { error: err.message });
    return null;
  }
}

async function formBody(req, res) {
  try {
    return await readForm(req);
  } catch (err) {
    html(res, 400, pages.errorPage({ title: "Petición no válida", message: err.message }));
    return null;
  }
}

function lanUrls() {
  const urls = [];
  for (const list of Object.values(os.networkInterfaces())) {
    for (const addr of list || []) {
      if (addr.family === "IPv4" && !addr.internal) {
        urls.push(`http://${addr.address}:${config.port}`);
      }
    }
  }
  if (urls.length === 0) {
    urls.push(`http://localhost:${config.port}`);
  }
  return urls;
}

// ---------------------------------------------------------------- rutas API

async function handleHealth(req, res) {
  const providers = ["twitch", "youtube", "kick", "facebook"].map((platform) => {
    const provider = registry[platform];
    return {
      platform,
      configured: Boolean(provider && provider.isConfigured()),
      mode: provider ? provider.mode : "manual"
    };
  });

  json(res, 200, {
    name: "Plura backend",
    version: "1.0.0",
    time: new Date().toISOString(),
    providers,
    relay: {
      ffmpeg: relay.ffmpegAvailable(),
      running: relay.isRunning(),
      channel: relay.channel
    }
  });
}

async function handleLinkStart(req, res) {
  const body = await jsonBody(req, res);
  if (body === null) {
    return;
  }

  const platform = normalizePlatform(body.platform);
  if (!platform) {
    json(res, 400, { error: "Plataforma no válida." });
    return;
  }

  pruneSessions();

  const { user, token } = ensureUser(req);
  const provider = providerForPlatform(platform, { forceManual: body.mode === "manual" });

  const session = {
    id: newId("ses"),
    code: newCode(6),
    platform,
    platformName: String(body.platformName || PLATFORM_LABELS[platform]),
    userId: user.id,
    provider: provider.id,
    mode: provider.mode,
    state: "pending",
    error: "",
    createdAt: Date.now(),
    expiresAt: Date.now() + config.linkTtlSeconds * 1000,
    interval: config.pollIntervalSeconds,
    verificationUrl: ""
  };

  let started;
  try {
    started = await provider.startLink({
      session,
      config,
      baseUrl: config.publicBaseUrl,
      platform,
      platformName: session.platformName
    });
  } catch (err) {
    json(res, 502, { error: err.message });
    return;
  }

  if (started.code) {
    session.code = String(started.code);
  }
  session.verificationUrl = String(started.verificationUrl || "");
  session.interval = clamp(Number(started.interval) || config.pollIntervalSeconds, 1, 30);
  session.expiresAt = Date.now() + (Number(started.expiresIn) || config.linkTtlSeconds) * 1000;

  if (findSessionByCode(platform, session.code)) {
    json(res, 409, {
      error: "Ya hay una vinculación activa con ese código. Inténtalo de nuevo."
    });
    return;
  }

  store.data.sessions[session.id] = session;
  store.save();

  json(res, 200, {
    sessionToken: token,
    code: session.code,
    verificationUrl: session.verificationUrl,
    interval: session.interval,
    expiresIn: Math.max(1, Math.round((session.expiresAt - Date.now()) / 1000)),
    mode: session.mode,
    status: "pending"
  });
}

async function handleLinkPoll(req, res, url) {
  const platform = normalizePlatform(url.searchParams.get("platform"));
  const code = url.searchParams.get("code") || "";

  if (!platform) {
    json(res, 400, { error: "Plataforma no válida." });
    return;
  }

  const session = findSessionByCode(platform, code);
  if (!session) {
    json(res, 404, { error: "Vinculación no encontrada o caducada." });
    return;
  }

  if (session.state === "linked") {
    json(res, 200, { status: "linked", accountLabel: session.accountLabel || "" });
    return;
  }

  if (session.state === "expired" || Date.now() > session.expiresAt) {
    session.state = "expired";
    store.save();
    json(res, 200, { status: "expired" });
    return;
  }

  if (session.state === "error") {
    json(res, 200, { status: "error", error: session.error || "Error de vinculación." });
    return;
  }

  const provider = providerById(session.provider);

  let result;
  try {
    result = await provider.pollLink({ session, config });
  } catch (err) {
    result = { status: "error", error: err.message };
  }

  if (result.status === "linked") {
    session.state = "linked";
    session.accountLabel = result.accountLabel || session.accountLabel || "";
    persistLink(session);
    json(res, 200, { status: "linked", accountLabel: session.accountLabel });
    return;
  }

  if (result.status === "expired") {
    session.state = "expired";
    store.save();
    json(res, 200, { status: "expired" });
    return;
  }

  if (result.status === "error") {
    session.state = "error";
    session.error = result.error || "Error de vinculación.";
    store.save();
    json(res, 200, { status: "error", error: session.error });
    return;
  }

  json(res, 200, { status: "pending" });
}

async function handleIngest(req, res, platform) {
  const user = userFromRequest(req);
  if (!user) {
    json(res, 401, { error: "Sesión no válida. Vincula la cuenta desde Plura." });
    return;
  }

  const link = user.links[platform];
  if (!link) {
    json(res, 404, {
      error: "La cuenta de " + PLATFORM_LABELS[platform] + " no está vinculada."
    });
    return;
  }

  const provider = providerById(link.provider);

  try {
    const ingest = await provider.resolveIngest({ link, config });
    if (!ingest || !ingest.serverUrl || !ingest.streamKey) {
      throw new Error("El backend no tiene credenciales válidas para esta plataforma.");
    }

    store.save();
    json(res, 200, {
      platform,
      serverUrl: ingest.serverUrl,
      streamKey: ingest.streamKey,
      accountLabel: ingest.accountLabel || link.accountLabel || ""
    });
  } catch (err) {
    json(res, 502, { error: err.message });
  }
}

async function handleUnlink(req, res) {
  const user = userFromRequest(req);
  if (!user) {
    json(res, 401, { error: "Sesión no válida." });
    return;
  }

  const body = await jsonBody(req, res);
  if (body === null) {
    return;
  }

  const platform = normalizePlatform(body.platform);
  if (!platform) {
    json(res, 400, { error: "Plataforma no válida." });
    return;
  }

  delete user.links[platform];

  for (const [id, session] of Object.entries(store.data.sessions)) {
    if (session.userId === user.id && session.platform === platform) {
      delete store.data.sessions[id];
    }
  }

  store.save();
  json(res, 200, { ok: true, platform });
}

// ------------------------------------------------------- relay modo consola

function requireUser(req, res) {
  const user = userFromRequest(req);
  if (!user) {
    json(res, 401, {
      error: "Sesión no válida. Vuelve a abrir Plura e inténtalo de nuevo."
    });
    return null;
  }
  return user;
}

/**
 * Crea (o reutiliza) la sesión de la consola. La app la pide la primera vez
 * que usa el modo consola; el token identifica al usuario ante el relay.
 */
async function handleSession(req, res) {
  const { token } = ensureUser(req);
  json(res, 200, { sessionToken: token });
}

/**
 * Ajustes que la app de la consola conserva aunque se reinstale: canal de
 * Twitch y Stream Keys. Viven en el backend (en la red local del usuario) para
 * no perderse al actualizar o reinstalar la app.
 */
function handleGetSettings(req, res) {
  if (!requireUser(req, res)) { return; }
  const saved = store.data.settings || {};
  json(res, 200, {
    twitchChannel: saved.twitchChannel || "",
    platforms: saved.platforms || {}
  });
}

async function handleSaveSettings(req, res) {
  if (!requireUser(req, res)) { return; }
  const body = await jsonBody(req, res);
  if (body === null) { return; }

  const channel = typeof body.twitchChannel === "string"
    ? body.twitchChannel.trim().toLowerCase()
    : "";

  const platforms = {};
  if (body.platforms && typeof body.platforms === "object") {
    for (const [id, value] of Object.entries(body.platforms)) {
      if (!/^[a-z0-9_-]+$/i.test(id)) { continue; }
      if (!value || typeof value !== "object") { continue; }
      platforms[id] = {
        streamKey: typeof value.streamKey === "string" ? value.streamKey : "",
        enabled: Boolean(value.enabled)
      };
    }
  }

  store.data.settings = { twitchChannel: channel, platforms };
  store.save();
  json(res, 200, { ok: true });
}

async function handleRelayStatus(req, res) {
  if (!requireUser(req, res)) { return; }
  json(res, 200, relay.snapshot());
}

async function handleRelayStart(req, res) {
  if (!requireUser(req, res)) { return; }

  const body = await jsonBody(req, res);
  if (body === null) { return; }

  try {
    const status = await relay.start({
      channel: body.channel,
      outputs: body.outputs
    });
    console.log(
      "[relay] iniciado canal=" + status.channel +
      " destinos=" + status.outputs.map((o) => o.platform).join(",")
    );
    json(res, 200, status);
  } catch (err) {
    const code = err instanceof RelayError ? err.status : 502;
    console.error("[relay] no se pudo iniciar (" + code + "): " + err.message);
    json(res, code, { error: err.message });
  }
}

async function handleRelayStop(req, res) {
  if (!requireUser(req, res)) { return; }
  console.log("[relay] detenido por la app");
  json(res, 200, relay.stop());
}

async function handleRelayChannel(req, res, url) {
  if (!requireUser(req, res)) { return; }

  try {
    const info = await resolveChannel(normalizeLogin(url.searchParams.get("login")));
    if (!info) {
      json(res, 404, { error: "Ese canal no existe en Twitch." });
      return;
    }
    json(res, 200, info);
  } catch (err) {
    json(res, err instanceof RelayError ? err.status : 502, { error: err.message });
  }
}

/** Foto del canal servida por el backend, sin depender de la CDN en la consola. */
async function handleTwitchAvatar(req, res, url) {
  try {
    const avatar = await resolveAvatar(normalizeLogin(url.searchParams.get("login")));
    if (!avatar) {
      res.writeHead(404, { "Cache-Control": "no-store" });
      res.end();
      return;
    }

    const bytes = Buffer.from(avatar.base64, "base64");
    res.writeHead(200, {
      "Content-Type": avatar.contentType || "image/png",
      "Content-Length": bytes.length,
      "Cache-Control": "public, max-age=600"
    });
    res.end(bytes);
  } catch (err) {
    res.writeHead(err instanceof RelayError ? err.status : 502);
    res.end();
  }
}

/** Canal de la cuenta de Twitch vinculada (para no escribirlo a mano). */
async function handleTwitchChannel(req, res) {
  const user = requireUser(req, res);
  if (!user) { return; }

  const link = user.links.twitch;
  if (!link || !link.tokens) {
    json(res, 404, {
      error: "Vincula tu cuenta de Twitch para detectar el canal automáticamente."
    });
    return;
  }

  const provider = providerById(link.provider);
  if (typeof provider.channelInfo !== "function") {
    json(res, 404, {
      error: "Este vínculo no permite detectar el canal. Escríbelo manualmente."
    });
    return;
  }

  try {
    const info = await provider.channelInfo({ link, config });
    json(res, 200, info);
  } catch (err) {
    json(res, 502, { error: err.message });
  }
}

// ---------------------------------------------------------------- páginas web

async function handleLinkPage(req, res, url) {
  const code = url.searchParams.get("code") || "";
  const session = findSessionByCodeAny(code);

  if (!session) {
    html(res, 404, pages.errorPage({
      title: "Código no válido",
      message: "Este código no existe o ya se usó. Genera uno nuevo desde la app de Xbox."
    }));
    return;
  }

  if (session.state === "linked") {
    html(res, 200, pages.donePage({ platformName: session.platformName }));
    return;
  }

  if (session.state === "expired" || Date.now() > session.expiresAt) {
    html(res, 410, pages.errorPage({
      title: "Código caducado",
      message: "El código ya no es válido. Genera uno nuevo desde la app de Xbox."
    }));
    return;
  }

  html(res, 200, pages.linkPage({
    session,
    defaults: { serverUrl: INGEST_DEFAULTS[session.platform] || "" }
  }));
}

async function handleLinkConfirm(req, res) {
  const form = await formBody(req, res);
  if (form === null) {
    return;
  }

  const session = findSessionByCodeAny(form.code);

  if (!session) {
    html(res, 404, pages.errorPage({
      title: "Código no válido",
      message: "Este código no existe o ya se usó."
    }));
    return;
  }

  if (session.mode !== "manual") {
    html(res, 400, pages.errorPage({
      title: "Vinculación automática",
      message: "Esta plataforma usa OAuth. Completa la autorización en la página del proveedor."
    }));
    return;
  }

  if (session.state === "linked") {
    redirect(res, "/link/done?platform=" + encodeURIComponent(session.platform));
    return;
  }

  if (Date.now() > session.expiresAt) {
    html(res, 410, pages.errorPage({
      title: "Código caducado",
      message: "El código ya no es válido. Genera uno nuevo desde la app de Xbox."
    }));
    return;
  }

  const serverUrl = normalizeServer(form.serverUrl);
  const streamKey = String(form.streamKey || "").trim();
  const error = validateIngest(serverUrl, streamKey);

  if (error) {
    html(res, 400, pages.linkPage({
      session,
      defaults: { serverUrl },
      error
    }));
    return;
  }

  const accountLabel =
    String(form.accountLabel || "").trim() || session.platformName;

  session.state = "linked";
  session.accountLabel = accountLabel;
  session.link = {
    provider: "manual",
    mode: "manual",
    accountLabel,
    ingest: { serverUrl, streamKey }
  };

  persistLink(session);
  redirect(res, "/link/done?platform=" + encodeURIComponent(session.platform));
}

async function handleOAuthCallback(req, res, url, providerId) {
  const provider = providerById(providerId);
  const state = url.searchParams.get("state") || "";
  const session = store.data.sessions[state];

  if (!session || session.provider !== providerId) {
    html(res, 400, pages.errorPage({
      title: "Vinculación no válida",
      message: "No encontramos la sesión de vinculación. Genera un código nuevo desde la app."
    }));
    return;
  }

  if (typeof provider.handleCallback !== "function") {
    html(res, 400, pages.errorPage({
      title: "Proveedor no compatible",
      message: "Este proveedor no admite vinculación por redirección."
    }));
    return;
  }

  const query = {};
  for (const [key, value] of url.searchParams) {
    query[key] = value;
  }

  try {
    const result = await provider.handleCallback({ query, session, config });
    session.state = "linked";
    session.accountLabel = result.accountLabel || session.accountLabel || "";
    persistLink(session);
    html(res, 200, pages.donePage({ platformName: session.platformName }));
  } catch (err) {
    session.state = "error";
    session.error = err.message;
    store.save();
    html(res, 502, pages.errorPage({
      title: "No se pudo vincular",
      message: err.message
    }));
  }
}

// ---------------------------------------------------------------- servidor

async function handle(req, res) {
  const url = new URL(req.url, "http://" + (req.headers.host || "localhost"));
  const path = url.pathname.replace(/\/+$/, "") || "/";

  res.setHeader("access-control-allow-origin", "*");
  res.setHeader("access-control-allow-headers", "authorization, content-type");
  res.setHeader("access-control-allow-methods", "GET, POST, OPTIONS");

  if (req.method === "OPTIONS") {
    res.writeHead(204);
    res.end();
    return;
  }

  if (req.method === "GET" && path === "/") {
    const providers = [
      { label: "Twitch", configured: registry.twitch.isConfigured() },
      { label: "YouTube", configured: registry.youtube.isConfigured() },
      { label: "Kick", configured: registry.kick.isConfigured() },
      { label: "Facebook Gaming", configured: registry.facebook.isConfigured() }
    ];

    html(res, 200, pages.homePage({ config, providers, lanUrls: lanUrls() }));
    return;
  }

  if (req.method === "GET" && path === "/v1/health") {
    await handleHealth(req, res);
    return;
  }

  if (req.method === "POST" && path === "/v1/link/start") {
    await handleLinkStart(req, res);
    return;
  }

  if (req.method === "POST" && path === "/v1/session") {
    await handleSession(req, res);
    return;
  }

  if (req.method === "GET" && path === "/v1/settings") {
    handleGetSettings(req, res);
    return;
  }

  if (req.method === "POST" && path === "/v1/settings") {
    await handleSaveSettings(req, res);
    return;
  }

  if (req.method === "GET" && path === "/v1/relay/status") {
    await handleRelayStatus(req, res);
    return;
  }

  if (req.method === "POST" && path === "/v1/relay/start") {
    await handleRelayStart(req, res);
    return;
  }

  if (req.method === "POST" && path === "/v1/relay/stop") {
    await handleRelayStop(req, res);
    return;
  }

  if (req.method === "GET" && path === "/v1/relay/channel") {
    await handleRelayChannel(req, res, url);
    return;
  }

  if (req.method === "GET" && path === "/v1/twitch/avatar") {
    await handleTwitchAvatar(req, res, url);
    return;
  }

  if (req.method === "GET" && path === "/v1/twitch/channel") {
    await handleTwitchChannel(req, res);
    return;
  }

  if (req.method === "GET" && path === "/v1/link/poll") {
    await handleLinkPoll(req, res, url);
    return;
  }

  if (req.method === "POST" && path === "/v1/link/unlink") {
    await handleUnlink(req, res);
    return;
  }

  const ingestMatch = /^\/v1\/ingest\/([a-z0-9_-]+)$/i.exec(path);
  if (req.method === "GET" && ingestMatch) {
    const platform = normalizePlatform(ingestMatch[1]);
    if (!platform) {
      json(res, 400, { error: "Plataforma no válida." });
      return;
    }
    await handleIngest(req, res, platform);
    return;
  }

  if (req.method === "GET" && path === "/link") {
    await handleLinkPage(req, res, url);
    return;
  }

  if (req.method === "POST" && path === "/link/confirm") {
    await handleLinkConfirm(req, res);
    return;
  }

  if (req.method === "GET" && path === "/link/done") {
    const platform = normalizePlatform(url.searchParams.get("platform"));
    html(res, 200, pages.donePage({
      platformName: PLATFORM_LABELS[platform] || "la plataforma"
    }));
    return;
  }

  const callbackMatch = /^\/oauth\/callback\/([a-z0-9_-]+)$/i.exec(path);
  if (req.method === "GET" && callbackMatch) {
    await handleOAuthCallback(req, res, url, callbackMatch[1].toLowerCase());
    return;
  }

  json(res, 404, { error: "Ruta no encontrada." });
}

const server = http.createServer((req, res) => {
  const startedAt = Date.now();
  const label = req.method + " " + (req.url || "/");

  // Registro de peticiones: ayuda a ver si la consola llega al backend.
  // El sondeo de salud (/v1/health) solo se registra si falla.
  res.on("finish", () => {
    const isHealth = (req.url || "").split("?")[0] === "/v1/health";
    if (isHealth && res.statusCode < 400) { return; }
    console.log(
      "[http] " + label + " -> " + res.statusCode +
      " (" + (Date.now() - startedAt) + " ms)"
    );
  });

  handle(req, res).catch((err) => {
    console.error("[http] " + label + " falló: " + (err && err.stack ? err.stack : err));
    if (!res.headersSent) {
      json(res, 500, { error: "Error interno del backend." });
    } else {
      res.end();
    }
  });
});

// El backend no debe caerse por un error aislado (Twitch, ffmpeg o red).
// Registramos el fallo y seguimos sirviendo al resto de destinos.
process.on("unhandledRejection", (reason) => {
  console.error(
    "[unhandledRejection] " + (reason && reason.stack ? reason.stack : reason)
  );
});

process.on("uncaughtException", (err) => {
  console.error("[uncaughtException] " + (err && err.stack ? err.stack : err));
});

server.listen(config.port, config.host, () => {
  const configured = [
    ["Twitch", registry.twitch.isConfigured()],
    ["YouTube", registry.youtube.isConfigured()],
    ["Kick", registry.kick.isConfigured()],
    ["Facebook", registry.facebook.isConfigured()]
  ]
    .filter(([, ok]) => ok)
    .map(([name]) => name);

  console.log("Plura backend escuchando en http://" + config.host + ":" + config.port);
  for (const address of lanUrls()) {
    console.log("  LAN: " + address);
  }
  console.log(
    "Proveedores OAuth: " +
      (configured.length > 0 ? configured.join(", ") : "ninguno (modo manual)")
  );
  console.log("Datos: " + config.dataFile);
});

function shutdown() {
  try { relay.stop(); } catch { }
  store.flush();
  server.close(() => process.exit(0));
  setTimeout(() => process.exit(0), 1500).unref();
}

process.on("SIGINT", shutdown);
process.on("SIGTERM", shutdown);
