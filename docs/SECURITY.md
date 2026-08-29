# Seguridad

La primera iteración no es un producto listo para exposición pública. Establece
límites que deben conservarse al añadir capacidades.

## Modelo inicial de herramientas

Cada herramienta declara nombre, descripción, esquema JSON y un perfil de riesgo:
impacto de lectura, modificación o ejecución; sensibilidad pública, privada o
sensible; exposición local o externa controlada; coste; confirmación y scopes
necesarios. «Solo lectura» no significa bajo riesgo: consultar presencia en casa,
calendario, correo, ubicación, cámaras, memoria, documentos o salud puede revelar
información privada sin modificar estado.

La política se impone fuera del modelo al filtrar el catálogo y de nuevo antes de
ejecutar. El contexto predeterminado es anónimo y sin scopes. Una API key local
autentica un único principal y le asigna scopes definidos en el servidor; ni el
cliente ni el modelo aportan scopes. Puede proceder del bootstrap de instalación local
o de la configuración educativa, nunca de ambas fuentes. Se configura o persiste solo
el hash SHA-256 de la clave, nunca la clave en el repositorio o en `appsettings.json`.
Datos privados o sensibles, scopes ausentes y exposición externa se deniegan por
defecto. Los cambios de estado, la ejecución, el coste significativo o una regla
explícita requieren la confirmación exacta ya implementada.

`IToolRegistry` es una allowlist. Una respuesta del modelo no puede descubrir ni
invocar métodos arbitrarios. No existe herramienta para comandos, scripts, archivos
o código generado.

El orquestador rechaza herramientas desconocidas y retiene en el servidor la llamada
exacta que requiere confirmación: herramienta, argumentos, proveedor, principal y
caducidad. La decisión posterior solo puede aprobar o rechazar esa llamada; es de un
único uso y no permite sustituir argumentos desde HTTP. Si la llamada se originó con
un principal autenticado, otro principal no puede consumirla. El almacenamiento actual
es en RAM: se pierde al reiniciar y no incorpora gestión de usuarios ni autorización
durable. Una conversación iniciada por el principal autenticado se vincula a ese
principal en memoria; otro principal o un cliente anónimo recibe el mismo resultado
que ante una conversación inexistente. Las conversaciones anónimas no tienen
propietario, se consideran públicas y efímeras, y no podrán persistirse como datos
privados sin un vínculo explícito. Existe una auditoría local en memoria de solicitudes,
decisiones, confirmaciones y ejecuciones, pero tampoco sobrevive un reinicio ni debe
tratarse como registro productivo. La confirmación tampoco sustituye la autorización
para leer un dato sensible.

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
- **Acciones repetidas:** la confirmación actual se consume una sola vez, pero no
  aporta idempotencia distribuida. `create_reminder` es el primer vertical slice:
  genera una clave de operación interna al retener la confirmación y crea el resultado
  de forma atómica por principal y clave dentro del proceso. La garantía no sobrevive
  a un reinicio ni cubre varios procesos, dispositivos o servicios externos; cada
  futura herramienta con efectos deberá definir su propia semántica antes de
  conectarse a esos destinos.
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
antes de exponer la API deberá acompañarse de identidad apta para el despliegue,
HTTPS, cuotas, límites de concurrencia y rate limiting.

## Persistencia privada futura

Una conversación no es una identidad y conocer su identificador no concede acceso a
una conversación que ya pertenezca a otro principal. Del mismo modo, autenticar un
dispositivo o asociarlo a una habitación no identifica automáticamente a la persona
presente. La vinculación actual solo cubre el principal único de la API key y existe
en memoria; antes de persistir conversaciones privadas, memoria, documentos o trazas
deberá evolucionar a un concepto mínimo de `User` o `Principal`, propiedad y alcance
de acceso durables.

La primera persistencia privada define retención, borrado selectivo, control de acceso
y las consecuencias operativas de la protección en reposo, backups y restauración. El
primer almacén elegido es SQLite local (ADR 0024), que no cifra datos por sí mismo. La
[guía operativa](OPERATIONS.md) delimita los controles del despliegue: cuenta de
ejecución, permisos de archivos, cifrado del volumen y custodia de las copias. La
aplicación persiste solo conversaciones autenticadas y notas personales, y no crea
ACLs, backups ni restauraciones automáticas.

El ciclo de vida aprobado en el ADR 0025 clasifica las conversaciones autenticadas
como datos personales del principal, con retención inicial de 30 días y borrado
selectivo transaccional. El endpoint `DELETE /api/conversations/{conversationId}`
exige un principal autenticado y una cabecera de confirmación exacta; las conversaciones
ajenas, anónimas o inexistentes responden todas `404` y el borrado válido invalida la
confirmación de herramienta pendiente bajo el mismo bloqueo de conversación. Las
conversaciones anónimas siguen fuera de SQLite. Los backups requieren protección
equivalente, y restaurar un punto histórico puede reintroducir sus datos sin
recalcular propietarios, scopes o caducidades; no constituye una excepción de acceso
ni una promesa de borrado global de copias ya existentes.

