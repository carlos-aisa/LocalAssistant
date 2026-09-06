# Diseño del incremento 2: compatibilidad TUI antes del arranque

## Contexto

El primer incremento de
`2026-09-06-terminal-tui-gap-corrections-implementation-plan.md` convierte la entrada
de la TUI en un canal cerrable y garantiza que cancelación y EOF no dejen lecturas
residuales. El segundo incremento debe cerrar un problema distinto: el cliente no
puede elegir la TUI únicamente porque stdin, stdout y stderr no estén redirigidos.

Una consola puede ser interactiva y, aun así, no admitir alguna operación necesaria
para ejecutar la TUI, como consultar sus dimensiones, comprobar la disponibilidad de
teclas, preparar el frame o restaurar el cursor. Si esa incompatibilidad se descubre
después de iniciar `TerminalClientApplication`, cambiar al modo textual duplicaría la
presentación de una aplicación que ya ha publicado estado o solicitado credenciales.

Este documento desarrolla exclusivamente el incremento 2 del plan general. No
modifica el protocolo de entrada, el layout, el transcript ni los contratos HTTP.

## Objetivo

Elegir definitivamente entre TUI y modo textual antes de ejecutar
`TerminalClientApplication.RunAsync`, usando una comprobación real y acotada de las
capacidades que necesita el driver.

La solución debe garantizar que:

- `--plain` y cualquier redirección seleccionan texto sin crear ni inicializar un
  driver TUI;
- una terminal interactiva solo selecciona TUI después de superar la comprobación;
- una inicialización fallida restaura de mejor esfuerzo cualquier preparación
  parcial y selecciona texto;
- se construye y ejecuta una sola aplicación con el renderer definitivo;
- después de iniciar la aplicación no existe fallback entre renderers.

## Alternativas consideradas

### Resultado de compatibilidad con driver inicializado

El selector recibe las capacidades básicas y una factory de driver. Solo crea el
driver cuando la terminal es candidata a TUI, ejecuta su inicialización y devuelve
una decisión estructurada. Una decisión TUI transporta el driver ya inicializado;
una decisión textual transporta únicamente un motivo seguro.

Esta es la opción elegida porque hace explícita la transferencia de propiedad y
evita comprobar un driver y ejecutar después otro distinto.

### Matriz declarativa de capacidades

Otra posibilidad sería modelar por separado soporte de dimensiones, teclado,
limpieza, escritura y cursor, y decidir a partir de booleanos. Facilitaría algunas
pruebas, pero duplicaría el contrato del driver y seguiría sin demostrar que las
operaciones reales funcionan juntas en la consola actual.

### Arranque optimista con fallback tardío

Consistiría en iniciar la TUI y pasar a texto si el primer render falla. Se descarta
porque la aplicación podría haber publicado snapshots, escrito salida o pedido un
secreto antes del cambio. Dos presentadores observarían parcialmente la misma
ejecución y la restauración del terminal sería ambigua.

## Arquitectura

### Capacidades básicas

`ITerminalPresentationCapabilities` expondrá por separado si stdin, stdout o stderr
están redirigidos. Estas propiedades solo realizan el filtro inicial y no afirman que
la TUI sea compatible.

El selector aplicará las reglas en este orden:

1. `--plain` produce modo textual con motivo `plain_requested`.
2. Cualquier stream redirigido produce modo textual con motivo `redirected`.
3. Solo entonces se crea el driver candidato.
4. El driver ejecuta la comprobación previa.
5. Se valida un tamaño mínimo de 40 columnas por 8 filas.
6. El resultado exitoso transfiere el driver inicializado al host TUI.

El orden evita tocar la consola en escenarios que ya requieren modo textual.

### Resultado de selección

La decisión será un contrato interno e inmutable con:

- modo `Plain` o `Tui`;
- código de motivo seguro y estable;
- driver inicializado únicamente cuando el modo sea `Tui`.

Sus invariantes serán:

- `Plain` nunca contiene un driver;
- `Tui` siempre contiene exactamente el driver que superó la comprobación;
- el consumidor no vuelve a inicializar el driver;
- el motivo no incluye texto de excepciones, rutas, entrada ni datos del usuario.

