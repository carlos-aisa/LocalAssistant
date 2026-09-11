# Diseño del incremento 8 de la fase 5: cierre operativo de Windows

## Estado y propósito

Diseño **implementado y verificado** (incremento 8 de la fase 5, 2026-09-10). Cierra la fase 5 haciendo
que el cliente terminal .NET sea *desplegable y operable* en el equipo Windows objetivo
sin recompilar y sin incrustar rutas personales ni secretos: publicación reproducible,
configuración fuera del binario, un modo de diagnóstico de un disparo y un smoke test
reproducible.

No cambia el comportamiento conversacional, la salida hablada, la TUI, el modelo de
estado ni el contrato HTTP. No introduce instalador, arranque automático de la API,
servicio de Windows, job de CI en Windows ni el proveedor neuronal de TTS (evolución
posterior descrita en `docs/ROADMAP.md`). SAPI sigue siendo la implementación de voz.

Este es el último de los ocho incrementos de la fase 5.

## Alcance

El incremento incluye:

- una publicación **dependiente de framework** para `win-x64` (`--self-contained false`),
  con layout de salida documentado y `appsettings.json` junto al ejecutable;
- carga de configuración con `Microsoft.Extensions.Configuration`: `appsettings.json`,
  variables de entorno `LocalAssistant__TerminalClient__*` y argumentos de línea de
  comandos, con esta precedencia (mayor gana): **CLI > entorno > `appsettings.json` >
  valores por defecto internos**;
- validación centralizada e inalterada: base URL solo HTTP(S) en loopback, proveedor
  `fake` u `ollama`, escenario no vacío, timeout de petición positivo y no mayor de
  una hora;
- un modo `--diagnostics` de un disparo que imprime un informe de entorno y preparación
  redactado y termina, sin abrir sesión, sin pedir credenciales y sin entrar al chat;
- `--version` y `--help`;
- mensajes de arranque accionables cuando la API no responde en el puerto configurado,
  dejando claro que el cliente **no** la arranca;
- un script `scripts/Invoke-TerminalClientSmoke.ps1` y un runbook reproducible en
  `docs/evaluations/`, con registro de la ejecución real;
- documentación de la ubicación del estado DPAPI y de que la publicación no lo incluye;
- actualización de `README.md`, `docs/ARCHITECTURE.md`, `docs/SECURITY.md` y, al final,
  `docs/ROADMAP.md`.

Quedan fuera:

- instalador, MSI, empaquetado MSIX, arranque automático o servicio de Windows;
- lanzar, alojar o supervisar la API desde el cliente;
- publicación autocontenida o de un solo archivo, `ReadyToRun` y trimming;
- un job de CI en Windows; la suite sigue ejecutándose en Linux y el cierre Windows es
  manual, como en el incremento 7;
- logging a fichero, telemetría o métricas persistidas;
- instalación o descarga automática de voces o motores;
- cualquier cambio de endpoints, OpenAPI, audio remoto o STT;
- secretos, tokens o rutas personales en `appsettings.json` o en la salida de
  `--diagnostics`.

## Configuración fuera del binario

### Modelo

Una sección `TerminalClient` con cuatro claves no secretas:

```jsonc
{
  "TerminalClient": {
    "BaseUrl": "http://localhost:5100",
    "Provider": "ollama",
    "Scenario": "direct",
    "RequestTimeout": "00:04:00"
  }
}
```

`appsettings.json` se publica junto al ejecutable con estos valores por defecto, se
marca `CopyToOutputDirectory=PreserveNewest` y es seguro versionarlo y editarlo. No
contiene ni contendrá credenciales, identificadores de cliente, desafíos, tokens,
rutas personales ni preferencias de voz.

Las preferencias de voz (voz, velocidad, volumen, silencio) **no** son configuración:
son estado de usuario y siguen viviendo solo en el payload DPAPI del incremento 7,
modificadas por `/voice`, `/rate`, `/volume`, `/mute` y `/unmute`.

### Fuentes y precedencia

1. Valores por defecto internos (los actuales de `TerminalClientOptions.Parse`).
2. `appsettings.json` en el directorio base de la aplicación
   (`AppContext.BaseDirectory`), opcional: su ausencia no es un error.
