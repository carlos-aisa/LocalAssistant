# Plan de implementación: solicitud de API key en el cliente de terminal

## Alcance confirmado

Este plan implementa
`docs/specs/2026-08-30-prompt-terminal-api-key-design.md`. Afecta solo al cliente
interactivo `scripts/Chat.ps1` y al apartado que lo documenta en `README.md`.

No modifica la autenticación de la API, el bootstrap, la persistencia, los scopes, los
endpoints ni el contrato OpenAPI. Los cambios no confirmados de almacenamiento privado
que ya existen en el árbol de trabajo quedan fuera de este incremento.

## 1. Resolver la clave de forma segura al iniciar el cliente

**Archivo:** `scripts/Chat.ps1`.

- Ajustar `Get-ApiKey` para que conserve el valor de
  `LOCALASSISTANT_API_KEY` cuando sea una cadena no vacía, salvo que se haya indicado
  `-PromptForApiKey`.
- Cuando la variable esté ausente, vacía o contenga solo espacios, solicitar `Local API
  key` con el flujo `SecureString` existente. Mantener la liberación del búfer nativo
  en el bloque `finally`.
- Conservar `-PromptForApiKey` como modo explícito que fuerza la solicitud segura aun
  cuando la variable esté definida; no introducir un parámetro alternativo ni aceptar
  secretos por argumentos.
- Mantener la clave resultante únicamente en `$apiKey` durante el proceso. La lógica
  existente seguirá enviando el header solo cuando haya una clave no vacía.

## 2. Hacer explícito el modo anónimo intencionado

**Archivo:** `scripts/Chat.ps1`.

- Justo después de resolver la clave y antes de la comprobación de salud, emitir un
  aviso visible solo si no se obtuvo una clave. Debe indicar que la sesión será
  anónima y que sus conversaciones son efímeras.
- No bloquear el inicio, no inventar una clave ni validar localmente su contenido. La
  API seguirá respondiendo `401` si se proporciona una clave inválida.
- No imprimir ni interpolar el valor de la clave en el aviso, el estado, excepciones o
  cualquier otra salida.

## 3. Actualizar la guía del cliente de terminal

**Archivo:** `README.md`, sección «Cliente de terminal para pruebas manuales».

- Sustituir la explicación actual que exige configurar `LOCALASSISTANT_API_KEY` o
  recordar `-PromptForApiKey` por el flujo predeterminado: el cliente solicita la
  clave sin eco cuando la variable no está presente.
- Mantener como alternativas documentadas la variable de entorno para la sesión y
  `-PromptForApiKey` para forzar la solicitud.
- Explicar que dejar la entrada vacía continúa en modo anónimo y que no permite
  persistir conversaciones privadas. Conservar la prohibición de pasar secretos por
  argumentos o guardarlos en archivos.

## 4. Verificación

No hay actualmente runner ni pruebas automatizadas de PowerShell en el repositorio.
No se añadirá Pester ni otra infraestructura para este cambio acotado. Se realizarán
comprobaciones reproducibles contra una API local con el proveedor `fake`, sin Ollama,
red externa ni secretos reales:

1. Con una variable de entorno de prueba no vacía, iniciar el cliente, ejecutar
   `/info` y confirmar que no aparece el prompt y que muestra `API key configured`.
2. Sin la variable, iniciar el cliente e introducir una clave de prueba; comprobar que
   se solicita sin eco y que `/info` muestra `API key configured`.
3. Con la variable definida y `-PromptForApiKey`, comprobar que se vuelve a solicitar
   la clave sin eco.
4. Sin la variable, dejar el prompt vacío y comprobar el aviso de sesión anónima y que
   `/info` muestra `anonymous`.
5. Ejecutar el análisis sintáctico de PowerShell sobre `scripts/Chat.ps1`,
   `dotnet format LocalAssistant.sln --verify-no-changes --no-restore`, build Release,
   `dotnet test LocalAssistant.sln --configuration Release --no-build --no-restore` y
   `git diff --check`.

La revisión final del diff comprobará específicamente que no se añadió ningún lugar
que escriba, imprima o acepte la API key como argumento.

## No objetivos

- No persistir la API key, ni crear un gestor de secretos o una configuración nueva.
- No cambiar las reglas de autenticación ni permitir acceso persistente sin una clave
  válida.
- No añadir endpoints, scopes, cambios de base de datos, Pester, Ollama ni servicios
  externos.
