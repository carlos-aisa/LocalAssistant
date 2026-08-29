# Diseño: recuperación híbrida de conversaciones autenticadas

- Estado: Aprobado para planificación
- Fecha: 2026-08-30

## Objetivo

Permitir que Jarvis recupere contexto útil de conversaciones autenticadas anteriores
del propietario actual. Una frase como «tengo más ideas para los menús de la semana
que viene» debe poder retomar una conversación sobre planificación de comidas aunque
no repita exactamente las mismas palabras.

La capacidad no retoma simplemente la última conversación ni incorpora historiales
completos al prompt. Busca conversaciones del propietario, selecciona pocos
fragmentos relevantes y deja visible su procedencia cuando Jarvis los use.

## Alcance

Incluye:

- Conversaciones autenticadas y persistidas que pertenezcan al principal actual.
- Búsqueda híbrida: texto, resumen, tema, palabras clave y similitud semántica local.
- Indexación automática después de 15 minutos de inactividad por conversación.
- Recuperación selectiva antes de llamar al proveedor solo cuando el mensaje parece
  referirse a información previa, o cuando el usuario la pide explícitamente.
- Desambiguación cuando varias conversaciones tengan relevancia comparable.
- Eliminación del índice junto con la conversación por borrado selectivo o retención.

No incluye:

- Conversaciones anónimas, datos de otro principal o reconocimiento de voz.
- Memoria personal genérica, extracción de preferencias o cambios automáticos del
  perfil de instalación.
- Servicios cloud, bases vectoriales externas, RAG documental, watchers o un proceso
  independiente.

## Modelo de datos y ciclo de indexación

La conversación existente sigue siendo la fuente de verdad. El almacenamiento SQLite
incorpora un índice derivado por conversación que contiene, como mínimo:

- Identificador de conversación y del propietario.
- Fecha de la última actividad y estado de indexación.
- Resumen breve, tema y palabras clave generados localmente.
- Texto preparado para búsqueda literal.
- Representación semántica local y su versión de modelo.

Cada turno autenticado actualiza inmediatamente el material de búsqueda textual, de
bajo coste. Cuando pasan 15 minutos sin mensajes nuevos, un componente hospedado
dentro de la API procesa la conversación una vez para actualizar resumen, tema,
palabras clave y representación semántica. No introduce un proceso nuevo. Al arrancar,
el componente revisa conversaciones pendientes que ya superaron ese periodo.

Un fallo temporal de indexación conserva el historial original, registra solo un
estado técnico seguro y permite reintento. La búsqueda literal puede continuar aunque
la representación semántica todavía no esté disponible.

## Recuperación

Antes de cada llamada al proveedor, una política de recuperación revisa el mensaje
actual. Activa la búsqueda cuando el usuario formula una petición explícita sobre el
historial o cuando aparecen señales de continuación acompañadas de un tema, por
ejemplo «seguimos», «más ideas», «lo de», «recuerda» o «qué dijimos sobre».

La búsqueda se ejecuta siempre condicionada por el propietario autenticado. Combina:

1. Coincidencias textuales en mensajes y palabras clave.
2. Similitud entre el mensaje actual y el resumen o representación semántica.
3. Una preferencia ligera por conversaciones más recientes cuando las demás señales
   sean parecidas.

La política devuelve un número pequeño y acotado de conversaciones, resúmenes y
fragmentos. El proveedor recibe únicamente ese contexto recuperado con fecha y origen,
nunca el historial completo. Si existe una coincidencia clara, Jarvis puede indicar
brevemente que retoma el tema. Si hay varias candidatas comparables, pregunta cuál se
quiere continuar. Si no hay una coincidencia útil, responde sin afirmar que recuerda
información inexistente.

## Privacidad y autorización

Solo se indexan y recuperan conversaciones autenticadas. El filtro por propietario se
aplica antes de obtener candidatos y antes de entregar fragmentos al proveedor. Las
conversaciones anónimas permanecen efímeras y no entran en el índice.

Los resúmenes, palabras clave y representaciones son datos privados derivados del
historial. Comparten la ruta, protección operativa, retención, copias de seguridad y
borrado de SQLite. Un borrado selectivo o la expiración elimina tanto los mensajes
como sus registros de recuperación. Los logs y la auditoría no deben incluir consultas,
resúmenes, palabras clave ni fragmentos recuperados.

La generación de resúmenes y similitud usa únicamente un proveedor local configurado.
La falta de un proveedor local compatible no autoriza el envío de conversaciones a un
proveedor externo.

## Pruebas y criterios de aceptación

- Un propietario solo encuentra sus propias conversaciones autenticadas.
- Una búsqueda literal encuentra términos presentes en el historial.
- Una búsqueda semántica relaciona expresiones equivalentes, como «menús» y
  «planificación de comidas».
- Tras 15 minutos de inactividad, la conversación queda indexada; tras un reinicio,
  las pendientes siguen siendo procesables.
- Una conversación activa no recibe actualizaciones semánticas redundantes.
- Una coincidencia clara aporta contexto acotado; varias candidatas requieren
  desambiguación; ninguna candidata no altera la respuesta normal.
- Un fallo de indexación conserva la búsqueda textual y se puede reintentar.
- Borrado selectivo, retención y acceso no autorizado eliminan o impiden recuperar
  el índice correspondiente.

## Consecuencias y evolución posterior

Este índice sirve exclusivamente para recuperar conversaciones del propietario. No
convierte conversaciones en una memoria genérica ni crea perfiles por voz. El diseño
de voz decidirá más adelante cómo se autentica un hablante antes de consultar memoria
personal; hasta entonces la recuperación queda limitada al canal autenticado actual.
