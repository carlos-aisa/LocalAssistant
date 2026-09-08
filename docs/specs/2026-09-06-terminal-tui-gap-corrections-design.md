# Correcciones de cierre para la TUI de terminal

## Contexto

Las PR 67 y 68 introdujeron la TUI mínima y corrigieron el bloqueo original de
`Ctrl+C`, el renderizado de estados prioritarios, el redimensionado, la abstracción
del terminal y la neutralización de secuencias de control. La implementación y sus
421 pruebas pasaron CI, pero la revisión posterior encontró rutas que todavía no
cumplen las garantías de cierre del punto 5 de la fase 5.

Este documento complementa y corrige exclusivamente
`2026-09-05-accessible-terminal-tui-evaluation-design.md`. No modifica la decisión de
usar un renderizador propio sobre `net8.0` ni amplía la TUI con comandos, audio,
animaciones o dependencias visuales nuevas.

## Problemas confirmados

### Cancelación terminal no persistente

El host completa la solicitud de entrada que está pendiente cuando se cancela su
token. Esa señal no queda registrada. Durante pairing, una entrada cancelada puede
llevar a `GetCredentialAsync` a solicitar inmediatamente el desafío secreto o el
nombre del cliente. La segunda lectura se crea después de que el callback de
cancelación ya se haya ejecutado y puede esperar indefinidamente.

La cancelación terminal debe ser una condición persistente del canal de entrada, no
un resultado puntual de una sola lectura.

### Buffer de entrada conservado tras cancelar

El callback de cancelación completa la espera del adaptador, pero no limpia el
`StringBuilder` que pertenece al host. Si contenía una credencial o un desafío, el
valor deja de mostrarse, pero puede permanecer en memoria hasta que se descarte el
host. El mismo buffer debe limpiarse en cancelación, EOF y cierre, siempre desde el
hilo propietario de la interfaz.

La limpieza será de mejor esfuerzo dentro de memoria administrada: eliminará el
contenido lógico, sustituirá el buffer y soltará referencias cuanto antes, pero no
prometerá un borrado físico verificable de las copias de `string` que necesita el
flujo de autenticación.

### Viewport que puede ocultar información operativa

El renderer crea primero estado, error, confirmación e input y después aplica
`Take(height)`. En tamaños reducidos, el input, que se añade al final, puede quedar
fuera. El truncado por ancho también puede ocultar el indicador de incertidumbre, la
caducidad de una confirmación o los caracteres más recientes de una entrada larga.

Los saltos de línea son válidos en el transcript público, pero no en metadatos de una
sola línea. Un salto incluido en un mensaje de error o nombre de herramienta produce
más filas físicas de las contabilizadas y rompe la reserva del viewport.

### Compatibilidad inferida solo mediante redirección

El composition root considera compatible cualquier consola con entrada, salida y
error no redirigidos. No comprueba que tamaño, lectura de teclas, limpieza y
restauración estén disponibles. Una consola no redirigida pero incompatible puede
seleccionar la TUI y fallar después de arrancar `TerminalClientApplication`, cuando ya
no debe cambiarse de renderer.

### Transcript limitado por entradas, no por volumen

El transcript conserva como máximo cien entradas, pero cada una puede tener tamaño
arbitrario. En cada render se vuelven a dividir todas las entradas conservadas. Una
sola respuesta muy grande puede provocar asignaciones excesivas y bloquear la entrada
interactiva aunque solo se muestre un viewport pequeño.

### EOF de producción no demostrado en Windows

El driver falso puede emitir EOF y el host reconoce `Ctrl+D`, pero el adaptador de
producción no define el comportamiento de `Ctrl+Z`, que es la convención esperable en
Windows. La prueba actual demuestra el contrato falso, no la traducción de teclas del
driver real.

## Decisiones

### Canal de entrada cerrable

`TerminalClientTuiConsoleAdapter` tendrá dos operaciones distintas:

- completar o cancelar solo la solicitud vigente;
- cerrar el canal de entrada de forma terminal.

El cierre del canal se serializará bajo el mismo bloqueo que protege la solicitud
pendiente. Marcará primero el canal como cerrado y después completará la espera actual
con `null`. Cualquier `RequestInput` posterior comprobará esa marca bajo el mismo
bloqueo y devolverá `null` inmediatamente, sin publicar una nueva solicitud.

