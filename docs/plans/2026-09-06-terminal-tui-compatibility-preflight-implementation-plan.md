# Plan de implementación del incremento 2: compatibilidad TUI previa al arranque

> **Estado: finalizado (2026-09-07).** Implementado y probado (suite completa
> 478/478), `dotnet format` y `dotnet build -c Release` sin cambios ni errores.
> Verificación manual en Windows Terminal + PowerShell registrada en
> `docs/evaluations/2026-09-05-terminal-tui-evaluation.md`. Los incrementos 3, 4 y 5
> del plan general siguen pendientes y el punto 5 de `ROADMAP.md` permanece desmarcado.

## Objetivo

Implementar el diseño aprobado en
`docs/specs/2026-09-06-terminal-tui-compatibility-preflight-design.md` para decidir
entre TUI y cliente textual antes de construir o ejecutar
`TerminalClientApplication`.

El incremento debe demostrar que la consola admite las operaciones reales que usa la
TUI, transferir al host el mismo driver que superó la comprobación y garantizar que
no se crea un segundo renderer después de comenzar la aplicación.

## Clasificación y límites

- **Backend/presentación de consola:** selector, driver y composition root del
  ejecutable.
- **Testing:** pruebas unitarias deterministas del selector, la frontera de consola y
  la composición.
- **Documentación:** este plan y, únicamente si el comportamiento final difiere, los
  documentos de evaluación de la TUI.
- **API y seguridad:** sin cambios en HTTP, OpenAPI, autenticación, autorización,
  credenciales ni persistencia.

No se introducirá una dependencia de terminal, un framework TUI ni una API pública.

## Estado de partida

El árbol actual contiene una implementación parcial:

- `TerminalPresentationSelector.Select` comprueba `--plain`, redirección, una marca
  genérica de compatibilidad, `TryInitialize` y tamaño;
- `TerminalPresentationDecision` no transporta el driver inicializado;
- `SystemTerminalDriver.TryInitialize` consulta tamaño y teclado, pero no demuestra
  que limpieza y escritura funcionen;
- `Program.Main` construye directamente ambas variantes y no ofrece una costura para
  demostrar el orden ni la ausencia de fallback tardío;
- las pruebas actuales cubren parte de la matriz del selector, pero no el driver real
  ni el composition root.

La implementación conservará las partes válidas y modificará solo lo necesario para
cerrar estos huecos.

## Paso 1: contrato de selección y propiedad del driver

**Archivos:**

- `src/LocalAssistant.TerminalClient/TerminalPresentationSelector.cs`
- `tests/LocalAssistant.Tests/TerminalClient/TerminalPresentationSelectorTests.cs`

### Cambios

1. Sustituir `SupportsInteractiveTui` por las capacidades observables necesarias:
   stdin, stdout y stderr redirigidos por separado. Ninguna propiedad declarará por sí
   sola que la TUI es compatible.
2. Hacer que el selector reciba una factory de `ITerminalDriver`. La factory solo se
   invocará después de descartar `--plain` y cualquier redirección.
3. Ampliar `TerminalPresentationDecision` con el driver comprobado y factorías o
   constructores internos que impidan combinaciones inválidas:
   - `Plain`: motivo seguro y `Driver == null`;
   - `Tui`: motivo `interactive_terminal` y driver no nulo.
4. Ejecutar `TryInitialize` y consultar el tamaño dentro de una única frontera de
   selección.
5. Ante `false`, tamaño inferior a `40×8` o excepción esperada, restaurar exactamente
   una vez y devolver modo textual.
6. No restaurar desde el selector cuando la decisión sea TUI: desde ese momento la
   restauración pertenece al `TerminalClientTuiHost`.
7. Mantener códigos de motivo cerrados y seguros. No incorporar mensajes de
   excepción.

### Tratamiento de errores

El selector traducirá únicamente `IOException`, `InvalidOperationException` y
`PlatformNotSupportedException` a incompatibilidad. Otras excepciones se propagarán
al tratamiento superior del ejecutable.

`Restore` será idempotente y absorberá solo los fallos esperados de consola. El
selector no reintentará la inicialización ni construirá un segundo driver.

### Pruebas obligatorias del selector

En `TerminalPresentationSelectorTests` se añadirán o ajustarán estos casos:

