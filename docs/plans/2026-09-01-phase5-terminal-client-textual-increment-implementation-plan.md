# Plan de implementación: fase 5, incremento 1 — cliente terminal textual

## Alcance

Implementar el primer cliente .NET de consola para Windows definido en
`docs/specs/2026-09-01-terminal-client-and-local-speech-design.md`. Será un proceso
`net8.0` independiente que solo consume el contrato HTTP ya publicado por
`LocalAssistant.Api`; no referencia proyectos del servidor, no abre SQLite y no añade
endpoints, DTOs del servidor, paquetes ni cambios de OpenAPI.

El cliente valida una URL HTTP(S) loopback, comprueba health, solicita de forma
interactiva el identificador y la credencial de un cliente privado, abre una sesión
bearer solo en memoria y procesa mensajes de texto consecutivos dentro del mismo
`ConversationId`. Muestra respuesta final, trazas de herramientas, iteraciones y
errores seguros, distinguiendo la selección `fake` de `ollama`.

## Decisiones y supuestos

- El incremento no hace pairing ni persiste credenciales. Ambos pertenecen al
  incremento 2. La credencial no se acepta como argumento de línea de comandos ni se
  escribe en consola, configuración o logs.
- El cliente solo conserva el bearer y el `ConversationId` en memoria del proceso.
  No implementa renovación, reintentos automáticos, `completion`, comandos de
  conversación, cancelación de turnos, historial, TUI, audio o TTS.
- El servidor ya expone `GET /health`, `POST /api/private/sessions` y
  `POST /api/conversations/messages`; por tanto no se modifica `docs/api/openapi.yaml`.
- La selección de proveedor se transmite como parte del request existente. `fake`
  muestra el escenario usado; `ollama` indica que el modelo lo configura el servidor.
  El cliente no replica ni valida el catálogo de escenarios fake.
- La aplicación configurará un timeout de petición que no venza antes del timeout de
  proveedor actual del servidor. Si vence el transporte, mostrará resultado incierto
  y no reenviará el turno; la política de recuperación detallada queda para el
  incremento 2.

## Pasos de implementación

### 1. Crear el ejecutable independiente y añadirlo a la solución

**Archivos:**

- Nuevo `src/LocalAssistant.TerminalClient/LocalAssistant.TerminalClient.csproj`.
- Nuevo `src/LocalAssistant.TerminalClient/Program.cs`.
- `LocalAssistant.sln`.

Crear un proyecto de consola `net8.0` con `Microsoft.NET.Sdk` y sin
`ProjectReference` ni `PackageReference`. Hereda nullable, analizadores y warnings as
errors de `Directory.Build.props`. Añadirlo a la solución en Debug y Release.

`Program.cs` será el único punto de composición: construirá `HttpClient`, analizará
los argumentos no secretos, ejecutará la aplicación y devolverá un código de salida
distinto de cero ante un error bloqueante de arranque. No arrancará la API, no leerá
`appsettings.json` del servidor ni intentará descubrir su almacenamiento.

**Pruebas:** una prueba de estructura comprobará que el proyecto no tiene referencias
a `LocalAssistant.Api`, `LocalAssistant.Core` ni `LocalAssistant.Infrastructure`.

### 2. Definir opciones, entrada de consola y contratos propios del borde HTTP

**Archivos:**

- Nuevo `src/LocalAssistant.TerminalClient/TerminalClientOptions.cs`.
- Nuevo `src/LocalAssistant.TerminalClient/TerminalConsole.cs`.
- Nuevo `src/LocalAssistant.TerminalClient/PrivateApiContracts.cs`.
- Nuevo `src/LocalAssistant.TerminalClient/TerminalClientApplication.cs`.

Aceptar únicamente opciones no secretas: URL base, proveedor (`fake` u `ollama`) y
escenario opcional. Convertir la URL a `Uri` absoluta y rechazar antes de construir
una petición cualquier esquema distinto de HTTP(S) o destino que no sea loopback.

Encapsular lectura y escritura de consola para que el flujo sea determinista en
pruebas. Solicitar `ClientId` y la credencial después de health; la lectura de la
credencial no debe ecoarse. Mantener ambos valores solo durante la ejecución y borrar
la referencia al cerrar el proceso en la medida que permite `string` en .NET. No
presentar este manejo transitorio como almacenamiento seguro.

Definir records locales mínimos para serializar y deserializar las formas públicas
existentes de sesión, mensaje y respuesta de conversación. Esos records pertenecen al
cliente y no reutilizan `LocalAssistant.Api.Contracts`; usar los nombres JSON y campos
que publica OpenAPI. No incluir operaciones de pairing, rotación, borrado, completion,
audio o historial.

**Pruebas:** opciones válidas e inválidas, rechazo de HTTP remoto/LAN y lectura de
credencial sin escribir su valor en la salida capturada.

### 3. Implementar el adaptador HTTP textual y sus errores seguros

**Archivos:**

- Nuevo `src/LocalAssistant.TerminalClient/PrivateApiClient.cs`.
- Ajustar `TerminalClientApplication.cs`.