El token del host, EOF, `Ctrl+C` y el cierre del host cerrarán el canal. `Escape`, una
línea vacía y las cancelaciones funcionales de un prompt no cerrarán el canal porque
la aplicación todavía puede solicitar otra entrada válida.

Esta regla garantiza que una cancelación durante cualquiera de los tres prompts de
pairing siempre alcanza `Closing` y `Closed` sin una segunda espera bloqueada.

### Propiedad y limpieza del buffer

El bucle del host seguirá siendo el único que modifica el buffer visual. El callback
de cancelación cerrará inmediatamente el canal para liberar al worker y publicará una
petición de limpieza segura para el bucle. El bucle procesará esa petición antes de
renderizar. El bloque `finally` limpiará también el buffer antes de restaurar el
terminal, cubriendo el caso en que la aplicación termine antes del siguiente ciclo.

La limpieza no se registrará, no publicará el contenido y no copiará secretos al
transcript ni al snapshot.

### Layout con prioridades explícitas

El renderer dejará de construir una lista de footer y recortarla por posición. Cada
frame asignará filas en este orden:

1. input activo;
2. confirmación pendiente, incluida su caducidad UTC;
3. error y marca explícita de resultado incierto;
4. ciclo de vida, actividad, proveedor y conversación;
5. transcript.

La terminal compatible tendrá un mínimo de 40 columnas y 8 filas. Si durante la
sesión cae por debajo, se mostrará una vista compacta de seguridad que conserva input
y la condición operativa más importante, junto con una indicación para ampliar la
ventana. El transcript puede desaparecer temporalmente, pero nunca desplazar el input
ni una confirmación.

Los valores operativos de una sola línea usarán una normalización específica que
represente saltos de línea, retornos y controles como escapes visibles. Los saltos de
línea seguros solo se conservarán dentro del transcript.

La entrada de una línea mostrará una ventana sobre su extremo activo cuando supere el
ancho disponible. El prompt puede abreviarse, pero los últimos caracteres y la
posición efectiva de edición deben permanecer visibles. La entrada secreta seguirá
mostrando únicamente enmascarado.

### Comprobación previa de compatibilidad

La decisión del composition root tendrá dos etapas, ambas anteriores a
`TerminalClientApplication.RunAsync`:

1. comprobar `--plain`, redirecciones y dimensiones mínimas;
2. inicializar el driver con operaciones no destructivas de tamaño y disponibilidad
   de teclado, y una primera preparación/restauración del frame sin leer ni descartar
   entrada.

Si cualquiera falla con `IOException`, `InvalidOperationException` o
`PlatformNotSupportedException`, se descarta la TUI y se crea el cliente textual.
Después de arrancar la aplicación no se permite cambiar de renderer; un fallo
posterior sigue el cierre controlado normal.

El resultado de la comprobación será un contrato explícito y testeable, no un booleano
fabricado directamente por los tests.

### Presupuesto acotado del transcript

El almacenamiento visual usará una cola con presupuesto acumulado. Se conservarán
como máximo 65.536 caracteres normalizados y 2.000 líneas de referencia (medidas a un
ancho fijo de 40, no al ancho actual del terminal). Al superar uno de los límites se
eliminarán primero las entradas completas más antiguas. Si una sola entrada excede el
presupuesto, se conservará su parte final con un marcador visible de truncado, porque
el viewport inicial prioriza el contenido más reciente.

El renderer calculará únicamente las líneas necesarias para el viewport y el offset
de scroll dentro de esos límites. Añadir contenido nuevo volverá al final; el scroll
del usuario no modificará mensajes ni comandos.

### EOF en Windows

El driver real traducirá `Ctrl+D` y `Ctrl+Z` con el buffer de entrada vacío al mismo
cierre terminal que EOF. Si el buffer contiene texto, estas combinaciones se ignorarán
y no enviarán ni aprobarán nada accidentalmente. La salida redirigida continuará
usando el flujo textual y su EOF nativo.

## Invariantes

- Después de cerrar el canal, ninguna lectura puede quedar pendiente ni publicar otro
  prompt.
- El buffer visual, el viewport y el driver se modifican serialmente desde el bucle
  del host, no desde un hilo dedicado.
- Cancelar o cerrar limpia el buffer antes de restaurar el terminal.
- El transcript y los snapshots nunca contienen entrada secreta.
- Una confirmación solo se resuelve mediante las líneas completas `approve` o
  `reject`; resize, scroll, EOF y combinaciones de control nunca la aprueban.
