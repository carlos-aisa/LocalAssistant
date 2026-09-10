# Plan de implementación: incremento 8 de fase 5 — cierre operativo de Windows

> **Estado: propuesto** (2026-09-10). Implementa
> `docs/specs/2026-09-10-phase5-windows-operational-close-design.md`. Cinco tandas:
> configuración fuera del binario, publicación y `--version`/`--help`, modo
> `--diagnostics`, script de smoke test, y documentación con cierre de la fase 5.

## Alcance confirmado

Hacer el cliente terminal publicable y operable en Windows sin recompilar: publicación
dependiente de framework para `win-x64`, configuración por `appsettings.json` + entorno
+ CLI con precedencia definida, `--diagnostics` de un disparo, `--version`, `--help`,
mensajes accionables cuando la API no responde, y un smoke test reproducible con
registro de ejecución.

Sin instalador, sin arranque de la API, sin job de CI en Windows, sin logging a
fichero, sin publicación autocontenida, sin cambios de contrato HTTP y sin el proveedor
neuronal de TTS.

## Suposiciones y decisiones de implementación

- `TargetFramework` sigue en `net8.0`; la publicación usa `-r win-x64` sin fijar RID en
  `build` para no romper el CI Linux.
- Configuración con `Microsoft.Extensions.Configuration(.Json/.EnvironmentVariables)`
  8.0.0, fijadas, más `System.Text.Json` 8.0.5 fijado para no arrastrar el aviso
  transitivo de la 8.0.0. No se añade `.Binder` (los valores se leen por indexador),
  `Microsoft.Extensions.Hosting` ni un contenedor DI.
- El prefijo de entorno es `LocalAssistant__` (igual que la API); la sección es
  `TerminalClient`.
- `TerminalClientOptions` permanece como el contrato inmutable validado que consume la
  aplicación. La composición de fuentes y el seguimiento de origen viven en un cargador
  nuevo; la validación es única y reutiliza los mensajes actuales.
- Las preferencias de voz no entran en configuración: siguen en el payload DPAPI.
- `--diagnostics`, `--version` y `--help` se resuelven antes de construir el grafo de
  presentación y de `HttpClient`; `--diagnostics` sí crea un `HttpClient` de solo lectura
  para un único `GET /health`.
- El informe de diagnóstico se construye en un tipo puro y testeable a partir de
  entradas ya recogidas (sondas inyectadas); la escritura a consola es una capa fina.
- El sondeo de voz reutiliza el seam existente (`WindowsSpeechSynthesizer.HasEnabledVoices`
  y la enumeración) sin construir SAPI fuera de Windows.
- Metadatos de versión: si la solución no fija versión, se introduce `1.0.0` explícito
  para el cliente en su `.csproj` y se documenta.

## Incremento 1 — Configuración fuera del binario

**Archivos**

- Modificar `src/LocalAssistant.TerminalClient/LocalAssistant.TerminalClient.csproj`
  (paquetes de configuración, `appsettings.json` como contenido copiado).
- Añadir `src/LocalAssistant.TerminalClient/appsettings.json`.
- Añadir `src/LocalAssistant.TerminalClient/TerminalClientConfiguration.cs`
  (cargador + resolución de origen + construcción de `TerminalClientOptions`).
- Modificar `src/LocalAssistant.TerminalClient/TerminalClientOptions.cs`
  (`RequestTimeout` configurable; conservar todas las reglas de validación y mensajes).
- Modificar `src/LocalAssistant.TerminalClient/Program.cs`
  (usar el cargador en `RunAsync`).
- Añadir `tests/LocalAssistant.Tests/TerminalClient/TerminalClientConfigurationTests.cs`.
- Modificar `tests/LocalAssistant.Tests/TerminalClient/TerminalClientOptionsTests.cs`.

**Implementación**

1. Añadir `Microsoft.Extensions.Configuration`, `.Json` y `.EnvironmentVariables`
   8.0.0 al `.csproj`, más `System.Text.Json` 8.0.5 fijado para cerrar el aviso
   transitivo que arrastra `.Json` 8.0.0.
2. Crear `appsettings.json` con la sección `TerminalClient` y los valores por defecto
   actuales (`http://localhost:5100`, `ollama`, `direct`, `00:04:00`).
   `CopyToOutputDirectory=PreserveNewest`.
