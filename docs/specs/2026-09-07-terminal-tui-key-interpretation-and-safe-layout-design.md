# Diseño del incremento 3: interpretación de teclas y layout seguro

## Contexto

El commit `Implement terminal TUI gap corrections and enhancements` adelantó gran parte
de la mecánica del incremento 3 del plan
`docs/plans/2026-09-06-terminal-tui-gap-corrections-implementation-plan.md`:

- `Ctrl+D` y `Ctrl+Z` con buffer vacío cierran el canal; con texto se ignoran
  (`TerminalClientTuiHost.ProcessKey` / `IsEndOfInputKey`);
- `TerminalTextSanitizer.NormalizeSingleLine` representa `CR`, `LF`, `ESC`, ANSI, OSC
  y controles C0/C1 como escapes visibles;
- `CreateFrame` asigna filas por prioridad (input, confirmación con caducidad UTC,
  error con marca de incertidumbre, estado, transcript);
- `CreateCompactFrame` produce una vista de seguridad para tamaños inferiores a
  `40×8` con una indicación para ampliar;
- `FitInputLine` / `FitTail` muestran el extremo activo de una entrada larga y la
  entrada secreta solo enmascarada;
- `TerminalClientTuiTranscript` acota el transcript a 65.536 caracteres y 2.000 líneas.

Este documento cierra lo que falta del incremento 3 y no reabre esas piezas. No
modifica el protocolo de entrada del canal cerrable (incremento 1), la comprobación de
compatibilidad previa (incremento 2), los contratos HTTP, la autenticación ni la
autorización.

## Problemas restantes

### Interpretación de teclas entrelazada con efectos

`ProcessKey` decide y ejecuta a la vez: muta el `StringBuilder` del buffer, cambia
`_scrollOffset`, llama a `CompleteInput`, dispara el cierre terminal y marca el frame
como sucio. No existe una unidad pura que traduzca una pulsación y el estado del
buffer a una intención. En consecuencia:

- cada prueba de teclado recorre el bucle asíncrono del host con sondeo temporal de
  frames, lo que es lento y sensible a *timing*;
- la invariante «resize, scroll, EOF y combinaciones de control nunca aprueban una
  confirmación» solo se demuestra de forma indirecta;
- añadir o cambiar una regla de teclado obliga a razonar sobre el host completo.

### Entrada aceptada sin solicitud activa

`ProcessKey` procesa teclas aunque no haya una solicitud de entrada publicada. Un
texto escrito mientras la aplicación procesa una respuesta queda en el buffer y, si a
continuación se publica una confirmación, ese buffer se asocia al nuevo prompt. Pulsar
`Enter` podría entonces aprobar una herramienta con un texto escrito antes de ver la
confirmación. El consentimiento debe escribirse después de mostrarse la confirmación
correspondiente.

### `Ctrl+C` entregado como tecla no cancela una operación en curso

`RequestTerminalClose` solo cierra el canal de entrada. Si la aplicación espera una
petición HTTP, la ruta de `Console.CancelKeyPress` cancela su `CancellationToken`,
pero un `Ctrl+C` que llegue al host como tecla no lo haría. Las dos rutas de `Ctrl+C`
deben ser equivalentes.

### Contrato de lectura de `SystemTerminalDriver` sin pruebas

`TerminalDriverTests` no ejercita `SystemTerminalDriver.TryReadInput`. No hay pruebas
de que:

- una tecla disponible se entregue como evento de tecla y **no** como EOF, incluidas
  `Ctrl+C`, `Ctrl+D` y `Ctrl+Z` (el driver solo transporta la tecla; el host la
  interpreta);
- la ausencia de tecla devuelva `null`;
- `IOException` e `InvalidOperationException` durante `KeyAvailable` o `ReadKey` se
  traduzcan a `TerminalInputEvent.EndOfInput`;
- la lectura use `intercept: true` y nunca haga eco.

La convención de `Ctrl+Z` en Windows solo puede confirmarse en la comprobación manual
del incremento 5; aquí se fija el contrato del driver, no el comportamiento de la
consola real.

### Huecos de cobertura del layout

