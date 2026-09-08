# Diseño del incremento 4: presupuesto acotado del transcript

## Contexto

El commit `Implement terminal TUI gap corrections and enhancements` introdujo
`TerminalClientTuiTranscript` con un presupuesto de caracteres (65.536) y otro de
líneas envueltas (2.000) que expulsa entradas antiguas. El incremento 3 añadió un
clamp de `_scrollOffset` en el bucle del host.

Este documento cierra lo que falta del incremento 4 del plan
`docs/plans/2026-09-06-terminal-tui-gap-corrections-implementation-plan.md`. No modifica
el historial persistido, el contenido enviado o recibido por HTTP, ni la interpretación
de teclas, el layout o la comprobación de compatibilidad de incrementos anteriores.

## Problemas confirmados

### El render reconstruye todo el transcript

`CreateFrame` llama a `_transcript.CreateLines(width)`, que envuelve todas las entradas
conservadas (hasta 2.000 líneas / 65.536 caracteres) y devuelve una lista completa, de
la que `Skip(first).Take(transcriptHeight)` toma una ventana pequeña. El bucle solo
renderiza cuando `_dirty` está activo, pero eso ocurre en cada pulsación, scroll o
cambio de estado, así que la reconstrucción completa es real y evitable. El diseño de
gap-corrections pide que *"el renderer calcule únicamente las líneas necesarias para el
viewport y el offset de scroll"*.

### La expulsión por líneas es destructiva y depende del ancho

`TrimWrappedLines(width)` se ejecuta dentro de `CreateLines` y **elimina entradas de
forma permanente** cuando el recuento de líneas envueltas al ancho actual supera 2.000.
Al reducir la terminal a un ancho pequeño el recuento se dispara y se pierden entradas
que, al volver a ensanchar, ya no reaparecen. Renderizar o redimensionar nunca debe
destruir contenido del transcript.

### La consulta muta estado

`CreateLines` evicta entradas y reescribe el valor de la entrada única, siendo un
método aparentemente de solo lectura. La retención debe depender exclusivamente de
`Add`.

### El presupuesto se recalcula en cada consulta

`CountWrappedLines` recorre todas las entradas y hace `Split('\n')` por entrada en cada
render. El coste del presupuesto debe mantenerse incrementalmente.

## Decisiones

### Constantes

```text
MaximumCharacters      = 65_536   // caracteres normalizados conservados
MaximumReferenceLines  = 2_000    // líneas de referencia conservadas
ReferenceWidth         = 40       // = TerminalClientTuiHost.MinimumWidth
TruncationMarker       = "[Earlier transcript content truncated]"
```

`MaximumWrappedLines` se renombra a **`MaximumReferenceLines`** porque ya no representa
las líneas renderizadas al ancho actual, sino una medida estable calculada a
`ReferenceWidth`.

### Coste de una entrada

El transcript almacena entradas ya normalizadas por `TerminalTextSanitizer`. El coste
de una entrada es:

- **caracteres:** `Content.Length` (incluye los `\n`).
- **líneas de referencia:** suma, sobre los segmentos lógicos de `Content` (los tramos
  entre `\n`), de `Math.Max(1, ceil(longitud del segmento / ReferenceWidth))`.

Los segmentos vacíos cuentan como una línea. `"a\n\nb"` → 3 líneas de referencia.
`"a\n"` → 2.

`Split('\n')` describe los segmentos, pero la implementación **no** usa `Split`: recorre
la cadena una vez (`ReadOnlySpan<char>` / `IndexOf('\n')`) y guarda los índices de
inicio de segmento sin materializar un array ni strings intermedios.

Cada entrada almacenada conserva su contenido, su recuento de caracteres, su recuento
de líneas de referencia y los índices de inicio de sus segmentos lógicos. El
transcript mantiene los totales `_characterCount` y `_referenceLineCount` y los ajusta
al añadir o expulsar entradas. `Add` cuesta el recorrido de la entrada nueva (ya
acotada, ver abajo) más la resta O(1) por cada entrada realmente expulsada, usando los
recuentos guardados: **una expulsión nunca recalcula el contenido de la entrada que
retira**.

### Reserva del marcador

```text
TruncationMarker.Length                = 38
TruncationPrefixCharacters             = TruncationMarker.Length + 1   // 39: incluye el "\n"
TruncationPrefixReferenceLines         = 1
```

El separador `\n` entre el marcador y el sufijo cuenta dentro de `MaximumCharacters`,
así que la reserva de caracteres es **39, no 38**. Reservar solo 38 dejaría el
resultado un carácter por encima del máximo.