3. `TerminalClientConfiguration.Load(string[] args, string baseDirectory, IEnvironment env)`:
   compone `AddJsonFile(appsettings.json, optional:true)` sobre `AppContext.BaseDirectory`,
   `AddEnvironmentVariables("LocalAssistant__")`, y superpone los argumentos CLI ya
   parseados. Devuelve un `TerminalClientConfigurationResult` con
   `TerminalClientOptions Options` y `IReadOnlyDictionary<string,string> Origins`
   (`BaseUrl`/`Provider`/`Scenario`/`RequestTimeout` → `Default|AppSettings|Environment|CommandLine`).
4. La validación (loopback, esquema, proveedor, escenario, timeout positivo y no
   mayor de `01:00:00`) se ejecuta una vez sobre el resultado combinado. Los mensajes
   son los actuales; se añade un sufijo que nombra la fuente (fichero o variable de
   entorno) cuando el valor rechazado no viene de un argumento.
5. `RequestTimeout` pasa de constante a propiedad de `TerminalClientOptions` con
   `RequestTimeout` por defecto conservado. `Program.CreateHttpClient` la usa.
6. `Program.RunAsync` llama al cargador; un `ArgumentException` mantiene el código `2`.
   `--plain` se sigue leyendo solo de `args`.

**Pruebas obligatorias**

- Cada clave resuelta desde cada fuente y con la precedencia CLI > entorno > fichero >
  defecto; origen reportado correcto en mezclas.
- `appsettings.json` ausente ⇒ defectos; malformado ⇒ código `2` claro.
- `RequestTimeout` cero, negativo, no parseable o mayor de `01:00:00` rechazados;
  `01:00:00` aceptado.
- Base URL no loopback / esquema inválido desde entorno menciona el origen.
- Todos los casos existentes de `TerminalClientOptions.Parse` siguen verdes.
- `HttpClient` usa el timeout configurado.

**Criterio de salida**

El cliente arranca con configuración externa y la validación no ha perdido ninguna regla
ni caso de prueba.

## Incremento 2 — Publicación, `--version` y `--help`

**Archivos**

- Modificar `src/LocalAssistant.TerminalClient/LocalAssistant.TerminalClient.csproj`
  (`RuntimeIdentifiers`, `SatelliteResourceLanguages`, versión).
- Modificar `src/LocalAssistant.TerminalClient/TerminalClientOptions.cs` o
  `TerminalClientConfiguration.cs` (reconocer `--version` y `--help` como modos).
- Modificar `src/LocalAssistant.TerminalClient/Program.cs` (atajos de modo antes del
  grafo).
- Añadir `src/LocalAssistant.TerminalClient/TerminalClientCommandLine.cs` si conviene
  separar el texto de ayuda y el volcado de versión.
- Modificar `tests/LocalAssistant.Tests/TerminalClient/TerminalClientProgramTests.cs`.

**Implementación**

1. `.csproj`: `<RuntimeIdentifiers>win-x64</RuntimeIdentifiers>`,
   `<SatelliteResourceLanguages>en</SatelliteResourceLanguages>`,
   `<Version>1.0.0</Version>` + `<InformationalVersion>` si la solución no las fija ya.
2. `--version`: imprime `LocalAssistant.TerminalClient <informational version>` y
   termina con `0`. No construye `HttpClient` ni presentación.
3. `--help`: imprime uso, todas las opciones (`--base-url=`, `--provider=`,
   `--scenario=`, `--request-timeout=`, `--plain`, `--diagnostics`, `--version`,
   `--help`), el prefijo `LocalAssistant__TerminalClient__` y la ubicación de
   `appsettings.json`. Termina con `0`.
4. Estos modos se detectan en `Program.RunAsync` tras parsear y antes de
   `RunConfiguredAsync`. Argumento desconocido sigue devolviendo `2`.

**Pruebas obligatorias**

- `--version` y `--help` terminan con `0`, escriben a salida estándar y no abren
  `HttpClient` ni presentación (dobles).
- `--help` menciona cada opción y el prefijo de entorno.
- `--version` produce una cadena no vacía.
- Argumento desconocido ⇒ `2`.
- La combinación `--help --diagnostics` prioriza `--help`.

**Criterio de salida**

`dotnet publish -c Release -r win-x64 --self-contained false` genera un ejecutable con
`appsettings.json` en la salida y los modos informativos funcionan.

## Incremento 3 — Modo `--diagnostics`

**Archivos**

- Añadir `src/LocalAssistant.TerminalClient/TerminalDiagnostics.cs`
  (`TerminalDiagnosticsReport` puro + `TerminalDiagnosticsProbe` con sondas inyectables +
  escritor).