Crear un adaptador que use exclusivamente `HttpClient` y las rutas existentes:

1. `GET /health` antes de solicitar la credencial.
2. `POST /api/private/sessions` con `ClientId` y credencial, sin bearer.
3. `POST /api/conversations/messages` con bearer, `message`, `provider`, `scenario`
   y el `ConversationId` actual cuando exista.

Validar respuestas JSON obligatorias antes de actualizar el estado local. Añadir el
header `Authorization: Bearer` solo a operaciones autenticadas, nunca a health ni a
la apertura de sesión. Traducir errores de conexión, JSON inválido y estados HTTP en
diagnósticos seguros sin imprimir cuerpos arbitrarios, cabeceras, credenciales ni
token. Para `401`, `403`, `404`, `409`, `422`, `502` y `504`, indicar la categoría
operativa sin afirmar que se ha reintentado o cancelado el turno.

No añadir reintentos automáticos. Un fallo de transporte posterior al envío se muestra
como resultado incierto, pues el servidor puede haber persistido el mensaje de usuario.

**Pruebas:** con un `HttpMessageHandler` determinista, comprobar el orden de rutas,
cuerpos JSON, ausencia de bearer en health/sesión, presencia exclusiva en mensaje,
errores HTTP y respuesta no JSON. El handler no abrirá puertos ni usará red.

### 4. Completar el bucle textual y la presentación observable

**Archivos:**

- Ajustar `TerminalClientApplication.cs`.
- Ajustar `Program.cs` si necesita propagar código de salida.

Después de abrir la sesión, anunciar de forma no sensible la URL loopback y el modo de
proveedor. Leer líneas no vacías hasta EOF o una salida explícita mínima. Para cada
respuesta exitosa, guardar el `ConversationId` devuelto, imprimir el texto final y
resumir iteraciones y trazas de herramientas sin exponer argumentos ni resultados.
Cuando el servidor devuelva una confirmación pendiente, mostrar que el incremento 1
no puede resolverla y mantener la conversación bloqueada hasta que el usuario salga;
la resolución de confirmaciones se implementará en el incremento 2.

El estado permanece en memoria. No se añade `/new`, `/info`, `/provider`, `/exit` con
completion ni cualquier apariencia de TUI; la salida debe funcionar en consola
redireccionada como texto lineal.

**Pruebas:** flujo fake directo de dos turnos que reutiliza el `ConversationId`,
presentación de herramientas e iteraciones, diferenciación visual fake/Ollama,
respuesta de confirmación pendiente y EOF limpio. Las aserciones deben verificar texto
visible y requests HTTP, no campos privados de la implementación.

### 5. Integrar las pruebas en la suite existente y actualizar la guía operativa

**Archivos:**

- `tests/LocalAssistant.Tests/LocalAssistant.Tests.csproj`.
- Nuevos `tests/LocalAssistant.Tests/TerminalClient/TerminalClientApplicationTests.cs`
  y `tests/LocalAssistant.Tests/TerminalClient/PrivateApiClientTests.cs`.
- `README.md`.
- `docs/ROADMAP.md` solo si el incremento queda efectivamente implementado.

Añadir una referencia de prueba al nuevo proyecto de cliente; esta referencia no crea
una dependencia del ejecutable hacia el servidor. Usar `TextReader`/`TextWriter` y
`HttpMessageHandler` controlados. Para una prueba de compatibilidad de borde, reutilizar
`LocalAssistantApiFactory` con `using` para factory, `HttpClient`, requests y respuestas;
no lanzar Kestrel, procesos auxiliares ni tareas en segundo plano. Así no queda un
test host residual al terminar la suite.

Documentar cómo ejecutar el nuevo cliente contra una API ya iniciada y cómo introducir
la credencial sin ponerla en argumentos. Declarar explícitamente que no persiste la
credencial ni admite pairing aún. Marcar el incremento en roadmap solo con pruebas y
documentación reales; de lo contrario conservarlo como planificación.

## Verificación

Desde la raíz, tras restaurar dependencias si fuera necesario:

```powershell
dotnet format LocalAssistant.sln --no-restore --verify-no-changes
dotnet build LocalAssistant.sln -c Release --no-restore
dotnet test LocalAssistant.sln -c Release --no-restore
git diff --check
```

Además, una ejecución manual reproducible inicia `LocalAssistant.Api` ya bootstrapada
en loopback, arranca el nuevo cliente con proveedor `fake`, introduce credenciales por
consola, completa dos mensajes directos y comprueba que el segundo request porta el
`ConversationId` del primero. La prueba manual no usa Ollama ni una red externa.

## No objetivos

- DPAPI, almacenamiento de credencial, pairing, rotación, revocación y renovación.
- Completion, comandos de conversación, historial, listado, selector y borrado.
- TUI, dependencias visuales, animación, audio, TTS, reproductor, STT y entrada de voz.
- API nueva, cambios en OpenAPI, cambios de persistencia, acceso directo a SQLite o
  referencias desde el cliente a los ensamblados del servidor.
- Lanzador, supervisor o instalación empaquetada de la API.
