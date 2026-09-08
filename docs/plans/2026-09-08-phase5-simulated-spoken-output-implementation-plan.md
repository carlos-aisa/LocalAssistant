# Plan de implementación: incremento 6 de fase 5 — salida hablada simulada

## Alcance confirmado

Implementar el plano local e intercambiable de salida hablada descrito en
`docs/specs/2026-09-08-phase5-simulated-spoken-output-design.md`. El cliente podrá
recorrer síntesis y reproducción mediante dobles deterministas, expondrá la capacidad
de salida en sus snapshots y activará `PlayingVoice` únicamente durante reproducción
efectiva. La composición normal seguirá declarando la capacidad como no disponible.

No se seleccionará ni instalará un motor TTS, no habrá audio real, comandos de voz,
persistencia de preferencias, cambios HTTP/OpenAPI ni trabajo en el servidor.

## Suposiciones registradas

- `SpokenOutputPreferences` será un valor inmutable de sesión con solo `IsMuted`.
- `SpokenOutputAvailability` tendrá `Unavailable` y `Ready`.
- La disponibilidad de producción será `Unavailable`; no se añadirá una bandera para
  activar un simulador desde el ejecutable.
- La aplicación seguirá esperando la salida simulada antes de solicitar otra línea.
- La cancelación del token de aplicación se propagará y acabará en el cierre existente;
  no se convertirá en un error de voz ni en resultado incierto.
- La retención para `repeat`, la cancelación específica de reproducción y la lectura
  concurrente se reservan para el incremento 7.

## Paso 1 — Contratos y coordinador local de salida

**Archivos de producción**

- Añadir `src/LocalAssistant.TerminalClient/SpokenOutput.cs`.

**Responsabilidad**

1. Declarar los contratos internos e inmutables:

   - `SpokenOutputPreferences`;
   - `SpokenOutputAvailability`;
   - snapshot seguro de salida para integrar en el estado del cliente;
   - `SpeechSynthesisRequest`;
   - `SynthesizedSpeech`, con stream, tipo de medio y `IAsyncDisposable`;
   - `ISpeechSynthesizer`;
   - `ISpeechPlayer`;
   - `ISpokenOutputCoordinator`;
   - `IPreparedSpokenOutput`;
   - resultados tipados y seguros de preparación y reproducción.

2. Implementar un coordinador que reciba sintetizador, reproductor, disponibilidad y
   preferencias. `PrepareAsync` deberá:

   - devolver omisión normal si la salida no está disponible o está silenciada;
   - serializar preparaciones mediante una exclusión cancelable;
   - crear el artefacto por medio del sintetizador;
   - traducir excepciones no vinculadas a cancelación en fallo seguro de síntesis;
   - transferir la exclusión y propiedad del artefacto al objeto preparado.

3. Implementar el objeto preparado de modo que `PlayAsync` traduzca fallos de
   reproducción en resultados seguros. Su disposición deberá liberar exactamente una
   vez el stream y el turno exclusivo, incluso tras fallo o cancelación.

4. Implementar `UnavailableSpokenOutputCoordinator`. No llamará a sintetizador ni
   reproductor, no esperará y devolverá una omisión por falta de disponibilidad.

**Seguridad, cancelación y retención**

- No incluir excepciones, texto sintetizado, bytes, rutas, proveedor o dispositivo en
  resultados seguros.
- No crear archivos temporales. El artefacto de esta fase será una abstracción con
  ciclo de vida explícito; los dobles usarán memoria.
- No capturar una `OperationCanceledException` causada por el token de aplicación como
  un fallo de síntesis o reproducción; deberá conservar la ruta de cierre existente.
- Un segundo `PrepareAsync` esperará a que se libere el primero o cancelará con su
  propio token: nunca solapará reproducción ni reutilizará el artefacto.

**Pruebas**

- Añadir `tests/LocalAssistant.Tests/TerminalClient/SpokenOutputTests.cs` con dobles
  simples de sintetizador, reproductor y artefacto.
- Verificar omisión por indisponibilidad y silencio sin invocaciones downstream.
- Verificar una preparación y reproducción exactamente una vez.
- Verificar que dos solicitudes concurrentes no se solapan.
- Verificar que fallo de síntesis no invoca el reproductor.
- Verificar fallo de reproducción, cancelación durante síntesis y cancelación durante
  reproducción.
