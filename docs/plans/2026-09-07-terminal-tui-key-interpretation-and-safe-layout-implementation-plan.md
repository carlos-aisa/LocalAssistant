# Plan de implementación del incremento 3: interpretación de teclas y layout seguro

> **Estado: finalizado (2026-09-07).** `TerminalKeyInterpreter` puro con `inputActive`,
> host como ejecutor de intenciones con CTS enlazado y buffer ligado a la solicitud,
> contrato de `SystemTerminalDriver.TryReadInput` probado y layout con prioridad de
> retención por umbrales. `dotnet format`, `dotnet build -c Release` y la suite completa
> (525/525) pasan. Incrementos 4 y 5 y el punto 5 de `ROADMAP.md` siguen pendientes.

## Objetivo

Implementar el diseño aprobado en
`docs/specs/2026-09-07-terminal-tui-key-interpretation-and-safe-layout-design.md`:
extraer la interpretación de teclas a `TerminalKeyInterpreter`, dejar
`TerminalClientTuiHost.ProcessKey` como ejecutor de intenciones, fijar el contrato de
`SystemTerminalDriver.TryReadInput` y cerrar los huecos de prueba del layout, sin
cambiar el comportamiento observable salvo las correcciones menores del diseño.

## Clasificación y límites

- **Presentación de consola:** intérprete de teclas, host de la TUI y frontera de
  consola del driver.
- **Testing:** pruebas unitarias deterministas del intérprete, contrato del driver y
  layout del host, sin esperas temporales como sincronización.
- **Documentación:** este plan, el spec del incremento y el estado del plan general.
- **API y seguridad:** sin cambios en HTTP, OpenAPI, autenticación, autorización,
  credenciales ni persistencia.

No se introduce una dependencia de terminal, un framework TUI ni una API pública.

## Estado de partida

El commit de gap-corrections ya dejó en la rama:

- `Ctrl+D`/`Ctrl+Z` con buffer vacío y no vacío tratados en `ProcessKey` /
  `IsEndOfInputKey`, con pruebas de host;
- `TerminalTextSanitizer.NormalizeSingleLine` y su uso en input, confirmación, error y
  estado;
- `CreateFrame` con presupuesto de filas por prioridad y `CreateCompactFrame` para
  `<40×8`;
- `FitInputLine` / `FitTail` para el extremo activo de la entrada;
- `TerminalClientTuiTranscript` con presupuesto acotado.

Queda sin hacer la interpretación pura, el contrato probado de
`SystemTerminalDriver.TryReadInput` y varias pruebas de layout. La implementación
conservará las partes válidas y solo tocará lo necesario.

## Paso 1: intérprete puro de teclas

**Archivos:**

- nuevo `src/LocalAssistant.TerminalClient/TerminalKeyInterpreter.cs`
- nuevo `tests/LocalAssistant.Tests/TerminalClient/TerminalKeyInterpreterTests.cs`

### Cambios

1. Definir `TerminalKeyAction` (`Insert`, `DeletePrevious`, `Scroll`, `Submit`,
   `SubmitEmpty`, `CancelApplication`, `CloseChannel`, `Ignore`) y `TerminalKeyIntent`
   como `readonly record struct` con `Action`, `Character` y `ScrollDelta`, con
   factorías internas por acción.
2. Implementar `TerminalKeyInterpreter.Interpret(ConsoleKeyInfo key, bool inputActive,
   bool bufferHasText)` con las reglas y el orden del diseño: `Ctrl+C` incondicional →
   `CancelApplication`; `Insert`/`DeletePrevious`/`Submit`/`SubmitEmpty` solo con
   `inputActive`, si no `Ignore`; `CancelApplication`/`CloseChannel`/`Scroll` no
   dependen de `inputActive`; solo `PageUp`/`PageDown` desplazan; `UpArrow`/`DownArrow`
   → `Ignore`.
3. El método no recibe ni conserva referencias a consola, host o buffer; sus únicas
   entradas de estado son `inputActive` y `bufferHasText`.
4. No registrar nada ni exponer el tipo fuera del ensamblado.