- La vista compacta (`<40×8`) no tiene pruebas que afirmen que input y confirmación
  sobreviven y que aparece la indicación de ampliar.
- Ningún test comprueba el render del extremo de una entrada mayor que el ancho, con
  el marcador `…` y los últimos caracteres visibles, ni su equivalente enmascarado.
- El redimensionado se prueba solo con entrada normal; el diseño pide también
  probarlo mientras el worker espera un secreto.
- `Backspace` en la TUI y «`PageUp`/`PageDown` no completan un prompt» son implícitos.

## Objetivo

Extraer la interpretación de teclas a una unidad pura y probada, dejar el host como
ejecutor delgado de intenciones, fijar el contrato de lectura de `SystemTerminalDriver`
sobre `ISystemTerminal` y cerrar los huecos de prueba del layout, sin cambiar el
comportamiento observable actual salvo las correcciones menores descritas.

## Alternativas consideradas

### Intérprete puro de teclas con intención tipada

Una función pura
`Interpret(ConsoleKeyInfo, bool inputActive, bool bufferHasText) → TerminalKeyIntent`
que no toca consola, host ni estado. La intención transporta todo lo que el host
necesita para ejecutar sin volver a inspeccionar la tecla.

Es la opción elegida. Aísla la única lógica no trivial del teclado, permite una tabla
de verdad exhaustiva en pruebas rápidas y hace directamente demostrable que solo
`Enter` con una solicitud activa produce un envío.

### Mantener `ProcessKey` y añadir solo pruebas

Menos código, pero incumple el punto 1 del plan del incremento, conserva la mezcla de
decisión y efecto y deja la invariante de confirmación probada de forma indirecta.
Descartada.

### Máquina de estados de edición completa

Un editor con cursor, selección e historia. Sobredimensionado: el modelo actual solo
edita al final de la línea y el incremento no añade edición avanzada. Descartada.

## Arquitectura

### `TerminalKeyInterpreter`

Clase estática interna con un único método:

```text
TerminalKeyIntent Interpret(ConsoleKeyInfo key, bool inputActive, bool bufferHasText)
```

- `inputActive`: existe una solicitud de entrada publicada por la aplicación.
- `bufferHasText`: el buffer de edición del host contiene al menos un carácter.

`TerminalKeyIntent` será un `readonly record struct` con:

- `TerminalKeyAction Action`
  (`Insert`, `DeletePrevious`, `Scroll`, `Submit`, `SubmitEmpty`, `CancelApplication`,
  `CloseChannel`, `Ignore`);
- `char Character` — solo para `Insert`;
- `int ScrollDelta` — solo para `Scroll`; positivo hacia contenido más antiguo.

`Submit` entrega el contenido del buffer; `SubmitEmpty` completa la solicitud vigente
con una cadena vacía. Ninguna de las dos interpreta ese valor: aprobar, rechazar,
iniciar pairing, elegir un predeterminado o volver a preguntar es competencia
exclusiva de `TerminalClientApplication`.

Reglas, en orden:

1. `Ctrl+C` → `CancelApplication` (incondicional).
2. `Ctrl+D` o `Ctrl+Z`: `Ignore` si `bufferHasText`; `CloseChannel` en caso contrario.
3. `PageUp` → `Scroll(+5)`; `PageDown` → `Scroll(-5)`.
4. `Enter` → `Submit` si `inputActive`; `Ignore` en caso contrario.
5. `Escape` → `SubmitEmpty` si `inputActive`; `Ignore` en caso contrario.
6. `Backspace` → `DeletePrevious` si `inputActive` y `bufferHasText`; `Ignore` en caso
   contrario.
7. `!char.IsControl(key.KeyChar)` → `Insert(key.KeyChar)` si `inputActive`; `Ignore` en
   caso contrario.
8. Cualquier otra tecla, incluidas `UpArrow`, `DownArrow`, `Tab` y las funciones →
   `Ignore`.

`CancelApplication`, `CloseChannel` y `Scroll` no dependen de `inputActive`. Toda
acción que lee o muta el buffer (`Insert`, `DeletePrevious`, `Submit`, `SubmitEmpty`)
solo se produce con una solicitud activa: una tecla escrita mientras la aplicación
procesa una respuesta se descarta y no queda asociada al siguiente prompt.

