# Plura

**Plura** es una app **nativa para Xbox** (UWP, C# + XAML, sin WebView) para transmitir
a varias plataformas a la vez desde la propia consola: **Twitch, YouTube, Kick y
Facebook Gaming**.

Solo se incluyen redes que emiten con una **clave de transmisión** (en Kick,
**clave de retransmisión**) sobre un servidor de ingesta
fijo. Plura **no pide cuentas, contraseñas ni tokens de API**: tú pegas solo la clave de
cada red. Durante la emisión la consola sigue trabajando sola.

Todo funciona **dentro de la propia consola**. No necesitas PC, capturadora, OBS ni
backend:

```
Xbox  ──Transmitir (nativo)──►  Twitch
                                   │  HLS
                                   ▼
                 Motor de Plura dentro de la app UWP
                 (HTTP + TS/fMP4 → FLV + RTMP/RTMPS en C#)
                                   │
                                   ▼
                 YouTube · Kick · Facebook Gaming
```

La interfaz es **roja y negra**, se maneja con el **mando** y usa **switches** para
elegir exactamente a qué plataformas quieres transmitir (todas o solo algunas).

## Estado de publicación

La versión de la Microsoft Store se llama **Plura Multistream**
(Store ID `9P1R7RRWPMF0`, PFN `Troler2002.PluraMultistream_r94ygybagfcxc`). El
nombre exacto «Plura» no estaba disponible para reservar, así que la primera
submission se envió a certificación con ese nombre visible el 26/11/2026. El
enlace público de la Store se añadirá aquí cuando la certificación termine.
Los despliegues locales de desarrollo (`Plura.OnDevice`) siguen apareciendo en
la consola como **Plura**.

---

## Cómo funciona

1. La **Xbox** emite con su función nativa a Twitch:
   **Guía → Capturar y compartir → Emisión en directo → Twitch → Ir en directo**.
2. El **motor local de Plura**, dentro de la app, descarga ese HLS y lo reenvía
   **sin recodificar** (TS → FLV, `-c copy` equivalente) a las plataformas que actives.
3. **Twitch es el origen**, no un destino: las demás plataformas son los destinos.
4. La app habla directamente con Twitch y con los servidores de ingesta. No hay PC,
   backend ni servidor intermedio.
5. Plura lee el **título real del directo de Twitch** cada pocos segundos, lo muestra
   automáticamente en la app y lo envía en los metadatos RTMP a cada destino cuando cambia.
   No se usa ningún token ni se llama a APIs de destino.

> **Mismo título que Twitch:** el título aparece en la cabecera y en el panel de control;
> si lo editas en Twitch, la app lo actualiza sola sin pulsar nada. El título viaja en los
> metadatos de la emisión y se reenvía cuando cambia. **No hay campo TOKEN API** y no se
> pide iniciar sesión en ninguna plataforma. Solo se guardan en la consola el canal de
> Twitch y la clave de cada red.

> Plura funciona **en segundo plano**: cuando pulsas **▶ Iniciar**, guarda la intención
> de emisión, mantiene el proceso vivo con audio silencioso de fondo y conserva la
> sesión de ejecución extendida. Puedes volver al juego o al dashboard y el relay sigue
> emitiendo. Si Xbox cierra la app por completo, al abrirla de nuevo recupera el canal
> y los destinos guardados y reinicia el relay automáticamente.

> **Sobre la Guía de Xbox:** la sección **Capturar y compartir** pertenece al sistema
> Xbox y **no se puede ampliar con apps de terceros**. Ninguna app puede añadir botones
> allí. Plura incluye su propia **Guía rápida** en la pantalla principal y usa la emisión
> nativa que sí vive en esa sección.

---

## Requisitos

| Elemento | Detalle |
|---|---|
| Consola | Xbox Series X/S o Xbox One; Store al publicarse o **Modo Desarrollador** para sideload |
| Emisión nativa | La Xbox emite a Twitch con la cuenta configurada en la consola |
| Red | La consola debe tener acceso a Internet (Twitch y servidores RTMP) |
| Backend | **Ninguno**. Node.js y ffmpeg ya no son necesarios |

---

## Instalación en la Xbox

1. Activa el **Modo Desarrollador** en la consola (app *Dev Mode*).
2. Instala el certificado `XboxMultiStreamNative/Plura_Dev.cer` en la consola si el
   despliegue lo solicita.
3. Compila y despliega desde Visual Studio 2022 (ver
   [`XboxMultiStreamNative/README.md`](XboxMultiStreamNative/README.md)).
4. La app sin PC aparece en la consola como **Plura** (`Plura.OnDevice`). Los
   despliegues antiguos (`PluraLoose`, `PluraNative` y `Plura.Xbox`) se conservan.

> Hasta que la certificación de la Store termine, la instalación requiere Modo
> Desarrollador. La versión publicada se instalará desde la Microsoft Store con el
> nombre **Plura Multistream**; no necesitará Modo Desarrollador.

---

## Configuración

1. **Motor local**: no hay nada que instalar ni configurar. El bloque **Motor local de
   Plura** muestra su estado; **Comprobar motor** valida el acceso a Twitch.
2. **Canal**: pega el nombre o el enlace del canal de Twitch con el botón **Pegar**
   (también puedes escribirlo). La comprobación se hace sola al dejar de escribir, o
   con **Comprobar canal**. La app muestra la foto del canal y si está en vivo.
3. **Destinos**: pega la clave de cada plataforma con el botón **Pegar** (en Kick,
   **clave de retransmisión**) y actívalas con sus switches. Los switches controlan
   solo los destinos (Kick, YouTube y Facebook Gaming); Twitch es únicamente el origen.
4. **Emitir**: en la Xbox abre la emisión nativa a Twitch. Cuando el canal figure
   **EN VIVO**, pulsa **▶ Iniciar**.

Cada destino corre en su propia tarea dentro de la app. Si una plataforma se cae o una
clave es incorrecta, las demás siguen emitiendo y el destino afectado se reconecta solo
(hasta 20 veces, con espera creciente).

> El relay usa el **HLS público de Twitch** con el Client-ID del reproductor web
> (`kimne78kx3ncx6brgo4mv6wki5h1ko`). Es una API no oficial: si Twitch la cambia, habrá
> que actualizar la app. Requiere que el canal esté en vivo y que la consola siga
> emitiendo a Twitch.

---

## Uso de la app

1. Revisa la **Guía rápida** de la pantalla principal.
2. Escribe el **canal de Twitch** que emite desde la Xbox.
3. En cada plataforma destino, pega su clave con el botón **Pegar**. El
   servidor de ingesta ya viene configurado y no hay que escribirlo.
4. Activa con los **switches** las plataformas que quieras, o usa
   **Transmitir en todas**.
5. Revisa el bloque **“Vas a transmitir a”**: muestra solo los destinos activos;
   Twitch aparece como origen en el panel **Canal de Twitch**, no como tarjeta.
6. Pulsa **▶ Iniciar**.
7. Durante el directo verás **bitrate, FPS, duración y estado** de cada destino.
8. **■ Detener** para parar el relay (la emisión de la Xbox a Twitch sigue activa).

El primer uso empieza sin canal ni claves precargadas. Los ajustes (canal, claves y
selección) se guardan automáticamente en la consola en cuanto se escriben y se recuperan
al volver a abrirla. No hay botón **Guardar** y no se envían a ningún servidor.

El relay también se guarda mientras emite: si pulsas el botón Xbox, abres un juego o
sales al dashboard, Plura sigue reenviando el directo en segundo plano. Al volver a la
app no se inicia un segundo relay; solo se renueva la sesión. Para pararlo, vuelve a
Plura y pulsa **■ Detener**.

---

## Clave de transmisión simplificada

Cada plataforma destino se configura con **una sola clave**. El servidor de ingesta ya
viene rellenado y no se puede romper al escribir. Plura usa el nombre de cada plataforma:
**clave de retransmisión** en Kick y **clave de transmisión** en YouTube, Twitch y
Facebook:

1. Abre el panel de la plataforma y copia su clave.
2. En Plura, pulsa **Pegar** en la tarjeta de esa plataforma. La clave se toma del
   portapapeles y se guarda automáticamente.
3. Activa el switch de la plataforma.

No hay cuentas, códigos de vinculación ni páginas web dentro de la app. El relay está
implementado en C# dentro de la propia UWP:

- `TwitchService`: consulta el canal y resuelve el HLS con token de reproducción.
- `MpegTsDemuxer`, `Fmp4Demuxer` y `UnifiedDemuxer`: convierten MPEG-TS o
  fragmented-MP4/CMAF en mensajes FLV (H.264 AVCC + AAC RAW), sin decodificar.
- `RtmpPublisher`: handshake RTMP/RTMPS, AMF0, chunks y publicación. En consola usa
  `SslStream` sobre `StreamSocket` para RTMPS, porque `StreamSocket` con `Tls12` se
  queda sin datos en Amazon IVS/Facebook.
- `OnDeviceRelayClient`: coordina los destinos, reintentos, métricas y la sesión de
  ejecución extendida.

El directorio [`server/`](server/) se conserva como referencia del diseño anterior, pero
**ya no es necesario** para usar la app.

---

## Plataformas y URLs de ingesta

| Plataforma | Servidor por defecto | Nota |
|---|---|---|
| Twitch | *(origen)* | La Xbox emite aquí con su función nativa |
| YouTube | `rtmp://a.rtmp.youtube.com/live2/` | Requiere transmisión activada en YouTube Studio |
| Kick | `rtmps://fa723fc1b171.global-contribute.live-video.net:443/app/` | Endpoint de Amazon IVS que usa Kick; exige RTMPS (por RTMP plano conecta pero rechaza `publish`) |
| Facebook Gaming | `rtmps://live-api-s.facebook.com:443/rtmp/` | Clave desde Facebook Live Producer; RTMPS |

El servidor de ingesta lo fija Plura para cada red; tú solo pegas la clave.
Las URLs guardadas en versiones anteriores se ignoran para no arrastrar endpoints
obsoletos. YouTube publica por RTMP plano `:1935`, que es lo que su ingesta espera.

---

## Estructura del repositorio

```
plura/
├── Plura.sln
├── index.html                     ← mockup web legado (PC/OBS, no es la app)
├── README.md
├── server/                        ← backend anterior (referencia, ya no se usa)
│   ├── package.json
│   ├── README.md
│   └── src/relay.js               ← relay ffmpeg anterior
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
    │   └── Streaming/
    │       ├── OnDevice/           ← motor local (Twitch, TS→FLV, RTMP, relay)
    │       └── ...                 ← credenciales y código legado de OBS
    ├── Properties/
    └── Assets/                    ← iconos rojo/negro
```

---

## Seguridad

- Las claves de transmisión se guardan en los ajustes locales de la app. No se envían a
  ningún backend ni servidor intermedio: solo se entregan al servidor RTMP/RTMPS de cada
  plataforma. Usa claves dedicadas y regenerables, y no compartas la consola.
- Plura **no pide cuentas, contraseñas ni tokens de API** y no abre páginas de OAuth.
  El título se copia en los metadatos de la emisión; no hace falta autorización.
- Kick y Facebook usan **RTMPS (TLS 1.2)** dentro de la propia consola. La pila TLS de
  `StreamSocket` fallaba en Xbox, así que Plura usa `SslStream` sobre el socket para
  completar el handshake; la clave viaja cifrada en esos dos destinos. YouTube
  usa RTMP plano porque su ingesta estándar es `rtmp://a.rtmp.youtube.com:1935/live2/`.
  El origen de Twitch va por HTTPS. Usa siempre claves dedicadas y regenerables.
- Plura solo habla con Twitch (HLS/API pública del reproductor) y con los servidores de
  ingesta de las plataformas activadas.

---

## Limitaciones conocidas

- **La Guía de Xbox no se puede ampliar**: la sección **Capturar y compartir** es del
  sistema y no admite apps de terceros. Plura muestra su propia **Guía rápida** dentro
  de la app.
- **No hay captura directa del juego** en la consola para apps de terceros (limitación
  de Xbox). El modo consola usa la emisión nativa a Twitch y la multiplica.
- El relay necesita que el canal de Twitch esté en vivo y que la cuenta permita la
  emisión desde la Xbox. El HLS de Twitch usa una API no oficial y puede cambiar.
- **Ejecución extendida**: al iniciar el relay se pide una sesión básica
  `ExtendedExecutionSession` (sin capacidades restringidas) junto con
  `backgroundMediaPlayback`. Si Xbox la revoca, el supervisor interno reanuda el relay
  al volver a la app; la concesión depende del sistema.
- El viaje ida y vuelta por Twitch añade latencia respecto a una captura directa, que no
  existe para apps de terceros en Xbox.
- La primera versión de Store está en certificación; hasta que esté publicada, el
  sideload sigue requiriendo **Modo Desarrollador**.
- Solo se admiten redes con servidor de ingesta fijo y clave manual. **TikTok** (URL por
  cuenta/región) y **Plura** (servidor propio) no encajan en el flujo de una sola clave
  y no aparecen en esta versión.