| ID | Escenario | Resultado observable requerido |
|---|---|---|
| `SEL-01` | `--plain` | `Plain/plain_requested`; factory de driver no invocada. |
| `SEL-02` | stdin redirigido | `Plain/redirected`; driver no creado. |
| `SEL-03` | stdout redirigido | `Plain/redirected`; driver no creado. |
| `SEL-04` | stderr redirigido | `Plain/redirected`; driver no creado. |
| `SEL-05` | tamaño `39×8` | `Plain/terminal_too_small`; una restauración. |
| `SEL-06` | tamaño `40×7` | `Plain/terminal_too_small`; una restauración. |
| `SEL-07` | tamaño `40×8` | `Tui/interactive_terminal`; conserva el mismo driver; el selector no restaura. |
| `SEL-08` | factory devuelve `null` | `Plain/unsupported_terminal`; no se intenta inicializar. |
| `SEL-09` | `TryInitialize` devuelve `false` | `Plain/tui_initialization_failed`; una restauración. |
| `SEL-10` | inicialización lanza `IOException` | Fallback textual y una restauración. |
| `SEL-11` | inicialización lanza `InvalidOperationException` | Fallback textual y una restauración. |
| `SEL-12` | inicialización lanza `PlatformNotSupportedException` | Fallback textual y una restauración. |
| `SEL-13` | consulta de tamaño lanza una excepción esperada | Fallback textual y una restauración. |
| `SEL-14` | inicialización lanza excepción no esperada | La excepción se propaga; no se presenta como incompatibilidad; si el driver ya existe se restaura exactamente una vez antes de propagar. |
| `SEL-15` | decisión textual | `Driver` es siempre `null`. |
| `SEL-16` | decisión TUI | No puede construirse sin driver. |

Los dobles registrarán creación, inicialización, tamaño y restauración. Los tests no
dependerán del orden de ejecución entre ellos ni de una terminal real.

## Paso 2: sonda real del driver mediante una frontera de consola

**Archivos:**

- `src/LocalAssistant.TerminalClient/TerminalDriver.cs`
- nuevo `tests/LocalAssistant.Tests/TerminalClient/TerminalDriverTests.cs`
- `tests/LocalAssistant.Tests/TerminalClient/TerminalClientTuiTests.cs`, únicamente
  para adaptar `FakeTerminalDriver` al contrato final si fuera necesario

### Cambios

1. Introducir en `TerminalDriver.cs` una frontera interna mínima sobre las operaciones
   estáticas de `Console` que ya utiliza el driver:
   - dimensiones;
   - disponibilidad de teclas;
   - `ReadKey` interceptado para el loop normal, no para la sonda;
   - visibilidad del cursor;
   - limpieza del frame;
   - escritura de líneas.
2. Mantener una implementación de producción que delegue directamente en `Console`.
   No trasladar a esta frontera la lógica de comandos, input semántico ni snapshots.
3. Inyectar la frontera en `SystemTerminalDriver`, conservando un constructor de
   producción sin parámetros y otro interno para tests.
4. Hacer que `TryInitialize` ejecute, en orden:
   - consulta de dimensiones;
   - consulta no consumidora de disponibilidad de teclado;
   - lectura y preparación reversible de la visibilidad del cursor cuando la
     plataforma lo admita;
   - limpieza y escritura de un frame inicial vacío y seguro.
5. No llamar a `ReadKey` durante la inicialización.
6. Conservar en el driver el estado mínimo necesario para que `Restore` sea
   idempotente y restaure una sola vez el cursor preparado.
7. Evitar que `GetSize` fabrique `80×24` durante la sonda. Un fallo de tamaño previo
   al arranque debe llegar al selector como incompatibilidad, no convertirse en un
   falso éxito. El fallback defensivo durante una sesión ya iniciada puede mantenerse
   si queda separado explícitamente de la sonda.
8. Mantener `Render` sanitizando cada línea y usando la misma frontera de consola.

### Seguridad y privacidad

El frame de comprobación contendrá únicamente una línea vacía o texto constante. No
accederá a transcript, snapshots, credenciales, desafíos ni entrada pendiente. La
sonda nunca registrará excepciones o propiedades de la terminal.

### Pruebas obligatorias del driver

`TerminalDriverTests` usará un doble simple de la frontera de consola:

| ID | Escenario | Resultado observable requerido |
|---|---|---|
| `DRV-01` | Todas las operaciones disponibles | Inicialización correcta; tamaño, teclado, cursor, limpieza y escritura fueron comprobados. |
| `DRV-02` | Falla la consulta de ancho | Excepción esperada visible para el selector; no se consume entrada. |
| `DRV-03` | Falla la consulta de alto | Mismo resultado que `DRV-02`. |
| `DRV-04` | Falla `KeyAvailable` | Inicialización fallida sin llamar a `ReadKey`. |
| `DRV-05` | Falla la preparación del cursor | Inicialización fallida y restauración posterior posible. |
| `DRV-06` | Falla `Clear` | Inicialización fallida antes de arrancar la aplicación. |
| `DRV-07` | Falla `WriteLine` | Inicialización fallida antes de arrancar la aplicación. |
| `DRV-08` | Sonda satisfactoria | Cero llamadas a `ReadKey`; ninguna tecla pendiente se descarta. |
| `DRV-09` | `Restore` llamado dos veces | El estado terminal se restaura como máximo una vez y no se lanza. |
| `DRV-10` | Restauración lanza excepción de consola esperada | El fallo se absorbe y no inicia otro renderer. |
| `DRV-11` | `Render` recibe contenido con controles | La frontera recibe únicamente texto normalizado. |
| `DRV-12` | Fallo de tamaño durante sesión | Se conserva el comportamiento defensivo documentado sin alterar la decisión inicial. |

