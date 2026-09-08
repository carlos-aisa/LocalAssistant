# Plan de implementación del incremento 4: presupuesto acotado del transcript

## Objetivo

Implementar el diseño aprobado en
`docs/specs/2026-09-08-terminal-tui-transcript-budget-design.md`: mover toda la
retención del transcript a `Add`, mantener los presupuestos de caracteres y de líneas
de referencia de forma incremental, reemplazar `CreateLines` por una consulta pura
`CreateView` que solo materializa la ventana visible, y hacer que `CreateFrame` adopte
el `ClampedScrollOffset` devuelto. Sin cambios en HTTP, OpenAPI, autenticación,
autorización, persistencia ni en la interpretación de teclas y el layout de los
incrementos anteriores.

## Clasificación y límites

- **Presentación de consola:** `TerminalClientTuiTranscript` y el uso que hace de él
  `TerminalClientTuiHost.CreateFrame`.
- **Testing:** pruebas unitarias deterministas del transcript y de la adopción del
  offset en el host; sin esperas temporales como sincronización, sin benchmarks.
- **Documentación:** este plan, el spec del incremento y el estado del plan general.
- **API y seguridad:** sin cambios.

No se introduce una dependencia, un proyecto ni una API pública. No se toca
`TerminalClientApplication` ni `Program`.

## Estado de partida

`TerminalClientTuiTranscript` (en `main`) tiene:

- `MaximumCharacters = 65_536`, `MaximumWrappedLines = 2_000`, marcador de truncación;
- `Add` acota por caracteres (`KeepTailWithinCharacterBudget`) y expulsa por caracteres
  (`TrimCharacters`);
- `CreateLines(width)` que **muta**: llama a `TrimWrappedLines(width)`, que expulsa
  entradas de forma permanente según el ancho actual y reescribe la entrada única;
- `CountWrappedLines(width)` recorre todas las entradas en cada render;
- `AddWrappedLines` envuelve todas las entradas y devuelve la lista completa.

`CreateFrame` llama a `_transcript.CreateLines(width)`, calcula `maxOffset`, ajusta
`_scrollOffset` y hace `Skip/Take`. `ProcessKey` acota `_scrollOffset` a
`[0, MaximumWrappedLines]` en `Scroll`.

## Paso 1: modelo incremental de entradas y presupuestos

**Archivos:**

- `src/LocalAssistant.TerminalClient/TerminalClientTuiTranscript.cs`
- nuevo `tests/LocalAssistant.Tests/TerminalClient/TerminalClientTuiTranscriptTests.cs`

### Cambios

1. Renombrar `MaximumWrappedLines` → `MaximumReferenceLines` (2.000). Añadir
   `internal const int ReferenceWidth = 40`,
   `TruncationPrefixCharacters = TruncationMarker.Length + 1` (39) y
   `TruncationPrefixReferenceLines = 1`. Conservar `MaximumCharacters = 65_536`.
2. Sustituir `LinkedList<string>` por `LinkedList<Entry>`, donde `Entry` guarda:
   - `string Content` (ya normalizado por quien llama);
   - `int CharacterCount` (= `Content.Length`);
   - `int ReferenceLineCount`;
   - `int[] SegmentStarts` — índices de inicio de cada segmento lógico, para saltar
     segmentos en O(1) durante el recorrido inverso.
3. `Entry` se construye con un único recorrido de `Content` (`ReadOnlySpan<char>` +
   `IndexOf('\n')`), **sin `Split`**, que a la vez registra `SegmentStarts` y acumula
   `ReferenceLineCount` como `Σ Math.Max(1, ceil(longitud del segmento /
   ReferenceWidth))`. Un `Content` que termina en `\n` tiene un segmento final vacío
   que cuenta 1. `"a\n\nb"` → `1 + 1 + 1 = 3`.
4. El transcript mantiene `_characterCount` y `_referenceLineCount` como totales;
   `Add` los incrementa con la entrada nueva y los decrementa por cada entrada
   expulsada restando `entry.CharacterCount` / `entry.ReferenceLineCount` guardados —
   **una expulsión nunca recorre el contenido de la entrada retirada**.
