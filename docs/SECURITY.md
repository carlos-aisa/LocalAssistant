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

El orquestador rechaza herramientas desconocidas y retiene en el servidor la llamada
exacta que requiere confirmación: herramienta, argumentos, proveedor y caducidad.
La decisión posterior solo puede aprobar o rechazar esa llamada; es de un único uso
y no permite sustituir argumentos desde HTTP. El almacenamiento actual es en RAM:
se pierde al reiniciar y no incorpora identidad, autorización durable ni auditoría.
No debe tratarse como autorización productiva. La confirmación tampoco sustituye la
autorización para leer un dato sensible.

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

## Módulos domésticos y recursos locales controlados

Un módulo no heredará acceso a archivos, datos o herramientas por estar instalado.
Cada principal doméstico autorizará recursos registrados y permisos concretos para
cada módulo. Hogares y módulos tendrán almacenamiento, configuración y auditoría
aislados. Leer, extraer, importar, exportar, crear, sobrescribir y eliminar serán
operaciones diferentes; la lectura será el valor predeterminado y la escritura o
destrucción exigirá una confirmación adicional con el recurso y efecto visibles.

`Controlled Local Resources` validará el destino resuelto inmediatamente antes de
cada acceso y evitará path traversal, rutas absolutas no registradas, enlaces,
junctions y cambios de destino. Aplicará allowlists de formato, tamaño y tipo de
contenido, límites de tiempo y memoria, hashes o versiones, y registro de la
operación. Revocar un ámbito impedirá nuevos accesos sin borrar silenciosamente el
estado ya importado; su retención se resolverá mediante una acción independiente.

Los archivos y sus metadatos serán contenido no confiable. Texto, Markdown, JSON,
CSV, hojas de cálculo, recetas y documentos importados podrán contener prompt
injection, fórmulas, macros, vínculos externos, contenido activo, cargas malformadas
o datos diseñados para agotar recursos. La extracción no ejecutará macros, fórmulas,
scripts ni contenido incrustado; los valores se tratarán como datos y no podrán
conceder permisos, cambiar políticas, solicitar secretos ni activar herramientas.
Formatos avanzados requerirán sus propios límites y pruebas antes de habilitarse.

Leer no significará importar. Antes de normalizar información se mostrará una
previsualización con origen, versión, datos interpretados, omisiones, advertencias,
suposiciones y campos pendientes, y se identificará al principal que confirma. El
archivo original permanecerá intacto. Generar o rellenar una plantilla Excel creará
una nueva versión por defecto; sobrescribir o eliminar requerirá autorización
específica. Importaciones y exportaciones conservarán trazabilidad sin copiar su
contenido completo al log.

Los datos de `BatchCooking` son privados. Alergias, restricciones médicas, dietas,
preferencias, presencia en casa, valoraciones e inventario pueden revelar salud,
hábitos o composición familiar. No se codificarán en el repositorio ni se mezclarán
entre hogares. El miembro que modifica o confirma un dato deberá quedar identificado;
los cambios relevantes de inventario, menú, restricciones y preferencias serán
auditables y corregibles.

Al usar modelos o servicios externos, datos familiares, archivos, plantillas,
recetas privadas y cualquier contenido derivado permanecerán inicialmente bajo
`LOCAL_FILES`, `LOCAL_DOCUMENTS`, `PRIVATE_CONFIG` o la categoría sensible aplicable
con egreso `DENY`. Un módulo no podrá levantar esa política. Una futura excepción
deberá declarar propósito, proveedor, destino, campos mínimos, principal autorizado
y retención; nunca incluirá secretos ni el historial completo por comodidad.

Cada hogar podrá revisar, corregir, exportar y eliminar sus datos y procedencia. La
eliminación deberá contemplar estado estructurado, archivos generados, historial,
auditoría compatible con la política de retención y copias de seguridad, sin borrar
originales del usuario salvo solicitud y autorización independientes.

## Privacidad futura del tutor de inglés

Perfil, ejercicios, errores, informes y transcripciones pertenecerán a un principal
y no se mezclarán entre usuarios. Pueden contener información profesional,
incidencias, clientes, arquitecturas o vocabulario confidencial; se tratarán como
datos privados aunque el objetivo sea pedagógico. El tutor ofrecerá escenarios
ficticios para practicar sin revelar información real.

El audio se procesará localmente siempre que resulte razonable y no se conservará
por defecto. Grabar, retener, reutilizar, exportar o eliminar audio serán decisiones
separadas de conservar una transcripción o informe. Cualquier envío de audio,
transcripción, perfil o contenido profesional a STT, TTS, LLM o evaluador externo
requerirá proveedor conocido, minimización y autorización explícita conforme a la
política de egreso.

Las inferencias pedagógicas serán revisables y conservarán su evidencia temporal.
Un error de transcripción no se atribuirá automáticamente al usuario, una sesión no
fijará silenciosamente su nivel y las puntuaciones internas no se presentarán como
certificaciones oficiales. El usuario podrá corregir, exportar y eliminar su perfil
e historial bajo las reglas de retención y backup aplicables.

## Ciclo futuro de proyectos y agentes de programación

