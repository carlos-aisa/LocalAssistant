# Seguridad

La primera iteración no es un producto listo para exposición pública. Establece
límites que deben conservarse al añadir capacidades.

## Modelo inicial de herramientas

Cada herramienta declara:

- nombre y descripción;
- impacto de solo lectura o modificación de estado;
- necesidad de confirmación;
- esquema JSON de argumentos.

Esta clasificación implementada es deliberadamente inicial. «Solo lectura» no
significa bajo riesgo: consultar presencia en casa, calendario, correo, ubicación,
cámaras, memoria, documentos o salud puede revelar información privada o sensible
sin modificar estado. Autorización y confirmación dependerán del riesgo y los datos,
no únicamente de lectura frente a escritura.

La política futura podrá considerar, de forma incremental y sin fijar todavía
contratos definitivos:

- impacto de la operación: lectura, modificación o ejecución;
- sensibilidad: pública, privada o sensible;
- principal y alcance autorizado;
- exposición externa inexistente o controlada;
- necesidad de confirmación;
- coste nulo, acotado o significativo y otros efectos relevantes.

`IToolRegistry` es una allowlist. Una respuesta del modelo no puede descubrir ni
invocar métodos arbitrarios. No existe herramienta para comandos, scripts, archivos
o código generado.

El orquestador rechaza herramientas desconocidas y bloquea las que requieren
confirmación salvo aprobación explícita por nombre. Esta aprobación es un punto de
extensión educativo: todavía no está vinculada a usuario, sesión, intención exacta,
caducidad ni autenticación. No debe reutilizarse como autorización productiva.
La confirmación tampoco sustituye la autorización para leer un dato sensible.

## Amenazas relevantes

- **Prompt injection:** texto del usuario, documentos o conectores pueden intentar
  manipular la elección de herramientas. La allowlist y las políticas deben
  imponerse fuera del modelo.
- **Argumentos inventados:** todo argumento producido por un modelo se considera no
  confiable y debe validarse contra reglas de la herramienta.
- **Exfiltración:** un proveedor externo futuro no debe recibir datos privados por
  defecto. La privacidad precede al routing: clasificación y minimización se aplican
  antes de comparar proveedores, y la falta de capacidad local no autoriza datos
  `DENY` para un LLM externo.
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

La ventana de contexto se limita a 4096 tokens por defecto para contener memoria y
latencia, especialmente en CPU. Este límite de Ollama no sustituye futuros límites
de longitud del cuerpo HTTP, conteo previo de tokens ni políticas de resumen y
retención del historial.

El timeout de proveedor predeterminado es de tres minutos para permitir inferencia
local en CPU. Es un límite de disponibilidad, no una defensa suficiente ante abuso;
antes de exponer la API deberá acompañarse de autenticación, cuotas, límites de
concurrencia y rate limiting.

## Persistencia privada futura

Una conversación no es una identidad y conocer su identificador no concederá acceso
a memoria personal. Del mismo modo, autenticar un dispositivo o asociarlo a una
habitación no identifica automáticamente a la persona presente. Antes de persistir
conversaciones privadas, memoria, documentos o trazas deberá existir un concepto
mínimo de `User` o `Principal`, propiedad y alcance de acceso.

La persistencia privada no se considerará completa hasta definir retención, borrado
selectivo, control de acceso, protección en reposo, auditoría y consecuencias de
backup y restauración. La elección podrá combinar permisos del sistema operativo,
cifrado de disco, capacidades de la base de datos o protección de aplicación según
el almacenamiento y despliegue reales; no se selecciona todavía una tecnología.

## Acceso futuro a documentos locales

La búsqueda documental usará una allowlist de raíces configuradas. La fuente inicial
será la ubicación Documentos resuelta por el sistema operativo, no una ruta absoluta
hardcodeada. No se recorrerán implícitamente discos, perfil completo, `AppData`,
sistema, repositorios ni ubicaciones arbitrarias. El LLM no dispondrá de operaciones
genéricas de filesystem, shell o apertura de rutas libres.

Búsqueda y lectura tendrán permisos y exposición diferentes. Buscar por metadatos no
abrirá el contenido completo cuando nombre, tipo, ruta relativa o fechas basten. Leer
requerirá seleccionar un resultado permitido y volver a comprobar el destino. Los
metadatos también pueden revelar información privada y respetarán principal,
propiedad y alcance de acceso.

Toda ruta o referencia influida por el modelo se tratará como no confiable. El
servicio validará el destino finalmente resuelto, no solo el texto de entrada, y
evitará escapar de las raíces mediante `..`, rutas absolutas, representaciones
alternativas, enlaces simbólicos, junctions o redirecciones equivalentes. La
comprobación se repetirá inmediatamente antes de abrir el archivo.

Lectura y extracción aplicarán allowlist de formatos, límites de tamaño, tiempo,
memoria y trabajo, y fallos explícitos para tipos no soportados o contenido mal
formado. Un documento puede ser hostil aunque proceda de una carpeta local. Su texto
será evidencia no confiable y no podrá cambiar instrucciones o políticas, conceder
permisos, activar herramientas, acceder a memoria u otros archivos ni provocar
egreso externo.