- Verificar disposición exacta tras éxito, fallo y cancelación.
- Verificar que los resultados y sus propiedades públicas no exponen contenido,
  bytes, rutas ni excepciones.

## Paso 2 — Estado observable y grafo de transiciones

**Archivos de producción**

- Modificar `src/LocalAssistant.TerminalClient/TerminalClientState.cs`.

**Responsabilidad**

1. Incorporar el snapshot seguro de salida como parte de
   `TerminalClientStateSnapshot`, con un valor inicial explícito (`Unavailable`, no
   silenciado).
2. Extender la validación completa de snapshot:

   - `PlayingVoice` exige `Lifecycle = Ready`, salida `Ready` y `IsMuted = false`;
   - los snapshots con salida no disponible o silenciada no pueden usar
     `PlayingVoice`;
   - el contexto seguro de salida puede cambiar observablemente mientras el cliente
     está listo sin romper las demás invariantes.
3. Extender el grafo únicamente con:

   - `SendingTurn -> PlayingVoice`;
   - `ResolvingConfirmation -> PlayingVoice`;
   - `PlayingVoice -> None`;
   - la transición general ya existente hacia `Closing`.

   Permanecerán prohibidos `None -> PlayingVoice`,
   `AwaitingConfirmation -> PlayingVoice` y los orígenes no declarados.

**Pruebas**

- Modificar `tests/LocalAssistant.Tests/TerminalClient/TerminalClientStateTests.cs`.
- Sustituir la prueba que prohíbe por completo `PlayingVoice` por la tabla aprobada.
- Verificar las dos rutas permitidas y todos los orígenes prohibidos relevantes.
- Verificar las combinaciones de snapshot inválidas por disponibilidad o silencio.
- Verificar publicaciones sin duplicados al cambiar disponibilidad, silencio y
  actividad, y mantener la prueba de aislamiento del sink.
- Ampliar la comprobación de contrato seguro para que el nuevo DTO no exponga
  contenido, audio, ruta, token, credencial o desafío.

## Paso 3 — Integración en `TerminalClientApplication`

**Archivos de producción**

- Modificar `src/LocalAssistant.TerminalClient/TerminalClientApplication.cs`.
- Modificar `src/LocalAssistant.TerminalClient/Program.cs`.

**Responsabilidad**

1. Inyectar el coordinador de salida y las preferencias como dependencias internas,
   conservando los constructores públicos existentes mediante valores por defecto no
   disponibles. La aplicación será la única que llame a `MoveTo` y publique snapshots.
2. Inicializar y conservar el contexto de salida en los snapshots de arranque,
   autenticación, listo, actividad, error, cierre y final. Ninguna transición podrá
   descartar accidentalmente la disponibilidad o el silencio.
3. Extraer una operación privada que reciba una `ConversationResponse` ya presentada
   y ejecute salida solo cuando sea elegible:

   - `Content` no vacío;
   - sin confirmación pendiente;
   - sin error conversacional;
   - procedente de completar un turno o de la última resolución de confirmación.

4. Llamar a esa operación después de escribir la respuesta pública y antes de volver a
   solicitar entrada. Los historiales, selectores, prompts, herramientas, errores y
   respuestas que dejan una confirmación pendiente no deberán llegar al coordinador.
5. Tras una preparación correcta, publicar `PlayingVoice` y llamar a `PlayAsync`.
   Garantizar la disposición del objeto preparado con `await using` o `finally`.
   Tras completar, volver a `Ready/None`.
6. Traducir fallos tipados a `speech_synthesis_failed` o `speech_playback_failed`,
   con operación `speech_output`, gravedad recuperable y `IsUncertain = false`.
   El error deberá sobrevivir a la operación que lo generó; no se repetirá el turno ni
   se reescribirá conversación, bearer o credencial.
7. Propagar cancelación de aplicación durante preparación o reproducción a la ruta
   existente `finally` de cierre. Actualizar `HandleCancellation` si hace falta para
   que una cancelación de voz sea local, conocida y no sustituya un error incierto
   previo de la conversación.
8. Componer `UnavailableSpokenOutputCoordinator` en modos plain y TUI. No introducir
   opciones de línea de comandos ni dependencias de motor.

**Pruebas**

- Modificar `tests/LocalAssistant.Tests/TerminalClient/TerminalClientApplicationTests.cs`.
- Agregar dobles de salida deterministas e inyectarlos por el constructor interno.
- Verificar respuesta final ordinaria: contenido público antes del inicio de
  reproducción y secuencia `SendingTurn -> PlayingVoice -> Ready/None`.