### `Add`

1. Una entrada vacía se ignora.
2. Se calcula el coste en caracteres (`Content.Length`, O(1)). Si supera
   `MaximumCharacters`, **primer recorte por caracteres**: quedarse con el sufijo de
   `MaximumCharacters − TruncationPrefixCharacters` caracteres, sin calcular todavía
   segmentos. Esto acota el trabajo posterior a como mucho ~65.536 caracteres aunque
   llegue una respuesta enorme o con millones de `\n`.
3. Sobre ese contenido (ya ≤ `MaximumCharacters`), recorrer una vez para calcular
   segmentos y líneas de referencia. Si el recuento supera `MaximumReferenceLines`,
   **segundo recorte por líneas**: recorrer los segmentos desde el final acumulando su
   coste y quedarse con el sufijo más largo (a partir de un límite de segmento) cuyas
   líneas de referencia quepan en `MaximumReferenceLines − TruncationPrefixReferenceLines`
   **y** cuyos caracteres quepan en `MaximumCharacters − TruncationPrefixCharacters`.
4. Si hubo cualquiera de los dos recortes, el contenido definitivo es
   `TruncationMarker + "\n" + sufijo` (el marcador se antepone **una sola vez**, aunque
   hayan actuado los dos recortes). Se recalcula el coste real del resultado —
   caracteres y líneas de referencia — recorriéndolo; no se suman costes aproximados. El
   sufijo puede quedar vacío (`marker + "\n"` = 2 segmentos, el segundo vacío = 2 líneas
   de referencia), pero con los presupuestos actuales siempre entra contenido.
5. Los índices de segmento definitivos se construyen **solo sobre el contenido
   finalmente conservado**.
6. Se añade la entrada y se actualizan los totales.
7. Mientras el total de caracteres **o** el total de líneas de referencia supere su
   máximo y haya más de una entrada, se expulsa la más antigua y se restan sus
   recuentos guardados. La última entrada restante siempre cumple ambos máximos porque
   cada entrada se acota al añadirse; el bucle termina con al menos una entrada.

Este es el único punto donde el transcript pierde contenido.

### `CreateView`

```text
TranscriptView CreateView(int width, int viewportHeight, int scrollOffset)
```

`TranscriptView` es un contrato inmutable con:

- `IReadOnlyList<string> Lines` — las líneas a mostrar, en orden **cronológico**
  (antiguo → reciente), con `Lines.Count ≤ viewportHeight`.
- `int ClampedScrollOffset` — el offset realmente aplicado.

Contrato:

- `width ≤ 0`, `viewportHeight < 0` o `scrollOffset < 0` lanzan
  `ArgumentOutOfRangeException`.
- `viewportHeight == 0` devuelve una vista vacía con `ClampedScrollOffset == 0`.
- `scrollOffset == 0` muestra las líneas más recientes.
- Un `scrollOffset` mayor que el máximo disponible se ajusta a ese máximo.
- `CreateView` **no** modifica entradas ni totales; llamarlo repetidamente con los
  mismos argumentos devuelve el mismo resultado.

Algoritmo:

1. `needed = viewportHeight + scrollOffset`, con toda la aritmética en `long` y el
   resultado saturado a `[0, int.MaxValue]` tras acotarlo a
   `viewportHeight + MaximumCharacters + MaximumReferenceLines` — cota superior del
   número de líneas envueltas que el transcript puede producir a cualquier ancho (nunca
   más de un carácter por línea, más una por segmento vacío). Ese techo no recorta
   ninguna ventana alcanzable; la saturación a `int.MaxValue` evita que un
   `viewportHeight` y un `scrollOffset` ambos enormes desborden `int` a un valor
   negativo. El host ya acota `_scrollOffset`, pero `CreateView` no confía en ello.
2. Recorrer las entradas de la más reciente a la más antigua y, dentro de cada entrada,
   sus segmentos de la última a la primera. Para un segmento de longitud `L` a `width`,
   los cortes de envoltura se calculan **siempre desde el inicio** (`0, width, 2·width,
   …`); se emiten en orden inverso: primero `[último_corte·width, L)`, luego el corte
   anterior, etc. Una línea de 100 caracteres a ancho 40 produce, hacia atrás, `20`,
   luego `40`, luego `40` — nunca se parte como `40+40+20` empezando por el final. Un
   segmento vacío emite una línea vacía.