Estas pruebas verifican llamadas observables a la frontera, no campos privados del
driver.

## Paso 3: composición previa al arranque y ausencia de fallback tardío

**Archivos:**

- `src/LocalAssistant.TerminalClient/Program.cs`
- nuevo `tests/LocalAssistant.Tests/TerminalClient/TerminalClientProgramTests.cs`

### Cambios

1. Mantener `Main` como frontera de proceso: parseo de opciones, creación del token,
   registro y retirada de `Console.CancelKeyPress`, códigos de salida y mensajes de
   error de nivel superior.
2. Extraer una función interna asíncrona de composición que reciba factories para:
   - capacidades;
   - driver;
   - ejecución textual;
   - ejecución TUI con el driver seleccionado;
   - escritura del aviso seguro de fallback.
3. Introducir una frontera interna mínima para registrar y retirar el handler de
   cancelación del proceso y escribir errores seguros. La implementación de producción
   delegará en `Console.CancelKeyPress` y `Console.Error`; el doble permitirá contar
   altas y bajas sin disparar eventos globales durante las pruebas.
4. Hacer que las factories construyan `TerminalClientApplication` únicamente después
   de recibir la decisión final. El `HttpClient` y el almacén de credenciales podrán
   seguir siendo dependencias locales del composition root, pero no se iniciará la
   aplicación durante la sonda.
5. En modo TUI, pasar al host exactamente `decision.Driver`; no crear otro
   `SystemTerminalDriver`. Envolver la construcción y ejecución TUI en un `try/finally`
   que llame a `Restore` sobre ese driver; al ser idempotente cubre un fallo previo a la
   entrada del host sin duplicar la restauración cuando el host ya la realizó.
6. En modo textual por incompatibilidad, escribir como máximo un aviso fijo y seguro.
   `--plain` y redirección no necesitan ruido adicional.
7. Tras invocar cualquiera de las dos factories de ejecución, no capturar fallos para
   probar el otro renderer. El error seguirá la ruta normal de cierre y código de
   salida.
8. Desregistrar siempre `Console.CancelKeyPress` mediante `finally`, incluso si falla
   la selección, construcción o ejecución.

La costura será `internal`, accesible a tests mediante el `InternalsVisibleTo`
existente. No se añadirá un proyecto ni un contenedor DI para esta composición.

### Pruebas obligatorias del composition root

`TerminalClientProgramTests` usará factories con contadores y resultados programados:

| ID | Escenario | Resultado observable requerido |
|---|---|---|
| `PRG-01` | `--plain` | Solo se construye y ejecuta la aplicación textual; driver y TUI permanecen sin invocar. |
| `PRG-02` | stream redirigido | Igual que `PRG-01`. |
| `PRG-03` | sonda incompatible | Driver se crea y restaura; después se ejecuta únicamente texto. |
| `PRG-04` | sonda compatible | Solo se construye y ejecuta TUI. |
| `PRG-05` | selección TUI | El host recibe por referencia el mismo driver comprobado. |
| `PRG-06` | ejecución textual satisfactoria | Factory y `RunAsync` se invocan exactamente una vez; se conserva el código de salida. |
| `PRG-07` | ejecución TUI satisfactoria | Factory y host se invocan exactamente una vez; se conserva el código de salida. |
| `PRG-08` | TUI falla después de comenzar | El error se propaga a la frontera superior; la factory textual nunca se invoca; la salvaguarda del composition root restaura el driver exactamente una vez. |
| `PRG-09` | texto falla después de comenzar | La factory TUI nunca se invoca. |
| `PRG-10` | fallback previo por excepción esperada | El aviso contiene solo el motivo estable, no el mensaje de excepción. |
| `PRG-11` | excepción inesperada durante selección | No se construye ninguna aplicación y se conserva el error de nivel superior. |
| `PRG-12` | salida normal, error de selección y error de ejecución | El handler de `Ctrl+C` queda desregistrado exactamente una vez en cada ruta. |
| `PRG-13` | cancelación después de elegir renderer | Se cancela únicamente la aplicación elegida; no se reevalúa la presentación. |

La prueba de ausencia de fallback tardío es bloqueante: un test que solo afirme el
modo elegido sin observar las factories no satisface este criterio.

## Paso 4: integración, documentación y revisión del incremento

**Archivos:**