3. Variables de entorno con prefijo `LocalAssistant__` (mismo prefijo que la API);
   `LocalAssistant__TerminalClient__BaseUrl`, `__Provider`, `__Scenario`,
   `__RequestTimeout`.
4. Argumentos de línea de comandos: `--base-url=`, `--provider=`, `--scenario=`,
   `--request-timeout=`, además de `--plain` (solo CLI, situacional).

La resolución produce un objeto con el **valor efectivo** y el **origen** de cada clave
(`Default`, `AppSettings`, `Environment`, `CommandLine`) para que `--diagnostics` lo
muestre. La validación se aplica una sola vez sobre el resultado combinado y produce
los mismos mensajes y código de salida `2` que hoy ante un valor inválido, indicando
qué fuente aportó el valor rechazado.

`--plain` permanece exclusivamente como argumento; no se lee de fichero ni de entorno.

### Frontera de implementación

`TerminalClientOptions` sigue siendo el contrato inmutable que consume la aplicación.
Se añade un cargador (`TerminalClientConfiguration`) que compone las fuentes, resuelve
orígenes y construye `TerminalClientOptions` validado. `Program.Main` llama al cargador
en lugar de a `TerminalClientOptions.Parse` directamente; `Parse` se conserva como la
capa CLI o se pliega dentro del cargador sin perder ninguna regla de validación ni
ningún caso de prueba existente.

## Modo de diagnóstico

`LocalAssistant.TerminalClient --diagnostics` produce un informe y termina. No abre
`HttpClient` para nada salvo un `GET /health` de solo lectura, no crea sesión, no lee
ni escribe el estado DPAPI salvo para inspeccionarlo, no pide credenciales, no entra al
bucle de chat y no reproduce audio.

### Contenido del informe (todo redactado)

- **Cliente**: versión informativa del ensamblado y versión de archivo.
- **Entorno**: versión del runtime .NET, descripción del SO, arquitectura del proceso,
  cultura actual.
- **Configuración**: ruta de `appsettings.json` y si existe; por cada clave efectiva,
  su valor y su origen (`Default` / `AppSettings` / `Environment` / `CommandLine`).
  Los cuatro valores (base URL, proveedor, escenario, timeout) no son secretos y se
  muestran completos.
- **API**: URI base resuelta y si es loopback; resultado de `GET /health` como uno de
  `reachable` (200), `unreachable` (conexión rechazada), `timeout`, o
  `unexpected_status <código>`. En cualquier caso distinto de `reachable`, una línea de
  ayuda: *"La API no responde en esta dirección. Arráncala manualmente antes de usar el
  cliente; el cliente no la inicia."*
- **Estado local**: ruta de `private-client.json`; si existe; si el JSON legible es
  válido; versión de formato; `ClientId` si está presente (es un identificador, no un
  secreto); si hay `LastConversationId` (solo sí/no). Nunca se descifra el payload ni se
  muestran credenciales.
- **Bearer**: línea fija comprobada — *"no persistido; solo en memoria durante la
  ejecución"*.
- **Presentación**: banderas de redirección de entrada/salida/error y qué presentación
  se elegiría (`tui` o `plain` con su motivo), reutilizando `TerminalPresentationSelector`.
- **Salida hablada**: si el proceso corre en Windows; si el subsistema de síntesis
  inicializa; número de voces habilitadas (**solo el recuento**, nunca los nombres) y si
  la disponibilidad sería `Ready` o `Unavailable`. No incluye voz, velocidad, volumen ni
  silencio efectivos: leerlos exigiría descifrar el payload DPAPI, y el informe nunca lo
  descifra. Esos valores siguen visibles con `/info` durante una sesión real.

### Código de salida

`0` si el informe se generó, incluso si la API está `unreachable`: la inalcanzabilidad
es información de diagnóstico, no un fallo del cliente. `2` si la configuración
combinada es inválida (mismo criterio que el arranque normal). `1` ante un error
inesperado al construir el informe.

### Sin logging a fichero

No se añade logging a fichero ni framework de logging. La única mejora en la ruta normal
es que el fallo de `health` al arrancar usa el mismo texto de ayuda accionable.

## Publicación

### Comando

```powershell
dotnet publish src/LocalAssistant.TerminalClient/LocalAssistant.TerminalClient.csproj `
  -c Release -r win-x64 --self-contained false `
  -o publish/terminal-client
```

