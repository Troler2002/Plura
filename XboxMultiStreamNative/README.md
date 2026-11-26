# Plura — App nativa para Xbox

App **UWP nativa (C# + XAML)** para consolas Xbox. Se llama **Plura** en la consola y usa
el tema rojo/negro. **No usa WebView**: toda la interfaz es XAML nativo y toda la lógica
de red es C#.

## Qué hace

- Interfaz de TV con navegación por **mando** (foco, `XYFocusKeyboardNavigation`, TV-safe).
- Diseño **negro con acento rojo** reorganizado: cabecera de estado, panel de emisión a la izquierda y destinos a la derecha en una fila uniforme de tres columnas.
- Twitch vive solo como **origen** en el panel “Canal de Twitch”; no aparece como tarjeta de destino.
- Cada destino conserva su color de identidad (Kick verde, YouTube rojo y Facebook azul) en insignia, borde y chip.
- **Sin campo TOKEN API**: ni se pide, ni se guarda, ni se envía. El título viaja en los metadatos de la propia emisión.
- **Guía rápida** integrada en la pantalla principal.
- **Switches** por plataforma + switch maestro **Transmitir en todas**.
- Resumen **“Vas a transmitir a”** con chips solo de los tres destinos reales.
- Estado por plataforma: **LISTA / CONECTANDO / EN VIVO / ERROR**.
- Estadísticas en vivo por destino: **bitrate, FPS, duración**.
- Inicio y parada **global** del relay.
- **Clave manual**: una sola clave por plataforma, con el nombre que usa cada una en
  español (**clave de retransmisión** en Kick, **clave de transmisión** en YouTube,
  Twitch y Facebook), botón **Pegar** desde el portapapeles y servidor de ingesta
  rellenado automáticamente.
- **Mismo título que Twitch**: Plura lee el título del directo en Twitch y lo envía
  en los metadatos RTMP a cada destino cuando cambia. No hay campo de token ni llamadas
  a APIs de las plataformas.
- **Sin acceso con cuenta**: Plura no pide usuario, contraseña, OAuth ni inicio de sesión.
- Ajustes persistentes en `ApplicationData.Current.LocalSettings`.

Plataformas: **Twitch es el origen** y los destinos visibles son **Kick, YouTube y
Facebook Gaming**. Solo se incluyen redes que emiten con una **clave de transmisión**
(en Kick, **clave de retransmisión**) sobre un servidor de ingesta fijo.

## Cómo funciona

La app funciona **dentro de la propia consola**:

1. La Xbox emite a Twitch con **Guía → Capturar y compartir → Emisión en directo**.
2. Plura descarga ese HLS directamente y lo convierte a FLV en C#, sin recodificar.
3. Plura publica por RTMP/RTMPS en cada destino activo, con una tarea por destino.
4. Plura consulta el título real del directo de Twitch cada pocos segundos y lo muestra
   automáticamente en la cabecera y en la tarjeta de estado. Si cambias el título en Twitch,
   la app lo actualiza sola sin que pulses nada.
5. El mismo título viaja en `@setDataFrame` a cada destino cuando cambia en Twitch.
   No se usa ningún token ni se llama a APIs de destino.

No se necesita PC, backend, capturadora ni OBS.

**Segundo plano y reanudación:** al pulsar **Iniciar**, la app guarda la intención de
emisión en `LocalSettings` **antes** de conectar, mantiene vivo el proceso con un audio
silencioso categorizado como `GameMedia` (no tapa el audio del juego) y conserva la
ejecución extendida. El relay sigue emitiendo si vuelves al juego o al dashboard. Si el
sistema cierra la app, al abrirla se reanuda automáticamente. Un supervisor interno
comprueba cada 2 segundos el estado, espera a que Twitch esté en vivo y vuelve a iniciar
los destinos si se cayeron o si dejaron de recibir media durante 45 segundos. Al abrir la
app, si el canal está en vivo y ya hay una clave guardada, el relay arranca también
sin pulsar nada. Si pulsas **Detener**, la intención se borra y no se reanuda nada.

**Título igual que en Twitch:** el título que durante el directo aparece en Twitch se
copia solo a la app (cabecera y estado) y se vuelve a leer cada ~6 s, así que cambiarlo en
Twitch cambia también lo que muestra Plura. El mismo título se reenvía en metadatos RTMP
`@setDataFrame` a cada destino cuando cambia. **No hay campo TOKEN API**: no se pide,
no se guarda y no se envía a ningún PC o backend.

Tampoco se pide iniciar sesión en Kick ni en ninguna plataforma. Sin autorización, cada
destino conserva el título que tenga configurado en su web, pero la emisión y el título
en metadatos funcionan igual. Solo se necesita la clave de retransmisión/transmisión.

**Pantalla:** la interfaz se dibuja a 1920x1080 y se escala de forma uniforme, por lo que
se adapta a la resolución de la Xbox y a distintos televisores sin recortar botones ni
textos.

> La sección **Capturar y compartir** de la Guía de Xbox es del sistema y **no admite
> apps de terceros**. No es posible añadir Plura allí; por eso la app incluye su propia
> **Guía rápida** y automatiza el resto.

## Requisitos previos

- **Visual Studio 2022** con la carga de trabajo
  **Desarrollo para la Plataforma Universal de Windows** y el SDK de Windows 10 (10.0.19041).
- **Xbox Series X|S o Xbox One** con **Modo Desarrollador** activo.
- **Acceso a Internet** desde la consola (Twitch y servidores de ingesta).
- No se necesita ningún PC, servidor ni ffmpeg.

> Compilar una app UWP requiere Visual Studio con la carga de trabajo UWP. El SDK de .NET
> por sí solo (por ejemplo `dotnet build`) **no** puede compilar este proyecto.

## Abrir y compilar

1. Abre `Plura.sln` en Visual Studio 2022.
2. Elige la plataforma **x64** (Xbox Series X/S y One) o **ARM64** si tu consola lo requiere.
3. **Compilar → Compilar solución**.
4. Para desplegar: clic derecho en el proyecto → **Implementar**, o **Depurar → Iniciar
   sin depurar** con el destino en la consola.

Si Visual Studio pide asociar la consola, usa **Device Portal** (Modo Desarrollador) con la
IP y el código de emparejamiento que muestra la consola.

### Firma

- Certificado de desarrollo: `Plura_TemporaryKey.pfx`
- Contraseña: `PluraDev123`
- Certificado público para instalar en la consola: `Plura_Dev.cer`
- Huella (thumbprint): `E3C8A19C9120E4636D5B4A6FD56C968B7FE3B81D`

El `Publisher` del manifiesto (`CN=Plura`) debe coincidir con el certificado. Si regeneras
el certificado, actualiza `PackageCertificateThumbprint` en `Plura.csproj` y el `Publisher`
en `Package.appxmanifest`.

### Identidad instalada en la consola

El paquete que funciona sin PC usa la identidad `Plura.OnDevice` y aparece en la consola
como **Plura**. Así convive con los despliegues anteriores (`PluraLoose`, `PluraNative` y
`Plura.Xbox`), que conservan la identidad `Plura.Xbox` y no se modifican.

- Paquete Release: `Plura_1.0.0.80_x64.msix` (compilado con .NET Native)
- Nombre completo instalado: `Plura.OnDevice_1.0.0.80_x64__e3cqhx40v3yww`

La versión Debug no se usa: depende de `Microsoft.NET.CoreRuntime.2.2`, que no está en
la imagen de Xbox. La Release incluye el runtime nativo y se instala por Device Portal
sin borrar las versiones anteriores.

## Configuración inicial

1. Activa el **Modo Desarrollador** en la Xbox.
2. Instala `Plura_Dev.cer` en la consola si el despliegue lo pide.
3. Dentro de Plura, pega el **canal de Twitch** (nombre o enlace) con **Pegar**. La
   comprobación se hace sola; la tarjeta muestra la foto y si está en vivo.
4. En cada plataforma destino, pega su **clave** con el botón **Pegar** (en Kick,
   **clave de retransmisión**). El servidor
   de ingesta ya viene configurado.
5. No hay que pulsar **Guardar**: el canal y las claves se guardan solos en la consola
   en cuanto se escriben, y se conservan al cerrar o actualizar la app.
6. Activa los switches y pulsa **▶ Iniciar** cuando el canal esté **EN VIVO**.
7. Mantén Plura abierta mientras dure el directo.

## Estructura del proyecto

```
XboxMultiStreamNative/
├── Plura.csproj                  ← proyecto UWP (x64 / ARM64, firma)
├── Package.appxmanifest          ← DisplayName Plura, fondo #050506, capacidades
├── Plura_TemporaryKey.pfx        ← certificado de desarrollo
├── Plura_Dev.cer                 ← certificado público
├── App.xaml / App.xaml.cs        ← punto de entrada, Frame raíz
├── MainPage.xaml                 ← interfaz nativa completa
├── MainPage.xaml.cs              ← lógica de UI y relay
├── Models/
│   ├── PlatformViewModel.cs      ← estado de cada plataforma (INotifyPropertyChanged)
│   └── SelectionChip.cs          ← chips del resumen de selección
├── Services/
│   ├── SettingsStore.cs          ← persistencia en LocalSettings
│   └── Streaming/
│       ├── OnDevice/             ← motor local (Twitch, TS/fMP4→FLV, RTMP, relay)
│       └── ...                   ← credenciales y código legado de OBS
├── Properties/
│   ├── AssemblyInfo.cs
│   └── Default.rd.xml
└── Assets/                       ← iconos y splash en rojo/negro
```

## Flujo del relay

1. Plura resuelve la clave manual de cada destino.
2. Consulta el canal directamente en Twitch y comprueba que esté en vivo.
3. Obtiene un token de reproducción y el playlist HLS de la emisión de la Xbox.
4. Descarga los segmentos HLS (MPEG-TS o fragmented-MP4/CMAF, según lo que sirva Twitch)
   y los convierte a mensajes FLV (H.264 AVCC + AAC RAW) sin decodificar ni recodificar.
5. Abre una conexión RTMP/RTMPS por destino y publica los mensajes FLV con chunks RTMP.
   Kick (Amazon IVS) y Facebook exigen RTMPS: en Xbox se completa el TLS 1.2 con
   `SslStream` sobre el socket, porque `StreamSocket` con `Tls12` se queda sin datos.
   YouTube usa RTMP plano `:1935`.
6. Cada destino se reconecta solo si falla. La UI consulta el estado local cada 2 s para
   mostrar bitrate, FPS, duración y estado.
7. **Detener** cancela las tareas y cierra las conexiones de forma limpia.

## Capacidades del manifiesto

```xml
<Capability Name="internetClient" />
<Capability Name="privateNetworkClientServer" />
<rescap:Capability Name="extendedExecutionUnconstrained" />
```

`internetClient` es necesario para hablar con Twitch y con los servidores de ingesta;
`privateNetworkClientServer` se conserva para compatibilidad con despliegues de desarrollo;
`extendedExecutionUnconstrained` permite pedir una sesión de ejecución extendida en primer
plano cuando el juego vuelve a la pantalla (solo posible en apps instaladas a mano).

## Problemas frecuentes

| Síntoma | Causa / solución |
|---|---|
| “El motor local no pudo consultar Twitch” | La consola no tiene Internet o Twitch ha cambiado su API. |
| “El canal no está en vivo” | Inicia primero la emisión nativa desde la Guía de Xbox. |
| El relay no arranca | Revisa que el canal esté en vivo y que haya al menos una clave pegada. |
| Un destino falla y los demás siguen | Es el comportamiento esperado: cada destino tiene su propia conexión RTMP. |
| Una plataforma no arranca | Revisa que su clave esté pegada y que la plataforma tenga la transmisión creada. |
| La app no se despliega | Consola y PC en la misma red (mejor por cable), Modo Desarrollador activo y salida de la consola a **1080p**. También puedes subir el paquete desde **Device Portal**. |

## Limitaciones

- **La Guía de Xbox no se puede ampliar**: “Capturar y compartir” es del sistema y no
  admite apps de terceros. Plura muestra su propia guía rápida.
- Xbox **no permite** que apps de terceros capturen el juego; el modo consola usa la
  emisión nativa a Twitch.
- El relay depende del HLS no oficial de Twitch y de que el canal esté en vivo.
- **Ejecución extendida**: Plura pide una sesión `ExtendedExecutionForegroundSession` al
  iniciar el relay para que Xbox no la suspenda cuando el juego pase al frente. La concesión
  depende del sistema; si se rechaza, se intenta la sesión extendida básica.
- El viaje ida y vuelta por Twitch añade latencia frente a una captura directa, que no
  existe para apps de terceros en Xbox.
- La app se instala en **Modo Desarrollador**; la Store requiere acuerdo de partner.
- Solo se admiten redes con servidor de ingesta fijo y clave manual. TikTok (URL por
  cuenta/región) y Plura (servidor propio) quedaron fuera de esta versión.

## Estado de validación

- Motor local implementado en C#: `TwitchService`, `MpegTsDemuxer`, `Fmp4Demuxer`,
  `UnifiedDemuxer`, `RtmpProtocol`, `RtmpPublisher` y `OnDeviceRelayClient`.
- Bancos de prueba: `PROTOCOL TESTS OK`, `TS TESTS OK` y conversión fMP4 real de Twitch
  (dos segmentos consecutivos con timestamps continuos: `0..1984` y `2000..3968`).
- Release `1.0.0.45` compilada con .NET Native e instalada en la consola como
  `Plura.OnDevice_1.0.0.45_x64__e3cqhx40v3yww` sin desinstalar los despliegues anteriores.
- **Pendiente**: prueba extremo a extremo con el canal en vivo y claves reales de
  YouTube/Kick/Facebook.
- La verificación de la interfaz se hace por captura del Device Portal y por
  `LocalState/plura.log`.
