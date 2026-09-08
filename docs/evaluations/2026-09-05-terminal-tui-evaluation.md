# Evaluación de TUI terminal — 2026-09-05

## Decisión

Se adopta un renderizador propio mínimo para el cliente `net8.0`. Conserva el modo
textual como referencia y fallback. No se añade dependencia de TUI en este incremento.

## Evidencia

| Alternativa | Resultado |
| --- | --- |
| Spectre.Console | Descartada. Su documentación de Live Display no admite combinar de forma segura el renderizado vivo con componentes interactivos; el cliente necesita ambas cosas. |
| Terminal.Gui v2 | Descartada para esta versión. Un prototipo `net8.0` con Terminal.Gui 2.1.0 falló en restore: el paquete actualmente publicado exige `net10.0`. La estable actual 2.4.17 también exige .NET 10. No se acepta una versión sin listar ni se eleva el framework sin autorización explícita. |
| Renderizador propio mínimo | Elegida. Un bucle dedicado procesa teclado no bloqueante, resize y snapshots mientras `TerminalClientApplication` espera entrada en su worker. No hay ANSI en redirección porque esta ruta no se inicia fuera de una terminal interactiva. |

## Trade-offs

El renderizador propio tiene menor coste de dependencia y mantiene `net8.0`, pero ofrece
solo el layout, transcript y scroll mínimos de este incremento. No sustituye un toolkit
general. La capa de entrada y snapshots queda separada para poder reevaluar una TUI con
dependencia cuando el framework del cliente cambie con una decisión explícita.

## Comprobación manual pendiente de cierre

Las pruebas automáticas del driver falso cubren cierre persistente del canal de entrada,
EOF, `Ctrl+D`, `Ctrl+Z`, resize, viewport, scroll, frames prioritarios, normalización y
límites del transcript. Sigue pendiente la comprobación manual en Windows Terminal +
PowerShell, con una API local configurada y una credencial de prueba:

1. Ejecutar el cliente sin `--plain`, reducir la ventana por debajo de 40×8 y ampliarla,
   y comprobar que la vista compacta conserva input y condición operativa, y que el
   estado, transcript e input vuelven a ser utilizables al recuperar tamaño.
2. Pegar una frase con `ñ`, tildes y signos de apertura; verificar que llega como una
   línea completa y que el transcript conserva los caracteres.
3. Solicitar una tool que requiera confirmación y comprobar que `approve`, `reject` y
   `cancel` la resuelven (`cancel` como rechazo, liberando la confirmación en el
   servidor) y que otras palabras vuelven a pedir la decisión. Enviar el mismo mensaje
   otra vez en la misma conversación debe volver a pedir confirmación, no repetir la
   respuesta anterior.
4. Abrir pairing, rotación o revocación y comprobar que el valor secreto aparece
   enmascarado y no queda en el transcript tras enviar o cancelar.
5. Pulsar `Ctrl+C`, `Ctrl+Z` con la entrada vacía y, en otra ejecución, usar `/exit`;
   comprobar que se restaura el cursor y la consola acepta el siguiente comando de
   PowerShell.
6. Ejecutar con `--plain` y con salida redirigida; comprobar que se conserva el flujo
   textual y que el archivo resultante no contiene secuencias ANSI.

## Comprobación manual — 2026-09-06

Equipo: Windows 11 / build 22621, Windows Terminal 1.24.11911.0, PowerShell 5.1.26100.9278.
API local en http://localhost:5100.
Modelo Ollama: no aplica (proveedor fake) / Qwen3.5:9b para los turnos de T11.

### Preflight (incremento 2)
- T1 TUI interactiva: layout mostrado, credencial enmascarada, /exit ExitCode=0, cursor restaurado — OK
- T2 `--plain`: modo texto, sin limpiar pantalla, sin aviso de fallback — OK
- T3 salida redirigida: modo texto (`redirected`), `salida.txt` sin secuencias ANSI — OK
- T4 <40x8: aviso `terminal_too_small` (motivo estable, sin excepción), fallback textual, cursor restaurado tras la sonda — OK

