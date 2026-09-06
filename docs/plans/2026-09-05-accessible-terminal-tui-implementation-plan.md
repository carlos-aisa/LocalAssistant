# Plan de implementación: fase 5, incremento 5 — evaluación y TUI accesible mínima

## Objetivo

Evaluar mediante prototipos desechables un máximo de tres presentadores de terminal y, con la evidencia obtenida, entregar una TUI mínima accesible en el mismo incremento. `TerminalClientApplication` seguirá siendo la única autoridad de comandos y de estado; el modo textual seguirá siendo el fallback elegido antes de arrancar la aplicación.

## Decisiones y límites aprobados

- La entrada es una línea y usa los comandos existentes (`approve`, `reject`, `/exit`, `/new`, `/provider`, `/conversations`, etc.). No habrá atajos globales ni aprobación de una sola tecla.
- La TUI no interpreta `Write` o `WriteLine`. El cliente emitirá salida estructurada mínima y segura para mensajes públicos de conversación, mensajes operativos, errores seguros y solicitudes de entrada. El modo textual formateará esos mismos elementos.
- El bucle TUI será el único que toca controles. `TerminalClientApplication` se ejecutará en un worker; `ReadLine` y `ReadSecret` pueden bloquear ese worker, nunca el bucle UI.
- El sink encolará snapshots y retornará inmediatamente. Coalescerá snapshots ordinarios al último completo y preservará hasta su primera aplicación todo cambio de confirmación pendiente o error seguro.
- Entrada secreta: no transcript, snapshot, diagnóstico ni salida estructurada; el control y buffer se limpian tras submit, cancelación o EOF.
- `--plain`, stdin/stdout redireccionado y terminal incompatible seleccionan texto sin ANSI. Si la inicialización TUI falla antes de `RunAsync`, se permite texto; durante una sesión no se cambia de presentador.
- Quedan fuera audio/TTS/animación, `PlayingVoice`, nuevas rutas HTTP, modelo adicional de estado y resumen seguro del efecto de una tool. La ausencia de este resumen seguirá documentada como límite de consentimiento.

## Riesgo de dependencia

El cliente actual apunta a `net8.0`. Terminal.Gui 2.4.17, estable actual, apunta a .NET 10. No se elevará el framework implícitamente: la evaluación debe demostrar una versión v2 estable mantenida compatible con `net8.0`; si no existe, Terminal.Gui no pasa el criterio. Una actualización de framework requeriría aprobación explícita posterior. Una versión histórica sin listar no es un sustituto aceptable.

## Paso 1: prototipos y decisión registrada

**Archivos:** directorio temporal no referenciado `prototypes/terminal-client-tui-evaluation/`; nuevo `docs/evaluations/2026-09-05-terminal-tui-evaluation.md`; ADR consecutiva en `docs/adr/` una vez decidida la arquitectura.

Crear tres prototipos mínimos y desechables: Spectre.Console, Terminal.Gui v2 y renderizado propio mínimo/texto de referencia. Cada uno debe demostrar snapshot sintético, entrada de línea, `Ctrl+C`, resize, estado asíncrono, confirmación resuelta solo con línea completa, redirección sin ANSI y pruebas sin terminal real. Registrar versión, framework, licencia, dependencias, mantenimiento, Windows Terminal + PowerShell, lector de pantalla, sin color y movimiento reducido.

Descartar Spectre si Live Display no admite interacción segura; descartar Terminal.Gui si no cumple `net8.0` o falla input, resize o driver falso. Elegir el candidato que pase todos los criterios con menor superficie. Si ambos paquetes fallan, el renderer propio solo se acepta si supera la misma matriz. El resultado incluye comandos reproducibles, trade-offs y ADR para la decisión durable. Eliminar los prototipos no elegidos antes del cierre.

## Paso 2: separar entrada, salida semántica y texto

**Archivos:** `TerminalConsole.cs`; nuevo `TerminalOutput.cs`; presentador textual nuevo o ajustado; `TerminalClientApplication.cs`; `TerminalClientApplicationTests.cs`.

Definir contratos internos inmutables para solicitud de entrada normal/secreta y salida pública de conversación, operativa y de error seguro. Conservar parser y flujo de `TerminalClientApplication`; solo cambiar cómo solicita entrada y publica contenido. `ShowResponse` e historial emitirán mensajes públicos tipados; herramientas, iteraciones, ayuda, selector y avisos serán operativos; errores permanecerán errores seguros clasificados. El presentador textual debe reproducir el formato actual.

