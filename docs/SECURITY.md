# Seguridad

La primera iteración no es un producto listo para exposición pública. Establece
límites que deben conservarse al añadir capacidades.

## Modelo inicial de herramientas

Cada herramienta declara:

- nombre y descripción;
- impacto de solo lectura o modificación de estado;
- necesidad de confirmación;
- esquema JSON de argumentos.

`IToolRegistry` es una allowlist. Una respuesta del modelo no puede descubrir ni
invocar métodos arbitrarios. No existe herramienta para comandos, scripts, archivos
o código generado.

El orquestador rechaza herramientas desconocidas y bloquea las que requieren
confirmación salvo aprobación explícita por nombre. Esta aprobación es un punto de
extensión educativo: todavía no está vinculada a usuario, sesión, intención exacta,
caducidad ni autenticación. No debe reutilizarse como autorización productiva.

## Amenazas relevantes

- **Prompt injection:** texto del usuario, documentos o conectores pueden intentar
  manipular la elección de herramientas. La allowlist y las políticas deben
  imponerse fuera del modelo.
- **Argumentos inventados:** todo argumento producido por un modelo se considera no
  confiable y debe validarse contra reglas de la herramienta.
- **Exfiltración:** un proveedor externo futuro no debe recibir datos privados por
  defecto. El routing debe aplicar clasificación y minimización.
- **Acciones repetidas:** los identificadores, confirmaciones e idempotencia serán
  necesarios antes de controlar dispositivos o servicios externos.
- **Denegación de servicio:** el límite de iteraciones y los timeouts reducen bucles
  y esperas, pero faltan cuotas, límites de tamaño y rate limiting.
- **Memoria sensible:** las conversaciones permanecen en RAM hasta terminar el
  proceso. No hay cifrado, borrado selectivo ni política de retención.
- **Endpoint de inferencia:** la URL de Ollama es configuración de confianza y no
  procede de cada petición. Apuntarla a otro host puede enviarle conversaciones y
  resultados de herramientas.

## Ollama y red local

Ollama está desactivado mientras no se configure un modelo. Su endpoint local no
debe exponerse a Internet ni asumirse seguro solo por estar en la LAN. Conviene
limitar la escucha al equipo o a interfaces expresamente necesarias y aplicar
firewall. Si se configura un host remoto, el operador debe proporcionar transporte,
autenticación y confianza de red adecuados; esta versión no gestiona credenciales
para Ollama.

El adaptador no registra cuerpos HTTP, prompts, argumentos ni respuestas. Los
errores se traducen por el orquestador sin devolver al cliente detalles internos
del proceso de inferencia.

La inspección previa envía a `/api/show` únicamente el nombre configurado del
modelo. No envía mensajes ni resultados de herramientas. Exigir que el modelo
declare `tools` evita una configuración incompatible, pero no demuestra que sus
decisiones o argumentos sean fiables; la allowlist y la validación del orquestador
siguen siendo obligatorias.

El timeout de proveedor predeterminado es de tres minutos para permitir inferencia
local en CPU. Es un límite de disponibilidad, no una defensa suficiente ante abuso;
antes de exponer la API deberá acompañarse de autenticación, cuotas, límites de
concurrencia y rate limiting.

## Privacidad y seguridad del audio futuro

Los satélites de habitación convierten el audio ambiente en un dato especialmente
sensible. Antes de habilitar captura real deberán cumplirse estos límites:

- El dispositivo mostrará físicamente cuándo escucha o captura mediante luz,
  pantalla u otro indicador inequívoco que no dependa solo de una interfaz remota.
- El micrófono podrá desactivarse mediante un control físico. El software deberá
  poder observar y mostrar ese estado, pero no reactivar un corte físico.
- La detección de wake word y el filtrado previo se ejecutarán localmente siempre
  que el hardware lo permita, minimizando audio enviado al núcleo.
- El streaming dentro de la red doméstica estará autenticado y cifrado; pertenecer
  a la misma LAN no constituirá confianza suficiente.
- Satélite, habitación, sesión y destino de respuesta se validarán en el servidor;
  un dispositivo no podrá suplantar libremente otra habitación.
- Se limitarán duración, tamaño, buffering y retención. No se conservará audio por
  defecto ni se reutilizará para entrenamiento sin una decisión explícita.
- Wake word falso, pulsación accidental, dispositivo comprometido y captura no
  autorizada se tratarán como amenazas de primer nivel y deberán dejar eventos de
  auditoría sin almacenar el audio completo.
- La cancelación de eco y la supresión de reproducción evitarán que el asistente
  procese su propia respuesta. La interrupción por voz requerirá distinguir audio
  del usuario y salida TTS sin ocultar el estado de captura.
- Un dispositivo desconectado, en error o silenciado tendrá un estado visible y no
  se considerará disponible para enrutar respuestas.

Los Google Nest Hub se tratarán como salidas Cast. No se solicitará ni diseñará
acceso a su micrófono, audio capturado, wake word interno o reemplazo de Google
Assistant. El contenido enviado a sus pantallas también deberá minimizar datos
privados visibles para otras personas de la habitación.

## Política inicial de logging

Se registran identificador de conversación, proveedor, iteración, nombre e id de
herramienta, éxito o código de error y tiempos.

No se registran por defecto:

- mensajes o prompts;
- argumentos de herramientas;
- resultados de herramientas;
- respuestas del modelo;
- tokens, claves, cabeceras de autorización o configuración personal.

Una futura captura de contenido deberá ser opt-in, redactada y tener retención
limitada.

## Antes de exponer la API

Será necesario añadir al menos autenticación, autorización, HTTPS en el entorno de
despliegue, límites de cuerpo y tasa, validación de origen, gestión externa de
secretos, auditoría, políticas de red y pruebas contra prompt injection.

Los fallos HTTP actuales no devuelven excepciones internas. Los logs locales sí
pueden contener la excepción del proveedor o herramienta para diagnóstico, por lo
que su acceso también debe protegerse.