### Pruebas obligatorias del intérprete

| ID | Escenario | Resultado |
|---|---|---|
| `KEY-01` | carácter imprimible, con solicitud activa | `Insert` con ese carácter |
| `KEY-02` | carácter español (`ñ`, `á`, `¿`), con solicitud activa | `Insert` con ese carácter |
| `KEY-03` | `Backspace` con solicitud activa y texto | `DeletePrevious` |
| `KEY-04` | `Backspace` con solicitud activa sin texto | `Ignore` |
| `KEY-05` | carácter, `Backspace`, `Enter`, `Escape` **sin** solicitud activa | `Ignore` |
| `KEY-06` | `PageUp` / `PageDown`, con y sin solicitud activa | `Scroll(+5)` / `Scroll(-5)` |
| `KEY-07` | `UpArrow` / `DownArrow` | `Ignore` (reservadas) |
| `KEY-08` | `Enter` con solicitud activa | `Submit` |
| `KEY-09` | `Escape` con solicitud activa | `SubmitEmpty`, nunca `CloseChannel` |
| `KEY-10` | `Ctrl+D` / `Ctrl+Z` sin texto | `CloseChannel` |
| `KEY-11` | `Ctrl+D` / `Ctrl+Z` con texto | `Ignore` |
| `KEY-12` | `Ctrl+C` con y sin texto, con y sin solicitud activa | `CancelApplication` |
| `KEY-13` | `Tab`, `F1`, `Ctrl+A` y demás no asignadas | `Ignore` |
| `KEY-14` | matriz de todas las teclas anteriores | solo `Enter` con solicitud activa produce `Submit` |

## Paso 2: host como ejecutor de intenciones

**Archivos:**

- `src/LocalAssistant.TerminalClient/TerminalClientTui.cs`
- `tests/LocalAssistant.Tests/TerminalClient/TerminalClientTuiTests.cs`

### Cambios

1. El host ejecuta `operation` con
   `CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)` y pasa el token
   enlazado; el registro sobre el token externo sigue cerrando el canal.
2. `ProcessKey` obtiene la solicitud activa (`_console.TryGetInputRequest`), religa el
   buffer a esa instancia si cambió (limpiándolo) y llama a
   `TerminalKeyInterpreter.Interpret(key, solicitud is not null, _input.Length > 0)`;
   despacha con un `switch` sobre `intent.Action`, sin condicionales de teclado.
3. `CancelApplication` cancela el `CancellationTokenSource` enlazado y cierra el canal;
   `CloseChannel` solo cierra el canal y limpia el buffer.
4. `Scroll` aplica `_scrollOffset = Math.Max(0, _scrollOffset + intent.ScrollDelta)`.
5. `Submit`/`SubmitEmpty` limpian el buffer y sueltan la ligadura antes de
   `CompleteInput`. `DeletePrevious` hace `_input.Length--` directamente.
6. Eliminar `IsEndOfInputKey`. El evento `IsEndOfInput` del driver se sigue tratando
   en `ProcessAvailableInput` como `CloseChannel` antes de interpretar teclas.
7. Añadir en `Render` un clamp de `_scrollOffset` al máximo útil según el número de
   líneas del transcript y la altura del viewport.
8. Reordenar `CreateCompactFrame` y el recorte de reserva de `CreateFrame` a la
   prioridad de retención del diseño: confirmación, input, aviso de ampliar, error,
   estado, transcript. No cambiar la mecánica de `FitLine`/`FitTail` ni el transcript.

### Pruebas del host

En `TerminalClientTuiTests`:

- conservar una prueba de integración de cierre por `Ctrl+D` con buffer vacío y la de
  `Escape` que permite el siguiente prompt; el resto de comprobación de teclas vive en
  `KEY-*`;
- entrada anticipada: escribir `approve` sin solicitud activa, publicar después una
  confirmación y verificar input vacío y que `Enter` entrega cadena vacía;
- `Ctrl+C` durante una operación en curso por las dos rutas
  (`Console.CancelKeyPress` externo y tecla del driver falso): el token de la
  aplicación se cancela y el canal se cierra en ambas;