Contenido, nombre, ruta, metadatos, texto extraído, índice y embeddings de documentos
locales permanecerán locales por defecto bajo `LOCAL_DOCUMENTS`, `LOCAL_FILES` y
`RAG_CONTENT` con política `DENY`. Encontrar o leer un dato no autoriza incluirlo en
una consulta web; el payload externo final seguirá sujeto a clasificación y
protección de datos derivados.

Abrir un documento no implicará ingesta, persistencia ni retención RAG. Esas acciones
serán explícitas y aplicarán el modelo de propiedad y almacenamiento privado. Los
repositorios y el código fuente quedan fuera de las fuentes documentales y no se
habilitarán accidentalmente mediante configuración amplia.

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

## Acceso futuro a Internet y conocimiento externo

El modelo no recibirá una conexión general a Internet. Búsqueda, lectura web,
Wikipedia, mapas, meteorología y otros servicios se expondrán como herramientas de
solo lectura registradas y atravesarán una pasarela controlada. Las políticas de
red, proveedor, credenciales, coste y permisos se aplicarán fuera del LLM.

### Política por categorías y ubicación

La protección de egreso distinguirá categorías de datos en lugar de considerar
todo contexto privado intercambiable. Código fuente, repositorios, documentos,
archivos, RAG, bases de datos, memoria, conversación, configuración, variables de
entorno, credenciales y secretos permanecerán en denegación automática. Una
transformación o resumen no levantará esa denegación.

`LOCATION` será una categoría personal permitida cuando sea funcionalmente
necesaria para la solicitud. Podrá incluir hogar, posición móvil autorizada,
dirección de destino, lugar explícito, coordenadas o ubicación aproximada para
routing, distancia, navegación, lugares cercanos, búsqueda local o meteorología.
Se aplicará divulgación mínima: el proveedor recibirá solo la representación y
precisión necesarias. Autorizar ubicación no autorizará historial, memoria, perfil,
documentos ni otros datos del mismo usuario.

`SEARCH_QUERY` requerirá saneado y minimización; `PUBLIC_DATA` podrá salir cuando la
política del destino lo permita. Las categorías nuevas o desconocidas se denegarán
por defecto. El sistema deberá poder añadir categorías y políticas sin rediseñar la
frontera de egreso completa.

La comprobación se aplicará al payload final y no solo a sus fuentes originales.
Consultas, resúmenes, nombres de proyecto, clases, hosts, URLs privadas,
identificadores o cualquier representación derivada conservarán la protección del
dato del que proceden. Contexto privado puede influir en una decisión local, pero
nunca se incorporará implícitamente a una petición externa.

Antes del primer acceso externo deberán cubrirse al menos estas amenazas:

- **SSRF y pivote a red privada:** permitir solo esquemas y destinos previstos,
  validar resolución y redirecciones, bloquear direcciones locales, privadas y de
  metadatos, y no aceptar una URL arbitraria propuesta por el modelo sin política.
- **Prompt injection indirecta:** páginas, fragmentos y resultados son datos no
  confiables, nunca instrucciones. No podrán ampliar permisos, modificar el plan ni
  solicitar otras herramientas fuera de las decisiones del orquestador. La capa de
  aislamiento no tendrá acceso a memoria, archivos, secretos ni ejecución directa.
- **Exfiltración y ubicación sensible:** minimizar consultas y contexto enviados a
  terceros; domicilio, rutas, presencia y destinos pueden revelar hábitos y no se
  compartirán con todos los proveedores por defecto.
- **Credenciales y costes:** mantener claves en configuración segura del adaptador,
  aplicar cuotas, rate limits, concurrencia, timeout y presupuesto agregados, y no
  exponer secretos al modelo, fuentes ni respuesta final.
- **Contenido hostil o excesivo:** limitar tipo, tamaño, redirecciones, compresión y
  tiempo de descarga; no ejecutar scripts, archivos ni contenido activo recuperado.
- **Evidencia engañosa o caducada:** conservar procedencia y fecha, detectar
  conflictos, diferenciar actualidad de conocimiento estable y comunicar
  incertidumbre en vez de fabricar consenso.
- **Retención y cumplimiento:** no almacenar páginas completas ni consultas
  sensibles por defecto; las futuras cachés respetarán caducidad, borrado,
  condiciones de uso y políticas de auditoría.

La ejecución multifuente y la investigación profunda conservarán límites globales,
además de los límites por herramienta. La trazabilidad registrará decisiones,
proveedores, tiempos y referencias necesarias sin convertir el log en una copia del
contenido recuperado o de la conversación.

La misma política gobernará toda salida del límite local, no solo las herramientas:
STT, TTS, LLM cloud, embeddings, telemetría, analítica, crash reporting,
actualizaciones y SDKs de terceros. Siempre que la plataforma lo permita, estos
componentes no tendrán ruta directa a Internet y deberán atravesar una frontera
técnica común. Las pruebas verificarán tanto las decisiones permitidas o denegadas
como intentos de bypass, contenido entrante malicioso y filtraciones mediante datos
derivados.

La auditoría de egreso será local y registrará proveedor, destino, propósito,
categorías autorizadas, volumen, resultado y tiempos. No almacenará por defecto la
ubicación precisa, consultas completas, cabeceras, credenciales ni cuerpos externos.

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
