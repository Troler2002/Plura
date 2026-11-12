# Plura — App nativa para Xbox

App **UWP nativa (C# + XAML)** para consolas Xbox. Se llama **Plura** en la consola y usa
el tema rojo/negro. **No usa WebView**: toda la interfaz es XAML nativo y toda la lógica
de red es C#.

## Qué hace

- Interfaz de TV con navegación por **mando** (foco, `XYFocusKeyboardNavigation`, TV-safe).
- **Switches** por plataforma + switch maestro **Transmitir en todas**.
- Resumen **“Vas a transmitir a”** con chips de las plataformas activas.
- Estado por plataforma: **LISTA / CONECTANDO / EN VIVO / ERROR**.
- Estadísticas en vivo por destino: **bitrate, FPS, duración**.
- Inicio y parada **global** o **por plataforma**.
- Conexión a OBS por **WebSocket v5** con contraseña, reconexión automática y detección
  del plugin **Multiple RTMP Output**.
- **Dos modos de credenciales**: manual (URL + clave) o **Conectar cuenta**
  (OAuth/backend), unificados en una sola URL de servidor + Stream Key.
- Vinculación por **código de dispositivo** (sin WebView) con el backend de referencia
  incluido en `server/`.
- Ajustes persistentes en `ApplicationData.Current.LocalSettings`.

Plataformas: **Twitch, YouTube, Kick, Facebook Gaming, TikTok y Plura**.

## Requisitos previos

- **Visual Studio 2022** con la carga de trabajo
  **Desarrollo para la Plataforma Universal de Windows** y el SDK de Windows 10 (10.0.19041).
- **Xbox Series X|S o Xbox One** con **Modo Desarrollador** activo.
- PC con **OBS Studio 28+** y el plugin
  **[Multiple RTMP Output (fork WebSocket)](https://github.com/romestylez/obs-multi-rtmp-websocket)**.
- (Opcional, para **Conectar cuenta**) Node.js 18+ para ejecutar `server/`.
- PC y consola en la **misma red local**.

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
3. En el PC, activa el **WebSocket de OBS** (puerto `4455`, con contraseña) e instala el
   plugin Multiple RTMP Output.
4. Permite el puerto `4455` en el Firewall de Windows (red privada).
5. Dentro de Plura escribe el **host** con la **IP LAN del PC** (ej. `192.168.1.20`).
   `localhost` no funciona desde la consola.
6. Configura cada plataforma en modo **Manual** (URL + clave) o **Conectar cuenta**
   (backend + código de vinculación).
7. Activa los switches y pulsa **▶ Iniciar stream**.

## Estructura del proyecto

```
XboxMultiStreamNative/
├── Plura.csproj                  ← proyecto UWP (x64 / ARM64, firma)
├── Package.appxmanifest          ← DisplayName Plura, fondo #050506, capacidades
├── Plura_TemporaryKey.pfx        ← certificado de desarrollo
├── Plura_Dev.cer                 ← certificado público
├── App.xaml / App.xaml.cs        ← punto de entrada, Frame raíz
├── MainPage.xaml                 ← interfaz nativa completa
├── MainPage.xaml.cs              ← lógica de UI, inicio/parada y polling
├── Models/
│   ├── PlatformViewModel.cs      ← estado de cada plataforma (INotifyPropertyChanged)
│   └── SelectionChip.cs          ← chips del resumen de selección
├── Services/
│   ├── ObsClient.cs              ← cliente WebSocket v5 + vendor romestylez.multi_rtmp
│   ├── SettingsStore.cs          ← persistencia en LocalSettings
│   └── Streaming/                ← proveedores manual/OAuth, coordinador y sink OBS
├── Properties/
│   ├── AssemblyInfo.cs
│   └── Default.rd.xml
└── Assets/                       ← iconos y splash en rojo/negro
```

## Cómo habla con OBS

`Services/ObsClient.cs` implementa el protocolo **obs-websocket v5** directamente sobre
`ClientWebSocket` (sin NuGet):

- Handshake: `Hello` (op 0) → `Identify` (op 1, con autenticación SHA-256) → `Identified` (op 2).
- Peticiones: `Request` (op 6) → `RequestResponse` (op 7), con timeout de 15 s.
- Vendor: `CallVendorRequest` con `vendorName = "romestylez.multi_rtmp"` (API PascalCase).

Flujo al iniciar una plataforma:

1. La primera plataforma seleccionada se convierte en la **transmisión principal** de
   OBS: `SetStreamServiceSettings` (`rtmp_custom`) + `StartStream`. Hay que esperar a que
   `GetStreamStatus.outputActive` sea `true`.
2. Para el resto, `EnsureTargetAsync`: `ListTargets` para buscar el destino por nombre,
   `AddTarget` si no existe, `UpdateServiceParam` (`key = "server"`), `UpdateStreamKey`
   y `StartTarget`.
3. El estado se refresca cada 2 s con `GetStreamStatus` + `ListTargets`/`GetTargetStats`.

El plugin reutiliza el **encoder principal** de OBS: sin la transmisión principal activa,
`PrepareOutputEncoders` muestra un aviso modal y bloquea la UI. Por eso nunca se inicia
un destino antes de que la principal esté activa. El fork no rellena `isRunning_`, así
que el estado en vivo se deduce de las estadísticas (bitrate, FPS o duración > 0).

Si la conexión se cae, Plura reintenta cada ~10 s.

## Capacidades del manifiesto

```xml
<Capability Name="internetClient" />
<Capability Name="privateNetworkClientServer" />
```

`privateNetworkClientServer` es imprescindible para llegar al OBS del PC por la LAN.

## Problemas frecuentes

| Síntoma | Causa / solución |
|---|---|
| “No se pudo conectar con OBS” | Host incorrecto: usa la IP LAN del PC, no `localhost`. Revisa puerto y contraseña. |
| Conecta pero no hay destinos | Falta el plugin Multiple RTMP Output o no se reinició OBS. |
| “OBS rechazó la conexión” | Contraseña del WebSocket incorrecta. |
| El stream no arranca | Stream Key o URL RTMP incorrectas; revisa el destino en OBS. |
| Aviso de encoder o el primer destino no arranca | OBS no está transmitiendo: Plura inicia la plataforma principal primero. Comprueba sus credenciales. |
| TikTok no conecta | Su URL RTMP es por cuenta/región: cópiala de TikTok LIVE Center. |
| El juego no aparece | La captura la hace OBS con la tarjeta HDMI; Plura solo controla OBS. |
| La app no se despliega | Consola y PC en la misma red (mejor por cable), Modo Desarrollador activo y salida de la consola a **1080p**. También puedes subir el paquete desde **Device Portal**. |

## Limitaciones

- Xbox **no permite** que apps de terceros capturen el juego: Plura controla OBS en un PC.
- La app se instala en **Modo Desarrollador**; la Store requiere acuerdo de partner.
- Resolución/FPS/bitrate de la app son de referencia: el encoder se configura en OBS.
- **Plura** como destino requiere un servidor RTMP propio.

## Estado de validación

- Compilación nativa (csc + ilc) y despliegue **loose** en Xbox: **correctos**.
- Conexión y autenticación contra OBS 33 + obs-websocket 5.7.6 desde la consola:
  **verificada**.
- Vendor `romestylez.multi_rtmp`: `ListTargets`, `AddTarget`, `StartTarget`,
  `StopTarget` y `GetTargetStats`: **verificados**.
- Transmisión principal + destino simultáneos contra dos receptores RTMP locales:
  **~6 Mbps / 30 fps en ambos**, con parada limpia.
- Modo **Conectar cuenta** con el backend de referencia: código → vinculación →
  `/v1/ingest/twitch` → `source=Account` → stream principal activo: **verificado**.
- La captura y la inyección de entrada no están disponibles desde Device Portal; la
  verificación de la interfaz se hace por logs (`LocalState/plura.log`).
