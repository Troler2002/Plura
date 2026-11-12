# Plura

**Plura** es una app **nativa para Xbox** (UWP, C# + XAML, sin WebView) para transmitir
a varias plataformas a la vez: **Twitch, YouTube, Kick, Facebook Gaming, TikTok y Plura**.

Tiene **dos modos**:

- **Modo consola (sin PC en tu casa)** — la Xbox emite con su función nativa a Twitch y
  el backend de Plura recibe esa señal y la reparte al resto de plataformas.
- **Modo PC / OBS** — la Xbox envía HDMI a una capturadora y Plura controla OBS Studio
  por red para emitir a todas las plataformas.

Toda la interfaz es **roja y negra**, se maneja con el **mando** y usa **switches** para
elegir exactamente a qué plataformas quieres transmitir (todas o solo algunas).

---

## Cómo funciona

Xbox no permite que una app de terceros capture el juego directamente. Plura ofrece dos
caminos reales según el equipo que tengas:

### Modo consola (recomendado si solo tienes la Xbox)

```
Xbox  ──Transmitir (nativo)──►  Twitch
                                   │  HLS
                                   ▼
                         Backend de Plura (ffmpeg -c copy)
                                   │
                                   ▼
                 YouTube · Kick · Facebook · TikTok · Plura
                                   ▲
                                   │  LAN / Internet
                            Xbox con Plura
```

- La **Xbox** emite a Twitch con la función **Guía → Capturar y compartir → Emisión en directo**.
- El **backend de Plura** (en un PC de la casa, un mini-servidor o un VPS) recibe el HLS
  de Twitch y lo reenvía **sin recodificar** (`-c copy`) a las plataformas activas.
- **Twitch es el origen**, no un destino: las demás plataformas son los destinos.
- La app solo envía al backend el canal y las credenciales de cada destino; el video no
  pasa por la consola.

> Necesitas **ffmpeg** en la máquina del backend y que tu canal de Twitch esté **en vivo**
> antes de pulsar Iniciar. El backend puede correr en otro equipo de tu red: no hace falta
> PC ni capturadora junto a la consola.

### Modo PC / OBS (máxima calidad y control)

```
Xbox  ──HDMI──►  Tarjeta de captura  ──USB──►  PC con OBS Studio
                                                   │
                                                   │  plugin Multiple RTMP Output
                                                   ▼
                              Twitch · YouTube · Kick · Facebook · TikTok · Plura
                                                   ▲
                                                   │  WebSocket v5 (LAN)
                                            Xbox con Plura
```

- La **consola** ejecuta Plura y le pide a OBS que inicie o detenga cada destino.
- El **PC** captura el HDMI de la Xbox y envía el video a todas las plataformas.
- Plura **no** captura video por sí misma: controla OBS por red.

> **Importante:** Xbox no expone captura de juego a apps de terceros
> (`Windows.Graphics.Capture` y `AppCapture` no están soportados en consola, y
> `AppBroadcastingUI` es solo de escritorio). La única transmisión directa sin PC es el
> botón **Transmitir** del sistema Xbox, que solo va a Twitch; el modo consola de Plura
> usa precisamente esa señal y la multiplica.

---

## Requisitos

| Elemento | Modo consola | Modo PC / OBS |
|---|---|---|
| Consola | Xbox Series X/S o Xbox One con **Modo Desarrollador** | Igual |
| Cuenta | Twitch (la emisión nativa de Xbox va a Twitch) | — |
| Servidor | PC/servidor con **Node.js 18+** y **ffmpeg** (puede ser un VPS) | Windows 10/11 con **OBS Studio 28+** |
| Plugin | — | **[Multiple RTMP Output con soporte WebSocket](https://github.com/romestylez/obs-multi-rtmp-websocket)** |
| Captura | — | Tarjeta de captura HDMI (Elgato, AverMedia, etc.) |
| Red | Consola y backend accesibles entre sí | PC y consola en la **misma red local** |

El modo consola no necesita capturadora ni PC junto a la consola: solo un backend con
ffmpeg al que la app pueda llegar (LAN o Internet).

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

## Configuración del modo consola

1. **Backend**: en el PC/servidor, instala Node.js 18+ y ffmpeg, y arranca el backend:

   ```powershell
   cd server
   $env:FFMPEG_PATH = "C:\ruta\a\ffmpeg.exe"   # o ten ffmpeg en el PATH
   node src/index.js
   ```

   El backend escucha en `8787` y muestra la IP LAN al arrancar.
2. **App**: en Plura elige el modo **CONSOLA · SIN PC** y escribe la URL del backend
   (`http://IP-DEL-BACKEND:8787`) en **Backend** → **Probar**.
3. **Canal**: escribe tu canal de Twitch o pulsa **Usar mi cuenta** para vincularlo y
   detectarlo automáticamente. **Comprobar canal** confirma si está en vivo.
4. **Destinos**: configura las demás plataformas (URL + clave o **Conectar cuenta**) y
   actívalas con sus switches. Twitch aparece como **ORIGEN** y no se puede desactivar.
5. **Emitir**: en la Xbox abre **Guía → Capturar y compartir → Emisión en directo →
   Twitch → Ir en directo**. Cuando el canal figure **EN VIVO**, pulsa **▶ Iniciar relay**.

El backend crea **un proceso ffmpeg por destino**. Si una plataforma se cae o una clave
es incorrecta, las demás siguen emitiendo y el destino afectado se reintenta solo.

> El relay usa el **HLS público de Twitch** con el Client-ID del reproductor web
> (`kimne78kx3ncx6brgo4mv6wki5h1ko`). Es una API no oficial: si Twitch la cambia, el relay
> puede dejar de funcionar hasta actualizar el backend. Requiere que el canal esté en vivo.

Variables del backend relacionadas (ver [`server/README.md`](server/README.md)):
`FFMPEG_PATH`, `TWITCH_PLAYBACK_CLIENT_ID`, `RELAY_MAX_OUTPUTS`,
`RELAY_RESTART_LIMIT`, `RELAY_RESTART_DELAY_MS`.

---

## Configuración de OBS

1. Instala el plugin **Multiple RTMP Output** (fork con obs-websocket v5) y reinicia OBS.
2. En OBS: **Herramientas → WebSocket Server Settings**
   - Marca **Enable WebSocket server**.
   - Puerto `4455`.
   - Activa autenticación y define una **contraseña**.
3. Permite el puerto `4455` en el **Firewall de Windows** (perfil de red privada).
4. Añade tu tarjeta de captura como fuente y configura el encoder en OBS.
5. En Plura escribe como **host** la **IP LAN del PC** (por ejemplo `192.168.1.20`).
   `localhost` no funciona desde la consola.

Plura se comunica con el fork **WebSocket** del plugin mediante
`CallVendorRequest` → `romestylez.multi_rtmp`, con la API en PascalCase:

| Acción | Request |
|---|---|
| Listar destinos | `ListTargets` |
| Estado / estadísticas | `GetTargetState` / `GetTargetStats` |
| Iniciar / detener | `StartTarget` / `StopTarget` |
| Crear destino | `AddTarget` (`name` + `protocol`) |
| Configurar servidor | `UpdateServiceParam` (`targetId`, `key = "server"`) |
| Configurar clave | `UpdateStreamKey` (`targetId`, `streamKey`) |

**Importante:** el plugin reutiliza el **encoder principal** de OBS, que solo
existe mientras OBS está transmitiendo o grabando. Por eso Plura inicia primero
la **transmisión principal de OBS** con la primera plataforma seleccionada y
después añade el resto como destinos del plugin. Detener la plataforma principal
detiene toda la sesión (encoder compartido).

El fork nunca rellena `isRunning_`, así que `ListTargets.state` siempre dice
`stopped`. Plura deduce si un destino está en vivo a partir de `GetTargetStats`
(bitrate, FPS o duración mayores que cero).

Si el plugin no está instalado, Plura lo detecta y muestra un aviso claro.

---

## Uso de la app

1. Elige el **modo de emisión**: **CONSOLA · SIN PC** o **PC · OBS**.
2. En modo consola, indica el **canal de Twitch** (o usa tu cuenta). En modo PC,
   escribe la **IP del PC** donde corre OBS.
3. Selecciona el **modelo de consola** (solo modo PC/OBS).
4. Configura cada plataforma en modo **Manual** (URL RTMP + Stream Key) o
   **Conectar cuenta** (código de vinculación con el backend).
5. Activa con los **switches** las plataformas que quieras, o usa
   **Transmitir en todas**.
6. Revisa el bloque **“Vas a transmitir a”**: muestra las plataformas activas
   (en modo consola, Twitch aparece como **origen**).
7. Pulsa **▶ Iniciar relay** (consola) o **▶ Iniciar stream** (OBS).
8. Durante el directo verás **bitrate, FPS, duración y estado** de cada destino.
9. **■ Detener** para cada plataforma o para todas.

Los ajustes (modo, canal, host, puerto, contraseña, claves y selección) se guardan en la
app y se recuperan al volver a abrirla.

---

## Dos formas de configurar cada plataforma

Cada tarjeta ofrece dos modos que terminan en el mismo resultado: una **URL de
servidor RTMP** y una **Stream Key** que se entregan al motor de video.

1. **Manual** — pegas la URL y la clave que te da la plataforma.
2. **Conectar cuenta** — la app pide un código al backend de Plura, tú lo
   confirmas en el navegador y la app recibe las credenciales automáticamente.

El backend es opcional para el modo manual, pero necesario para **Conectar
cuenta** y para el **modo consola**. Está en [`server/`](server/) y no tiene
dependencias externas:

```powershell
cd server
$env:FFMPEG_PATH = "C:\ruta\a\ffmpeg.exe"
node src/index.js
```

Después, en la app: **Backend** → `http://IP-DEL-PC:8787` → **Probar**.

El backend entrega credenciales reales vía OAuth cuando configuras sus variables
de entorno (Twitch, YouTube, Kick y Facebook). Sin esas credenciales, la página
de vinculación permite pegar la URL y la clave, así que el flujo se puede probar
completo. TikTok siempre es manual porque su URL RTMP es por cuenta/región.
Consulta [`server/README.md`](server/README.md) para las variables y el detalle
de cada proveedor.

### API del relay (modo consola)

| Método | Ruta | Uso |
|---|---|---|
| `POST` | `/v1/session` | Crea/reutiliza la sesión de la consola |
| `GET` | `/v1/relay/status` | Estado del relay y de cada destino |
| `POST` | `/v1/relay/start` | `{ channel, outputs:[{platform,serverUrl,streamKey}] }` |
| `POST` | `/v1/relay/stop` | Detiene todos los procesos ffmpeg |
| `GET` | `/v1/relay/channel?login=` | Comprueba si un canal está en vivo |
| `GET` | `/v1/twitch/channel` | Canal de la cuenta de Twitch vinculada |

Todas requieren `Authorization: Bearer <sessionToken>`.

---

## Plataformas y URLs de ingesta

| Plataforma | Servidor por defecto | Nota |
|---|---|---|
| Twitch | `rtmp://live.twitch.tv/app/` | Clave desde el panel de Twitch |
| YouTube | `rtmp://a.rtmp.youtube.com/live2/` | Requiere transmisión activada en YouTube Studio |
| Kick | `rtmps://fa723fc1b171.global-contribute.live-video.net:443/app` | Endpoint de Amazon IVS que usa Kick |
| Facebook Gaming | `rtmps://live-api-s.facebook.com:443/rtmp/` | Clave desde Facebook Live Producer |
| TikTok | *(vacío)* | La URL **cambia por cuenta y región**: cópiala de TikTok LIVE Center |
| Plura | *(vacío)* | Es tu **servidor RTMP propio**; no existe un servicio público `plura.tv` |

Todas las URLs son editables en la tarjeta de cada plataforma.

> **TikTok** emite en vertical: un stream 16:9 se verá con bandas. Para TikTok conviene un
> lienzo 9:16 en OBS o aceptar el letterbox.

---

## Versión web (opcional)

El repositorio incluye `index.html`, una versión de la interfaz para navegador que habla
con el mismo OBS. Sirve para probar la conexión y la selección de plataformas desde el PC.
Esta versión web **inicia y detiene destinos que ya existan en OBS**; la app de Xbox además
los **crea y configura** con la URL RTMP y la Stream Key. La app de Xbox es la versión
nativa de `XboxMultiStreamNative/`; **no usa WebView**.

---

## Estructura del repositorio

```
plura/
├── Plura.sln
├── index.html                     ← versión web (pruebas desde el PC)
├── README.md
├── server/                        ← backend de referencia (Node, sin dependencias)
│   ├── package.json
│   ├── README.md
│   └── src/                       ← API, proveedores OAuth, página de vinculación
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
    │   ├── ObsClient.cs           ← WebSocket v5 + vendor romestylez.multi_rtmp
    │   ├── SettingsStore.cs       ← persistencia en LocalSettings
    │   └── Streaming/             ← credenciales manuales/OAuth, relay y coordinador
    ├── Properties/
    └── Assets/                    ← iconos rojo/negro
```

---

## Seguridad

- Las **Stream Keys** del modo manual se guardan en los ajustes locales de la app,
  sin cifrado adicional. Usa claves dedicadas y regenerables, y no compartas la consola.
- En el modo **Conectar cuenta**, las claves y los tokens OAuth los guarda el backend.
  La implementación de referencia los escribe en `server/data/state.json` en texto
  plano: para producción, cifra ese archivo, usa HTTPS y rota los secretos.
- El **relay del modo consola** acepta credenciales de destino en cada petición y las
  mantiene en memoria; protege el backend con HTTPS y no lo expongas a Internet sin
  autenticación. La sesión de la consola es un token portador de larga duración.
- El WebSocket de OBS debe tener **contraseña** si el PC está en una red compartida.
- Plura no envía datos a ningún servidor de terceros: habla con tu OBS, tu backend y
  (en modo consola) con el HLS público de Twitch.

---

## Limitaciones conocidas

- **No hay captura directa del juego** en la consola para apps de terceros (limitación de Xbox).
  El modo consola resuelve el caso “solo tengo Xbox” usando la emisión nativa a Twitch.
- El **modo consola** necesita un backend con ffmpeg, que el canal de Twitch esté en vivo
  y que la cuenta de Twitch permita la emisión desde la Xbox. El HLS de Twitch usa una API
  no oficial y puede cambiar.
- El **modo PC/OBS** requiere PC + OBS + tarjeta de captura.
- La app se instala en **Modo Desarrollador**, no desde la Store.
- Los ajustes de resolución/FPS/bitrate de Plura son de referencia: el encoder real se
  configura en OBS (modo PC) o lo fija la emisión nativa de Xbox (modo consola).
- **Plura** como plataforma necesita que tú aportes un servidor RTMP.