Las notas de memoria personal son un recurso SQLite separado de las conversaciones,
pero usan la misma activación explícita y retención configurada. Solo un principal
autenticado con `memory.personal.read` puede listarlas, y solo uno con
`memory.personal.write` puede crearlas o borrar las propias. Las consultas y borrados
se condicionan también por propietario; una nota ajena se comporta como inexistente.
No se registra, recupera para el modelo, entrega a herramientas ni transmite a un
proveedor. SQLite y sus backups siguen sin aportar cifrado propio y deben protegerse
como datos privados del principal según la guía operativa.

El bootstrap de instalación concede explícitamente `memory.personal.read` y
`memory.personal.write` al propietario local para que pueda acceder a sus propias
notas. El estado anterior se migra una vez al esquema 2, preservando identidad y hash
de la clave. `installation.owner` no sustituye los scopes concretos y la migración no
concede acceso documental, recordatorios ni permisos futuros.

## Identidad, autorización y acceso de invitados futuros

El bootstrap actual crea un único propietario desde una consola local y se invalida
tras completarse; no existe endpoint de autoalta. La clave se muestra una vez y solo
su hash se guarda en el directorio local de la aplicación. La API key configurada
permanece como frontera educativa. Ninguna es la identidad doméstica definitiva: la
instalación futura distinguirá un hogar, principales humanos e identidades técnicas.
Los nombres de personas, relaciones familiares y credenciales serán configuración
privada, nunca datos del repositorio.

El modelo combinado de autorización, los límites de voz, el filtrado previo de
memoria y el aislamiento de invitados se fijan respectivamente en los ADR
[0017](adr/0017-combine-roles-capabilities-context-and-risk-for-authorization.md),
[0018](adr/0018-treat-voice-as-context-not-strong-authentication.md),
[0019](adr/0019-authorize-memory-before-retrieval.md) y
[0020](adr/0020-isolate-guests-in-expiring-sessions.md).

### Decisión de autorización

La autorización se aplicará fuera del LLM y combinará:

- rol provisional y concesiones o denegaciones específicas;
- capacidad solicitada, distinguiendo lectura, modificación, aprobación y ejecución;
- propietario, hogar, módulo y ámbito personal o compartido del recurso;
- sensibilidad, coste, reversibilidad y nivel de riesgo de la acción;
- dispositivo, canal, habitación y presencia conocida;
- método, confianza y antigüedad de la autenticación;
- confirmación exacta o autenticación reforzada cuando corresponda.

El modelo podrá solicitar una herramienta, pero no decidir quién es el usuario,
conceder capacidades, levantar una denegación ni considerar una frase como prueba de
autorización. Un módulo podrá declarar capacidades, nunca asignarlas o modificar las
políticas que lo gobiernan.

Los nombres siguientes ilustran la granularidad buscada y no son un catálogo cerrado:

```text
conversation.general
memory.personal.read
memory.personal.write
memory.household.read
memory.household.write
batchcooking.menu.read
batchcooking.menu.write
batchcooking.preference.rate
shopping_list.read
shopping_list.write
home.safe_actions.execute
home.sensitive_actions.execute
english_tutor.use
projects.read
projects.modify
extensions.request
extensions.approve
extensions.activate
users.invite_guest
users.manage
audit.read
system.configure
```

### Roles domésticos provisionales

| Rol o identidad | Acceso inicial esperado | Denegaciones y límites predeterminados |
| --- | --- | --- |
| `Owner/Administrator` | Configuración, usuarios, dispositivos, proveedores, módulos, integraciones, privacidad y auditoría. | No evita confirmaciones destructivas, secretos, privacidad de salidas, auditoría ni separación entre implementar, integrar, activar y desplegar. |
| `Adult Household Member` | Conversación, memoria propia, datos compartidos autorizados, módulos cotidianos y acciones de bajo riesgo. Puede invitar solo con `users.invite_guest`. | No se autoeleva, cambia políticas, instala módulos, aprueba extensiones, lee datos ajenos ni autoriza alto riesgo por defecto. |
| `Child Household Member` | Consultas, aprendizaje, tutor adaptado, menú, valoraciones, recordatorios simples y acciones expresamente seguras. | Sin administración, invitados, compras, secretos, código, módulos, datos privados adultos ni acciones sensibles. Límites de contenido y proveedor configurables. |
| `Guest` | Consulta pública y capacidades temporales expresamente concedidas. | Sin memoria familiar, documentos, calendarios, inventario, herramientas domésticas, automatizaciones, repositorios, agentes, compras ni persistencia por defecto. |
| `Device/Service` | Operaciones técnicas mínimas de satélite, worker o conector. | No suplanta a una persona ni hereda roles humanos; credenciales revocables y ámbito limitado. |

