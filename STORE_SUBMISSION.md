# Publicación de Plura en la Microsoft Store (Xbox)

## Estado real

- Producto creado en Partner Center: **Plura Multistream**
  (`Store ID 9P1R7RRWPMF0`, PFN
  `Troler2002.PluraMultistream_r94ygybagfcxc`).
- Submission 1 enviada a certificación el 26/11/2026. Microsoft la rechazó el
  27/11/2026 por las palabras clave (política 10.1.3 Search Terms). Se corrigieron,
  la submission se reenvió y se publicó el 28/11/2026.
- Enlaces públicos confirmados en Product identity:
  https://apps.microsoft.com/detail/9P1R7RRWPMF0 y
  `ms-windows-store://pdp/?productid=9P1R7RRWPMF0`.
- El nombre exacto **Plura** no está disponible y no se pudo reservar para este
  producto. Por decisión del usuario, el paquete de la Store usa
  `DisplayName=Plura Multistream` para pasar la validación. Las instalaciones
  locales anteriores (`Plura.OnDevice`) siguen mostrando **Plura**.
- Paquete publicado: `StorePackages/Plura_1.0.0.0_x64_bundle.msixupload`
  (versión `1.0.0.0`: el cuarto componente debe ser cero en la Store).
- Submission 2 (update): creada con **Start update** el 28/11/2026 sin cancelar ni
  editar la submission publicada. Paquete
  `StorePackages/Plura_1.1.0.0_x64_bundle.msixupload`, validado sin errores y
  enviado a certificación el 28/11/2026 (`submitted=true`,
  `Submission_Status_InCertification`). Hasta que pase certificación sigue
  publicada la versión `1.0.0.0`.
- Script reproducible: `XboxMultiStreamNative/build-store.ps1`.
- Captura de la ficha: `StoreAssets/Screenshot-1.png` (1920×1080).
- Política de privacidad: `PRIVACY.md`, publicada en
  `https://github.com/Troler2002/Plura/blob/main/PRIVACY.md`.
- Se aceptó la versión 8.11 del Windows App Developer Agreement.

## Cambios de la versión 1.1.0.0

- Salud por destino con `LastMediaUtc`/`MediaFresh`, métricas que se limpian al
  reconectar y `RelayStatus.Healthy` para que el supervisor no confíe solo en
  `status.Running`.
- Watchdog interno independiente del dispatcher que sustituye workers en
  `error`, desconectados o sin media durante 45 s.
- Renovación del HLS de Twitch con reintentos acotados, incluida la refresh
  programada a los 9 minutos; los segmentos fallidos se reintentan sin tumbar
  el relay.
- Rearme del archivo de silencio (`MediaPlayer`) y de la sesión básica de
  ejecución extendida cuando se revocan; excepciones no controladas marcadas
  como handled para que no cierren la app en segundo plano.
- Box art 1080×1080 regenerado desde `Assets/LargeTile.png`, la misma imagen
  que usa la ficha web (`og:image`), para que el icono de Xbox coincida con la
  Store. El poster art sigue siendo el original.
- Notas de la versión en la ficha: corrección de la pérdida de señal en segundo
  plano, renovación segura del HLS y rearme del audio de fondo.

## Configuración de la ficha

- **Título:** Plura Multistream
- **Descripción corta:** Multidifusión de tu directo de Xbox a Kick, YouTube y
  Facebook Gaming, sin PC.
- **Descripción completa:** Plura Multistream retransmite el directo que ya
  emites desde tu Xbox hacia Twitch y lo reenvía a Kick, YouTube y Facebook
  Gaming, todo dentro de la consola. No necesitas PC, capturadora, servidor ni
  iniciar sesión en las plataformas de destino.

  - Elige los destinos con los switches o usa «Transmitir en todas».
  - Copia automáticamente el título del directo de Twitch cuando está disponible.
  - Escribe tu canal de Twitch y la clave de retransmisión de cada destino una
    sola vez: se guardan en la consola y se recuperan al volver a abrir la app.
  - Muestra estado, bitrate, FPS y duración de cada destino.
  - El relay sigue en segundo plano y se reanuda al volver a la app o tras
    suspender/reaniciar la consola.

- **Palabras clave:** stream, directo, multidifusión, retransmisión, emisión,
  consola, sin pc. Microsoft rechazó la primera revisión porque `xbox` y
  `xbox multistream twitch kick` son títulos de productos de terceros; se
  sustituyeron por términos genéricos antes de reenviar la submission.
- **Categoría:** Entertainment → Utilities + tools
- **Dispositivos:** Xbox One / Xbox Series X|S (Windows 10/11 Xbox).
- **Precio:** Gratis.

## Capacidades y justificaciones

| Capacidad | Por qué la usa Plura |
| --- | --- |
| `internetClient` | Consultar el estado y el título del canal de Twitch y publicar en los servidores RTMP/RTMPS elegidos. |
| `privateNetworkClientServer` | Modo opcional con OBS en la misma red local. |
| `backgroundMediaPlayback` | Reproducir un archivo de silencio para mantener vivo el proceso mientras el relay emite en segundo plano. |
| `ExtendedExecutionSession` (básica, sin capacidad restringida) | Pedir tiempo extra en segundo plano sin `extendedExecutionUnconstrained`, para evitar bloqueos de certificación. |

## Política y avisos

- La app no pide cuentas ni contraseñas de los destinos; usa únicamente claves de
  emisión introducidas por el usuario.
- El canal y las claves se guardan solo en
  `ApplicationData.Current.LocalSettings` de la consola.
- Revisa que el uso de Plura cumpla los términos de Twitch y de los destinos.

## Pendiente tras la certificación de la submission 2

- Comprobar que la ficha pública y la consola muestran el box art rojo/negro
  regenerado desde `LargeTile.png`.
- Actualizar este documento con la fecha de publicación de `1.1.0.0` y el
  nuevo estado `Submission_Status_In_Store`.
- Hacer commit y push a `https://github.com/Troler2002/Plura.git`.
