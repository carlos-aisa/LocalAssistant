# Diseño: solicitud de API key en el cliente de terminal

## Objetivo

Evitar que una persona olvide configurar una API key antes de iniciar
`scripts/Chat.ps1`, sin almacenar ni mostrar la credencial. El cliente debe poder
seguir iniciando conversaciones anónimas cuando el operador lo decida expresamente.

## Alcance

El cambio afecta únicamente al cliente interactivo PowerShell y a su documentación.
No modifica la autenticación de la API, el bootstrap de identidad, los scopes, la
persistencia SQLite ni el contrato HTTP.

## Comportamiento

Al comenzar, el cliente resolverá la API key en este orden:

1. Si `LOCALASSISTANT_API_KEY` contiene un valor, la usará sin solicitar nada.
2. Si no contiene un valor, pedirá `Local API key` mediante entrada segura, sin eco.
3. Si la entrada está vacía, informará de que la sesión continuará sin autenticar y,
   por tanto, sus conversaciones serán anónimas y efímeras.

La opción actual `-PromptForApiKey` se mantendrá por compatibilidad. Cuando se use,
forzará la solicitud segura incluso si existe la variable de entorno.

La clave solo residirá en memoria durante la ejecución del script. No se imprimirá, no
se escribirá en archivos ni se aceptará como argumento de la línea de comandos.

## Diseño técnico

`Get-ApiKey` seguirá siendo la única función que obtiene la credencial. Su condición
para solicitarla cambiará de «se recibió `-PromptForApiKey`» a «se recibió
`-PromptForApiKey` o la variable de entorno no aporta una clave». El flujo existente
que transforma brevemente el `SecureString` en una cadena y libera el búfer nativo se
conservará.

El arranque mostrará un aviso explícito solo si no se obtuvo una clave. Las peticiones
seguirán añadiendo `X-LocalAssistant-Api-Key` únicamente cuando la cadena no esté
vacía, por lo que no cambia el comportamiento HTTP de una sesión anónima.

## Errores y límites

El cliente no validará localmente que la clave sea correcta: la API continúa siendo la
autoridad y devolverá su error de autenticación habitual. Una entrada vacía no es un
error del cliente, sino una elección consciente de modo anónimo. La clave tampoco se
persistirá al cerrar la consola; se volverá a solicitar en una nueva sesión si la
variable de entorno sigue ausente.

## Verificación

La implementación incluirá pruebas o comprobaciones reproducibles del script que
cubran: uso preferente de la variable, solicitud segura en su ausencia, solicitud
forzada por `-PromptForApiKey` y continuación anónima tras una entrada vacía. El
README se actualizará para describir el flujo predeterminado y conservará la advertencia
de no proporcionar secretos como argumentos.

La validación final ejecutará formato, compilación Release, la suite de pruebas y la
revisión del diff. No se introducirán dependencias de red, Ollama, variables personales
ni una nueva infraestructura de pruebas para este cambio acotado.

## Criterios de aceptación

- Ejecutar `./scripts/Chat.ps1 -Provider ollama` sin variable de entorno solicita la
  clave sin eco.
- Una clave presente en `LOCALASSISTANT_API_KEY` evita el prompt por defecto.
- `-PromptForApiKey` conserva la posibilidad de pedir una clave de forma explícita.
- Dejar el prompt vacío inicia una sesión anónima e informa de su carácter efímero.
- Ninguna salida ni archivo nuevo contiene la API key.
