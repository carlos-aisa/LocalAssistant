# Diseño del incremento 6 de la fase 5: salida hablada intercambiable simulada

## Estado y propósito

Diseño aprobado para el incremento 6 de la fase 5. Este incremento incorpora al
cliente terminal el plano local de salida hablada mediante contratos intercambiables
y dobles deterministas, pero no selecciona, instala ni ejecuta un motor TTS real.

El objetivo es demostrar que una respuesta textual final puede atravesar de forma
segura el ciclo de síntesis y reproducción, que ese ciclo queda representado en el
estado observable y que un fallo local de voz nunca altera el resultado de la
conversación. La composición de producción permanecerá sin salida hablada disponible
hasta la evaluación acotada del incremento 7.

## Alcance

El incremento incluye:

- contratos internos de síntesis, artefacto de audio, reproducción y coordinación;
- una preferencia inmutable de sesión para silenciar la salida;
- disponibilidad de salida y silencio como contexto seguro del snapshot;
- integración de respuestas finales elegibles en `TerminalClientApplication`;
- activación honesta de `PlayingVoice` únicamente durante reproducción efectiva;
- liberación determinista de cualquier artefacto producido;
- fallos locales recuperables con mensajes seguros;
- presentación textual y TUI del estado de salida mediante etiquetas;
- dobles de síntesis y reproducción y pruebas deterministas del flujo completo.

El incremento no incluye:

- un motor TTS, SDK, proceso externo o dependencia de audio real;
- selección de voz, velocidad, volumen o rangos dependientes de un motor;
- comandos `mute`, `unmute`, `stop` o `repeat`;
- persistencia de preferencias;
- reproducción simultánea con lectura interactiva;
- archivos temporales de audio en producción;
- entrada de audio, micrófono, STT, wake word o transporte multimedia;
- cambios en la API HTTP, OpenAPI, el orquestador o la persistencia del servidor.

## Decisiones de arquitectura

### Límite local e interno

La salida hablada pertenece exclusivamente al proceso
`LocalAssistant.TerminalClient`. Sus contratos serán internos y el proyecto no
referenciará servicios ni contratos internos del servidor. La API continuará
transportando únicamente texto y control conversacional.

`TerminalClientApplication` seguirá siendo la única autoridad del estado operacional.
Decidirá si una respuesta es elegible, publicará las transiciones y tratará el
resultado del plano hablado. El coordinador de salida no recibirá el coordinador de
estado ni publicará snapshots por sí mismo.

La composición normal inyectará una implementación que declara la salida hablada como
no disponible. Esta implementación no sintetizará, no esperará artificialmente, no
reproducirá y no activará `PlayingVoice`. Las pruebas inyectarán dobles deterministas
para ejercitar el mismo flujo de aplicación sin fingir actividad en producción.

### Contratos

Los contratos internos serán:

- `SpokenOutputPreferences`: valor inmutable de sesión que contiene `IsMuted`.
- `SpokenOutputAvailability`: distingue al menos `Unavailable` y `Ready`.
- `SpeechSynthesisRequest`: contiene el texto elegible y la preferencia aplicable.
- `SynthesizedSpeech`: artefacto de audio reproducible, con contenido en stream y
  tipo de medio, cuyo ciclo de vida es explícito y asíncronamente liberable.
- `ISpeechSynthesizer`: transforma una solicitud en un artefacto sintetizado.
- `ISpeechPlayer`: reproduce un artefacto y observa cancelación.
- `ISpokenOutputCoordinator.PrepareAsync`: impide solapamientos, evalúa
  disponibilidad y silencio, ejecuta la síntesis y devuelve un resultado tipado.
- `IPreparedSpokenOutput`: representa exclusivamente una preparación correcta,
  permite ejecutar `PlayAsync` y posee la liberación del artefacto y del turno
  exclusivo del coordinador.
- una implementación no disponible para la composición de producción del incremento.