### Propiedades del proyecto

- `TargetFramework` permanece en `net8.0` para que el CI Linux siga compilando y
  ejecutando la suite (restricción heredada del incremento 7).
- Se añade `<RuntimeIdentifiers>win-x64</RuntimeIdentifiers>` para habilitar la
  publicación dirigida sin fijar un RID en `build`.
- `<SatelliteResourceLanguages>en</SatelliteResourceLanguages>` para no arrastrar
  recursos localizados de las dependencias.
- `appsettings.json` con `CopyToOutputDirectory=PreserveNewest`.
- Metadatos de versión (`Version`, `InformationalVersion`) coherentes con el resto de la
  solución; si la solución no fija versión hoy, se usa `1.0.0` como base explícita del
  cliente y se documenta.
- Sin `PublishSingleFile`, `PublishTrimmed` ni `PublishReadyToRun`.

### Requisitos del equipo

La publicación dependiente de framework requiere el **runtime .NET 8** (`Microsoft.NETCore.App`)
instalado; el equipo objetivo ya lo tiene porque ejecuta la API. `System.Speech` y
`System.Windows.Extensions` se despliegan como DLLs junto al ejecutable y no requieren
el runtime de escritorio (WPF/WinForms).

### Layout de salida

Un directorio con `LocalAssistant.TerminalClient.exe`, sus DLLs, `appsettings.json` y
los archivos de depuración. El estado DPAPI **no** está en la salida: vive en
`%LOCALAPPDATA%\LocalAssistant\TerminalClient\private-client.json` y se crea en la
primera sesión válida.

## Smoke test reproducible

### Script

`scripts/Invoke-TerminalClientSmoke.ps1`, parámetros:

- `-BaseUrl` (por defecto `http://localhost:5100`);
- `-Provider` (`fake` por defecto para no depender de Ollama);
- `-PublishDir` (opcional: si se indica, ejecuta el `.exe` publicado; si no, `dotnet run`);
- `-SkipManual` (omite la sección guiada manual).

Comprobaciones automáticas (fallo ⇒ salida distinta de cero):

1. Publicación en un directorio temporal si `-PublishDir` no se indicó, o validación de
   que el `.exe` existe si sí.
2. `--version` imprime una versión no vacía.
3. `--diagnostics` produce el informe; se afirma que contiene la versión, que la base
   URL resuelta es loopback y que reporta la ruta del estado DPAPI.
4. No hay ningún `*.wav` bajo el directorio de trabajo ni bajo el de publicación.
5. Si `private-client.json` existe, su parte legible es JSON válido y **no** contiene las
   claves `credential`, `accessToken`, `bearer` ni `challenge`.

Sección guiada manual (salvo `-SkipManual`): imprime la lista de pasos (emparejar,
un turno real, `/voice`, `/repeat`, `/stop`, `/exit`), espera confirmación del operador
y luego re-comprueba que no quedan procesos `LocalAssistant.TerminalClient`,
`LocalAssistant.Api` ni `testhost` y que no ha aparecido ningún `.wav`.

El script no hace egreso de red fuera de loopback ni escribe secretos.

### Runbook y registro

`docs/evaluations/2026-09-10-windows-operational-close.md`: procedimiento reproducible
(publicar → configurar por fichero, entorno y CLI → `--diagnostics` con API parada y
arrancada → un turno real → voz → `/exit`) y un apartado donde se registra la ejecución
real con su fecha y resultado. El punto 8 del roadmap solo se marca tras esa ejecución.

## Seguridad y privacidad

- `appsettings.json` y la salida de `--diagnostics` no contienen secretos, tokens,
  desafíos ni rutas personales. Las cuatro claves de configuración no son sensibles.
- `--diagnostics` es de solo lectura: un `GET /health`, inspección no destructiva del
  estado local y sondeo del subsistema de voz. No descifra el payload DPAPI, no crea
  sesión y no escribe estado.
- El recuento de voces habilitadas se muestra; los nombres no, coherente con el
  incremento 7 (`/voice` es el único listado solicitado por el usuario).
- El cliente sigue sin poder arrancar, alojar ni supervisar la API. El diagnóstico de
  inalcanzabilidad solo orienta.
