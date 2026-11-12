/** Utilidades HTTP compartidas por los proveedores OAuth. */

async function requestJson(url, options = {}) {
  const res = await fetch(url, {
    method: options.method || "GET",
    headers: { accept: "application/json", ...(options.headers || {}) },
    body: options.body
  });

  const text = await res.text();
  let data;
  try {
    data = text ? JSON.parse(text) : {};
  } catch {
    data = { raw: text };
  }

  return { ok: res.ok, status: res.status, data };
}

function postForm(url, params) {
  return requestJson(url, {
    method: "POST",
    headers: { "content-type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams(params).toString()
  });
}

function postJson(url, value, headers = {}) {
  return requestJson(url, {
    method: "POST",
    headers: { "content-type": "application/json", ...headers },
    body: JSON.stringify(value)
  });
}

function getJson(url, headers = {}) {
  return requestJson(url, { headers });
}

function describe(data) {
  if (!data) {
    return "respuesta vacía";
  }
  if (data.error && typeof data.error === "object") {
    return data.error.message || "error del proveedor";
  }
  return data.message || data.error_description || data.error || data.raw || "respuesta inesperada";
}

module.exports = { requestJson, postForm, postJson, getJson, describe };