5. `Add`:
   - entrada vacía → return;
   - si `entry.Length > MaximumCharacters`, primer recorte por caracteres
     (`KeepTailWithinBudgets`, paso 2), tomando el sufijo por `Length` sin construir
     segmentos todavía;
   - construir `Entry` (paso 3) sobre ese contenido; si
     `ReferenceLineCount > MaximumReferenceLines`, segundo recorte por líneas y
     reconstruir `Entry` sobre el sufijo;
   - si hubo cualquier recorte, `Content` definitivo = `TruncationMarker + "\n" +
     sufijo` (marcador antepuesto **una vez**), y se reconstruye `Entry` sobre él para
     tener el coste real;
   - `AddLast`, sumar totales;
   - `while ((_characterCount > MaximumCharacters || _referenceLineCount >
     MaximumReferenceLines) && _entries.Count > 1)` → `RemoveFirst`, restar totales.
6. Eliminar `TrimCharacters`, `TrimWrappedLines`, `CountWrappedLines`, `AddWrappedLines`,
   `KeepTailWithinCharacterBudget` y `CreateLines`.

### Pruebas (transcript)

| ID | Escenario | Resultado |
|---|---|---|
| `TR-01` | `"a\n\nb"` | coste 3 líneas de referencia |
| `TR-02` | `"a\n"` y `"a\nb\n"` | 2 y 3 líneas; segmento final vacío cuenta 1 |
| `TR-03` | segmento de 100 caracteres, `ReferenceWidth` 40 | 3 líneas de referencia |
| `TR-04` | total de caracteres exactamente en `MaximumCharacters` | no se expulsa nada |
| `TR-05` | total de caracteres excede en 1 | se expulsa la entrada más antigua |
| `TR-06` | total de líneas de referencia exacto y excedido en 1 | igual que `TR-04/05` |
| `TR-07` | muchas entradas por encima de ambos límites | se expulsan primero las más antiguas, en orden |

## Paso 2: acotado de una entrada individual sobredimensionada

**Archivos:** los mismos.

### Cambios

El acotado tiene **dos pasos separados** para no construir metadatos sobre una entrada
sin límite:

1. **Recorte por caracteres** (solo si `content.Length > MaximumCharacters`): quedarse
   con el sufijo de `MaximumCharacters - TruncationPrefixCharacters` (65.536 − 39)
   caracteres usando únicamente `Length`/slice; no se calculan segmentos. Acota el
   trabajo siguiente a ≤ ~65.536 caracteres aunque la respuesta traiga millones de `\n`.
2. **Construir `Entry`** sobre ese contenido (recorrido único, sin `Split`).
3. **Recorte por líneas** (solo si `ReferenceLineCount > MaximumReferenceLines`):
   `KeepTail` recorre `SegmentStarts` desde el final acumulando coste y devuelve el
   sufijo más largo (a partir de un límite de segmento) que cumple a la vez
   `MaximumReferenceLines - TruncationPrefixReferenceLines` líneas y
   `MaximumCharacters - TruncationPrefixCharacters` caracteres.
4. Si actuó **cualquiera** de los dos recortes, `Content` definitivo =
   `TruncationMarker + "\n" + sufijo` — el marcador se antepone **una sola vez**. Se
   reconstruye `Entry` sobre el resultado final para obtener el coste real (recorrido,
   no suma aproximada). El sufijo puede quedar vacío; con los presupuestos actuales
   siempre entra contenido.
5. `SegmentStarts` de la entrada refleja siempre el `Content` final.
6. El marcador ocupa su propia línea de referencia (`TruncationPrefixReferenceLines`) y
   sus 39 caracteres; ambos totales lo reflejan.

### Pruebas (transcript)

| ID | Escenario | Resultado |
|---|---|---|
| `TR-08` | una entrada con `MaximumCharacters + N` caracteres y una cola reconocible | conserva la cola, antepone el marcador, `CharacterCount == MaximumCharacters` como máximo (verifica reserva 39) |
| `TR-09` | una entrada con más de `MaximumReferenceLines` saltos de línea | conserva la cola, antepone el marcador, cumple `MaximumReferenceLines`; no se elimina (sigue habiendo una entrada) |
| `TR-10` | entrada de ~70.000 `\n` (casi solo saltos) | dispara ambos recortes; el marcador aparece una sola vez; cumple ambos máximos |
| `TR-11` | entrada que excede caracteres y, tras el primer recorte, todavía excede líneas | marcador no duplicado; coste real del resultado cumple ambos máximos |
| `TR-12` | tras acotar, `Add` de una entrada nueva pequeña | la nueva no reintroduce el contenido truncado; la antigua puede expulsarse por presupuesto |
| `TR-13` | entrada acotada: `SegmentStarts` corresponde al `Content` final | `CreateView` sobre esa entrada devuelve los cortes correctos |