El orden garantiza que `Ctrl+C`, `Ctrl+D`/`Ctrl+Z` y las teclas de navegación (cuyo
`KeyChar` es de control) se resuelvan antes de la regla de inserción.

### Cancelación frente a cierre del canal

Son dos intenciones distintas con efectos distintos:

- **`Ctrl+C` → `CancelApplication`.** Cancela la ejecución completa. El evento EOF
  nativo no lo produce; solo `Ctrl+C`.
- **EOF, `Ctrl+D` y `Ctrl+Z` con buffer vacío → `CloseChannel`.** Cierra el canal de
  entrada de forma terminal. Una lectura pendiente devuelve `null`; la aplicación
  termina su flujo normal. No cancela una operación HTTP en curso.

El host ejecuta la aplicación con un `CancellationTokenSource` enlazado al token
recibido. `CancelApplication` cancela ese `CancellationTokenSource` **y** cierra el
canal de entrada. Así las dos rutas de `Ctrl+C` —la del `Console.CancelKeyPress`
externo y la de la tecla entregada por el driver— son equivalentes incluso durante una
petición HTTP: ambas cancelan el token y desbloquean la lectura pendiente.

`Ctrl+C` normalmente lo intercepta `Console.CancelKeyPress` y no llega al intérprete,
pero esa intercepción no está garantizada en todos los hosts. La ruta de tecla existe
como defensa en profundidad; el cierre y la cancelación son idempotentes, así que no
importa que también los haya iniciado `Console.CancelKeyPress`. El bloqueo al pulsar
`Ctrl+C` fue el modo de fallo de la primera TUI, por lo que se cubre en las dos rutas.

### Buffer de edición ligado a la solicitud

El buffer de edición del host se asocia a la instancia concreta de
`TerminalInputRequest` activa. Antes de aplicar una acción que muta el buffer, si la
solicitud activa no es la instancia a la que el buffer está ligado, el host limpia el
buffer y lo religa a la nueva. `Submit`, `SubmitEmpty` y el cierre del canal limpian el
buffer y sueltan la ligadura.

Esto, junto con la regla de `inputActive`, garantiza que un texto escrito antes de que
se publique un prompt nunca se entregue como respuesta a ese prompt.

### Decisión: teclas de flecha reservadas

La primera TUI evitó deliberadamente atajos de navegación. Solo `PageUp` y `PageDown`
desplazan el transcript. `UpArrow` y `DownArrow` se dejan como `Ignore` en este
incremento para no comprometer un futuro historial de comandos, edición con cursor o
navegación por sugerencias. Reintroducirlas será una decisión explícita de un
incremento posterior, no una mecánica interna accidental. Esto reduce el
comportamiento actual, que también desplazaba con las flechas; ninguna prueba depende
de ello.

### Ejecución en el host

El host ejecuta la aplicación con
`CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)` y pasa el token
enlazado a `operation`. El registro sobre el token externo sigue cerrando el canal de
entrada de inmediato.

`ProcessKey` obtiene la solicitud activa (`_console.TryGetInputRequest`), religa el
buffer si esa instancia cambió y despacha con un `switch` sobre `intent.Action`, sin
lógica de decisión:

- `CancelApplication` → cancelar el `CancellationTokenSource` enlazado y cerrar el
  canal de entrada;
- `CloseChannel` → cerrar el canal de entrada y limpiar el buffer, sin cancelar el
  token;
- `Scroll` → `_scrollOffset = Math.Max(0, _scrollOffset + intent.ScrollDelta)` y frame
  sucio;
- `Submit` → tomar el buffer, limpiarlo, soltar la ligadura y `CompleteInput(valor)`;
- `SubmitEmpty` → limpiar el buffer, soltar la ligadura y `CompleteInput(string.Empty)`;
- `DeletePrevious` → `_input.Length--` (el intérprete garantizó solicitud activa y
  buffer con texto);
- `Insert` → `_input.Append(intent.Character)`;
- `Ignore` → nada.