- Verificar respuesta final tras resolver la última confirmación:
  `ResolvingConfirmation -> PlayingVoice -> Ready/None`.
- Verificar exclusión de confirmación pendiente, error conversacional, contenido vacío,
  historial, listado y mensajes operativos.
- Verificar fallos de síntesis y reproducción: error seguro recuperable, texto visible,
  continuidad del siguiente turno y conservación de proveedor, conversación, bearer y
  credencial.
- Verificar que el error de voz no desaparece inmediatamente y que un éxito posterior
  aplica la política existente de limpieza.
- Verificar cancelación real con el mismo `CancellationToken` durante cada etapa y la
  secuencia `Closing -> Closed`, sin incertidumbre.
- Verificar que la composición de producción mantiene salida `Unavailable` y no
  publica `PlayingVoice`.

## Paso 4 — Presentación accesible del estado de salida

**Archivos de producción**

- Modificar `src/LocalAssistant.TerminalClient/TerminalClientStateTextSink.cs`.
- Modificar `src/LocalAssistant.TerminalClient/TerminalClientTui.cs`.

**Responsabilidad**

1. Extender el sink textual con una etiqueta breve para cambios reales de
   disponibilidad o silencio, sin repetirla cuando el valor no cambia y sin imprimir
   contenido conversacional.
2. Extender la línea de estado de la TUI para incluir salida no disponible, disponible,
   silenciada o en reproducción. `PlayingVoice` seguirá procediendo de la actividad,
   no de una animación ni un temporizador.
3. Conservar las garantías existentes de transcript seguro, redimensionado, prioridad
   de confirmación, entrada secreta y ausencia de ANSI cuando la salida está
   redirigida.

**Pruebas**

- Modificar `tests/LocalAssistant.Tests/TerminalClient/TerminalClientTuiTests.cs` y
  los tests del sink textual existentes o añadir un archivo específico si mejora la
  legibilidad.
- Verificar etiquetas comprensibles sin color para cada estado de salida y para
  `PlayingVoice`.
- Verificar que actualizaciones de estado no duplican la respuesta del asistente.
- Verificar que texto controlado por servidor no aparece como dato de salida ni
  introduce secuencias ANSI en la presentación redirigida.

## Paso 5 — Documentación de comportamiento entregado

**Archivos de documentación**

- Modificar `README.md` si describe el cliente terminal y su comportamiento de voz.
- Modificar `ARCHITECTURE.md` para reflejar la frontera local y los contratos sin
  presentar un motor real como implementado.
- Modificar `SECURITY.md` para precisar que este incremento no envía audio, no retiene
  artefactos y no muestra contenido en snapshots o errores.
- Modificar `docs/ROADMAP.md` para marcar el punto 6 solo cuando los pasos y la
  verificación estén completos.
- Mantener como referencia el diseño y este plan; no crear ADR porque la frontera
  arquitectónica ya está decidida en ADR 0034.

**Comprobaciones documentales**

- Diferenciar simulación determinista actual de TTS real pendiente del incremento 7.
- No afirmar que `mute`, `stop` o `repeat` existen todavía.
- No afirmar que hay audio persistido, SDK de TTS, endpoint de audio o cancelación de
  turnos HTTP.

## Paso 6 — Verificación final

Ejecutar desde la raíz, en este orden:

```powershell
dotnet format LocalAssistant.sln --verify-no-changes
dotnet build LocalAssistant.sln -c Release --no-restore
dotnet test tests/LocalAssistant.Tests/LocalAssistant.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~TerminalClient"
dotnet test tests/LocalAssistant.Tests/LocalAssistant.Tests.csproj -c Release --no-build
git diff --check
```

Antes de entregar, comprobar que no queden procesos `testhost`, `dotnet` de pruebas o
instancias API iniciadas por la suite. Revisar el diff completo por contenido,
credenciales, tokens, desafíos, audio o artefactos generados antes de solicitar commit.

## Criterio de cierre

El incremento queda listo para revisión solo si los contratos son intercambiables, la
salida normal de producción permanece no disponible, las dos rutas de `PlayingVoice`
son las únicas permitidas, los fallos locales son recuperables y conocidos, los
recursos se liberan, la presentación es accesible y toda la matriz de pruebas anterior
pasa sin red, reloj, motor de audio ni terminal real.