- El bearer sigue solo en memoria; la publicación no incluye el estado DPAPI; el estado
  se crea con permisos de usuario en `%LOCALAPPDATA%`.
- Las variables de entorno pueden ser visibles a otros procesos del usuario; por eso
  solo se admiten valores no secretos por esa vía, igual que en la API.
- El script de smoke test no realiza egreso externo y afirma la ausencia de artefactos
  de audio y de claves sensibles en el estado legible.

## Estrategia de pruebas obligatoria

### Configuración

- Precedencia exacta entre defecto, `appsettings.json`, entorno y CLI para cada clave.
- Ausencia de `appsettings.json` no es error; JSON malformado produce el código `2` con
  mensaje claro.
- `RequestTimeout` inválido (cero, negativo, no parseable o mayor de una hora) se
  rechaza; `01:00:00` se acepta.
- Base URL no loopback o de esquema no HTTP(S) se rechaza indicando la fuente.
- Proveedor y escenario conservan las reglas y mensajes actuales.
- El origen resuelto por clave es correcto en combinaciones mixtas.
- Todos los casos de `TerminalClientOptions.Parse` existentes siguen pasando.

### Diagnóstico

- El informe incluye todas las secciones y ninguna contiene credencial, token, desafío
  ni nombres de voz.
- `health` alcanzable, conexión rechazada, timeout y estado inesperado se clasifican y
  cada caso no alcanzable incluye la línea de ayuda.
- Estado local ausente, JSON legible válido, versión de formato conocida y desconocida,
  y `ClientId`/`LastConversationId` presentes o ausentes se reflejan sin descifrar nada.
- `--diagnostics` no abre sesión, no pide credenciales, no entra al bucle y no reproduce
  audio (verificado con dobles).
- Código de salida `0` con API inalcanzable; `2` con configuración inválida.
- La selección de presentación reportada coincide con `TerminalPresentationSelector`.
- Fuera de Windows, la sección de voz informa no disponible sin sondear SAPI.

### Argumentos

- `--version` y `--help` imprimen y terminan con código `0` sin abrir `HttpClient`.
- `--help` enumera todas las opciones y el prefijo de variables de entorno.
- Un argumento desconocido sigue devolviendo código `2`.

### Publicación y empaquetado

- El proyecto restaura y compila con `-r win-x64`.
- `appsettings.json` aparece en la salida de `publish`.
- La suite completa permanece verde en Linux sin RID.
- `dotnet list package --vulnerable --include-transitive` sobre el cliente está limpio.

### Smoke test

- El script falla de forma visible si falta el `.exe`, si aparece un `.wav` o si el
  estado legible contiene una clave sensible.
- `-SkipManual` ejecuta solo las comprobaciones automáticas y su código de salida es
  correcto en éxito y fallo.

## Verificación y cierre

Por cada incremento de código:

```powershell
dotnet format LocalAssistant.sln --verify-no-changes
dotnet build LocalAssistant.sln -c Release --no-restore
dotnet test tests/LocalAssistant.Tests/LocalAssistant.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~TerminalClient"
git diff --check
```

Antes de commit o PR: suite completa, `dotnet list package --vulnerable --include-transitive`
sobre el proyecto del cliente (por los paquetes de configuración nuevos), y comprobación
de que no quedan procesos `testhost`, `LocalAssistant.TerminalClient` ni
`LocalAssistant.Api` residuales.

El punto 8 del roadmap y la fase 5 quedan cerrados únicamente cuando:

- `dotnet publish -r win-x64 --self-contained false` produce un ejecutable operable con
  `appsettings.json` en la salida;
- la configuración se resuelve por defecto, fichero, entorno y CLI con la precedencia
  indicada y la validación intacta;
- `--diagnostics`, `--version` y `--help` funcionan y no filtran secretos ni nombres de
  voz;
- la credencial DPAPI vive fuera de la publicación y el bearer no se persiste;
- el cliente nunca arranca la API y orienta cuando no responde;
- el script de smoke test y el runbook existen y la ejecución real está registrada;
- CI Linux y la suite completa siguen verdes;
- `README.md`, `docs/ARCHITECTURE.md`, `docs/SECURITY.md` y `docs/ROADMAP.md` describen
  exclusivamente lo entregado y la fase 5 figura cerrada con sus ocho incrementos.