- Modificar `src/LocalAssistant.TerminalClient/Program.cs` (rama `--diagnostics`).
- Modificar `src/LocalAssistant.TerminalClient/PrivateClientCredentialStore.cs` solo si
  hace falta exponer una lectura no destructiva de metadatos legibles (versión,
  `ClientId`, presencia de `LastConversationId`) sin descifrar.
- Modificar `src/LocalAssistant.TerminalClient/WindowsSpokenOutput.cs` solo para
  exponer, si no existe, un recuento de voces habilitadas reutilizable sin construir el
  coordinador.
- Añadir `tests/LocalAssistant.Tests/TerminalClient/TerminalDiagnosticsTests.cs`.

**Implementación**

1. `TerminalDiagnosticsReport` es un registro con secciones tipadas (cliente, entorno,
   configuración+orígenes, API, estado local, bearer, presentación, voz). `ToText()`
   produce el informe redactado.
2. `TerminalDiagnosticsProbe.BuildAsync(...)` recibe: opciones+orígenes ya resueltos,
   un `Func<CancellationToken,Task<HealthProbeResult>>` para `GET /health`, un lector no
   destructivo del estado local, `TerminalPresentationSelector` (con capacidades y
   factoría de driver reales), y una sonda de voz.
3. La sonda de `health` clasifica `reachable` / `unreachable` / `timeout` /
   `unexpected_status`. Todo lo que no sea `reachable` añade la línea de ayuda de
   arranque manual.
4. La sección de estado local nunca descifra: solo versión de formato, `ClientId`
   textual y `LastConversationId` como sí/no. Si el archivo no existe, se indica.
5. La sección de voz: en Windows sondea inicialización y recuenta voces habilitadas
   (**recuento, no nombres**); fuera de Windows informa no disponible sin sondear.
   Añade velocidad/volumen/silencio efectivos del estado DPAPI.
6. `Program`: si `--diagnostics`, construye el informe, lo escribe, y devuelve `0`
   (informe generado), `2` (configuración inválida) o `1` (fallo inesperado). No abre
   sesión, no pide credenciales, no entra al bucle, no reproduce audio.

**Pruebas obligatorias**

- El informe contiene todas las secciones; ninguna incluye credencial, token, desafío o
  nombre de voz.
- Los cuatro estados de `health` se clasifican y los no alcanzables llevan la ayuda.
- Estado local ausente/presente, versión conocida/desconocida, `ClientId` y
  `LastConversationId` presentes/ausentes, sin descifrado.
- Código `0` con API inalcanzable; `2` con configuración inválida; `1` ante fallo de
  sonda simulado.
- No se abre sesión ni se piden credenciales ni se reproduce audio (dobles que fallarían
  si se invocaran).
- La presentación reportada coincide con `TerminalPresentationSelector` para entradas
  redirigidas y no redirigidas.
- Fuera de Windows la sección de voz no toca SAPI.
- Orígenes de configuración correctos en el informe.

**Criterio de salida**

Un operador puede ejecutar `--diagnostics` con la API parada y arrancada y obtener un
informe accionable y sin secretos.

## Incremento 4 — Script de smoke test reproducible

**Archivos**

- Añadir `scripts/Invoke-TerminalClientSmoke.ps1`.
- Modificar `README.md` (breve referencia de uso; el detalle en el runbook).

**Implementación**

1. Parámetros `-BaseUrl`, `-Provider` (`fake`), `-PublishDir`, `-SkipManual`.
2. Si no hay `-PublishDir`, `dotnet publish` a un directorio temporal
   (`-c Release -r win-x64 --self-contained false`); si lo hay, validar el `.exe`.
3. Comprobaciones automáticas: `--version` no vacío; `--diagnostics` contiene versión,
   base URL loopback y ruta del estado DPAPI; sin `*.wav` bajo CWD ni bajo publish;
   `private-client.json` legible sin claves `credential`/`accessToken`/`bearer`/`challenge`.
4. Sección guiada manual (salvo `-SkipManual`): imprime los pasos, espera confirmación,
   re-comprueba procesos residuales y ausencia de `.wav`.
5. Salida `PASS`/`FAIL` y código distinto de cero ante cualquier fallo automático. Sin
   egreso de red fuera de loopback.

**Pruebas obligatorias**

Este script se valida por ejecución en el runbook (no hay pruebas .NET). Se ejecuta
`-SkipManual` en la sesión y se registra el resultado; se comprueba que un `.wav`
sembrado a mano hace fallar el script.

**Criterio de salida**

El script corre de principio a fin en Windows, pasa en verde con `-SkipManual` y falla
de forma visible ante un artefacto de audio o una clave sensible.