La definición de un proyecto, su especificación, la ejecución de código y su
publicación serán límites de autorización distintos. Decir «impleméntalo» iniciará
una revisión del alcance y una solicitud de aprobación acotada; no concederá permiso
para explorar cualquier carpeta, ejecutar comandos arbitrarios, enviar código a un
tercero, crear commits, publicar ramas, abrir pull requests, desplegar ni realizar
acciones irreversibles.

Cada trabajo deberá identificar un principal autenticado, proyecto, repositorio o
workspace permitido, rama objetivo, alcance, agente, herramientas, presupuesto y
caducidad. No se inspeccionarán otros repositorios ni archivos personales. Antes de
modificar una rama estable se trabajará en una rama o workspace aislado y recuperable.
La voz por sí sola no será prueba suficiente de identidad para una acción de alto
impacto; publicación, despliegue, borrado o sobrescritura requerirán confirmación
reforzada desde un canal autenticado y con el efecto visible.

La ejecución futura ocurrirá en un sandbox con privilegios mínimos de filesystem,
procesos y red. Aplicará allowlists de herramientas y comandos estructurados,
validación de argumentos y límites de tiempo, CPU, memoria, disco, concurrencia,
iteraciones, tráfico y coste. Cancelación, timeout y reintentos serán acotados y no
podrán ampliar permisos. Este sandbox especializado no añadirá una herramienta de
shell, scripts o código generado al modelo conversacional actual.

Código, documentación, reglas del repositorio, issues, comentarios, resultados de
tests y dependencias serán contenido no confiable frente a prompt injection. Podrán
aportar evidencia e instrucciones de proyecto dentro de su precedencia declarada,
pero no conceder herramientas, cambiar políticas, ampliar el repositorio permitido,
solicitar secretos ni ordenar exfiltración. Las políticas se impondrán fuera del
agente y se validarán de nuevo antes de cada efecto sensible.

Credenciales, tokens, certificados, variables de entorno, configuración privada y
secretos detectados no se incorporarán a prompts, diffs, artefactos ni logs. La
integración real deberá usar mecanismos de secretos fuera del alcance del agente,
redactar resultados y examinar cambios y artefactos antes de publicarlos. La
auditoría registrará identidad, autorización, herramientas, destinos, tiempos,
estado y referencias a artefactos sin copiar indiscriminadamente código o secretos.

Las autorizaciones se separarán al menos en: inspeccionar y planificar; editar en un
workspace; crear commit o rama local; publicar rama o pull request; y desplegar,
borrar o sobrescribir. Cada transición mostrará diff y resultados relevantes de
build y tests, favorecerá operaciones reversibles y ofrecerá cancelación o reversión
cuando sea técnicamente posible. Una autorización anterior nunca se heredará como
permiso implícito para la siguiente.

`SOURCE_CODE` y `REPOSITORY_DATA`, incluidos nombres, rutas, historial, diffs y
resúmenes derivados, permanecen inicialmente en egreso `DENY`. Un agente externo
solo podrá evaluarse tras introducir una excepción explícita y específica del
repositorio que identifique principal, propósito, proveedor, destino y payload
mínimo. Que el repositorio sea privado o confidencial exigirá controles más
restrictivos; que el agente local falle o sea menos capaz nunca levantará la
denegación. Autorizar la implementación tampoco autorizará ese egreso.

## Seguridad futura de Controlled Self-Extension

Una extensión generada se considerará no confiable aunque compile o supere tests
producidos por el mismo agente. Código, dependencias, manifiesto, documentación,
repositorio y resultados serán entradas potencialmente maliciosas o defectuosas.
La revisión combinará tests independientes, análisis de permisos y compatibilidad,
inspección de dependencias y supply-chain, escaneo de secretos y revisión humana
proporcional al impacto.

La generación y las pruebas ocurrirán en sandbox con mínimos privilegios de red,
filesystem, procesos, secretos, tiempo, coste y recursos. Instalar dependencias,
acceder a un registry, ejecutar herramientas, leer repositorios o usar credenciales
serán capacidades explícitas y auditadas. El contenido del repositorio no podrá
ampliar el alcance ni convertir instrucciones incrustadas en autorizaciones.

La instancia activa y sus políticas estarán fuera del workspace modificable. Una
extensión común no podrá editar el núcleo, motor de autorización, auditoría,
clasificación de datos, frontera de egreso, verificador de firmas o mecanismo de
rollback; tampoco podrá concederse permisos, marcarse revisada ni aprobar su propia
instalación. Un cambio del núcleo seguirá el flujo normal del producto y revisión
humana obligatoria.

Generar, modificar, integrar, instalar, activar y desplegar tendrán autorizaciones
independientes. Acciones críticas exigirán un principal verificado mediante un canal
o dispositivo autenticado; una orden de voz aislada no bastará. La instalación
usará artefactos versionados y compatibles, no el workspace mutable del agente.

Antes de activar se validarán permisos, migraciones, estrategia de desactivación,
health checks y rollback. Tras activar se observarán fallos y consumo dentro de
límites; una extensión podrá suspenderse automáticamente sin autoaprobar una versión
alternativa. Revertir código no implicará que una migración de datos sea reversible,
por lo que backup, compatibilidad hacia atrás y recuperación se probarán por separado.

La auditoría cubrirá requisitos, agente, herramientas, dependencias, red, secretos
referenciados, aprobaciones, artefactos, instalación, activación, fallos, suspensión
y rollback sin copiar código o credenciales indiscriminadamente.

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