No se convierte este resultado en API pública ni se registra como telemetría.

### Comprobación del driver

`ITerminalDriver.TryInitialize` representará una preparación real, no una consulta
decorativa. Sin leer ni descartar teclas, deberá demostrar que están disponibles las
operaciones que el host utilizará inmediatamente:

- consulta de ancho y alto;
- consulta no consumidora de disponibilidad de teclado;
- preparación del cursor y del frame;
- limpieza y escritura de un frame inicial seguro;
- restauración del estado terminal controlado por el driver.

La comprobación puede limpiar el área visible porque solo se ejecuta cuando la
consola ya es candidata a TUI. No promete reconstruir el contenido anterior del
buffer del terminal. Si finalmente se degrada a texto, se mostrará después un mensaje
seguro que explica el fallback.

`TryInitialize` no consumirá `Console.ReadKey` ni ninguna entrada pendiente. En caso
de éxito, dejará el driver preparado y su propiedad pasará al host, que lo restaurará
en su `finally`. En caso de `false` o excepción esperada, el selector solicitará una
única restauración de mejor esfuerzo y descartará el driver.

`Restore` conservará un contrato idempotente y no propagará los fallos de consola
esperados. Esto permite usarlo tanto después de una preparación parcial como al cerrar
una sesión TUI completa.

### Composition root testeable

`TerminalClientProgram.Main` conservará el parseo de argumentos, el
`CancellationTokenSource`, el registro de `Console.CancelKeyPress` y el tratamiento
de errores de nivel superior. La selección y ejecución se delegarán a una función
interna con factories inyectables para:

- capacidades de terminal;
- driver TUI;
- aplicación textual;
- aplicación y host TUI.

Esta costura no contendrá lógica conversacional. Su responsabilidad será:

1. obtener una decisión;
2. construir un único grafo de presentación acorde con ella;
3. ejecutar una sola vez la aplicación;
4. no reevaluar el renderer tras comenzar `RunAsync`.

Las factories de aplicación no se invocarán durante la sonda. Así se podrá demostrar
que ningún constructor con estado, credenciales o dependencias HTTP se ejecuta antes
de fijar el modo.

## Flujo de ejecución

```text
Parse options
    |
    +-- --plain o stream redirigido --> decisión Plain
    |
    +-- terminal candidata
            |
            +-- crear driver
            +-- TryInitialize
            +-- validar tamaño
                    |
                    +-- fallo esperado --> Restore --> decisión Plain
                    |
                    +-- éxito ---------> decisión Tui + mismo driver

decisión definitiva
    |
    +-- construir aplicación textual --> RunAsync
    |
    +-- construir aplicación/host TUI --> RunAsync

No se vuelve a seleccionar renderer.
```

## Errores y restauración

Durante la comprobación se consideran incompatibilidades esperadas:

- `IOException`;
- `InvalidOperationException`;
- `PlatformNotSupportedException`;
- resultado explícito `false` del driver;
- dimensiones inferiores al mínimo.

Todas producen modo textual y un motivo seguro. Las excepciones no esperadas no se
ocultan como incompatibilidad: llegan al tratamiento general de `Main` y conservan el
código de salida de error del cliente. Antes de propagarse, si el driver candidato ya
se había creado, el selector solicita una única restauración de mejor esfuerzo, porque
`TryInitialize` puede haber ocultado el cursor o limpiado la pantalla antes de fallar.

Si falla una restauración de mejor esfuerzo después de una comprobación fallida, no
se intenta arrancar la TUI ni repetir la inicialización. El driver absorbe únicamente
las excepciones de consola esperadas; el programa continúa en modo textual y no
expone detalles internos.

Una inicialización correcta transfiere la restauración final al `TerminalClientTuiHost`,
pero el grafo de presentación (`HttpClient`, consola, sink, aplicación) se construye
antes de que el `finally` del host pueda ejecutarse. El composition root mantiene una
salvaguarda `try/finally` alrededor de toda la construcción y ejecución TUI que llama a
`Restore`. Como `SystemTerminalDriver.Restore` es idempotente, esa llamada es inocua
cuando el host ya ha restaurado.

