const fs = require("node:fs");
const path = require("node:path");

/**
 * Persistencia mínima en JSON. La escritura se agrupa para no golpear el
 * disco en cada petición y se hace con rename atómico.
 */
class Store {
  constructor(file) {
    this.file = file;
    this.data = { users: {}, sessions: {} };
    this.timer = null;
    this.load();
  }

  load() {
    try {
      const raw = fs.readFileSync(this.file, "utf8");
      const parsed = JSON.parse(raw);
      if (parsed && typeof parsed === "object") {
        this.data.users = parsed.users || {};
        this.data.sessions = parsed.sessions || {};
      }
    } catch (err) {
      if (err.code !== "ENOENT") {
        console.warn("[store] no se pudo leer " + this.file + ": " + err.message);
      }
    }
  }

  save() {
    if (this.timer) {
      return;
    }

    this.timer = setTimeout(() => {
      this.timer = null;
      this.flush();
    }, 150);

    if (typeof this.timer.unref === "function") {
      this.timer.unref();
    }
  }

  flush() {
    try {
      fs.mkdirSync(path.dirname(this.file), { recursive: true });
      const tmp = this.file + ".tmp";
      fs.writeFileSync(tmp, JSON.stringify(this.data, null, 2));
      fs.renameSync(tmp, this.file);
    } catch (err) {
      console.error("[store] no se pudo guardar " + this.file + ": " + err.message);
    }
  }
}

module.exports = { Store };
