# Plan de implementación: cierre de gaps de la TUI de terminal

## Objetivo

Implementar las decisiones aprobadas en
`docs/specs/2026-09-06-terminal-tui-gap-corrections-design.md` para que cancelación,
EOF, compatibilidad, layout, secretos y volumen del transcript cumplan los criterios
de cierre del punto 5 de la fase 5.

La implementación conserva `TerminalClientApplication` como única autoridad de
comandos y estado, mantiene el cliente en `net8.0` y no cambia contratos HTTP,
autenticación, autorización ni OpenAPI.

## Estado

- Incremento 1 — **finalizado**.
- Incremento 2 — **finalizado** (2026-09-07). Diseño y plan de detalle en
  `docs/specs/2026-09-06-terminal-tui-compatibility-preflight-design.md` y
  `docs/plans/2026-09-06-terminal-tui-compatibility-preflight-implementation-plan.md`.
  Verificación manual registrada en
  `docs/evaluations/2026-09-05-terminal-tui-evaluation.md`.
- Incremento 3 — **finalizado** (implementado 2026-09-07, verificación manual
  2026-09-08). Diseño y plan de detalle en
  `docs/specs/2026-09-07-terminal-tui-key-interpretation-and-safe-layout-design.md` y
  `docs/plans/2026-09-07-terminal-tui-key-interpretation-and-safe-layout-implementation-plan.md`.
  `TerminalKeyInterpreter` puro, host como ejecutor de intenciones, contrato de
  `TryReadInput` probado y layout con prioridad de retención por umbrales. Verificación
  manual en `docs/evaluations/2026-09-05-terminal-tui-evaluation.md`; hallazgo abierto:
  EOF/`Ctrl+C` en el prompt de confirmación deja la confirmación pendiente en el
  servidor (arreglo en su propio commit).
- Incrementos 4 y 5 — pendientes. El punto 5 de `ROADMAP.md` permanece desmarcado.

## Supuestos de implementación

- El canal de entrada distingue entre completar la solicitud actual y cerrarse de
  forma terminal.
- El host sigue siendo el único propietario del buffer visual y del driver.
- `Ctrl+D` y `Ctrl+Z` se interpretan en la frontera de entrada del host, donde se
  conoce si existe texto en el buffer. El driver real solo entrega la tecla; esta
  distribución conserva el comportamiento aprobado sin introducir estado visual en
  el driver.
- El tamaño mínimo compatible es 40 columnas por 8 filas.
- Los límites visuales son 65.536 caracteres normalizados y 2.000 líneas envueltas.
- Los tests pueden usar timeouts únicamente como protección contra un cuelgue; el
  avance normal se sincroniza mediante señales observables del driver y adaptador.

## Incremento 1: canal de entrada terminal y limpieza del buffer

**Archivos:**

- `src/LocalAssistant.TerminalClient/TerminalClientTui.cs`
- `tests/LocalAssistant.Tests/TerminalClient/TerminalClientTuiTests.cs`
- `tests/LocalAssistant.Tests/TerminalClient/TerminalClientApplicationTests.cs`

### Implementación

1. Añadir al adaptador de consola un estado de canal cerrado protegido por
   `_inputLock`.
2. Separar `CompleteInput`, que resuelve solo el prompt vigente, de `CloseInput`, que
   marca el canal como cerrado y completa con `null` cualquier espera actual.
3. Hacer que `RequestInput` compruebe el cierre dentro del mismo lock antes de
   publicar una solicitud. Las lecturas posteriores devolverán inmediatamente `null`
   para líneas y el valor vacío semántico existente para secretos.
4. Sustituir la cancelación puntual registrada por el host por el cierre persistente
   del canal.
5. Solicitar al bucle del host que descarte el buffer visual después de cancelación o
   EOF. Limpiarlo también en `finally`, antes de restaurar el terminal.
6. Mantener `Escape` y las cancelaciones funcionales como finalización del prompt
   vigente, sin cerrar el canal completo.

La señal de limpieza cruzará hilos mediante estado atómico. El callback de
cancelación no modificará directamente el `StringBuilder` ni llamará al driver.

### Pruebas deterministas

- Cancelar cada uno de los tres prompts del pairing y comprobar que no aparece una
  espera posterior.
- Cancelar entre dos prompts consecutivos y comprobar `Closing → Closed`.
- Escribir parcialmente un secreto, cancelar y verificar que los frames posteriores
  no conservan enmascarado ni contenido.
- Comprobar que una lectura creada después de `CloseInput` termina inmediatamente.
- Comprobar que `Escape` no impide solicitar una nueva línea.
- Verificar restauración única en cierre normal, cancelación y excepción capturada.

