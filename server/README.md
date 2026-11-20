# Plura backend (referencia)

Backend local para el **modo consola** de la app de Xbox. Recibe la emisión nativa de
Twitch y la reparte al resto de plataformas con ffmpeg usando la **Stream Key** que la
app envía en cada petición.

La app **no usa cuentas ni OAuth**: solo crea una sesión, envía el canal y las claves de
los destinos, y consulta el estado del relay. Las rutas de vinculación/OAuth siguen en
este backend de referencia por compatibilidad, pero la app no las llama.

- **Sin dependencias**: solo Node.js 18 o superior (`node:http`, `node:crypto`,
  `fetch` nativo).
- **Relay**: requiere **ffmpeg** instalado en la máquina del backend.
- **Solo Stream Key**: las credenciales de destino llegan en `POST /v1/relay/start`.

## Arrancar

La forma recomendada es el lanzador, que encuentra Node y ffmpeg y **reinicia
el backend automáticamente** si se cae:

```powershell
cd server
.\start-backend.cmd
```

También se puede ejecutar el script directamente:

```powershell
.\start-backend.ps1            # bucle de reinicio
.\start-backend.ps1 -Once      # una sola vez, sin reinicio
```

Para que arranque solo al iniciar sesión en Windows hay una tarea programada
llamada **PluraBackend** (se puede ver en el Programador de tareas). Si se
prefiere arrancar a mano:

```powershell
cd server
$env:FFMPEG_PATH = "C:\ruta\a\ffmpeg.exe"   # o ten ffmpeg en el PATH
node src/index.js
```

El servidor escucha en `0.0.0.0:8787` y muestra por consola las direcciones LAN.
En la app de Xbox: **Backend de Plura** → `http://IP-DEL-PC:8787` → **Probar backend**.

Si el backend no está encendido, la app muestra **SIN BACKEND** y explica cómo
arrancarlo; el relay no se puede iniciar hasta que la consola vuelva a conectar.

Variables de entorno útiles:

| Variable | Por defecto | Uso |
|---|---|---|
| `PORT` | `8787` | Puerto HTTP |
| `HOST` | `0.0.0.0` | Interfaz de escucha |
| `PUBLIC_BASE_URL` | `http://IP-LAN:8787` | URL que verá el teléfono/PC al vincular |
| `DATA_FILE` | `server/data/state.json` | Persistencia de usuarios y vínculos |
| `LINK_TTL_SECONDS` | `300` | Caducidad de los códigos |
| `POLL_INTERVAL_SECONDS` | `2` | Intervalo sugerido a la app |
| `FFMPEG_PATH` | `ffmpeg` | Binario de ffmpeg para el relay (o `server/bin/ffmpeg.exe`) |
| `TWITCH_PLAYBACK_CLIENT_ID` | Client-ID público web | Cliente GQL para pedir el HLS de Twitch |
| `RELAY_MAX_OUTPUTS` | `6` | Máximo de destinos simultáneos |
| `RELAY_RESTART_LIMIT` | `20` | Reintentos por destino antes de marcarlo en error |
| `RELAY_RESTART_DELAY_MS` | `4000` | Espera entre reintentos |

## Cómo funciona la vinculación

1. La app llama a `POST /v1/link/start`.
2. El backend crea una sesión y responde con un **código**, una **URL de
   verificación** y un **token de sesión**.
3. El usuario abre la URL en el teléfono/PC:
   - **Modo manual**: pega la URL RTMP y la clave en el formulario.
   - **OAuth de dispositivo** (Twitch, YouTube): introduce el código en la
     página del proveedor.
   - **OAuth con redirección** (Kick, Facebook): autoriza y el proveedor vuelve
     a `/oauth/callback/...`.
4. La app consulta `GET /v1/link/poll` cada pocos segundos hasta `linked`.
5. Al iniciar el stream, la app pide `GET /v1/ingest/:platform` con
   `Authorization: Bearer <sessionToken>` y recibe las credenciales.

## API

| Método | Ruta | Auth | Respuesta |
|---|---|---|---|
| `GET` | `/v1/health` | — | `{ name, version, providers[], relay }` |
| `POST` | `/v1/session` | Bearer opcional | `{ sessionToken }` (crea o reutiliza la sesión) |
| `POST` | `/v1/link/start` | Bearer opcional | `{ sessionToken, code, verificationUrl, interval, expiresIn, mode, status }` |
| `GET` | `/v1/link/poll?platform=&code=` | — | `{ status: pending\|linked\|expired\|error, accountLabel?, error? }` |
| `GET` | `/v1/ingest/:platform` | Bearer | `{ platform, serverUrl, streamKey, accountLabel }` |
| `POST` | `/v1/link/unlink` | Bearer | `{ ok: true, platform }` |
| `GET` | `/v1/relay/status` | Bearer | `{ running, channel, outputs[], error }` |
| `POST` | `/v1/relay/start` | Bearer | `{ channel, outputs: [{ platform, serverUrl, streamKey }] }` |
| `POST` | `/v1/relay/stop` | Bearer | `{ running: false }` |
| `GET` | `/v1/relay/channel?login=` | Bearer | `{ login, displayName, live, title, viewers }` |
| `GET` | `/v1/twitch/channel` | Bearer | Canal vinculado (legado, la app no lo usa) |
| `GET` | `/link?code=` | — | Página de vinculación (legado) |
| `POST` | `/link/confirm` | — | Formulario del modo manual (legado) |
| `GET` | `/oauth/callback/:provider` | — | Retorno de Kick/Facebook (legado) |