Los permisos finales podrán restringirse o ampliarse por principal dentro de límites
administrativos. La edad o el rol podrán cambiar sin reasignar ni perder el historial
propio; la transición y su autor quedarán auditados. La supervisión de menores será
proporcionada, configurable y visible, no vigilancia oculta.

### Riesgo y confianza de autenticación

Los niveles conceptuales ayudan a expresar requisitos, pero no sustituyen la
evaluación multidimensional de cada herramienta:

| Nivel | Ejemplos | Requisito orientativo |
| --- | --- | --- |
| 0 — Público | Pregunta general o conversación sin memoria privada. | Puede ser anónimo. |
| 1 — Personal bajo | Preferencia propia, recordatorio sencillo o sesión de inglés. | Principal autenticado y capacidad personal. |
| 2 — Doméstico compartido | Menú, lista común, recordatorio familiar o dispositivo seguro. | Capacidad doméstica, contexto válido y posible confirmación. |
| 3 — Sensible | Cerradura, alarma, finanzas, documentos privados, credenciales, servicios externos o publicación de código. | Autenticación fuerte reciente, capacidad específica y confirmación. |
| 4 — Administrativo | Usuarios, permisos, módulos, proveedores, políticas, despliegue o eliminación. | Canal administrativo, `step-up`, transición exacta y auditoría reforzada. |

La identidad observada por voz podrá clasificarse como confirmada, probable,
desconocida, invitada o insuficiente para la acción. Wake word, habitación,
dispositivo compartido y reconocimiento de hablante no serán prueba suficiente para
acciones sensibles. Ruido, errores, grabaciones, cambios de voz y presencia múltiple
impiden tratarlos como autenticación fuerte.

Cuando la confianza sea insuficiente, el sistema solicitará `step-up` sin revelar
antes el dato protegido. Los mecanismos candidatos incluyen confirmación en una
aplicación autenticada, PIN introducido —nunca dictado—, passkey, biometría en un
dispositivo personal, código temporal o aprobación administrativa. El método concreto
se elegirá con la interfaz y el riesgo reales.

### Invitaciones y sesiones efímeras

Un invitado no podrá autoactivarse ni ser creado por una petición verbal desconocida.
La invitación la iniciará el propietario o un adulto con `users.invite_guest`; un
menor no tendrá esa capacidad. La autorización incluirá anfitrión, duración,
caducidad, dispositivos o habitaciones, capacidades, proveedor, cuota, presupuesto,
persistencia y revocación inmediata.

Las sesiones invitadas serán aisladas y efímeras por defecto: no usarán memoria
personal o familiar, no expondrán herramientas privadas, no crearán perfil persistente
sin consentimiento y expirarán automáticamente. Activar una sesión en una habitación
no convertirá otros dispositivos ni el hogar completo en invitados. QR, enlace,
código de un solo uso o alta desde aplicación son alternativas futuras, no decisiones
tecnológicas actuales.

### Datos, RAG y respuestas compartidas

Se separarán conocimiento general, memoria personal, memoria del hogar, memoria de
módulo, memoria administrativa y sesión efímera. Cada elemento tendrá propietario,
ámbito, sujetos autorizados, fuente, sensibilidad, fecha, retención y borrado. La
eliminación de una cuenta definirá por separado datos personales, elementos
compartidos, automatizaciones, valoraciones, historial y auditoría obligatoria.

El filtro de autorización se aplicará durante la selección y recuperación de memoria,
documentos y fragmentos RAG. El LLM no recibirá contenido que el principal no pueda
consultar; filtrar solo la respuesta final no evita divulgación ni influencia
indebida. Índices, embeddings y metadatos respetarán la misma propiedad.

Una salida hablada o visible añadirá otra decisión. Incluso un administrador puede
estar ante un altavoz o pantalla compartidos. Habitación, personas conocidas,
sensibilidad, dispositivo y preferencias podrán reducir el detalle o desviar el
resultado a un dispositivo personal. Nunca se leerán en voz alta contraseñas, tokens,
datos médicos o financieros detallados, conversaciones privadas ni repositorios
confidenciales.

### Aplicación a módulos y acciones

- `BatchCooking`: adultos y propietario según capacidades; menores podrán consultar
  menú y valorar; invitados denegados por defecto; alergias y restricciones médicas
  serán sensibles.
