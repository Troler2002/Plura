# Plura — App nativa para Xbox

App **UWP nativa (C# + XAML)** para consolas Xbox. Se llama **Plura** en la consola y usa
el tema rojo/negro. **No usa WebView**: toda la interfaz es XAML nativo y toda la lógica
de red es C#.

## Qué hace

- Interfaz de TV con navegación por **mando** (foco, `XYFocusKeyboardNavigation`, TV-safe).
- **Guía rápida** integrada en la pantalla principal.
- **Switches** por plataforma + switch maestro **Transmitir en todas**.
- Resumen **“Vas a transmitir a”** con chips de los destinos y Twitch como **origen**.
- Estado por plataforma: **LISTA / CONECTANDO / EN VIVO / ERROR**.
- Estadísticas en vivo por destino: **bitrate, FPS, duración**.
- Inicio y parada **global** del relay.
- **Stream Key manual**: una sola clave por plataforma, con botón **Pegar** desde el
  portapapeles y servidor de ingesta rellenado automáticamente.
- **Sin acceso con cuenta**: Plura no pide usuario, contraseña ni OAuth.
- Ajustes persistentes en `ApplicationData.Current.LocalSettings`.

Plataformas: **Twitch (origen), YouTube, Kick y Facebook Gaming**. Solo se incluyen
redes que emiten con una **Stream Key** sobre un servidor de ingesta fijo.

## Cómo funciona

La app funciona **solo con la consola**:

1. La Xbox emite a Twitch con **Guía → Capturar y compartir → Emisión en directo**.
2. Plura pide al backend que reciba ese HLS y lo reparta a los destinos activos.
3. El backend usa **ffmpeg** (`-c copy`) y un proceso por destino.

No se necesita PC junto a la consola, capturadora ni OBS.

> La sección **Capturar y compartir** de la Guía de Xbox es del sistema y **no admite
> apps de terceros**. No es posible añadir Plura allí; por eso la app incluye su propia
> **Guía rápida** y automatiza el resto.

## Requisitos previos

- **Visual Studio 2022** con la carga de trabajo
  **Desarrollo para la Plataforma Universal de Windows** y el SDK de Windows 10 (10.0.19041).
- **Xbox Series X|S o Xbox One** con **Modo Desarrollador** activo.
- **Backend** con Node.js 18+ y **ffmpeg** (en tu red o un VPS).
- Consola y backend accesibles entre sí.

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

## Configuración inicial

1. Activa el **Modo Desarrollador** en la Xbox.
2. Instala `Plura_Dev.cer` en la consola si el despliegue lo pide.
3. Arranca el backend con `FFMPEG_PATH` y anota su IP LAN.
4. Dentro de Plura escribe la **URL del backend** en **Backend de Plura** y pulsa
   **Probar backend**.
5. Pega el **canal de Twitch** (nombre o enlace) con **Pegar** y pulsa
   **Comprobar canal**. La tarjeta muestra la foto del canal y si está en vivo.
6. En cada plataforma destino, pega su **Stream Key** con el botón **Pegar**. El servidor
   de ingesta ya viene configurado.
7. Activa los switches y pulsa **▶ Iniciar relay** cuando el canal esté **EN VIVO**.

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
│   └── Streaming/                ← credenciales, cliente del relay y coordinador
├── Properties/
│   ├── AssemblyInfo.cs
│   └── Default.rd.xml
└── Assets/                       ← iconos y splash en rojo/negro
```

## Flujo del relay

1. La app pide la sesión al backend (`POST /v1/session`).
2. Resuelve la Stream Key manual de cada destino.
3. Envía `POST /v1/relay/start` con el canal y los destinos.
4. El backend comprueba que el canal esté en vivo, obtiene el HLS y lanza un ffmpeg por
   destino con `-c copy`.
5. La app consulta `GET /v1/relay/status` cada 2 s para mostrar bitrate, FPS, duración y
   estado; el canal se comprueba cada 10 s.
6. `POST /v1/relay/stop` termina los procesos ffmpeg de forma limpia.

## Capacidades del manifiesto

```xml
<Capability Name="internetClient" />
<Capability Name="privateNetworkClientServer" />
```

`internetClient` es necesario para el backend y para comprobar el canal de Twitch;
`privateNetworkClientServer` para llegar a un backend en la LAN.

## Problemas frecuentes

| Síntoma | Causa / solución |
|---|---|
| “No se pudo conectar con el backend” | URL incorrecta o backend apagado. Usa la IP LAN, no `localhost`. |
| “El canal no está en vivo” | Inicia primero la emisión nativa desde la Guía de Xbox. |
| El relay no arranca | Falta `FFMPEG_PATH` o ffmpeg no está instalado en el servidor. |
| Un destino falla y los demás siguen | Es el comportamiento esperado: cada destino tiene su propio ffmpeg. |
| Una plataforma no arranca | Revisa que su Stream Key esté pegada y que la plataforma tenga la transmisión creada. |
| La app no se despliega | Consola y PC en la misma red (mejor por cable), Modo Desarrollador activo y salida de la consola a **1080p**. También puedes subir el paquete desde **Device Portal**. |

## Limitaciones

- **La Guía de Xbox no se puede ampliar**: “Capturar y compartir” es del sistema y no
  admite apps de terceros. Plura muestra su propia guía rápida.
- Xbox **no permite** que apps de terceros capturen el juego; el modo consola usa la
  emisión nativa a Twitch.
- El relay depende del HLS no oficial de Twitch y de que el canal esté en vivo.
- La app se instala en **Modo Desarrollador**; la Store requiere acuerdo de partner.
- Solo se admiten redes con servidor de ingesta fijo y Stream Key. TikTok (URL por
  cuenta/región) y Plura (servidor propio) quedaron fuera de esta versión.

## Estado de validación

- Compilación nativa (csc + ilc) y despliegue **loose** en Xbox: **correctos**.
- Relay verificado de extremo a extremo con dos receptores RTMP locales: ~7 Mbps por
  destino, estado `running`, parada limpia sin procesos ffmpeg residuales.
- Flujo de **Stream Key manual** con botón **Pegar**: implementado en la app.
- La captura y la inyección de entrada no están disponibles desde Device Portal; la
  verificación de la interfaz se hace por logs (`LocalState/plura.log`).