## Incremento 5 — Documentación, verificación manual y cierre de la fase 5

**Archivos**

- Modificar `README.md` (publicación, configuración externa y precedencia,
  `--diagnostics`/`--version`/`--help`, ubicación del estado DPAPI, referencia al smoke
  test).
- Modificar `docs/ARCHITECTURE.md` (el cliente terminal se publica dependiente de
  framework, se configura fuera del binario y expone un diagnóstico de solo lectura; no
  aloja ni arranca la API).
- Modificar `docs/SECURITY.md` (configuración no secreta, `--diagnostics` de solo
  lectura y sin nombres de voz, estado DPAPI fuera de la publicación, variables de
  entorno solo para valores no sensibles).
- Añadir `docs/evaluations/2026-09-10-windows-operational-close.md` (runbook + registro
  de ejecución real).
- Modificar `docs/ROADMAP.md` **al final**: marcar el punto 8 `[x]` y ajustar el texto
  de cierre de la fase 5 si procede.

**Implementación documental**

1. Documentar el comando de `publish`, el layout de salida y el requisito de runtime.
2. Documentar la sección `TerminalClient`, el prefijo de entorno, los argumentos y la
   precedencia; dejar claro que `appsettings.json` no lleva secretos ni preferencias de
   voz.
3. Documentar `--diagnostics` (qué informa, que es de solo lectura, que no arranca la
   API) y `--version`/`--help`.
4. Registrar los paquetes de configuración nuevos y sus versiones/licencias.
5. Ejecutar el runbook en Windows (API parada y arrancada, un turno real, voz, `/exit`,
   smoke script) y registrar el resultado real con fecha.
6. Marcar el punto 8 y el cierre de la fase 5 solo cuando la suite, la auditoría de
   dependencias y la ejecución manual hayan pasado.

**Escenarios manuales obligatorios**

- `dotnet publish` produce el ejecutable; `appsettings.json` presente en la salida.
- Configurar por fichero, por variable de entorno y por CLI, comprobando la precedencia
  con `--diagnostics`.
- `--diagnostics` con la API parada muestra `unreachable` + ayuda; con la API arrancada
  muestra `reachable`.
- Emparejar, un turno real, `/voice`, `/repeat`, `/stop`, `/exit`; cursor y terminal
  restaurados.
- Sin `*.wav` antes y después; `private-client.json` sin claves sensibles en su parte
  legible; bearer no persistido.
- Sin procesos `LocalAssistant.TerminalClient`, `LocalAssistant.Api` ni `testhost`
  residuales.

**Criterio de salida**

`README.md`, `docs/ARCHITECTURE.md`, `docs/SECURITY.md` y `docs/ROADMAP.md` describen
solo lo entregado; la fase 5 figura cerrada con sus ocho incrementos y el runbook tiene
una ejecución real registrada.

## Verificación por incremento

Al terminar cada incremento de código:

```powershell
dotnet format LocalAssistant.sln --verify-no-changes
dotnet build LocalAssistant.sln -c Release --no-restore
dotnet test tests/LocalAssistant.Tests/LocalAssistant.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~TerminalClient"
git diff --check
```

Tras el incremento 1 y antes del cierre:

```powershell
dotnet list src/LocalAssistant.TerminalClient/LocalAssistant.TerminalClient.csproj package --include-transitive
dotnet list src/LocalAssistant.TerminalClient/LocalAssistant.TerminalClient.csproj package --vulnerable --include-transitive
```

Antes de solicitar commit o PR: suite completa y comprobación de procesos residuales. El
pre-PR aplica el checklist de revisión del repositorio al diff completo.

## Criterio de cierre del incremento 8 y de la fase 5

El punto 8 queda cerrado únicamente cuando:

- `dotnet publish -r win-x64 --self-contained false` produce un ejecutable operable con
  `appsettings.json` en la salida;
- la configuración se resuelve por defecto, fichero, entorno y CLI con precedencia
  CLI > entorno > fichero > defecto y la validación intacta;
- `--diagnostics`, `--version` y `--help` funcionan sin filtrar secretos ni nombres de
  voz;
- la credencial DPAPI vive fuera de la publicación y el bearer no se persiste;
- el cliente nunca arranca la API y orienta cuando no responde;
- el script de smoke test y el runbook existen y hay una ejecución real registrada;
- CI Linux y la suite completa siguen verdes;
- la documentación y el roadmap reflejan exactamente lo entregado y la fase 5 figura
  cerrada.