## Incremento 2: compatibilidad comprobada antes del arranque

**Archivos:**

- `src/LocalAssistant.TerminalClient/TerminalPresentationSelector.cs`
- `src/LocalAssistant.TerminalClient/TerminalDriver.cs`
- `src/LocalAssistant.TerminalClient/Program.cs`
- `tests/LocalAssistant.Tests/TerminalClient/TerminalPresentationSelectorTests.cs`
- `tests/LocalAssistant.Tests/TerminalClient/TerminalClientTuiTests.cs`
- nuevo
  `tests/LocalAssistant.Tests/TerminalClient/TerminalClientProgramTests.cs`

### Implementación

1. Sustituir `UseTui` por una selección que devuelva un resultado interno de
   compatibilidad con modo, motivo seguro y, solo al elegir TUI, el driver ya
   inicializado. El contrato no se convertirá en API pública.
2. Comprobar `--plain`, redirecciones, tamaño mínimo y disponibilidad de las
   operaciones de consola necesarias.
3. Añadir `TryInitialize` al contrato interno del driver. La operación se ejecutará
   antes de construir `TerminalClientApplication`, validará tamaño y operaciones
   de consola, preparará y restaurará un frame inicial, y no leerá ni descartará
   teclas.
4. Ante `IOException`, `InvalidOperationException` o
   `PlatformNotSupportedException`, restaurar cualquier preparación parcial y elegir
   el cliente textual.
5. Extraer de `Main` una función interna de composición con factories inyectables de
   driver y aplicación. Su única responsabilidad será fijar el renderer, construir la
   aplicación correspondiente y ejecutarla. Esta costura permitirá comprobar el orden
   sin publicar una API ni trasladar lógica de negocio fuera de
   `TerminalClientApplication`.
6. Crear la aplicación con el renderer definitivo solo después de obtener una
   decisión satisfactoria. No añadir fallback después de publicar el estado inicial.

La razón del fallback puede mostrarse mediante un mensaje fijo y seguro, sin incluir
excepciones internas ni datos de entrada.

### Pruebas deterministas

- Matriz de `--plain`, cada redirección, tamaño `39×8`, `40×7` y `40×8`.
- Fallo controlado de cada operación de inicialización y restauración posterior.
- Confirmar mediante la costura de composición que la factory TUI no se invoca al
  fallar su inicialización y que se construye una sola aplicación textual.
- Confirmar que el modo textual se elige una sola vez y que no existe cambio de
  renderer después del arranque.
- Mantener los tests existentes de selección interactiva y adaptar sus dobles al
  resultado real de compatibilidad.

## Incremento 3: interpretación de EOF y layout seguro

**Archivos:**

- `src/LocalAssistant.TerminalClient/TerminalClientTui.cs`
- `src/LocalAssistant.TerminalClient/TerminalDriver.cs`
- `src/LocalAssistant.TerminalClient/TerminalTextSanitizer.cs`
- `tests/LocalAssistant.Tests/TerminalClient/TerminalClientTuiTests.cs`

### Implementación

1. Centralizar la interpretación pura de teclas para distinguir texto, edición,
   scroll y cierre terminal.
2. Traducir `Ctrl+D` y `Ctrl+Z` con buffer vacío a `CloseInput`. Con texto presente,
   ignorarlos sin completar el prompt.
3. Añadir una normalización de metadatos de una sola línea. Representará `CR`, `LF`,
   ESC, ANSI/OSC y controles C0/C1 como escapes visibles. La normalización actual del
   transcript continuará conservando saltos seguros.
4. Reemplazar el footer posicional por un compositor con presupuesto de filas. Reservar
   primero input, confirmación con UTC, error con incertidumbre y estado; asignar solo
   el espacio restante al transcript.
5. Para tamaños inferiores a `40×8`, producir el frame compacto definido por la
   especificación y una indicación para ampliar la terminal.
6. Mostrar el extremo activo de una entrada larga. Mantener únicamente máscaras para
   secretos y preservar backspace sobre el final lógico de la línea.
7. Garantizar que cada string entregado al driver representa exactamente una fila
   física.

### Pruebas deterministas

- `Ctrl+D` y `Ctrl+Z` con buffer vacío y no vacío; ninguna variante puede aprobar una
  confirmación.
- Frames de `40×8`, de tamaños mayores y de modo compacto.
- Error incierto, confirmación, caducidad, estado e input visibles simultáneamente en
  el mínimo compatible.
- Metadatos con `CR`, `LF`, ESC, ANSI, OSC y controles C0/C1 sin filas adicionales ni
  secuencias ejecutables.