- `Ctrl+D` con buffer vacío durante una operación en curso: cierra el canal, el token
  no se cancela;
- `Backspace` reduce el buffer y el frame lo refleja;
- `PageUp` seguido de cancelación: el worker devuelve el código de cancelación (el
  prompt no se completó) y el viewport cambió;
- `_scrollOffset` acotado: probado tras `PageUp` repetido, tras reducir el transcript y
  tras un resize.

## Paso 3: contrato de lectura del adaptador `SystemTerminalDriver`

**Archivos:**

- `src/LocalAssistant.TerminalClient/TerminalDriver.cs` (solo si el doble de
  `ISystemTerminal` necesita ampliar la superficie de prueba; sin cambio de
  comportamiento)
- `tests/LocalAssistant.Tests/TerminalClient/TerminalDriverTests.cs`

### Cambios

1. Ampliar el doble de `ISystemTerminal` de las pruebas para configurar la tecla
   devuelta por `ReadKey`, el valor de `KeyAvailable`, las excepciones de ambos y
   registrar el argumento `intercept`.
2. No modificar `SystemTerminalDriver.TryReadInput`; el contrato ya es correcto.
3. Las pruebas verifican el adaptador sobre `ISystemTerminal`, no la consola real de
   Windows; la traducción real de teclas queda para la comprobación manual del
   incremento 5.

### Pruebas obligatorias del adaptador

| ID | Escenario | Resultado |
|---|---|---|
| `DRV-13` | tecla disponible | `TerminalInputEvent(key, IsEndOfInput=false)`; `intercept` fue `true` |
| `DRV-14` | `ReadKey` devuelve `Ctrl+C`, `Ctrl+Z` y `Ctrl+D` | se entregan como tecla, no como EOF |
| `DRV-15` | sin tecla disponible | `null` |
| `DRV-16` | `KeyAvailable` lanza `IOException` | `TerminalInputEvent.EndOfInput` |
| `DRV-17` | `ReadKey` lanza `InvalidOperationException` | `TerminalInputEvent.EndOfInput` |

## Paso 4: pruebas de layout seguro

**Archivos:**

- `tests/LocalAssistant.Tests/TerminalClient/TerminalClientTuiTests.cs`

### Pruebas obligatorias del layout

| ID | Escenario | Resultado |
|---|---|---|
| `LAY-01` | `20×6` con confirmación y error | frame conserva confirmación e input y muestra la indicación de ampliar; toda línea `≤ 20` |
| `LAY-02` | `10×2` y `1×1` con confirmación y error | el render no lanza, ninguna línea supera el ancho, ningún `\n` ni `ESC` |
| `LAY-03` | entrada normal más larga que el ancho | frame muestra `…` y los últimos caracteres; la posición de edición (final) es visible |
| `LAY-04` | entrada secreta más larga que el ancho | frame muestra solo asteriscos del extremo; ni el valor ni `…` sobre texto claro |
| `LAY-05` | redimensionado por debajo y por encima del mínimo mientras el worker espera un secreto | ningún frame contiene el secreto; confirmación e input se conservan |
| `LAY-06` | nombres de herramienta y errores con `\n`, `ESC`, ANSI, OSC y C0/C1, sobre un rango de anchos | toda línea de todo frame mide `≤ ancho` y no contiene `\n` ni `ESC` |
| `LAY-07` | `40×8` con input, confirmación, error incierto y estado | los cuatro visibles simultáneamente (regresión de `MinimumViewportKeepsInputConfirmationErrorAndStateVisible`) |

## Paso 5: integración y documentación

**Archivos:**

- `docs/plans/2026-09-06-terminal-tui-gap-corrections-implementation-plan.md`
- `docs/evaluations/2026-09-05-terminal-tui-evaluation.md`, solo si el comportamiento
  final difiere de lo registrado

### Trabajo

1. Ejecutar juntas las suites de intérprete, driver, host y programa para detectar
   incompatibilidades entre dobles.