3. Cada línea emitida se acumula en un buffer *reciente → antiguo*. En cuanto el buffer
   alcanza `needed` líneas, se detiene todo el recorrido; los segmentos y entradas más
   antiguos no se materializan. Los índices de segmento almacenados permiten saltar
   segmentos en O(1), de modo que el trabajo es proporcional a
   `viewportHeight + scrollOffset` más los segmentos de la última entrada visitada.
4. `available = buffer.Count`. `effectiveOffset = min(scrollOffset,
   max(0, available − viewportHeight))`. Si el recorrido agotó las entradas,
   `available` es el total real y el clamp es exacto; si se detuvo antes,
   `available == needed` y `effectiveOffset == scrollOffset`.
5. La ventana visible son los índices `[effectiveOffset, effectiveOffset +
   viewportHeight)` del buffer *reciente → antiguo*, recortada a `buffer.Count`,
   **invertida** para devolverla en orden cronológico.

### Complejidad

El trabajo de `CreateView` es proporcional a `viewportHeight + scrollOffset` (más los
segmentos de la última entrada visitada), no al tamaño total del transcript, porque:

- los índices de inicio de segmento se calculan una sola vez en `Add`;
- el recorrido inverso salta segmentos en O(1) y se detiene al completar `needed`
  líneas;
- `_scrollOffset` ya está acotado a `MaximumReferenceLines` por el clamp del
  incremento 3, y `CreateView` vuelve a devolver un `ClampedScrollOffset` acotado.