Una vez iniciada `TerminalClientApplication`, cualquier fallo pertenece al flujo de
cierre de ese renderer. No activa una segunda aplicación ni un segundo host.

## Invariantes

- La decisión de presentación precede a la construcción de
  `TerminalClientApplication`.
- La sonda nunca lee ni descarta entrada.
- `--plain` y las redirecciones no crean el driver.
- El host TUI recibe el mismo driver que fue comprobado.
- Cada ejecución construye una sola aplicación y llama una sola vez a `RunAsync`.
- Una selección textual no conserva referencias a un driver fallido.
- Una preparación fallida solicita restauración exactamente una vez, incluso cuando la
  excepción es inesperada y se vuelve a lanzar.
- Una inicialización correcta transfiere la restauración final al host; el composition
  root añade una salvaguarda idempotente que cubre un fallo previo a la entrada del host.
- No existe fallback después del primer snapshot, prompt o salida de aplicación.
- Los motivos de fallback son seguros y no contienen excepciones internas.

## Estrategia de pruebas

### Selector

Una matriz cubrirá:

- `--plain`;
- stdin, stdout y stderr redirigidos individualmente;
- tamaños `39×8`, `40×7` y `40×8`;
- driver ausente o factory no disponible;
- `TryInitialize` igual a `false`;
- `IOException`, `InvalidOperationException` y
  `PlatformNotSupportedException` durante la inicialización;
- fallo al consultar dimensiones después de una preparación válida;
- restauración única después de cada fallo esperado;
- ausencia de restauración por parte del selector tras un éxito.

Los dobles registrarán creación, inicialización, consulta de tamaño, lectura de teclas,
render y restauración. Las pruebas afirmarán explícitamente que la sonda no invoca la
lectura consumidora.

### Driver real mediante frontera inyectable

Las operaciones estáticas de `Console` se ocultarán detrás de una frontera interna
mínima para probar sin una terminal real:

- preparación completa satisfactoria;
- fallo de tamaño, teclado, limpieza, escritura y cursor;
- limpieza/escritura del frame inicial sin contenido no confiable;
- restauración idempotente;
- cero llamadas a `ReadKey` durante la sonda.

No se simulará una consola completa ni se trasladará el loop de la TUI a esta
abstracción.

### Composition root

Las pruebas de programa comprobarán:

- que una incompatibilidad crea exclusivamente la aplicación textual;
- que un éxito crea exclusivamente la aplicación y el host TUI;
- que el driver comprobado es el que recibe el host;
- que cada aplicación se ejecuta una sola vez;
- que un fallo posterior a `RunAsync` no crea el renderer alternativo;
- que el handler de `Ctrl+C` se desregistra en todas las salidas;
- que el mensaje de fallback usa el motivo seguro y no el texto de la excepción.

Los tests usarán contadores y señales observables; los timeouts solo protegerán contra
cuelgues.

## Criterios de aceptación

El incremento estará terminado cuando:

- toda decisión TUI esté respaldada por una comprobación real del driver;
- el modo textual se elija antes del arranque ante opción explícita, redirección,
  tamaño insuficiente o incompatibilidad esperada;
- las operaciones reales de preparación, render mínimo y restauración estén cubiertas
  mediante una frontera testeable;
- la composición demuestre que solo existe una aplicación y un renderer por
  ejecución;
- no se consuma entrada durante la sonda;
- las pruebas del incremento, formato, build Release y suite afectada pasen;
- no queden procesos `testhost` ni `LocalAssistant.Api` residuales.

## Fuera de alcance

- Cambiar la lógica de comandos o `TerminalClientApplication`.
- Modificar contratos HTTP, autenticación, autorización u OpenAPI.
- Corregir todavía el layout, EOF de Windows o los límites del transcript; pertenecen
  a los incrementos 3 y 4.
- Añadir fallback después de iniciar una interacción.
- Publicar interfaces de presentación para consumidores externos.
- Introducir una dependencia TUI nueva, audio, TTS o animaciones.