El artefacto no expondrá rutas de archivos ni se incorporará a snapshots, transcript,
errores o diagnósticos. El coordinador será propietario del artefacto desde que la
síntesis lo entregue y lo liberará en un `finally` tras éxito, fallo o cancelación.

El resultado de `PrepareAsync` distinguirá, sin excepciones de proveedor expuestas:

- operación omitida por falta de disponibilidad;
- operación omitida por silencio;
- preparación correcta, que entrega un `IPreparedSpokenOutput`;
- fallo de síntesis;
- cancelación local.

`IPreparedSpokenOutput.PlayAsync` distinguirá reproducción completada, fallo de
reproducción y cancelación local. La aplicación publicará `PlayingVoice` justo antes
de invocarlo. El objeto preparado retendrá el turno exclusivo del coordinador hasta
su liberación; una segunda preparación esperará de forma cancelable y nunca se
solapará con la primera. Esta separación permite representar con honestidad el inicio
de reproducción sin entregar al coordinador autoridad sobre el estado ni callbacks
capaces de iniciar transiciones.

### Preferencias y disponibilidad

La única preferencia del incremento es `IsMuted` y vive en memoria durante la sesión.
No se guarda junto a la credencial DPAPI ni en otro archivo. La disponibilidad es una
capacidad del plano de salida, no una preferencia: un motor ausente se representa como
`Unavailable`, no como si el usuario hubiese silenciado un motor disponible.

No se fijan todavía identificadores de voz ni semánticas o rangos de velocidad y
volumen. El incremento 7 los definirá después de evaluar motores reales. Los contratos
de síntesis, reproducción y coordinación no deberán necesitar cambios para añadir
esas opciones al valor de preferencias.

El snapshot incorporará contexto seguro equivalente a:

```text
SpokenOutput:
  Availability: Unavailable | Ready
  IsMuted: boolean
```

Este contexto es inmutable. No contiene texto, artefactos, rutas, nombre de motor ni
detalles del dispositivo. La actividad existente `PlayingVoice` representa únicamente
reproducción efectiva.

## Elegibilidad del contenido

Una respuesta entra en salida hablada solo si cumple simultáneamente estas condiciones:

1. procede de completar un turno nuevo o de resolver su última confirmación;
2. contiene `Content` no vacío ni compuesto solo por espacios;
3. no contiene una confirmación pendiente;
4. no contiene un error conversacional.

El texto se presenta siempre antes de iniciar la salida hablada. No se sintetizan:

- mensajes de usuario;
- historial cargado al reanudar o seleccionar una conversación;
- listados, títulos o metadatos de conversaciones;
- prompts y ayuda del cliente;
- nombres, argumentos, resultados o resúmenes de herramientas;
- confirmaciones pendientes;
- errores conversacionales u operativos;
- credenciales, tokens, desafíos o cualquier entrada secreta.

El coordinador no conservará en este incremento la última respuesta. El incremento 7
definirá la retención necesaria y el comportamiento de `repeat` junto con el motor
elegido. Volver a entregar un texto al coordinador seguirá siendo posible sin cambiar
sus fronteras principales.

## Flujo y transiciones

La salida será secuencial en este incremento. Mientras se prepara o reproduce, la
aplicación no solicitará la siguiente línea. La lectura concurrente necesaria para
usar `/stop` durante una reproducción pertenece al incremento 7.

| Origen | Condición | Destino |
| --- | --- | --- |
| `SendingTurn` | Respuesta no elegible, salida no disponible o silenciada | `Ready/None` |
| `SendingTurn` | Síntesis completada y comienza reproducción | `Ready/PlayingVoice` |
| `ResolvingConfirmation` | Respuesta no elegible, salida no disponible o silenciada | `Ready/None` |
| `ResolvingConfirmation` | Síntesis completada y comienza reproducción | `Ready/PlayingVoice` |
| `PlayingVoice` | Reproducción completada | `Ready/None` |
| `SendingTurn` o `ResolvingConfirmation` | Fallo de síntesis | `Ready/None` con error recuperable |
| `PlayingVoice` | Fallo de reproducción | `Ready/None` con error recuperable |
| `PlayingVoice` | Cancelación controlada del proceso | `Closing`, después `Closed` |

