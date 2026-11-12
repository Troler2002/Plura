const manual = require("./manual");
const twitch = require("./twitch");
const google = require("./google");
const kick = require("./kick");
const facebook = require("./facebook");

/**
 * Cada plataforma usa su proveedor si tiene credenciales OAuth configuradas.
 * Si no, cae en el modo manual sin cambiar el contrato de la app.
 */
const registry = {
  twitch,
  youtube: google,
  kick,
  facebook,
  tiktok: manual,
  plura: manual
};

function providerForPlatform(platform, { forceManual = false } = {}) {
  if (forceManual) {
    return manual;
  }

  const provider = registry[platform];
  if (provider && provider.isConfigured()) {
    return provider;
  }

  return manual;
}

function providerById(id) {
  if (id === "google") {
    return google;
  }
  return registry[id] || manual;
}

module.exports = { registry, providerForPlatform, providerById, manual };