### Restauración de consola (correcciones gap 1 y gap 2)
- T5 `/exit`: ExitCode=0, CursorVisible=True, consola usable — OK
- T6 `Ctrl+C`: cierre limpio, ExitCode=0 (2 solo al interrumpir una operación en curso), cursor restaurado. — OK
- T7 `Ctrl+Z`+Enter con input vacío: cierra como EOF, cursor restaurado — OK
- T8 `Ctrl+Z`/`Ctrl+D` con texto pendiente: se ignoran; cierran solo con buffer vacío — OK
- T9 health falla tras elegir TUI: consola restaurada, ExitCode≠0, sin fallback textual, motivo seguro — OK

### Pase funcional TUI
- T10 resize en caliente: vista compacta prioritaria y recuperación al ampliar — OK
- T11 unicode pegado: una sola línea, caracteres conservados — OK
- T12 confirmación: approve/reject/cancel la resuelven (cancel como rechazo, libera la confirmación en el servidor); otras palabras vuelven a pedir decisión; reenviar el mismo mensaje vuelve a pedir confirmación — OK
- T13 secreto: entrada enmascarada, ausente del transcript tras enviar/cancelar — OK

### Residuos
- Sin procesos `testhost` / `LocalAssistant.Api` tras cerrar — OK

### Hallazgos corregidos durante la verificación

Todos con test de regresión; suite completa 478/478.

- **Preflight sin restauración ante excepción inesperada (gap 1).** El selector solo
  restauraba ante `IOException`/`InvalidOperationException`/`PlatformNotSupportedException`;
  una excepción distinta se propagaba sin restaurar aunque la sonda ya hubiera ocultado
  el cursor. Ahora restaura una vez y relanza.
- **Ventana sin propietario de restauración antes del host TUI (gap 2).** Un fallo al
  construir el grafo de presentación (HttpClient, consola, sink, aplicación) no entraba
  en el `finally` del host. Salvaguarda idempotente en el composition root.
- **Prompts de entrada consecutivos no se repintaban (host TUI).** Tras `ReadLine` →
  `ReadSecret` sin snapshot ni transcript intermedios, el segundo prompt no aparecía
  hasta pulsar una tecla. `DetectInputRequestChange` en el bucle.
- **Escenarios fake `reminder`/`temperature`.** `dueAtUtc` hardcodeado y caducado
  (`create_reminder` lo rechaza); y el segundo turno en la misma conversación repetía
  el resultado de tool del turno anterior en vez de volver a pedirlo. Fecha dinámica +
  búsqueda del resultado acotada al turno actual.
- **`cancel` de confirmación desincronizaba cliente/servidor.** Era un fallo solo local:
  la confirmación seguía pendiente y bloqueaba el siguiente turno (`confirmation_pending`).
  Ahora `cancel` envía la decisión al servidor como rechazo (Opción A).

Conclusión: incremento 2 verificado manualmente.
Notas: los motivos de fallback exóticos (`tui_initialization_failed`, `unsupported_terminal`)
no son reproducibles en Windows Terminal + PowerShell y quedan cubiertos por pruebas unitarias.

## Comprobación manual — 2026-09-08 (incremento 3: interpretación de teclas y layout seguro)

Equipo: Windows 11, Windows Terminal + PowerShell. API local con persistencia y
`--provider=fake`/`--provider=ollama` según el caso. Suite completa 525/525.

### FASE 1 — Interpretación de teclas
- T1 flechas `↑`/`↓` no hacen nada (reservadas en el incremento 3) — OK
- T2 `PageUp`/`PageDown` desplazan el transcript con `You:` y `State:` fijos abajo; `PageDown` responde tras muchos `PageUp` — OK
- T3 `Backspace` acorta la línea; sin efecto con la línea vacía — OK
- T4 `Escape` vacía la línea sin cerrar el cliente — OK
- T5 `Ctrl+D`/`Ctrl+Z` con texto se ignoran; con la línea vacía cierran limpio — OK
- T6 `Ctrl+C` durante la conexión cancela la ejecución, `ExitCode` ≠ 0, cursor restaurado — OK

