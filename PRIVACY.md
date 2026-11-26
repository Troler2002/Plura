# Política de privacidad de Plura

Plura es una aplicación que se ejecuta íntegramente en tu consola Xbox. No tiene
servidor propio, no crea cuentas y no pide iniciar sesión en Twitch, Kick,
YouTube ni Facebook Gaming.

## Datos que introduces

- **Canal de Twitch:** nombre o enlace que escribes para que Plura lea el estado y
  el título del directo.
- **Claves de emisión:** claves de retransmisión que pegas para Kick, YouTube y
  Facebook Gaming.

Estos datos se guardan solamente en el almacenamiento local de la aplicación en tu
consola (`ApplicationData.Current.LocalSettings`). Se conservan al cerrar la app
y se borran si desinstalas Plura. No hay botón de guardado: se guardan en cuanto
los escribes.

## Datos que se envían

- Plura consulta a **Twitch** el estado y el título del canal indicado, usando la
  API pública de Twitch con un identificador de cliente público.
- Cuando tú pulsas **Iniciar**, Plura envía el vídeo y el título del directo a los
  servidores de ingesta RTMP/RTMPS de **las plataformas que hayas activado**
  (Kick, YouTube y/o Facebook Gaming). Las claves se envían únicamente a la
  plataforma correspondiente.

Plura no envía estos datos a ningún servidor de Plura ni a terceros distintos de
los servicios anteriores. No utiliza analítica, publicidad ni rastreadores.

## Permisos

- `internetClient`: consultar Twitch y publicar en los destinos elegidos.
- `privateNetworkClientServer`: modo opcional con OBS en la misma red local.
- `backgroundMediaPlayback` y ejecución extrema: mantener el reenvío activo
  mientras juegas o usas otra app.

## Contacto

Troler2002 — https://github.com/Troler2002/Plura/issues
