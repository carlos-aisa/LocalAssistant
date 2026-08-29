# Plan de implementación: perfil global del asistente de instalación

## Alcance confirmado

Este plan implementa
`docs/specs/2026-08-30-installation-assistant-profile-design.md`. Añade un perfil de
instalación estructurado cuyo único campo es `DisplayName`, la herramienta confirmada
`set_assistant_name` y una instrucción de sistema no persistida para cada proveedor.

No añade memoria genérica, extracción automática de preferencias, endpoints nuevos,
tablas SQLite para el perfil, usuarios domésticos, sincronización ni campos de
personalidad adicionales. Los cambios no confirmados de otros incrementos quedarán
fuera de este trabajo.

## 1. Definir el contrato global y la instrucción de sistema

**Archivos:** nuevo módulo de perfil en `src/LocalAssistant.Core`; `ConversationContracts.cs`;
`ConversationOrchestrator.cs`; sus pruebas de orquestación.

- Introducir `AssistantProfile` e `IAssistantProfileStore` en el núcleo. El contrato
  expondrá la lectura del perfil y la actualización validada del nombre, siempre con
  `CancellationToken` y sin depender de JSON ni de rutas de archivos.
- Establecer `LocalAssistant` como perfil predeterminado. La validación centralizará
  recorte, ausencia de texto, caracteres de control y límites de longitud para que la
  herramienta y el almacenamiento apliquen la misma regla.
- Añadir `System` a `ConversationRole`. El orquestador construirá para cada llamada al
  proveedor una lista efímera que empieza por la instrucción fiable derivada del perfil
  actual y continúa con el historial almacenado. Nunca llamará a `AppendAsync` para
  ese mensaje.
- Consultar el perfil antes de cada iteración del bucle de herramientas, no solo al
  comienzo del turno. Así la respuesta posterior a una aprobación observa el nombre
  recién guardado. Si la lectura del perfil falla, el turno fallará como una frontera
  de almacenamiento, sin sustituir silenciosamente el perfil por datos del usuario.

## 2. Persistir el perfil de instalación de forma aislada

**Archivos:** nuevo adaptador de perfil bajo `src/LocalAssistant.Api`; `Program.cs`;
pruebas junto a `InstallationIdentityStoreTests`.

- Implementar un almacén de archivos que resuelva
  `assistant-profile.json` dentro de `LocalAssistant:Installation:StateDirectory`,
  aplicando la misma exigencia de ruta absoluta y directorio predeterminado que el
  estado de identidad.
- Si el archivo no existe, devolver `AssistantProfile.Default` sin crearlo. Al cambiar
  el nombre, crear el directorio necesario y publicar el JSON validado mediante un
  archivo temporal y reemplazo atómico.
- Proteger lectura y escritura dentro del proceso mediante sincronización asíncrona
  del singleton. No se afirmará coordinación entre varios procesos, pues el estado de
  instalación actual tampoco la ofrece.
- Registrar el adaptador como singleton de `IAssistantProfileStore` en la composición
  de la API. No dependerá de `ConversationPersistence`; bootstrap y configuración
  educativa podrán seguir resolviendo la autenticación como hasta ahora.
- Probar perfil predeterminado, escritura y lectura desde una instancia nueva,
  normalización y rechazo de valores inválidos, estado corrupto y ruta configurada.

## 3. Añadir la herramienta confirmada de cambio de nombre

**Archivos:** nuevo `SetAssistantNameTool` en `src/LocalAssistant.Core/Tools`;
`Program.cs`; pruebas de herramienta, política y orquestación.

- Definir la allowlist `set_assistant_name` con un único argumento requerido
  `displayName`, tipo cadena, límites de longitud y `additionalProperties: false`.
- Declarar un perfil de riesgo de modificación local privada, sin coste, con
  confirmación obligatoria y scope exclusivo `installation.owner`.
- Validar la forma del JSON y delegar la validación semántica al perfil. Los argumentos
  inválidos producirán un resultado de herramienta seguro y no modificarán archivos.
- Registrar la herramienta en `Program.cs`. El modelo podrá solicitarla, pero el
  orquestador seguirá filtrándola para anónimos o scopes insuficientes y retendrá la
  llamada exacta para confirmación antes de ejecutar.
- Añadir pruebas de contrato y argumentos para la herramienta, decisiones de política
  para anónimo y principal sin scope, confirmación pendiente, rechazo sin cambio y
  aprobación con actualización efectiva. Una prueba del bucle verificará que la segunda
  llamada al proveedor del mismo turno recibe la instrucción `System` con el nuevo
  nombre.

## 4. Adaptar los proveedores al contexto de sistema

**Archivos:** `OllamaLanguageProvider.cs`; contratos y pruebas de proveedores fake y
Ollama.

- Mapear `ConversationRole.System` al rol `system` de Ollama. Mantener sin cambios el
  agrupado de tool calls y el mapeo de resultados de herramientas.
- Actualizar las pruebas de contrato y los dobles de proveedor para aceptar el mensaje
  adicional sin confundirlo con texto de usuario ni alterar sus secuencias
  deterministas.
- Añadir una prueba del cuerpo HTTP de Ollama que compruebe que la instrucción de
  sistema contiene el nombre configurado y precede al historial, sin registrar ni
  serializar como `user` el contenido del perfil.

## 5. Alinear documentación y contratos públicos

**Archivos:** `README.md`, `docs/ARCHITECTURE.md`, `docs/SECURITY.md`,
`docs/OPERATIONS.md` y el ADR nuevo que corresponda; `docs/api/openapi.yaml` solo si
la inspección confirma que el contrato HTTP cambia.

- Documentar que el nombre es una preferencia global de instalación, no un historial
  ni memoria personal, y que se modifica mediante herramienta confirmada para el
  propietario.
- Añadir `assistant-profile.json` al inventario de almacenamiento privado, backups y
  restauración de la guía operativa.
- Registrar la decisión de separar el perfil de identidad, conversaciones y notas en
  un ADR con sus consecuencias de ámbito, autorización y extensibilidad explícita.
- No cambiar OpenAPI si la herramienta continúa siendo parte del cuerpo ya documentado
  de la conversación y no se exponen endpoints, DTOs ni códigos nuevos. Si la
  inspección encuentra una descripción contractual de herramientas que deba ampliarse,
  actualizarla junto con pruebas HTTP.

## 6. Verificación y revisión

- Ejecutar las pruebas nuevas de perfil, herramienta, orquestación y proveedor, y
  después `dotnet format LocalAssistant.sln --verify-no-changes --no-restore`,
  `dotnet build LocalAssistant.sln --configuration Release --no-restore` y
  `dotnet test LocalAssistant.sln --configuration Release --no-build --no-restore`.
- Ejecutar `git diff --check` y revisar el diff para asegurar que ningún argumento de
  herramienta, nombre configurado, ruta de estado o contenido de sistema se registra
  de forma indiscriminada.
- Verificar de forma manual con el proveedor fake: propietario autenticado solicita el
  cambio, confirma, recibe la respuesta final; una conversación nueva usa el nombre
  guardado; una conversación anónima no puede solicitar ni ejecutar la modificación.