### FASE 2 — Entrada anticipada
- Texto escrito sin una solicitud activa no se asocia al siguiente prompt; el input aparece vacío al publicarse la confirmación — OK

### FASE 3 — La confirmación solo se resuelve con `approve`/`reject`
- `approve`/`reject` (y `cancel` como rechazo) resuelven; palabras sueltas vuelven a pedir decisión — OK
- `PageUp`/`PageDown`, flechas, `Ctrl+Z` y `Ctrl+C` con confirmación pendiente **no** la aprueban — OK

### FASE 4 — Layout seguro
- T7 compacto `30×6` con confirmación pendiente: se conservan `CONFIRM:` e input y aparece el aviso de ampliar; input adyacente al borde inferior — OK
- T8 terminal `12×3` con un mensaje: sin cuelgue, sin desbordar el ancho, sin secuencias ANSI — OK
- T9 entrada de ~50 caracteres a `30` de ancho: muestra `…` y el extremo activo; `Backspace` sobre el final — OK
- T10 secreto largo (`/admin rotate`) con resize arriba/abajo: solo asteriscos del extremo, nunca el valor en claro — OK

Conclusión: incremento 3 verificado manualmente.

### Hallazgo pendiente (cambio aparte)

**EOF/`Ctrl+C` en el prompt de confirmación deja la confirmación pendiente en el
servidor.** `Ctrl+Z`/`Ctrl+D`/`Ctrl+C` en `Type approve, reject, or cancel:` cierran el
cliente sin avisar al servidor; la confirmación sigue pendiente ~5 min
(`ConfirmationTimeout`) y el siguiente mensaje de esa conversación devuelve
`confirmation_pending`. Recuperación: `/new`, `/exit` o esperar la caducidad. La
invariante de seguridad es correcta (EOF/`Ctrl+C` nunca aprueban); falta la limpieza
en el servidor. Arreglo propuesto en `TerminalClientApplication.ResolveConfirmationAsync`
(EOF → `reject` al servidor; `Ctrl+C` → dejar caducar), en su propio commit por la
restricción del plan padre sobre `TerminalClientApplication`.

### Correcciones de revisión posteriores al pase manual

Suite completa tras las correcciones: 527/527. Detectadas en revisión del código
commiteado:

- **El prompt desaparecía en modo compacto.** `FitInputLine` devolvía una línea vacía
  cuando el prompt era más largo que el ancho y el buffer estaba vacío (los prompts de
  client id, desafío de pairing y confirmación superan 30 caracteres). Ahora muestra la
  cabeza reconocible del prompt con el buffer vacío y el extremo activo del valor al
  escribir. Pruebas a anchuras 20 y 30, normal y secreto.
- **Un snapshot prioritario podía renderizarse con el tamaño anterior.** `Render`
  usaba `_lastSize` cacheado; un snapshot drenado tras un resize y antes de
  `DetectResize` producía líneas más anchas que la terminal. Ahora `Render` consulta
  siempre el tamaño vigente. Prueba que reduce el tamaño y publica confirmación y error
  a la vez.
- **Las pruebas del host dependían de `Task.Delay`.** El driver falso pasa a ofrecer
  señales observables (`WaitForFrameAsync`, `WhenInputChangedAsync`) y estructura
  interna con bloqueo; los timeouts se conservan solo como protección contra cuelgues.

Pendiente de re-verificación manual: **T7** con un prompt más largo que el ancho en
modo compacto (p. ej. `/admin rotate` a `mode con: cols=30`); en el pase anterior solo
se probó con `You:`, que no dispara el fallo del prompt invisible.