El cierre del canal y la cancelación son idempotentes. `IsEndOfInputKey` se elimina. El
evento `IsEndOfInput` del driver se sigue tratando en `ProcessAvailableInput` como
`CloseChannel` antes de interpretar teclas.

El intérprete no recibe ni conserva referencias a consola, host, buffer o driver.

### Contrato de lectura del adaptador `SystemTerminalDriver`

`SystemTerminalDriver.TryReadInput` no cambia de comportamiento; se fija su contrato
sobre la frontera `ISystemTerminal`:

- entrega `new TerminalInputEvent(key, IsEndOfInput: false)` para cualquier tecla,
  incluidas `Ctrl+C`, `Ctrl+D` y `Ctrl+Z`, sin interpretarlas;
- devuelve `null` cuando no hay tecla disponible;
- traduce `IOException` e `InvalidOperationException` de `KeyAvailable` o `ReadKey` a
  `TerminalInputEvent.EndOfInput`;
- lee siempre con `intercept: true`.

Estas pruebas usan un doble de `ISystemTerminal` con tecla, disponibilidad y
excepciones configurables y registro del argumento `intercept`. Verifican el adaptador
del driver del sistema, no el comportamiento de la consola real de Windows: si
`Console.ReadKey` entrega `Ctrl+Z` como tecla, como carácter de sustitución o como
EOF nativo es una cuestión de la consola y se comprueba en la prueba manual del
incremento 5.

### Layout

Se fija una **prioridad de retención** única, de mayor a menor:

```text
confirmación pendiente (con caducidad UTC)
  > input activo
  > aviso de ampliar (solo modo compacto)
  > error con marca de incertidumbre
  > estado (ciclo de vida, actividad, proveedor, conversación)
  > transcript
```

Una confirmación pendiente bloquea el progreso, por lo que se prioriza incluso por
encima del input. El comportamiento por tamaño, con umbrales explícitos:

- **`≥ 40×8` (compatible):** todas las filas prioritarias caben. El transcript ocupa
  el espacio restante. Visualmente el input queda adyacente al borde inferior;
  confirmación, error y estado se muestran encima; el transcript arriba.
- **`20×6 ≤ tamaño < 40×8` (compacto):** las filas se asignan en orden de prioridad y
  se recorta por el final. Se garantizan confirmación e input; el aviso de ampliar, el
  error y el estado se muestran solo si la altura alcanza. El transcript puede
  desaparecer, pero nunca desplaza confirmación ni input.
- **`< 20×6`:** no se garantiza ningún contenido concreto. Se garantiza únicamente que
  el render no lanza, no desborda el ancho y no emite `\n` ni `ESC`, aplicando la
  misma prioridad para decidir qué cabe.

Cambios de código respecto al estado actual:

- reordenar `CreateCompactFrame` (y el recorte de reserva de `CreateFrame`) a la
  prioridad anterior, con la confirmación antes del input;
- clamp de `_scrollOffset` al máximo útil dentro de `Render`, para que mantener
  `PageDown`/`PageUp` no acumule un entero sin límite;
- verificación explícita de que toda línea entregada al driver mide como máximo el
  ancho y no contiene `\n` ni `ESC`, para cualquier ancho y entrada hostil.

### Límite conocido: ancho en celdas

«Una fila física» se define en este incremento sobre `string.Length` y texto latino
compatible. Caracteres de ancho doble (CJK), emojis y marcas combinantes rompen la
equivalencia entre `string.Length` y celdas del terminal y pueden producir una fila
que ocupe más o menos de una línea visual. Medir el ancho visual real queda fuera del
alcance de este incremento y se documenta como limitación.

## Flujo de una pulsación

```text
driver.TryReadInput()
   |
   +-- IsEndOfInput --> CloseChannel (cerrar canal + limpiar buffer)
   |
   +-- Key --> Interpret(key, inputActive, bufferHasText)
                   |
                   +-- CancelApplication --> cancelar CTS enlazado + cerrar canal
                   +-- CloseChannel      --> cerrar canal + limpiar buffer
                   +-- Submit            --> CompleteInput(buffer)      [solo con prompt]
                   +-- SubmitEmpty       --> CompleteInput("")          [solo con prompt]
                   +-- Insert/Delete     --> mutar buffer religado      [solo con prompt]
                   +-- Scroll            --> mover viewport
                   +-- Ignore            --> nada
```

