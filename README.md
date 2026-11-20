# Plura

**Plura** es una app **nativa para Xbox** (UWP, C# + XAML, sin WebView) para transmitir
a varias plataformas a la vez desde la propia consola: **Twitch, YouTube, Kick y
Facebook Gaming**.

Solo se incluyen redes que emiten con una **Stream Key** sobre un servidor de ingesta
fijo. Plura **no pide cuentas, contraseñas ni OAuth**: tú pegas la clave de cada red.

Todo funciona **solo con la consola**. No necesitas PC junto a la Xbox, ni capturadora,
ni OBS:

```
Xbox  ──Transmitir (nativo)──►  Twitch
                                   │  HLS
                                   ▼
                         Backend de Plura (ffmpeg -c copy)
                                   │
                                   ▼
                 YouTube · Kick · Facebook Gaming
                                   ▲
                                   │  LAN / Internet
                            Xbox con Plura
```

La interfaz es **roja y negra**, se maneja con el **mando** y usa **switches** para
elegir exactamente a qué plataformas quieres transmitir (todas o solo algunas).

---

## Cómo funciona

1. La **Xbox** emite con su función nativa a Twitch:
   **Guía → Capturar y compartir → Emisión en directo → Twitch → Ir en directo**.
2. El **backend de Plura** recibe el HLS de Twitch y lo reenvía **sin recodificar**
   (`-c copy`) a las plataformas que actives.
3. **Twitch es el origen**, no un destino: las demás plataformas son los destinos.
4. La app solo envía al backend el canal y la **Stream Key** de cada destino; el video
   no pasa por la consola.

El backend puede correr en un equipo de tu red, un mini-servidor o un VPS. No hace falta
PC ni capturadora junto a la consola.

> **Sobre la Guía de Xbox:** la sección **Capturar y compartir** pertenece al sistema
> Xbox y **no se puede ampliar con apps de terceros**. Ninguna app puede añadir botones
> allí. Plura incluye su propia **Guía rápida** en la pantalla principal y usa la emisión
> nativa que sí vive en esa sección.

---

## Requisitos

| Elemento | Detalle |
|---|---|
| Consola | Xbox Series X/S o Xbox One con **Modo Desarrollador** activo |
| Emisión nativa | La Xbox emite a Twitch con la cuenta configurada en la consola |
| Servidor | PC/servidor con **Node.js 18+** y **ffmpeg** (puede ser un VPS) |
| Red | Consola y backend accesibles entre sí (LAN o Internet) |

---

## Instalación en la Xbox

1. Activa el **Modo Desarrollador** en la consola (app *Dev Mode*).
2. Instala el certificado `XboxMultiStreamNative/Plura_Dev.cer` en la consola si el
   despliegue lo solicita.
3. Compila y despliega desde Visual Studio 2022 (ver
   [`XboxMultiStreamNative/README.md`](XboxMultiStreamNative/README.md)).
4. La app aparece en la consola como **Plura**.

> La instalación requiere Modo Desarrollador. Publicar en la Store de Xbox requiere un
> acuerdo de partner con Microsoft; no es algo que se pueda hacer con un certificado
> autofirmado.

---

## Configuración del backend

1. En el PC/servidor, instala Node.js 18+ y ffmpeg, y arranca el backend:

   ```powershell
   cd server
   .\start-backend.cmd
   ```

   El lanzador encuentra Node y ffmpeg, arranca el backend en `8787`, lo
   **reinicia solo si se cae** y se registra como tarea **PluraBackend** para
   iniciarse con Windows. Si prefieres arrancarlo a mano:

   ```powershell
   $env:FFMPEG_PATH = "C:\ruta\a\ffmpeg.exe"   # o ten ffmpeg en el PATH
   node src/index.js
   ```

   El backend escucha en `8787` y muestra la IP LAN al arrancar. Si no está
   encendido, la app muestra **SIN BACKEND** en lugar de fallar en silencio.
2. **App**: escribe la URL del backend (`http://IP-DEL-BACKEND:8787`) en
   **Backend de Plura** → **Probar backend**.
3. **Canal**: pega el nombre o el enlace del canal de Twitch con el botón **Pegar**
   (también puedes escribirlo) y pulsa **Comprobar canal**. La app muestra la foto
   del canal y si está en vivo.
4. **Destinos**: pega la **Stream Key** de cada plataforma con el botón **Pegar** y
   actívalas con sus switches. Twitch aparece como **ORIGEN** y no se puede desactivar.
5. **Emitir**: en la Xbox abre la emisión nativa a Twitch. Cuando el canal figure
   **EN VIVO**, pulsa **▶ Iniciar relay**.

El backend crea **un proceso ffmpeg por destino**. Si una plataforma se cae o una clave
es incorrecta, las demás siguen emitiendo y el destino afectado se reintenta solo.

> El relay usa el **HLS público de Twitch** con el Client-ID del reproductor web
> (`kimne78kx3ncx6brgo4mv6wki5h1ko`). Es una API no oficial: si Twitch la cambia, el relay
> puede dejar de funcionar hasta actualizar el backend. Requiere que el canal esté en vivo.

Variables del backend (ver [`server/README.md`](server/README.md)):
`FFMPEG_PATH`, `TWITCH_PLAYBACK_CLIENT_ID`, `RELAY_MAX_OUTPUTS`,
`RELAY_RESTART_LIMIT`, `RELAY_RESTART_DELAY_MS`.

---

## Uso de la app

1. Revisa la **Guía rápida** de la pantalla principal.
2. Escribe el **canal de Twitch** que emite desde la Xbox.
3. En cada plataforma destino, pega su **Stream Key** con el botón **Pegar**. El
   servidor de ingesta ya viene configurado y no hay que escribirlo.
4. Activa con los **switches** las plataformas que quieras, o usa
   **Transmitir en todas**.
5. Revisa el bloque **“Vas a transmitir a”**: muestra los destinos activos y Twitch
   como **origen**.
6. Pulsa **▶ Iniciar relay**.
7. Durante el directo verás **bitrate, FPS, duración y estado** de cada destino.
8. **■ Detener** para parar el relay (la emisión de la Xbox a Twitch sigue activa).

Los ajustes (canal, backend, claves y selección) se guardan en la app y se recuperan al
volver a abrirla.

---

## Stream Key simplificada

Cada plataforma destino se configura con **una sola clave**. El servidor de ingesta ya
viene rellenado y no se puede romper al escribir:

1. Abre el panel de la plataforma y copia su **Stream Key**.
2. En Plura, pulsa **Pegar** en la tarjeta de esa plataforma. La clave se toma del
   portapapeles y se guarda automáticamente.
3. Activa el switch de la plataforma.

No hay cuentas, códigos de vinculación ni páginas web dentro de la app. El backend solo
se usa para el **relay**. Está en [`server/`](server/) y no tiene dependencias externas:

```powershell
cd server
$env:FFMPEG_PATH = "C:\ruta\a\ffmpeg.exe"
node src/index.js
```

### API del relay

| Método | Ruta | Uso |
|---|---|---|
| `POST` | `/v1/session` | Crea/reutiliza la sesión de la consola |
| `GET` | `/v1/relay/status` | Estado del relay y de cada destino |
| `POST` | `/v1/relay/start` | `{ channel, outputs:[{platform,serverUrl,streamKey}] }` |
| `POST` | `/v1/relay/stop` | Detiene todos los procesos ffmpeg |
| `GET` | `/v1/relay/channel?login=` | Comprueba si un canal está en vivo |

Todas requieren `Authorization: Bearer <sessionToken>`.

---

## Plataformas y URLs de ingesta

| Plataforma | Servidor por defecto | Nota |
|---|---|---|
| Twitch | *(origen)* | La Xbox emite aquí con su función nativa |
| YouTube | `rtmp://a.rtmp.youtube.com/live2/` | Requiere transmisión activada en YouTube Studio |
| Kick | `rtmps://fa723fc1b171.global-contribute.live-video.net:443/app` | Endpoint de Amazon IVS que usa Kick |
| Facebook Gaming | `rtmps://live-api-s.facebook.com:443/rtmp/` | Clave desde Facebook Live Producer |

El servidor de ingesta lo fija Plura para cada red; tú solo pegas la Stream Key.

---

## Estructura del repositorio

```
plura/
├── Plura.sln
├── index.html                     ← mockup web legado (PC/OBS, no es la app)
├── README.md
├── server/                        ← backend (Node, sin dependencias)
│   ├── package.json
│   ├── README.md
│   └── src/                       ← API, relay y vinculación (legado)
│       └── relay.js               ← relay Twitch → plataformas (ffmpeg)
└── XboxMultiStreamNative/         ← app nativa UWP para Xbox
    ├── Plura.csproj
    ├── Package.appxmanifest
    ├── App.xaml / App.xaml.cs
    ├── MainPage.xaml / MainPage.xaml.cs
    ├── Models/
    │   ├── PlatformViewModel.cs
    │   └── SelectionChip.cs
    ├── Services/
    │   ├── ObsClient.cs           ← legado del modo PC (no se usa en consola)
    │   ├── SettingsStore.cs       ← persistencia en LocalSettings
    │   └── Streaming/             ← credenciales, relay y coordinador
    ├── Properties/
    └── Assets/                    ← iconos rojo/negro
```

---

## Seguridad

- Las **Stream Keys** se guardan en los ajustes locales de la app, sin cifrado
  adicional. Usa claves dedicadas y regenerables, y no compartas la consola.
- Plura **no pide cuentas ni contraseñas** y no abre páginas de OAuth.
- El **relay** acepta la Stream Key de cada destino en cada petición y la mantiene en
  memoria; protege el backend con HTTPS y no lo expongas a Internet sin autenticación.
- Plura no envía datos a ningún servidor de terceros: habla con tu backend y (para el
  relay) con el HLS público de Twitch.

---

## Limitaciones conocidas

- **La Guía de Xbox no se puede ampliar**: la sección **Capturar y compartir** es del
  sistema y no admite apps de terceros. Plura muestra su propia **Guía rápida** dentro
  de la app.
- **No hay captura directa del juego** en la consola para apps de terceros (limitación
  de Xbox). El modo consola usa la emisión nativa a Twitch y la multiplica.
- El **relay** necesita un backend con ffmpeg, que el canal de Twitch esté en vivo y que
  la cuenta permita la emisión desde la Xbox. El HLS de Twitch usa una API no oficial y
  puede cambiar.
- La app se instala en **Modo Desarrollador**, no desde la Store.
- Solo se admiten redes con servidor de ingesta fijo y Stream Key. **TikTok** (URL por
  cuenta/región) y **Plura** (servidor propio) no encajan en el flujo de una sola clave
  y no aparecen en esta versión.