## Paso 3: `CreateView` como consulta pura y acotada

**Archivos:** los mismos.

### Cambios

1. Nuevo tipo `readonly record struct TranscriptView(IReadOnlyList<string> Lines, int ClampedScrollOffset)`.
2. Contador de diagnóstico `internal int LastViewVisitedSegmentCount { get; private set; }`
   (solo `internal`, para las pruebas; no entra en la superficie pública ni afecta al
   resultado).
3. `TranscriptView CreateView(int width, int viewportHeight, int scrollOffset)`:
   - `ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width)`;
   - `ArgumentOutOfRangeException.ThrowIfNegative(viewportHeight)`;
   - `ArgumentOutOfRangeException.ThrowIfNegative(scrollOffset)`;
   - `viewportHeight == 0` → `new([], 0)`;
   - `needed = viewportHeight + (long)scrollOffset`, acotado a
     `viewportHeight + MaximumCharacters + MaximumReferenceLines` y devuelto a `int`
     sin desbordar;
   - recorrer entradas de la última a la primera; dentro de cada entrada, segmentos de
     `SegmentStarts` del último al primero; para un segmento de longitud `L` a `width`,
     emitir los trozos en orden inverso: `[k·width, L)` con `k = (L-1)/width`, luego
     `k-1`, …, `0`; segmento vacío → una línea vacía; incrementar
     `LastViewVisitedSegmentCount` por cada segmento visitado;
   - acumular en `recentToOld`; en cuanto `recentToOld.Count == needed`, cortar todo el
     recorrido (`break` explícito en ambos bucles);
   - `available = recentToOld.Count`;
   - `clamped = Math.Min(scrollOffset, Math.Max(0, available - viewportHeight))`;
   - ventana = `recentToOld[clamped .. clamped + viewportHeight]` recortada a
     `recentToOld.Count`, **invertida** a orden cronológico;
   - `return new(window, clamped)`.
4. `CreateView` no toca `_entries`, `_characterCount` ni `_referenceLineCount`.

### Pruebas (transcript)

| ID | Escenario | Resultado |
|---|---|---|
| `TR-14` | 100 caracteres a `width` 40, viewport grande | líneas `40 / 40 / 20` en orden cronológico |
| `TR-15` | `scrollOffset` 0 | muestra las líneas más recientes; `ClampedScrollOffset == 0` |
| `TR-16` | `scrollOffset` intermedio válido | ventana desplazada; `ClampedScrollOffset` sin cambios |
| `TR-17` | `scrollOffset` por encima del máximo | `ClampedScrollOffset == max(0, total - viewportHeight)` |
| `TR-18` | `scrollOffset` cercano a `int.MaxValue` | no desborda; `ClampedScrollOffset` válido |
| `TR-19` | `viewportHeight == 0` | `Lines` vacío, `ClampedScrollOffset == 0` |
| `TR-20` | `Lines.Count` siempre `≤ viewportHeight` | en todos los casos anteriores |
| `TR-21` | `CreateView` repetido con los mismos argumentos | resultado idéntico; `_characterCount` y `_referenceLineCount` sin cambios |
| `TR-22` | `CreateView(40, h, 0)`, luego `CreateView(12, h, 0)`, luego `CreateView(40, h, 0)` | el tercer resultado coincide con el primero; ninguna entrada fue expulsada |
| `TR-23` | tras muchos `Add`, consultar a ancho estrecho y volver al ancho ancho | contenido almacenado intacto (retención solo en `Add`) |
| `TR-24` | `width <= 0`, `viewportHeight < 0`, `scrollOffset < 0` | `ArgumentOutOfRangeException` |
| `TR-25` | transcript al límite de caracteres, `CreateView(40, 8, 0)` | devuelve exactamente 8 líneas; `_characterCount` intacto |
| `TR-26` | parada temprana: transcript al límite, `CreateView(40, 8, 0)` | `LastViewVisitedSegmentCount ≤ 8 + segmentos de la última entrada visitada`, muy por debajo del total de segmentos |