La síntesis mantiene la actividad de origen. `PlayingVoice` comienza solamente cuando
existe un artefacto válido y el reproductor va a consumirlo. No se emplean retardos,
ondas, temporizadores ni estados decorativos para simular reproducción.

El grafo rechazará `Ready/None -> Ready/PlayingVoice` y cualquier entrada en
`PlayingVoice` desde una actividad distinta de `SendingTurn` o
`ResolvingConfirmation`. Un snapshot con `PlayingVoice` será inválido si la salida no
está disponible o está silenciada. Desde `PlayingVoice` se permitirán la vuelta a
`Ready/None` y el cierre controlado ya admitido por el ciclo de vida.

## Errores y cancelación

Los fallos de salida son locales. La respuesta textual y sus efectos conversacionales
ya son válidos, por lo que un fallo de voz:

- no modifica `ConversationId`, proveedor, bearer o credencial;
- no repite el turno ni llama de nuevo a la API;
- no se clasifica como resultado incierto;
- no bloquea posteriores mensajes;
- conserva visible la respuesta textual;
- deja un error recuperable hasta que una operación posterior termine correctamente.

Los códigos seguros serán al menos:

- `speech_synthesis_failed`, con operación `speech_output`;
- `speech_playback_failed`, con operación `speech_output`.

Los mensajes no incluirán el texto de entrada, bytes, formato, ruta, nombre de motor,
mensaje de excepción ni información del dispositivo. Las excepciones concretas se
capturarán en la frontera correspondiente y podrán conservarse solo como detalle
interno no publicado si resulta necesario para diagnóstico futuro.

La cancelación del token de la aplicación durante síntesis o reproducción detendrá el
trabajo local y continuará por la ruta global `Closing -> Closed` garantizada por
`finally`. Es un resultado conocido y local, nunca incierto. La cancelación específica
de reproducción que necesitará el futuro comando `stop` se mantendrá separada de la
cancelación del proceso; su contrato quedará preparado, pero el comando y la lectura
concurrente no se activan todavía.

## Presentación

El modo textual y la TUI mostrarán disponibilidad, silencio y actividad con etiquetas
estables y comprensibles sin color. No mostrarán progreso inventado ni duplicarán la
respuesta conversacional. El renderer continuará consumiendo snapshots completos; no
reconstruirá el estado a partir de eventos ni inspeccionará mensajes para decidir si
deben hablarse.

En salida redirigida se conservará el comportamiento textual sin secuencias ANSI. La
incorporación del contexto de voz no debilitará el presupuesto de transcript, las
reglas de resize ni la restauración del terminal del incremento 5.

## Invariantes

- `TerminalClientApplication` es la única autoridad del snapshot.
- El texto final se hace visible antes de cualquier reproducción.
- Solo existe una operación de salida hablada a la vez.
- `PlayingVoice` significa reproducción efectiva.
- Toda actividad hablada vuelve a `Ready/None` o entra en el cierre controlado.
- Todo artefacto sintetizado se libera exactamente una vez.
- Disponibilidad ausente o silencio son resultados normales, no errores.
- Un fallo local de voz no cambia el resultado ni la certeza del turno HTTP.
- El error producido no se limpia como parte de la misma operación fallida.
- Ningún snapshot, error, log o diagnóstico contiene contenido hablado o audio.
- La composición de producción del incremento no activa salida ni tiempos simulados.

## Estrategia de pruebas obligatoria

### Contratos, coordinación y recursos