- Los metadatos operativos ocupan el número de filas contabilizado por el layout.
- En dimensiones compatibles, input, confirmación, incertidumbre y estado no quedan
  ocultos por el transcript.
- La selección del modo textual o TUI termina antes de iniciar la autoridad de estado.
- El trabajo de render queda acotado por constantes probadas y no por el tamaño total
  recibido del servidor.
- `PlayingVoice` continúa sin activarse.

## Estrategia de pruebas

Las pruebas usarán el driver falso y sincronización observable, sin esperas basadas en
tiempo como mecanismo principal. Cubrirán:

- `Ctrl+C` en cada prompt de pairing y entre dos prompts consecutivos;
- cancelación con un secreto ya escrito, comprobando cierre, limpieza y ausencia en
  transcript y frames;
- EOF, `Ctrl+D` y `Ctrl+Z`, con buffer vacío y no vacío;
- cierre normal, excepción de aplicación y fallo de driver, verificando una sola
  restauración;
- redimensionado por encima y por debajo del mínimo mientras el worker espera entrada
  normal y secreta;
- conservación visible del input, confirmación, caducidad, error e incertidumbre en
  el viewport mínimo soportado;
- entradas mayores que el ancho, backspace y caracteres españoles;
- errores y nombres de herramienta con saltos, ESC, ANSI, OSC y controles C0/C1;
- una respuesta individual mayor que el presupuesto y muchas respuestas acumuladas;
- selección textual ante `--plain`, redirección, dimensiones insuficientes o fallo de
  inicialización del driver;
- ausencia de cambio de renderer después de iniciar la aplicación;
- flujo completo `/exit` y restauración final.

La prueba manual en Windows Terminal y PowerShell seguirá los pasos del documento de
evaluación y añadirá `Ctrl+Z`, terminal por debajo del mínimo, entrada larga y pairing
cancelado. El punto 5 del roadmap seguirá sin marcarse hasta completar esta prueba.

## Criterios de cierre

La corrección estará terminada cuando:

- ninguna cancelación o EOF pueda crear una segunda espera bloqueada;
- el buffer se limpie en todas las salidas y no se filtre contenido secreto;
- el layout conserve la información operativa prioritaria dentro del tamaño mínimo;
- terminales incompatibles entren en modo textual antes de iniciar la aplicación;
- el coste de transcript y render esté acotado;
- las pruebas nuevas, formato, build Release y suite completa pasen;
- la comprobación manual en Windows Terminal y PowerShell quede registrada;
- el roadmap y la evaluación reflejen exactamente el estado verificado.

## Corrección posterior

Durante la implementación, los incrementos 3 y 4 refinaron dos áreas de este diseño y
tienen sus propios documentos, que prevalecen sobre el esquema de arriba:

- **Interpretación de teclas y layout seguro** —
  [`2026-09-07-terminal-tui-key-interpretation-and-safe-layout-design.md`](2026-09-07-terminal-tui-key-interpretation-and-safe-layout-design.md).
  `Escape` sobre un prompt entrega una línea vacía (`SubmitEmpty`), no cierra el canal;
  `Ctrl+C` como tecla cancela la aplicación (`CancelApplication`) por una ruta distinta
  al cierre del canal (`CloseChannel`); el layout compacto usa umbrales explícitos
  (`≥ 40×8`, `20×6 ≤ tamaño < 40×8`, `< 20×6`) con prioridad de retención
  «confirmación > input > aviso de tamaño > error > estado > transcript».
- **Presupuesto acotado del transcript** —
  [`2026-09-08-terminal-tui-transcript-budget-design.md`](2026-09-08-terminal-tui-transcript-budget-design.md).
  El presupuesto de líneas es de **líneas de referencia a ancho 40**, no de líneas
  envueltas al ancho actual (esa variante era destructiva al reducir la ventana). La
  retención ocurre solo en `Add`; la consulta de render (`CreateView`) es pura y
  materializa únicamente la ventana visible.

## Fuera de alcance

- Cambios en contratos HTTP, autenticación o autorización.
- Nuevos comandos o un parser específico para la TUI.
- Historial de comandos, selección con ratón o atajos sensibles.
- Toolkit TUI externo, migración de framework o API pública de presentación.
- Audio, TTS, ondas, animaciones o activación de `PlayingVoice`.