## Invariantes

- Solo `Enter` con una solicitud activa entrega el contenido del buffer (`Submit`).
  `Escape` con una solicitud activa la completa con una cadena vacía (`SubmitEmpty`);
  interpretar ese valor corresponde exclusivamente a `TerminalClientApplication`.
- Ninguna combinación de scroll, EOF o control produce `Submit`.
- Sin una solicitud de entrada activa, ninguna tecla muta ni entrega el buffer. Un
  texto escrito antes de que se publique un prompt no se asocia a ese prompt.
- El buffer de edición está ligado a una instancia de `TerminalInputRequest`; cambiar
  de solicitud limpia el buffer antes de aceptar entrada nueva.
- `Ctrl+D` y `Ctrl+Z` con texto en el buffer no envían, no aprueban y no cierran.
- `Ctrl+C` cancela la ejecución completa (token enlazado) y cierra el canal, por las
  dos rutas (`Console.CancelKeyPress` y tecla entregada por el driver); ambas son
  idempotentes y equivalentes durante una operación HTTP.
- El evento EOF nativo, `Ctrl+D` y `Ctrl+Z` con buffer vacío cierran el canal pero no
  cancelan una operación HTTP en curso.
- El intérprete es puro: mismas entradas, misma intención; no toca consola ni estado.
- El buffer visual, el viewport y las operaciones ordinarias del driver se modifican
  serialmente desde el bucle del host; las señales entre hilos (cierre del canal,
  petición de limpieza, cancelación) usan sincronización explícita.
- Toda cadena entregada al driver ocupa exactamente una fila física para texto latino
  compatible (ver límite conocido).
- Con la prioridad de retención: a `≥ 40×8` no quedan ocultos input, confirmación,
  incertidumbre ni estado; a `20×6 ≤ tamaño < 40×8` no quedan ocultos confirmación ni
  input; por debajo de `20×6` solo se garantiza render seguro.
- El driver transporta las teclas sin interpretarlas; la traducción de `Ctrl+C`,
  `Ctrl+D` y `Ctrl+Z` ocurre en el host.
- `PlayingVoice` sigue sin activarse.

## Estrategia de pruebas

### Intérprete

Tabla de verdad en `TerminalKeyInterpreterTests`, sin host ni consola:

- con solicitud activa: inserción de un carácter imprimible y de uno español;
  `Backspace` con y sin texto; `Enter` → `Submit`; `Escape` → `SubmitEmpty`;
- sin solicitud activa: carácter imprimible, `Backspace`, `Enter` y `Escape` → `Ignore`;
- `PageUp` / `PageDown` producen `Scroll` con el signo y la magnitud esperados, con y
  sin solicitud activa, y nunca `Submit` ni `Insert`;
- `UpArrow`, `DownArrow`, `Tab`, `F1` y demás teclas no asignadas producen `Ignore`;
- `Ctrl+D` y `Ctrl+Z` con buffer vacío producen `CloseChannel`; con texto, `Ignore`;
- `Ctrl+C` produce `CancelApplication` con y sin texto y con y sin solicitud activa;
- afirmación explícita: para toda tecla distinta de `Enter` con solicitud activa, la
  acción no es `Submit`.

### Adaptador del driver del sistema

En `TerminalDriverTests`, con el doble de `ISystemTerminal`:

- tecla disponible → `TerminalInputEvent(key, IsEndOfInput=false)`, con `intercept`
  registrado como `true`;
- la misma comprobación con una `ConsoleKeyInfo` de `Ctrl+C`, `Ctrl+Z` y `Ctrl+D`: se
  entregan como tecla, no como EOF;
- sin tecla disponible → `null`;
- `KeyAvailable` lanza `IOException` → `EndOfInput`;
- `ReadKey` lanza `InvalidOperationException` → `EndOfInput`.

### Host y layout

En `TerminalClientTuiTests`, con el driver falso y sincronización observable:

- se conservan una o dos pruebas de integración de teclado (cierre por `Ctrl+D`,
  `Escape` permite el siguiente prompt) para cubrir el cableado host↔intérprete;