No se usan benchmarks temporales como prueba. Una prueba de salida ("una vista pequeña
devuelve exactamente la ventana pedida") demuestra el resultado pero no que el recorrido
se detuviera antes de visitar todo el transcript. La parada temprana se verifica de dos
formas complementarias:

- **revisión estructural del algoritmo:** la enumeración inversa de entradas y
  segmentos y el `break` al alcanzar `needed` líneas son explícitos y localizados;
- **contador de diagnóstico interno:** `CreateView` expone, solo `internal` (visible a
  las pruebas por `InternalsVisibleTo`, nunca en la superficie pública), el número de
  segmentos visitados en la última llamada. Una prueba con un transcript al límite y un
  viewport pequeño comprueba que ese número está acotado por
  `viewportHeight + scrollOffset` más los segmentos de la última entrada visitada, no
  por el total de segmentos almacenados.

El contador no influye en el resultado y no se expone fuera del ensamblado.

### `CreateFrame`

```text
transcriptHeight = max(0, height − priorityLines.Count)
view = _transcript.CreateView(width, transcriptHeight, _scrollOffset)
_scrollOffset = view.ClampedScrollOffset
frame = view.Lines.Concat(priorityLines).Take(height)
```

El host adopta el `ClampedScrollOffset` devuelto. Se eliminan `CreateLines`,
`AddWrappedLines`, `TrimWrappedLines` y `CountWrappedLines`.

## Invariantes

- El transcript solo pierde contenido en `Add`; renderizar o redimensionar nunca
  destruye entradas.
- `Add` mantiene los totales incrementalmente; no recorre todas las entradas salvo las
  que expulsa.
- Una entrada que por sí sola excede cualquiera de los dos presupuestos conserva su
  final, antepone el marcador y cumple ambos máximos; nunca queda vacía.
- El marcador se antepone una sola vez aunque actúen los dos recortes (caracteres y
  luego líneas); nunca se duplica.
- El acotado recorta primero por caracteres (solo con `Length`) y solo después calcula
  segmentos; nunca se construyen metadatos sobre una entrada sin límite.
- Los índices de segmento de una entrada reflejan siempre su contenido final (el
  posterior al truncado, si lo hubo).
- El marcador de truncación consume ambos presupuestos (39 caracteres, 1 línea de
  referencia).
- Los segmentos vacíos cuentan como una línea de referencia.
- `CreateView` es una consulta pura: no muta entradas ni totales.
- `CreateView` devuelve como máximo `viewportHeight` líneas, en orden cronológico, y un
  `ClampedScrollOffset` dentro del rango válido.
- Consultar a un ancho, luego a otro más estrecho y de nuevo al primero devuelve el
  mismo contenido almacenado.
- El trabajo de render está acotado por `viewportHeight + scrollOffset`, no por el
  volumen total recibido del servidor.
- `PlayingVoice` sigue sin activarse.

## Estrategia de pruebas

### `TerminalClientTuiTranscript`

- coste de líneas de referencia: `"a\n\nb"` → 3; `"a\n"` → 2; saltos consecutivos y
  salto final;
- envoltura de 100 caracteres a ancho 40 conserva los cortes `40 / 40 / 20`;
- presupuesto de caracteres exacto y excedido por un carácter;
- presupuesto de líneas de referencia exacto y excedido por una línea;
- una entrada con más de 65.536 caracteres conserva el final y el marcador, y cumple
  `MaximumCharacters` exactamente (verifica la reserva de 39, no 38);
- una entrada con más de 2.000 saltos conserva el final y el marcador; no se elimina;
- una entrada formada casi por completo de `\n` (p. ej. 70.000 `\n`): dispara los dos
  recortes, el marcador aparece una sola vez, y el resultado cumple ambos máximos;
- una entrada que necesita primero recorte por caracteres y luego por líneas: el
  marcador no se duplica y el coste real del resultado (recorrido, no aproximado)
  cumple ambos máximos;
- tras un truncado, los índices de segmento de la entrada corresponden a su contenido
  final (comprobado indirectamente: `CreateView` devuelve los cortes correctos sobre
  esa entrada);
- el marcador consume ambos presupuestos (la entrada acotada cumple los dos máximos);
- muchas entradas: se expulsan primero las más antiguas al superar caracteres o líneas;
  cada expulsión resta los recuentos guardados sin volver a recorrer el contenido
  (comprobado por los totales resultantes);
- `CreateView` con offset cero, intermedio y superior al máximo; el resultado está en
  orden cronológico y `Lines.Count ≤ viewportHeight`;
- `CreateView` con `viewportHeight == 0` devuelve vista vacía;
- `CreateView` repetido devuelve lo mismo y no altera `_characterCount` ni
  `_referenceLineCount`;
- `CreateView` con un `scrollOffset` desmesurado (cercano a `int.MaxValue`) no
  desborda `needed` y devuelve un `ClampedScrollOffset` válido; y el caso combinado de
  `viewportHeight` **y** `scrollOffset` ambos cercanos a `int.MaxValue` tampoco
  desborda (`needed` satura en `int.MaxValue`);
- parada temprana: sobre un transcript al límite y un viewport pequeño, el contador
  interno de segmentos visitados está acotado por `viewportHeight + scrollOffset` más
  los segmentos de la última entrada visitada;
- consultar a ancho 40, luego a un ancho estrecho, luego de nuevo a 40 devuelve el
  mismo contenido almacenado;
- argumentos inválidos (`width ≤ 0`, `viewportHeight < 0`, `scrollOffset < 0`) lanzan.

### Host

- `CreateFrame` adopta el `ClampedScrollOffset` que devuelve `CreateView`;
- reducir el ancho de la terminal y volver a ampliarlo no expulsa entradas (regresión
  del bug destructivo);
- una vista pequeña sobre un transcript cercano al límite devuelve exactamente la
  ventana pedida.

## Criterios de aceptación

El incremento 4 estará terminado cuando:

- la retención dependa únicamente de `Add`, con presupuestos de caracteres y de líneas
  de referencia mantenidos incrementalmente;
- una entrada que exceda cualquiera de los dos presupuestos conserve su final con
  marcador (antepuesto una sola vez, reserva de 39 caracteres) y cumpla ambos máximos,
  sin construir metadatos sobre contenido sin límite;
- `CreateView` calcule solo la ventana necesaria, sea una consulta pura, no desborde
  con `scrollOffset` grande y devuelva un offset acotado en orden cronológico;
- la parada temprana esté respaldada por revisión estructural y por el contador interno
  de segmentos visitados, sin benchmarks temporales;
- `CreateFrame` use `CreateView` y adopte su `ClampedScrollOffset`;
- reducir y ampliar la terminal no destruya contenido;
- `dotnet format`, `dotnet build -c Release` y la suite completa pasen;
- no queden procesos `testhost` ni `LocalAssistant.Api` residuales;
- el plan general y `ROADMAP.md` sigan reflejando que el incremento 5 y el punto 5 de
  la fase 5 continúan pendientes.

## Fuera de alcance

- Historial persistido, contenido HTTP, autenticación o autorización.
- Reintroducir un cambio de renderer, comandos, ratón o dependencias visuales.
- Incremento 5 (cierre documental y verificación manual completa) y los incrementos
  posteriores de la fase 5.
- Medición de ancho visual en celdas (limitación ya documentada en el incremento 3).
- Audio, TTS, animaciones o activación de `PlayingVoice`.