- `README.md`, solo si el texto actual no describe fielmente la selección previa;
- `docs/evaluations/2026-09-05-terminal-tui-evaluation.md`, solo para registrar el
  comportamiento automatizado que realmente haya quedado verificado;
- `docs/specs/2026-09-06-terminal-tui-compatibility-preflight-design.md`, únicamente
  para corregir una discrepancia descubierta durante la implementación. Un cambio de
  decisión requiere aprobación del usuario.

### Trabajo

1. Ejecutar conjuntamente los tests de selector, driver, programa y host para detectar
   incompatibilidades entre sus dobles.
2. Revisar que ningún test use `Console` real, red, reloj real o esperas temporales
   como mecanismo de sincronización.
3. Confirmar que no se han añadido logs con excepciones, entrada o secretos.
4. Revisar el diff contra los no objetivos y eliminar cualquier refactor no requerido.
5. No marcar todavía el punto completo de la TUI en `ROADMAP.md`: los incrementos 3,
   4 y la verificación manual del incremento 5 siguen pendientes.

## Matriz mínima para cerrar el incremento

El incremento 2 no se considerará cerrado hasta que estén implementados y pasando
todos estos grupos:

| Grupo | Casos requeridos | Bloqueante |
|---|---:|---:|
| Selección explícita y redirecciones | `SEL-01` a `SEL-04` | Sí |
| Tamaños límite | `SEL-05` a `SEL-07` | Sí |
| Fallos y propiedad del selector | `SEL-08` a `SEL-16` | Sí |
| Operaciones reales del driver | `DRV-01` a `DRV-12` | Sí |
| Orden y unicidad de composición | `PRG-01` a `PRG-07` | Sí |
| Ausencia de fallback tardío | `PRG-08`, `PRG-09`, `PRG-13` | Sí |
| Errores seguros y limpieza del host | `PRG-10` a `PRG-12` | Sí |
| Regresión del host TUI | suite completa de `TerminalClientTuiTests` | Sí |

Los identificadores sirven para revisar cobertura y pueden conservarse en comentarios
del plan; los nombres de los tests deben describir el comportamiento y no necesitan
incluir el identificador.

## Comandos de verificación

Después de implementar cada paso:

```powershell
dotnet format LocalAssistant.sln --no-restore --verify-no-changes
dotnet build LocalAssistant.sln -c Release --no-restore
dotnet test tests/LocalAssistant.Tests/LocalAssistant.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~TerminalPresentationSelectorTests|FullyQualifiedName~TerminalDriverTests|FullyQualifiedName~TerminalClientProgramTests|FullyQualifiedName~TerminalClientTuiTests"
git diff --check
```

Antes de declarar cerrado el incremento:

```powershell
dotnet format LocalAssistant.sln --no-restore --verify-no-changes
dotnet build LocalAssistant.sln -c Release --no-restore
dotnet test tests/LocalAssistant.Tests/LocalAssistant.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~TerminalClient"
git diff --check
```

Después de los tests se comprobará que no quedan procesos residuales:

```powershell
Get-Process testhost, LocalAssistant.Api -ErrorAction SilentlyContinue
```

La ausencia de salida será el resultado esperado. No se terminarán procesos ajenos sin
identificarlos y comprobar que pertenecen a esta ejecución.

Antes de publicar una PR se ejecutarán además la suite completa y la revisión
pre-landing exigida por `AGENTS.md`:

```powershell
dotnet test LocalAssistant.sln -c Release --no-restore
```

## Criterios de cierre

El incremento 2 estará cerrado únicamente cuando:

- los casos `SEL-01` a `SEL-16`, `DRV-01` a `DRV-12` y `PRG-01` a `PRG-13` estén
  cubiertos por pruebas deterministas y pasen;
- `--plain` y las tres redirecciones no creen un driver;
- tamaño insuficiente o fallo esperado degraden a texto antes de construir la
  aplicación;
- el driver compruebe tamaño, teclado no consumidor, cursor, limpieza y escritura;
- el host reciba exactamente el driver inicializado;
- se construya y ejecute una sola aplicación;
- un error posterior al arranque no active fallback;
- la restauración sea única e idempotente en las rutas relevantes;
- formato, build Release, suite de `TerminalClient` y `git diff --check` pasen;
- no queden procesos residuales;
- la documentación describa únicamente comportamiento ya verificado.

## No objetivos

- Implementar los incrementos 3, 4 o 5 del plan general.
- Cambiar layout, scroll, EOF, normalización o presupuesto del transcript.
- Modificar `TerminalClientApplication` o su máquina de estados.
- Cambiar contratos HTTP, OpenAPI, autenticación, autorización o persistencia.
- Añadir dependencias, nuevos proyectos, telemetría o una API pública.
- Hacer commit, push o crear PR sin solicitud expresa del usuario.
