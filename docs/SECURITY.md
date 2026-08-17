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