## Paso 4: el host adopta `CreateView`

**Archivos:**

- `src/LocalAssistant.TerminalClient/TerminalClientTui.cs`
- `tests/LocalAssistant.Tests/TerminalClient/TerminalClientTuiTests.cs`

### Cambios

1. `CreateFrame`:
   ```csharp
   var transcriptHeight = Math.Max(0, height - priorityLines.Count);
   var view = _transcript.CreateView(width, transcriptHeight, _scrollOffset);
   _scrollOffset = view.ClampedScrollOffset;
   return view.Lines.Concat(priorityLines).Take(height).ToList();
   ```
2. En `ProcessKey`, el clamp de `Scroll` mantiene el límite superior en
   `TerminalClientTuiTranscript.MaximumReferenceLines` (renombrado); `CreateView`
   vuelve a acotar al valor útil real en el siguiente render.
3. Sustituir cualquier referencia a `MaximumWrappedLines` por `MaximumReferenceLines`
   (host y pruebas).
4. No cambiar `CreateCompactFrame`, `FitLine`, `FitInputLine`, `DetectResize`,
   `Render` ni la interpretación de teclas.

### Pruebas (host)

| ID | Escenario | Resultado |
|---|---|---|
| `HV-01` | `PageUp` repetido más allá del contenido y luego una sola `PageDown` | el host adoptó el `ClampedScrollOffset`, así que una `PageDown` vuelve al final (`item-11` visible, `item-00` no) |
| `HV-02` | frame de `40×8` con transcript largo | `frame.Count == 8`; las dos filas prioritarias (`State:` / `You:`) quedan abajo y las seis de arriba son la ventana de transcript |

La retención al variar el ancho consultado (regresión del bug destructivo de
`TrimWrappedLines`) se cubre en el nivel de transcript (`TR-27`), que es determinista y
directa.

## Paso 5: reescribir las pruebas existentes del transcript

**Archivos:**

- `tests/LocalAssistant.Tests/TerminalClient/TerminalClientTuiTests.cs`
- nuevo `tests/LocalAssistant.Tests/TerminalClient/TerminalClientTuiTranscriptTests.cs`

1. Los dos casos de transcript que vivían en `TerminalClientTuiTests`
   (`TranscriptKeepsTheRecentTailWithinItsCharacterAndLineBudgets` y
   `OversizedSingleTranscriptEntryKeepsItsTailAndTruncationMarker`) se sustituyen por
   la batería `TR-01`..`TR-27` en el archivo nuevo, usando `CreateView` y los totales
   incrementales (`CharacterCount`, `ReferenceLineCount`) en lugar de `CreateLines` y
   `lines.Count`.
2. `TranscriptIsClippedAndScrollKeysOnlyChangeTheViewport` sigue válida sin cambios (no
   usaba la firma antigua del transcript).
3. Ninguna prueba nueva usa `Console` real, red, reloj real ni `Task.Delay` como
   sincronización.

## Paso 6: integración y documentación

**Archivos:**

- `docs/plans/2026-09-06-terminal-tui-gap-corrections-implementation-plan.md`
- `docs/evaluations/2026-09-05-terminal-tui-evaluation.md` (registro de la verificación
  manual del incremento 4)

### Trabajo

1. Ejecutar la suite completa; confirmar que host, programa y transcript coinciden.
2. Revisar el diff contra los no objetivos; eliminar refactors no requeridos.
3. Confirmar que no se añadieron logs con contenido del transcript.
4. En el plan general, marcar el incremento 4 como finalizado en `## Estado` y dejar
   el 5 y el punto 5 de `ROADMAP.md` como pendientes.
5. Registrar la verificación manual (abajo) en el documento de evaluación.

## Comandos de verificación

Después de cada paso:

```powershell
dotnet format LocalAssistant.sln --no-restore --verify-no-changes
dotnet build LocalAssistant.sln -c Release --no-restore
dotnet test tests/LocalAssistant.Tests/LocalAssistant.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~TerminalClientTuiTests|FullyQualifiedName~TerminalClientProgramTests"
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

## Verificación manual (Windows Terminal + PowerShell)

1. Sesión larga: mantener una conversación con respuestas largas del proveedor falso
   hasta superar visualmente varias pantallas. Comprobar que el scroll con
   PageUp/PageDown llega hasta el marcador de truncación y no más allá.
2. Redimensionar la ventana a un ancho pequeño y volver a ensancharla varias veces
   durante la conversación. Comprobar que ninguna línea antigua desaparece de forma
   permanente.
3. Provocar una respuesta muy larga en un solo turno (petición que fuerce una
   respuesta extensa). Comprobar que aparece el marcador
   `[Earlier transcript content truncated]` al principio de esa entrada y que su final
   permanece legible.
4. Registrar los tres pasos en `docs/evaluations/2026-09-05-terminal-tui-evaluation.md`.

## Matriz mínima para cerrar el incremento

| Grupo | Casos | Bloqueante |
|---|---:|---:|
| Presupuestos incrementales | `TR-01` a `TR-07` | Sí |
| Acotado de entrada individual | `TR-08` a `TR-13` | Sí |
| `CreateView` puro y acotado | `TR-14` a `TR-26` | Sí |
| Retención independiente del ancho | `TR-27` | Sí |
| Integración en el host | `HV-01`, `HV-02` | Sí |
| Regresión del host TUI | suite completa de `TerminalClientTuiTests` | Sí |
| Regresión del programa | suite completa de `TerminalClientProgramTests` | Sí |
| Verificación manual | pasos 1–3 registrados | Sí |

## Criterios de cierre

El incremento 4 estará cerrado únicamente cuando:

- la retención dependa exclusivamente de `Add`; ni `CreateView` ni el resize ni el
  render expulsan o modifican entradas;
- `Add` mantenga `_characterCount` y `_referenceLineCount` de forma incremental, sin
  recorrer todas las entradas salvo las que expulsa;
- una entrada que exceda cualquiera de los dos presupuestos conserve su final, anteponga
  el marcador y cumpla ambos máximos, sin quedar vacía;
- el marcador consuma ambos presupuestos (39 caracteres, 1 línea), no se duplique con
  los dos recortes, y los segmentos vacíos cuenten como una línea;
- el acotado no construya metadatos sobre contenido sin límite (recorte por caracteres
  antes de calcular segmentos);
- `CreateView` sea puro, devuelva a lo sumo `viewportHeight` líneas en orden
  cronológico y un `ClampedScrollOffset` válido, valide sus argumentos y no desborde
  con `scrollOffset` grande;
- la parada temprana esté respaldada por revisión estructural y por el contador
  interno `LastViewVisitedSegmentCount` (`TR-26`), sin benchmarks temporales;
- `CreateFrame` use `CreateView` y adopte su `ClampedScrollOffset`;
- reducir y ampliar la terminal no destruya contenido (`HV-02`);
- `TR-01`..`TR-27` y `HV-01`..`HV-02` pasen como pruebas deterministas, sin esperas
  temporales ni benchmarks;
- `dotnet format`, `dotnet build -c Release`, la suite completa y `git diff --check`
  pasen;
- no queden procesos `testhost` ni `LocalAssistant.Api` residuales;
- la verificación manual esté registrada;
- el plan general marque el incremento 4 como finalizado y el 5 y el punto 5 de
  `ROADMAP.md` como pendientes.

## No objetivos

- Historial persistido, contenido HTTP, OpenAPI, autenticación, autorización o
  persistencia.
- Cambiar `TerminalClientApplication` o `Program`.
- Reintroducir un cambio de renderer, comandos, ratón o dependencias visuales.
- Medición de ancho visual en celdas del terminal (limitación documentada en el
  incremento 3).
- Benchmarks temporales como prueba.
- Implementar el incremento 5 o reabrir los incrementos 1–3.
- El hallazgo abierto EOF/`Ctrl+C` en confirmación (su propio commit).
- Hacer commit, push o crear PR sin solicitud expresa del usuario.