- entrada anticipada: escribir `approve` sin solicitud activa; después publicar una
  confirmación; el input renderizado aparece vacío y `Enter` entrega una cadena vacía
  (no aprueba con el texto anticipado);
- `Ctrl+C` en las dos rutas, durante una operación en curso: el `Console.CancelKeyPress`
  externo y una `ConsoleKeyInfo` de `Ctrl+C` entregada por el driver cancelan el token
  de la aplicación y cierran el canal por igual;
- `Ctrl+D` con buffer vacío durante una operación en curso: cierra el canal pero el
  token de la aplicación no se cancela;
- vista compacta a `20×6` con confirmación y error: el frame conserva confirmación e
  input y muestra la indicación de ampliar; ninguna línea supera el ancho;
- vista por debajo de `20×6` (`10×2`, `1×1`): el render no lanza, no desborda el ancho
  y no emite `\n` ni `ESC`;
- entrada normal más larga que el ancho: el frame muestra `…` y los últimos
  caracteres; su equivalente secreto muestra solo asteriscos del extremo;
- `Backspace` reduce el buffer y se refleja en el frame;
- `PageUp` seguido de cancelación: el prompt no se completó (el worker devuelve el
  código de cancelación) y el viewport cambió;
- clamp de `_scrollOffset`: probado tras reducir el transcript y tras un resize, no
  solo tras repetir `PageUp`;
- redimensionado por debajo y por encima del mínimo mientras el worker espera un
  secreto, sin filtrar el secreto a ningún frame;
- propiedad, sobre un rango de anchos y con nombres de herramienta y errores que
  contienen `\n`, `ESC`, ANSI, OSC y C0/C1: toda línea de todo frame mide `≤ ancho` y
  no contiene `\n` ni `ESC`.

## Criterios de aceptación

El incremento 3 estará terminado cuando:

- la interpretación de teclas viva en `TerminalKeyInterpreter`, sea pura, reciba
  `inputActive` y esté cubierta por una tabla de verdad;
- `ProcessKey` no contenga lógica de decisión de teclado y religue el buffer a la
  solicitud activa;
- sin solicitud activa ninguna tecla mute ni entregue el buffer, con prueba de entrada
  anticipada;
- `Ctrl+C` cancele el token de la aplicación por las dos rutas, verificado durante una
  operación en curso; `Ctrl+D`/`Ctrl+Z`/EOF cierren el canal sin cancelar el token;
- el contrato de `SystemTerminalDriver.TryReadInput` esté probado para tecla, ausencia
  de tecla, `Ctrl+C`, `Ctrl+D`, `Ctrl+Z` y excepciones esperadas;
- la vista compacta desde `20×6`, la vista sin garantías por debajo de `20×6`, la
  entrada larga normal y secreta, el `Backspace`, el scroll que no envía, el clamp de
  scroll y el redimensionado con secreto estén cubiertos;
- ninguna prueba de teclado dependa de esperas temporales como mecanismo de
  sincronización;
- `dotnet format`, `dotnet build -c Release` y la suite completa pasen;
- no queden procesos `testhost` ni `LocalAssistant.Api` residuales;
- el plan general y `ROADMAP.md` sigan reflejando que los incrementos 4 y 5 y el
  punto 5 de la fase 5 continúan pendientes.

## Fuera de alcance

- Edición en el interior de la línea, cursor movible, selección o historial de
  comandos.
- Reintroducir `UpArrow`/`DownArrow` como scroll o navegación: será una decisión
  explícita de un incremento posterior.
- Medición de ancho visual en celdas; la garantía de una fila física se limita a texto
  latino compatible.
- Traducción real de `Ctrl+Z`, `Ctrl+D` y `Ctrl+C` en la consola de Windows:
  pertenece a la comprobación manual del incremento 5.
- Reabrir el presupuesto del transcript (incremento 4) o la comprobación de
  compatibilidad (incremento 2).
- Cambios en contratos HTTP, autenticación, autorización u OpenAPI.
- Nuevos comandos, parser de TUI, ratón, atajos sensibles o dependencias visuales.
- Audio, TTS, animaciones o activación de `PlayingVoice`.