- Entrada normal mayor que el ancho, entrada secreta equivalente, backspace y texto
  español.
- Resize mientras el worker espera entrada normal y mientras espera un secreto.
- Confirmar que PageUp/PageDown solo cambian el viewport y nunca completan un prompt.

## Incremento 4: transcript con presupuesto acotado

**Archivos:**

- `src/LocalAssistant.TerminalClient/TerminalClientTui.cs`
- nuevo `src/LocalAssistant.TerminalClient/TerminalClientTuiTranscript.cs`
- `tests/LocalAssistant.Tests/TerminalClient/TerminalClientTuiTests.cs`

### Implementación

1. Sustituir el límite por número de entradas por una cola que contabilice caracteres
   normalizados y líneas envueltas.
2. Eliminar entradas completas antiguas al superar 65.536 caracteres o 2.000 líneas.
3. Truncar por el inicio una entrada individual demasiado grande, conservar su final y
   anteponer un marcador visible que también consuma presupuesto.
4. Evitar reconstruir contenido fuera del rango necesario para el viewport y scroll.
5. Recalcular de forma acotada las líneas al cambiar el ancho, sin conservar cachés que
   puedan quedar asociados a dimensiones anteriores.
6. Mantener el scroll en un rango válido después de recortar entradas o redimensionar.

No se modifica el historial persistido ni el contenido enviado o recibido por HTTP;
los límites pertenecen únicamente a la representación visual de la sesión actual.

### Pruebas deterministas

- Una entrada exactamente en cada límite y otra que lo exceda.
- Una respuesta individual muy grande conserva final y marcador.
- Muchas respuestas expulsan primero las más antiguas.
- Resize y scroll posteriores al recorte no producen offsets inválidos.
- El número de caracteres y líneas procesados permanece dentro del presupuesto.
- El contenido truncado no reaparece al enviar una línea nueva ni llega a secretos o
  snapshots.

## Incremento 5: cierre documental y comprobación manual

**Archivos:**

- `README.md`
- `docs/SECURITY.md`
- `docs/ROADMAP.md`
- `docs/evaluations/2026-09-05-terminal-tui-evaluation.md`
- `docs/specs/2026-09-06-terminal-tui-gap-corrections-design.md`, únicamente para
  corregir una discrepancia descubierta durante la implementación; cualquier cambio
  de decisión requerirá nueva aprobación

### Trabajo

1. Documentar tamaño mínimo, fallback previo al arranque, teclas EOF, límites del
   transcript y comportamiento ante resize compacto.
2. Explicar honestamente la limpieza de mejor esfuerzo de secretos en memoria
   administrada.
3. Ejecutar y registrar en Windows Terminal + PowerShell:
   - resize por encima y debajo del mínimo;
   - `Ctrl+C`, `Ctrl+Z`, `/exit` y restauración del cursor;
   - pairing cancelado en cada prompt;
   - entrada larga, pegado y caracteres españoles;
   - confirmación escrita con `approve` y `reject`;
   - `--plain` y salida redirigida sin ANSI.
4. Mantener el punto 5 desmarcado si falta cualquier prueba automática o manual.
   Marcarlo únicamente cuando el registro de verificación esté completo.

No se crea un ADR nuevo: la elección de renderer no cambia y el ADR 0035 continúa
vigente.

## Verificación por incremento

Durante cada incremento:

```powershell
dotnet format LocalAssistant.sln --no-restore --verify-no-changes
dotnet build LocalAssistant.sln -c Release --no-restore
dotnet test tests/LocalAssistant.Tests/LocalAssistant.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~TerminalClient"
git diff --check
```

Antes de publicar la PR:

```powershell
dotnet format LocalAssistant.sln --no-restore --verify-no-changes
dotnet build LocalAssistant.sln -c Release --no-restore
dotnet test LocalAssistant.sln -c Release --no-restore
git diff --check
```

Después de los tests se comprobará que no quedan procesos `testhost` ni
`LocalAssistant.Api`. Antes de crear la PR se ejecutará la revisión pre-landing exigida
por `AGENTS.md`, incluyendo concurrencia, secretos, límites, documentación y ausencia
de cambios de contrato.

## No objetivos

- Cambiar endpoints, DTO HTTP, scopes, autenticación, autorización u OpenAPI.
- Modificar `TerminalClientApplication` salvo los tests necesarios para demostrar su
  cierre durante pairing.
- Añadir comandos, historial de comandos, selección con ratón o atajos sensibles.
- Introducir Terminal.Gui, Spectre.Console u otra dependencia TUI.
- Elevar el target framework o publicar una API de presentación.
- Añadir audio, TTS, animaciones o activar `PlayingVoice`.