- `Conversational English Coach`: perfil e historial por usuario; configuración
  apropiada para menores; invitado efímero sin perfil persistente; audio y
  transcripciones sujetos a su política.
- `Home Assistant`: acciones por riesgo; invitados denegados, menores limitados a
  acciones seguras y operaciones sensibles con `step-up`.
- `Controlled Self-Extension`: un miembro autorizado podrá proponer; por defecto solo
  el administrador aprobará instalación, activación o cambios del núcleo. Ninguna
  extensión modificará usuarios, capacidades o políticas.

### Bootstrap, ciclo de vida y auditoría

El bootstrap del primer propietario se ejecutará solo durante la configuración
inicial, creará una única cuenta y quedará invalidado al completarse. No dependerá de
credenciales conocidas ni permitirá que otro equipo de la red reclame la instalación.
La recuperación administrativa tendrá evidencias, revocación y auditoría propias sin
crear una puerta trasera permanente.

El ciclo de vida contemplará invitación, activación, configuración inicial, cambio de
rol, suspensión, revocación, caducidad, eliminación, exportación y recuperación. La
auditoría proporcional cubrirá sesiones, invitaciones, cambios de rol o capacidades,
acciones sensibles, confirmaciones, denegaciones, administración, módulos y
revocación de dispositivos. No copiará conversaciones completas y estará protegida
frente a modificación por usuarios ordinarios, módulos y extensiones.

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

La búsqueda textual de contenido requiere además `documents.content.search`, que no
queda concedido por `documents.search` ni `documents.read`. Puede abrir solo los
formatos textuales permitidos y acotados a 1 MiB para decidir una coincidencia
literal, pero no devuelve ni registra contenido o fragmentos. Esta operación no crea
índices, embeddings, memoria derivada ni egreso a proveedores.

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

### Actividades conversacionales con estado

Una actividad activa pertenecerá a un principal y a una conversación autorizada, pero
mantendrá identidad y estado distintos de ambos. Antes de recuperar su contexto o de
entregar un turno al tutor, el servidor comprobará propiedad, autorización y ámbito;
un `ConversationId` conocido no permitirá secuestrar una sesión ni acceder a su
historial. Esta comprobación se aplicará también al continuar una práctica desde otro
canal o dispositivo.

El modelo no podrá cambiar el enrutamiento, el propietario ni el estado de una
actividad. Solo podrá proponer acciones estructuradas; el servidor validará controles
universales, transición, caducidad, concurrencia y efectos. Cancelar, suspender,
reanudar o terminar seguirán accesibles aunque un módulo esté activo, y una petición
ambigua no se interpretará como cierre.

Los perfiles de proveedor limitarán las categorías de datos que cada modelo puede
recibir. No se enviará automáticamente el perfil completo, el historial entero ni
contenido de otro canal a un nuevo proveedor por conveniencia. La auditoría del ciclo
de vida será proporcional: registrará la transición y su procedencia sin convertir
argumentos, transcripciones o resultados sensibles en registros indiscriminados. Los
límites de actividad, inactividad, cancelación, recuperación y retención se
definirán antes de habilitar reintentos o procesamiento diferido duradero.

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
por defecto. La pasarela actual evalúa descriptores y solo entrega los valores a un
adaptador registrado cuando la política los permite. Adaptador, destino y operación
proceden de una allowlist, no de una URL libre propuesta por el modelo. No existe aún
un adaptador real ni comunicación externa. Una marca de saneado procedente del modelo
o del cliente no será prueba suficiente cuando se incorpore el sanitizador.

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

La auditoría en memoria añade identidad disponible, decisión de política, estado de
confirmación y transición de ejecución. No registra el mensaje del usuario, los
argumentos, el contenido enviado al proveedor ni el resultado de la herramienta.
El detalle técnico de un fallo puede conservarse en el historial interno para que el
proveedor continúe el protocolo, pero la respuesta HTTP utiliza solo el mensaje seguro
declarado por la herramienta o un mensaje genérico.

No se registran por defecto:

- mensajes o prompts;
- argumentos de herramientas;
- resultados de herramientas;
- respuestas del modelo;
- tokens, claves, cabeceras de autorización o configuración personal.

Una futura captura de contenido deberá ser opt-in, redactada y tener retención
limitada.

## Antes de exponer la API

Será necesario añadir una identidad y autorización aptas para el despliegue, HTTPS,
límites de cuerpo y tasa, validación de origen, gestión externa de secretos,
auditoría durable y protegida, políticas de red y pruebas contra prompt injection.

Los fallos HTTP actuales no devuelven excepciones internas. Los logs locales sí
pueden contener la excepción del proveedor o herramienta para diagnóstico, por lo
que su acceso también debe protegerse.