- Salida no disponible no invoca sintetizador ni reproductor.
- Salida silenciada no invoca sintetizador ni reproductor.
- Una solicitud elegible sintetiza y reproduce exactamente una vez.
- Dos solicitudes concurrentes no se solapan.
- Un fallo de síntesis no invoca el reproductor.
- Un fallo de reproducción no altera el resultado textual.
- El artefacto se libera exactamente una vez tras éxito, fallo y cancelación.
- La cancelación llega al sintetizador y al reproductor según la etapa activa.
- Un resultado o error publicado no contiene texto, bytes ni detalles de excepción.

### Coordinador de estado

- Acepta `SendingTurn -> PlayingVoice -> Ready/None`.
- Acepta `ResolvingConfirmation -> PlayingVoice -> Ready/None`.
- Rechaza `Ready/None -> PlayingVoice`.
- Rechaza `PlayingVoice` si la salida está ausente o silenciada.
- Publica cambios distintos de disponibilidad, silencio y reproducción sin duplicados.
- Conserva el estado correcto si el sink lanza una excepción.
- Permite `PlayingVoice -> Closing -> Closed` ante cancelación.

### Aplicación

- Una respuesta final ordinaria se imprime y después atraviesa la salida simulada.
- Una respuesta final posterior a la última confirmación sigue el mismo flujo.
- No se sintetizan respuestas con confirmación pendiente, error o contenido vacío.
- No se sintetizan historial, listados ni mensajes operativos.
- Un fallo de síntesis publica `speech_synthesis_failed`, vuelve a `Ready/None` y
  permite enviar otro mensaje.
- Un fallo de reproducción publica `speech_playback_failed`, vuelve a `Ready/None` y
  permite enviar otro mensaje.
- El error hablado no desaparece durante la misma operación que lo produjo y una
  operación posterior satisfactoria aplica la política general de limpieza.
- Cancelar durante síntesis o reproducción termina en `Closing -> Closed` sin
  incertidumbre.
- La composición normal permanece `Unavailable` y nunca publica `PlayingVoice`.
- Proveedor, conversación, bearer y credencial no cambian debido al plano local.

### Presentación y seguridad

- El modo textual representa salida no disponible, disponible, silenciada y en
  reproducción con etiquetas, sin color obligatorio.
- La TUI representa los mismos estados sin animaciones ni audio fingido.
- La salida redirigida no contiene secuencias ANSI.
- Snapshots, salida capturada y errores no contienen texto sintetizado, secretos,
  bytes ni rutas.
- Los dobles no crean archivos temporales ni dependen del reloj, red, audio o terminal
  reales.

## Verificación y cierre

Antes de marcar el punto 6 como completado deberán pasar:

1. formato del código afectado;
2. compilación `Release` con warnings como errores;
3. todas las pruebas específicas anteriores;
4. la suite completa del repositorio;
5. `git diff --check`;
6. revisión de que no quedan procesos de test o API residuales;
7. revisión de documentación para que `README.md`, `ARCHITECTURE.md`, `SECURITY.md` y
   `ROADMAP.md` describan exactamente el comportamiento entregado.

La demostración del incremento será automatizada mediante los dobles deterministas y
la secuencia observable de snapshots. No se añadirá una opción de producción para
fingir audio. El roadmap solo se marcará después de implementar y verificar todo el
alcance.

## Preparación explícita para el incremento 7

El siguiente incremento podrá sustituir las implementaciones inyectadas, ampliar el
valor de preferencias y añadir comandos sin mover la síntesis al servidor ni cambiar
el contrato conversacional. Deberá evaluar primero un máximo acotado de motores TTS y
resolver:

- motor local predeterminado y degradación segura;
- selección de voz y validación de velocidad y volumen;
- persistencia de preferencias;
- lectura concurrente para `stop`;
- retención mínima y privada necesaria para `repeat`;
- comandos `mute`, `unmute`, `stop` y `repeat`;
- política de recursos temporales del motor elegido;
- comprobación manual de audio en Windows.

Este diseño no prejuzga el motor, formato definitivo de audio, dispositivo de salida
ni estrategia de almacenamiento temporal del incremento 7.
