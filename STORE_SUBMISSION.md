# Publicación de Plura en la Microsoft Store (Xbox)

## Estado real

Ya está hecho (técnicamente):

- Paquete de publicación generado: `Plura_1.0.0.80_x64_bundle.msixupload`
  (contiene `msixbundle` + `appxsym`, firmado por Microsoft al subirlo).
- Script reproducible: `XboxMultiStreamNative/build-store.ps1`.
- Nombre de la app en Xbox corregido: **Plura** (antes "Plura Local").
- Capturas para la ficha: `StoreAssets/Screenshot-1.png` (1920×1080).

**No puedo hacer la parte final por ti**: publicar en la Store exige iniciar sesión en
**Partner Center** con tu cuenta de Microsoft, aceptar contratos legales, verificar
identidad de desarrollador y reservar el nombre de la app. Eso solo lo puedes
autorizar/hacer tú; no tengo ni debo pedir tu contraseña.

## Lo que falta de tu parte

1. Entra en https://partner.microsoft.com/dashboard con tu cuenta de Microsoft.
2. Si aún no estás dado de alta: **Windows & Xbox → Register as a developer**
   (Windows Developer Program; verificación de identidad).
3. **Windows & Xbox → Apps → New product → Reserve a name.**
   Reserva el nombre **Plura**. La Store te asignará un *Package Name* y un
   *Publisher* (por ejemplo `Troler2002.Plura` / `CN=...`).
4. En Visual Studio: **Proyecto → Store → Associate App with the Store…**
   con esa misma cuenta. Eso reescribe automáticamente `Identity Name`,
   `Identity Publisher` y `PublisherDisplayName` en `Package.appxmanifest`.
5. Ejecuta `powershell -File XboxMultiStreamNative\build-store.ps1`.
   Salida: `StorePackages\Plura_<versión>_x64_bundle.msixupload`.
6. En la ficha del producto: **Packages → Upload packages** y sube ese
   `.msixupload`.
7. Rellena la ficha con el texto de abajo, sube la captura, elige categoría,
   clasificación por edad y **Privacy policy URL**.
8. En **Properties / Supported devices** marca **Xbox One / Xbox Series X|S**
   (y PC si lo deseas). La app es UWP con .NET Native y ya funcionó en Xbox.
9. En **Capabilities** declara y justifica las capacidades (ver abajo).
10. **Pricing and availability → Free → Publish**. Envía a certificación.

La primera certificación suele tardar más que las actualizaciones (días, no
horas). Hasta que no pase, la app seguirá instalable solo por Modo Desarrollador.

## Texto sugerido para la ficha

**Título:** Plura

**Descripción corta:**
Multidifusión de tu directo de Xbox a Kick, YouTube y Facebook Gaming, sin PC.

**Descripción completa:**
Plura retransmite el directo que ya emites desde tu Xbox a Twitch hacia Kick,
YouTube y Facebook Gaming, todo dentro de la consola. No necesitas PC,
capturadora, servidor ni iniciar sesión en esas plataformas.

- Elige los destinos con los switches o usa «Transmitir en todas».
- Plura copia el título de tu directo de Twitch automáticamente.
- Escribe tu canal de Twitch y la clave de retransmisión de cada destino una
  sola vez: se guardan en la consola y se recuperan al volver a abrir la app.
- Estado, bitrate, FPS y duración en pantalla para cada destino.
- El relay sigue en segundo plano y se reanuda al volver a la app o tras
  suspender/reaniciar la consola.

**Palabras clave:** xbox, stream, directo, multistream, twitch, kick, youtube,
facebook gaming, retransmisión, relay

**Categoría sugerida:** Utilities / Entertainment (según las opciones disponibles).

## Capacidades y justificaciones

| Capacidad | Por qué la usa Plura |
| --- | --- |
| `internetClient` | Leer el estado y el título del canal de Twitch y enviar el vídeo a los servidores de ingesta RTMP/RTMPS de los destinos. |
| `privateNetworkClientServer` | Modo OBS opcional: conectarse a un PC de la misma red local. |
| `backgroundMediaPlayback` | Reproducir un archivo de silencio para mantener el proceso vivo mientras el relay emite con la app en segundo plano. |
| `extendedExecutionUnconstrained` **restrictiva** | Mantener el reenvío RTMP cuando otra app o juego está en primer plano. **Declararla y justificarla en el formulario; si Microsoft la rechaza, quitarla del manifiesto y del código (`OnDeviceRelayClient.RequestExtendedExecutionAsync`) y apoyarse solo en `backgroundMediaPlayback`.** |

## Política y avisos

- Añade una URL de **privacidad** (puede ser un `README` publicado en GitHub Pages
  o una página estática): explica que el canal y las claves se guardan solo en
  `ApplicationData.Current.LocalSettings` del dispositivo, que no hay backend y
  que los datos se envían únicamente a Twitch y a los servicios de ingesta
  elegidos por el usuario.
- La app no pide cuentas ni contraseñas de las plataformas de destino; usa solo
  claves de emisión que introduce el usuario.
- Revisa que el uso de Plura cumpla los términos de Twitch y de los destinos:
  una denegación de certificación por motivos de terceros es posible.
- Requisitos específicos de Xbox: interfaz a 10 pies, zona segura de TV, foco
  visible con mando, suspend/resume y sin dependencia de teclado. La UI ya está
  diseñada con `XYFocusKeyboardNavigation` y navegación por mando.

## Después de publicar

- Actualiza `README.md` y `XboxMultiStreamNative/README.md` con el enlace de la
  Store y el nombre definitivo del paquete.
- Haz commit y push de esos cambios a `https://github.com/Troler2002/Plura.git`.
