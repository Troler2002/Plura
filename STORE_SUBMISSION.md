# Publicación de Plura en la Microsoft Store (Xbox)

## Estado real

- Producto creado en Partner Center: **Plura Multistream**
  (`Store ID 9P1R7RRWPMF0`, PFN
  `Troler2002.PluraMultistream_r94ygybagfcxc`).
- Submission 1 enviada a certificación el 26/11/2026. El enlace de la Store se muestra
  en Partner Center cuando el producto esté publicado.
- El nombre exacto **Plura** no está disponible y no se pudo reservar para este
  producto. Por decisión del usuario, el paquete de la Store usa
  `DisplayName=Plura Multistream` para pasar la validación. Las instalaciones
  locales anteriores (`Plura.OnDevice`) siguen mostrando **Plura**.
- Paquete de publicación: `StorePackages/Plura_1.0.0.0_x64_bundle.msixupload`
  (versión `1.0.0.0`: el cuarto componente debe ser cero en la Store).
- Script reproducible: `XboxMultiStreamNative/build-store.ps1`.
- Captura de la ficha: `StoreAssets/Screenshot-1.png` (1920×1080).
- Política de privacidad: `PRIVACY.md`, publicada en
  `https://github.com/Troler2002/Plura/blob/main/PRIVACY.md`.
- Se aceptó la versión 8.11 del Windows App Developer Agreement.

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

- **Palabras clave:** xbox, stream, directo, multistream, twitch, kick, youtube,
  facebook gaming, retransmisión, relay
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

## Después de publicar

- Actualizar `README.md` y `XboxMultiStreamNative/README.md` con el enlace de la
  Store y el nombre definitivo del paquete.
- Hacer commit y push a `https://github.com/Troler2002/Plura.git`.