2. Revisar que ninguna prueba nueva use `Console` real, red, reloj real o esperas
   temporales como sincronización.
3. Confirmar que no se han añadido logs con teclas, entrada o secretos.
4. Revisar el diff contra los no objetivos y eliminar cualquier refactor no requerido.
5. En el plan general, marcar el incremento 3 como finalizado en la sección `Estado` y
   dejar 4 y 5 pendientes. `ROADMAP.md` punto 5 sigue sin marcar.

## Matriz mínima para cerrar el incremento

| Grupo | Casos | Bloqueante |
|---|---:|---:|
| Intérprete puro | `KEY-01` a `KEY-14` | Sí |
| Contrato del adaptador del driver | `DRV-13` a `DRV-17` | Sí |
| Layout seguro | `LAY-01` a `LAY-07` | Sí |
| Regresión del host TUI | suite completa de `TerminalClientTuiTests` | Sí |
| Regresión del programa | suite completa de `TerminalClientProgramTests` | Sí |

## Comandos de verificación

Después de cada paso:

```powershell
dotnet format LocalAssistant.sln --no-restore --verify-no-changes
dotnet build LocalAssistant.sln -c Release --no-restore
dotnet test tests/LocalAssistant.Tests/LocalAssistant.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~TerminalKeyInterpreterTests|FullyQualifiedName~TerminalDriverTests|FullyQualifiedName~TerminalClientTuiTests|FullyQualifiedName~TerminalClientProgramTests"
git diff --check
```

Antes de declarar cerrado el incremento:

```powershell
dotnet format LocalAssistant.sln --no-restore --verify-no-changes
dotnet build LocalAssistant.sln -c Release --no-restore
dotnet test LocalAssistant.sln -c Release --no-restore
git diff --check
Get-Process testhost, LocalAssistant.Api -ErrorAction SilentlyContinue
```

La ausencia de salida del último comando es el resultado esperado.

## Criterios de cierre

El incremento 3 estará cerrado únicamente cuando:

- `KEY-01` a `KEY-14`, `DRV-13` a `DRV-17` y `LAY-01` a `LAY-07` estén cubiertos por
  pruebas deterministas y pasen;
- `TerminalKeyInterpreter` contenga toda la lógica de decisión de teclado, reciba
  `inputActive` y sea puro;
- `ProcessKey` sea un despacho sobre la intención sin condicionales de teclado y
  religue el buffer a la solicitud activa;
- sin solicitud activa ninguna tecla mute ni entregue el buffer (prueba de entrada
  anticipada);
- `Ctrl+C` cancele el token de la aplicación por sus dos rutas durante una operación
  en curso; `Ctrl+D`/`Ctrl+Z`/EOF cierren el canal sin cancelar el token;
- el contrato de `TryReadInput` esté probado para tecla, ausencia, `Ctrl+C`, `Ctrl+D`,
  `Ctrl+Z` y excepciones esperadas;
- la prioridad de retención del layout esté implementada y probada por umbrales
  (`≥ 40×8`, `20×6 ≤ tamaño < 40×8`, `< 20×6`);
- ninguna prueba de teclado dependa de esperas temporales como sincronización;
- formato, build Release, suite completa y `git diff --check` pasen;
- no queden procesos residuales;
- el plan general refleje el incremento 3 como finalizado y 4, 5 y `ROADMAP.md`
  punto 5 como pendientes.

## No objetivos

- Edición en el interior de la línea, cursor movible, selección o historial.
- Reintroducir `UpArrow`/`DownArrow` como scroll o navegación.
- Medición de ancho visual en celdas del terminal.
- Traducción real de `Ctrl+C`/`Ctrl+D`/`Ctrl+Z` en la consola de Windows (incremento 5).
- Implementar los incrementos 4 o 5, ni reabrir el 1 o el 2.
- Cambiar `TerminalClientApplication`, contratos HTTP, OpenAPI, autenticación o
  persistencia.
- Añadir dependencias, proyectos, telemetría o una API pública.
- Hacer commit, push o crear PR sin solicitud expresa del usuario.