La lectura secreta devolverá el mismo valor vacío de cancelación que hoy, pero nunca escribirá su valor. Pruebas: secuencia estructurada equivalente en texto, transcript tipado sin análisis de cadenas y ausencia de credencial, bearer y desafío en toda salida.

## Paso 3: composición y fallback

**Archivos:** `TerminalClientOptions.cs`, `Program.cs`, nuevo `TerminalPresentationSelector.cs`, pruebas nuevas de selector.

Añadir `--plain`. Extraer una sonda inyectable que, antes de construir e iniciar la aplicación, seleccione texto para `--plain`, redirección o incompatibilidad; TUI únicamente para terminal interactiva aprobada. Si el host TUI falla antes de `RunAsync`, liberar recursos y construir texto. Mantener `Console.CancelKeyPress` y desregistrarlo siempre. Probar toda la matriz con una sonda falsa, incluida inicialización fallida previa al arranque y salida textual sin ANSI.

## Paso 4: host TUI elegido, colas y secreto

**Archivos según decisión:** referencia concreta en `LocalAssistant.TerminalClient.csproj` si el paquete elegido es compatible; nuevos `TerminalClientTuiHost`, `TerminalClientTuiConsoleAdapter`, `TerminalClientTuiStateSink` y `TerminalClientTuiPresenter`; `Program.cs`; pruebas de adaptadores.

Implementar el host con un único hilo dueño de controles. Ejecutar `RunAsync` en worker y usar cola de solicitudes/completadores para `ReadLine` y `ReadSecret`. El UI procesa resize, pegado, `Ctrl+C`, EOF, submit y snapshots, pero no interpreta ni ejecuta comandos: entrega la línea completa al mismo camino de entrada textual.

El sink será seguro para hilos, almacenará el último snapshot, preservará los cambios prioritarios de confirmación/error y programará un único drenaje del bucle UI. Renderizará solo desde ese hilo y nunca transicionará estado. El host restaurará el terminal mediante `finally` para `/exit`, EOF, cancelación o fallo capturado.

La TUI tendrá estado (lifecycle, proveedor, conversación abreviada), transcript público con scroll, confirmación destacada, actividad/error seguro y entrada de una línea. Secretos tendrán etiqueta clara y máscara o entrada vacía; su buffer se vacía inmediatamente. No se añaden atajos globales ni voz.

## Paso 5: pruebas deterministas y accesibilidad

**Archivos:** pruebas nuevas de host, entrada, renderer y coalescencia en `tests/LocalAssistant.Tests/TerminalClient/`; posible ajuste de `TerminalClientStateTests.cs`.

Con driver y reloj falsos, cubrir worker bloqueado mientras UI procesa resize y snapshots, coalescencia que no oculta confirmación/error, controles solo en hilo UI, aislamiento de sink, confirmación únicamente por `approve`/`reject`, secreto enmascarado y ausente del transcript, render de historial/proveedor/conversación/actividad/error incierto, cierre con `Ctrl+C`/EOF/`/exit`, y ausencia de ANSI en texto/redirección. Mantener los flujos existentes para probar que pairing, sesión, selector y confirmaciones pasan por el mismo `TerminalClientApplication`.

## Paso 6: documentación y cierre

**Archivos:** `README.md`, `docs/SECURITY.md`, `docs/ROADMAP.md`, especificación de evaluación, registro de evaluación y ADR.

Documentar elección, versión, fallback, `--plain`, limitaciones de accesibilidad y secreto. Aclarar que transcript es solo público y seguro, confirmación requiere texto explícito y todavía no hay resumen seguro de efecto. Marcar el punto 5 solo tras evidencias completas. Añadir guía de comprobación manual Windows Terminal + PowerShell: resize, `Ctrl+C`, pegado, caracteres españoles, `--plain`, confirmación, secreto sin eco y restauración de cursor/pantalla.

## Verificación

Durante desarrollo se ejecutarán tests focalizados. Antes del cierre:

```powershell
dotnet restore LocalAssistant.sln
dotnet format LocalAssistant.sln --no-restore --verify-no-changes
dotnet build LocalAssistant.sln -c Release --no-restore
dotnet test LocalAssistant.sln -c Release --no-restore
git diff --check
```

Antes de PR se ejecutará la revisión de diff obligatoria de `AGENTS.md`; se corregirán hallazgos mecánicos y se repetirán los checks afectados. La comprobación manual se registrará como evidencia y no sustituirá tests deterministas.
