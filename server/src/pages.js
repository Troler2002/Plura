const { escapeHtml } = require("./util");

function layout({ title, body, status = "ok" }) {
  const accent = status === "error" ? "#ff6b7a" : "#e60023";

  return `<!doctype html>
<html lang="es">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${escapeHtml(title)} · Plura</title>
<style>
  :root { color-scheme: dark; }
  * { box-sizing: border-box; }
  body {
    margin: 0; min-height: 100vh; padding: 24px;
    background: radial-gradient(1200px 600px at 50% -10%, #1b0207 0%, #050506 55%);
    color: #f4f4f5;
    font-family: "Segoe UI Variable", "Segoe UI", system-ui, -apple-system, sans-serif;
    display: flex; align-items: center; justify-content: center;
  }
  .card {
    width: min(560px, 100%);
    background: #0e0e11;
    border: 1px solid #232329;
    border-radius: 22px;
    padding: 30px;
    box-shadow: 0 30px 80px rgba(0, 0, 0, .65);
  }
  .brand { display: flex; align-items: center; gap: 12px; margin-bottom: 24px; }
  .dot {
    width: 14px; height: 14px; border-radius: 50%; background: ${accent};
    box-shadow: 0 0 24px ${accent};
  }
  .brand b { font-size: 20px; letter-spacing: .18em; }
  h1 { font-size: 25px; line-height: 1.2; margin: 0 0 10px; }
  p { color: #a8a8b3; line-height: 1.55; margin: 0 0 16px; }
  .code {
    font-family: ui-monospace, Consolas, monospace; font-size: 30px;
    letter-spacing: .26em; text-align: center; color: #fff;
    background: #000; border: 1px dashed #3a3a44; border-radius: 14px;
    padding: 14px 18px; margin: 20px 0;
  }
  label {
    display: block; margin: 16px 0 6px; font-size: 12px; color: #a8a8b3;
    text-transform: uppercase; letter-spacing: .09em;
  }
  input {
    width: 100%; padding: 13px 14px; border-radius: 12px;
    border: 1px solid #2c2c35; background: #08080a; color: #fff;
    font-size: 16px; outline: none;
  }
  input:focus { border-color: ${accent}; box-shadow: 0 0 0 3px ${accent}33; }
  button {
    width: 100%; margin-top: 24px; padding: 15px; border: 0; border-radius: 12px;
    background: ${accent}; color: #fff; font-size: 16px; font-weight: 700;
    cursor: pointer;
  }
  button:hover { filter: brightness(1.08); }
  .hint { font-size: 13px; color: #777783; margin-top: 16px; }
  .pill {
    display: inline-block; padding: 7px 13px; border-radius: 999px;
    background: #1a1a20; color: #c9c9d1; font-size: 13px;
  }
  .ok { color: #7ee8a0; }
  .bad { color: #ff6b7a; }
  a { color: ${accent}; word-break: break-all; }
  ul { color: #a8a8b3; line-height: 1.7; padding-left: 20px; }
  code { color: #fff; }
</style>
</head>
<body>
  <main class="card">
    <div class="brand"><span class="dot"></span><b>PLURA</b></div>
    ${body}
  </main>
</body>
</html>`;
}

function homePage({ config, providers, lanUrls }) {
  const rows = providers
    .map((p) => {
      const state = p.configured
        ? '<span class="ok">OAuth configurado</span>'
        : '<span class="bad">modo manual</span>';
      return `<li><b>${escapeHtml(p.label)}</b> · ${state}</li>`;
    })
    .join("");

  return layout({
    title: "Backend",
    body: `
      <h1>Backend de Plura</h1>
      <p>Servicio local para vincular cuentas y entregar a la Xbox una URL RTMP y una clave ya normalizadas.</p>
      <p class="pill">Versión 1.0.0 · puerto ${escapeHtml(config.port)}</p>
      <ul>${rows}</ul>
      <p class="hint">Direcciones de este equipo:</p>
      <ul>${lanUrls.map((u) => `<li><code>${escapeHtml(u)}</code></li>`).join("")}</ul>
      <p class="hint">En la app: Ajustes → Backend → escribe una de estas direcciones y pulsa Probar.</p>
    `
  });
}

function linkPage({ session, defaults = {}, error = "" }) {
  if (session.mode !== "manual") {
    return layout({
      title: "Vincular " + session.platformName,
      body: `
        <h1>Vincula ${escapeHtml(session.platformName)}</h1>
        <p>Abre este enlace en tu teléfono o PC e introduce el código para autorizar Plura:</p>
        <p><a href="${escapeHtml(session.verificationUrl)}" target="_blank" rel="noreferrer">${escapeHtml(session.verificationUrl)}</a></p>
        <div class="code">${escapeHtml(session.code)}</div>
        <p class="hint">La app de Xbox detectará la vinculación automáticamente. Puedes cerrar esta página.</p>
      `
    });
  }

  return layout({
    title: "Vincular " + session.platformName,
    status: error ? "error" : "ok",
    body: `
      <h1>Vincula ${escapeHtml(session.platformName)}</h1>
      <p>Pega la URL RTMP y la clave de transmisión. Se guardarán en este backend y la Xbox las recibirá automáticamente.</p>
      ${error ? `<p class="bad">${escapeHtml(error)}</p>` : ""}
      <p class="pill">Código <b>${escapeHtml(session.code)}</b></p>
      <form method="post" action="/link/confirm">
        <input type="hidden" name="code" value="${escapeHtml(session.code)}">
        <label for="serverUrl">Servidor RTMP</label>
        <input id="serverUrl" name="serverUrl" required spellcheck="false"
               placeholder="rtmp://..." value="${escapeHtml(defaults.serverUrl || "")}">
        <label for="streamKey">Clave de transmisión</label>
        <input id="streamKey" name="streamKey" required autocomplete="off" spellcheck="false"
               placeholder="live_...">
        <label for="accountLabel">Nombre de la cuenta (opcional)</label>
        <input id="accountLabel" name="accountLabel" placeholder="Mi canal">
        <button type="submit">Vincular cuenta</button>
      </form>
      <p class="hint">TikTok: copia la URL y la clave desde TikTok LIVE Center. Plura: usa tu servidor RTMP propio.</p>
    `
  });
}

function donePage({ platformName }) {
  return layout({
    title: "Cuenta vinculada",
    body: `
      <h1>Cuenta vinculada</h1>
      <p><b>${escapeHtml(platformName)}</b> ya está conectada. Vuelve a la app de Xbox: la detección es automática.</p>
      <p class="ok">Todo listo.</p>
    `
  });
}

function errorPage({ title, message }) {
  return layout({
    title,
    status: "error",
    body: `
      <h1>${escapeHtml(title)}</h1>
      <p class="bad">${escapeHtml(message)}</p>
      <p class="hint">Vuelve a la app de Xbox y genera un código nuevo.</p>
    `
  });
}

module.exports = { homePage, linkPage, donePage, errorPage };