> La app de Xbox usa únicamente `/v1/session`, `/v1/relay/*` y
> `/v1/relay/channel`. Las rutas `/v1/link/*`, `/v1/ingest/*` y `/oauth/*` son
> legado del backend de referencia y no forman parte del flujo de la app.

Cuerpo de `POST /v1/link/start`:

```json
{
  "platform": "twitch",
  "platformName": "Twitch",
  "deviceName": "Xbox · Plura",
  "mode": "auto"
}
```

`mode: "manual"` fuerza el formulario aunque el proveedor tenga OAuth.

Plataformas aceptadas: `twitch`, `youtube`, `kick`, `facebook`, `tiktok`,
`plura`.

## Proveedores OAuth

Sin variables de entorno, **todas** las plataformas usan el formulario manual.
Con credenciales, el backend activa el flujo automático correspondiente.

### Twitch (device code)

```powershell
$env:TWITCH_CLIENT_ID = "..."
$env:TWITCH_CLIENT_SECRET = "..."
```

- App en <https://dev.twitch.tv/console/apps>.
- Scope: `channel:read:stream_key`.
- Flujo: `https://id.twitch.tv/oauth2/device` y `twitch.tv/activate`.
- Ingesta: `rtmp://live.twitch.tv/app/`.

### YouTube (device code)

```powershell
$env:GOOGLE_CLIENT_ID = "..."
$env:GOOGLE_CLIENT_SECRET = "..."
```

- Cliente OAuth de tipo **TVs and Limited Input devices** en Google Cloud.
- Activa **YouTube Data API v3**.
- Scope: `https://www.googleapis.com/auth/youtube`.
- El canal debe tener **transmisión en directo** habilitada.
- El backend crea un `liveStream` y usa `cdn.ingestionInfo` (servidor + clave).

### Kick (redirección + PKCE)

```powershell
$env:KICK_CLIENT_ID = "..."
$env:KICK_CLIENT_SECRET = "..."
```

- App en el portal de desarrolladores de Kick.
- Redirect URI: `http://IP-DEL-PC:8787/oauth/callback/kick`.
- Scopes: `user:read channel:read streamkey:read`.
- Ingesta: `rtmps://fa723fc1b171.global-contribute.live-video.net:443/app/`.

### Facebook Gaming (redirección)

```powershell
$env:FACEBOOK_APP_ID = "..."
$env:FACEBOOK_APP_SECRET = "..."
```

- App de Facebook con **Facebook Login**.
- Redirect URI: `http://IP-DEL-PC:8787/oauth/callback/facebook`.
- Permisos: `pages_show_list`, `pages_read_engagement`,
  `pages_manage_posts`, `publish_video`.
- El backend crea `POST /{page-id}/live_videos` y divide `stream_url` en
  servidor + clave.
- Facebook suele exigir **HTTPS** para redirect URIs fuera de `localhost`; para
  producción pon el backend detrás de HTTPS.

### TikTok y Plura

- **TikTok**: siempre manual. La URL RTMP y la clave son por cuenta/región y se
  copian de TikTok LIVE Center.
- **Plura**: manual con tu propio servidor. Opcionalmente:

```powershell
$env:PLURA_INGEST_SERVER = "rtmp://tu-servidor/live/"
$env:PLURA_STREAM_KEY = "opcional"
```

## Probar el flujo completo

```powershell
$b = "http://127.0.0.1:8787"

# 1. Crear la vinculación (forzando manual para no necesitar OAuth)
$s = Invoke-RestMethod "$b/v1/link/start" -Method Post `
  -ContentType "application/json" `
  -Body '{"platform":"twitch","platformName":"Twitch","mode":"manual"}'
$s | ConvertTo-Json

# 2. Abre $s.verificationUrl y envía el formulario, o confirma por HTTP:
Invoke-WebRequest "$b/link/confirm" -Method Post `
  -ContentType "application/x-www-form-urlencoded" `
  -Body @{ code=$s.code; serverUrl="rtmp://127.0.0.1:1935/live/"; streamKey="main"; accountLabel="Prueba" } `
  -UseBasicParsing | Select-Object StatusCode

# 3. La app (o este comando) ve el estado vinculado
Invoke-RestMethod "$b/v1/link/poll?platform=twitch&code=$($s.code)"

# 4. Credenciales normalizadas para el motor de video
Invoke-RestMethod "$b/v1/ingest/twitch" -Headers @{
  Authorization = "Bearer $($s.sessionToken)"
}
```

## Persistencia y seguridad

- `server/data/state.json` contiene usuarios, tokens de sesión, tokens OAuth y
  credenciales de ingesta. En esta referencia está **en texto plano**.
- Para producción: cifra el archivo, usa HTTPS, rota los secretos y limita el
  acceso a la LAN.
- Los códigos caducan a los 5 minutos. Los tokens de sesión son aleatorios de
  32 bytes y se comparan en tiempo constante.
- El endpoint de polling no requiere token (igual que el flujo de dispositivo
  estándar); los códigos son de un solo uso y caducan.

## Limitaciones

- Es una **referencia** para probar el contrato, no un servicio en la nube.
- Los flujos OAuth dependen de que cada proveedor apruebe la app, los scopes y
  la redirect URI.
- Las credenciales de TikTok no se pueden obtener por API: usa el modo manual.
- Facebook puede rotar o cerrar el `live_video`; si el directo caduca, vuelve a
  vincular la cuenta.
