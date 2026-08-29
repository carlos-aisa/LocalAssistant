# Roadmap

El orden expresa dependencias técnicas, no fechas. Cada fase debe aportar un flujo
vertical comprobable antes de introducir infraestructura adicional. En particular,
la voz se valida primero en un único dispositivo; solo después se distribuye por
habitaciones.

## Lectura por horizonte

- **Capacidades actuales:** fases 1, 2 y la mayor parte de la fase 3 completadas:
  núcleo conversacional, fake, Ollama, herramientas locales, confirmación exacta,
  política de riesgo, egreso controlado, identidad local opcional, auditoría local y
  exposición segura de errores; la fase 4 comienza con identidad de instalación con
  propietario único y propiedad de conversaciones autenticadas en memoria.
- **Próximo incremento:** completado con `create_reminder`: una operación local
  privada, confirmada e idempotente en memoria. La siguiente herramienta con efecto
  deberá ampliar esta garantía según su almacenamiento o destino real.
- **Horizonte cercano:** tutor de inglés escrito con role-play, correcciones e
  informe; persistencia e identidad de la fase 4; capacidad mínima de módulos,
  `Controlled Local Resources`, migración preparada y `BatchCooking` MVP. Home
  Assistant y voz podrán avanzar sobre dependencias comunes sin bloquear esos flujos
  escritos.
- **Horizonte intermedio:** perfil temporal de aprendizaje, conversación inglesa de
  baja latencia, automatizaciones y dispositivos para `BatchCooking`, extensibilidad
  estabilizada, agente simulado y ejecución aislada.
- **Horizonte avanzado:** tutor desde satélites, pronunciación específica,
  conocimiento externo, ciclo conversacional de proyectos completo y primeras
  skills o tools mediante `Controlled Self-Extension`.
- **Ideas exploratorias:** importaciones multimodales, integraciones comerciales,
  optimización avanzada, módulos completos generados, propuestas proactivas y
  cambios controlados del núcleo mediante el desarrollo normal. No constituyen
  compromisos de los MVP.

## Fase 1 — Protocolo mínimo (completada)

**Resultado utilizable:** una API local capaz de completar conversaciones y ejecutar
una herramienta permitida mediante un protocolo visible y probado.

**Dependencias:** ninguna fase anterior.

**Capacidades incluidas:**

- [x] API de conversación.
- [x] Proveedor fake secuencial.
- [x] Bucle explícito de herramientas.
- [x] Herramienta de hora, políticas iniciales, logging y tests.
- [x] Conversaciones en memoria.

**Capacidades excluidas:** persistencia, modelo real, identidad, voz e integraciones.

**Criterio de finalización:** flujo directo y tool calling deterministas cubiertos
por pruebas sin red.

## Fase 2 — Primer modelo local (completada)

**Resultado utilizable:** el mismo protocolo funciona con un modelo Ollama local y
mantiene el fake como proveedor de pruebas.

**Dependencias:** fase 1 y una instalación opcional de Ollama para smoke tests.

**Capacidades incluidas:**

- [x] Adaptador HTTP de Ollama detrás de `ILanguageProvider`.
- [x] Selección y configuración explícitas desde la API, desactivado por defecto.
- [x] Pruebas deterministas del contrato HTTP sin red ni GPU.
- [x] Smoke test de respuesta directa y tool calling con Ollama `0.32.14` y
  `qwen3:1.7b` sobre CPU.
- [x] Validación previa y cacheada de que el modelo existe y declara `tools`.
- [x] Evaluación reproducible de tool calling para `qwen3:1.7b`, separada de CI.
- [x] Pruebas de contrato reutilizables entre el fake y Ollama.
- [x] Ventana de contexto explícita y validada contra metadatos disponibles.
- [x] Cancelación de inferencia propagada hasta la petición HTTP y probada en vuelo.

**Capacidades excluidas:** routing cloud, memoria persistente y nuevas herramientas.

**Criterio de finalización:** contratos comunes y adaptador validados con tests
deterministas, más smoke test local reproducible.

## Fase 3 — Herramientas y permisos

**Resultado utilizable:** una segunda capacidad útil puede invocarse con política,
confirmación y auditoría vinculadas a su solicitud.

**Dependencias:** fases 1 y 2.

**Capacidades incluidas:**

- [x] Ampliar el catálogo con una segunda herramienta local de solo lectura útil:
  conversión de temperatura con argumentos validados.
- [x] Definir clasificación de datos y políticas de egreso por categoría antes del
  primer acceso a un servicio externo.
- [x] Definir el primer contrato de herramienta externa y una pasarela controlada
  (`Tools Gateway`) que mantenga proveedores, credenciales y acceso de red fuera
  del núcleo conversacional.
- [x] Confirmación vinculada a una solicitud concreta, no aprobación global por
  nombre.
- [x] Tratar el riesgo como multidimensional: impacto de operación, sensibilidad del
  dato, identidad, alcance solicitado, exposición externa, coste y otros efectos;
  una lectura no será de bajo riesgo por definición.
- [x] Introducir identidad, autorización y confirmación de forma incremental según las
  necesidades del siguiente vertical slice, antes de leer datos privados o ejecutar
  operaciones con efectos relevantes; no diseñar todavía un sistema completo.
- [x] Registrar en memoria eventos estructurados de solicitud, decisión de política,
  confirmación y ejecución, sin argumentos ni resultados de herramienta.
- [x] Separar el detalle de fallo que recibe el proveedor del mensaje seguro que se
  expone al cliente HTTP.
- [x] Definir y probar una clave de operación interna con `create_reminder`, la
  primera herramienta real que cambia estado. El almacén en memoria crea el resultado
  de forma atómica por principal y operación; no es una garantía distribuida ni
  durable.

**Capacidades excluidas:** un sistema completo de roles, acceso general a archivos y
acciones domésticas de escritura no justificadas por el vertical slice.

**Criterio de finalización:** la capacidad elegida demuestra autorización,
confirmación, denegación y auditoría mediante pruebas deterministas.

## Fase 4 — Persistencia y memoria de conversación

**Resultado utilizable:** conversaciones privadas y una primera fuente documental
local pueden persistirse y consultarse con identidad, propiedad y retención.

**Dependencias:** política mínima de identidad y permisos de la fase 3.

**Capacidades incluidas:**

- [x] Elegir almacenamiento tras medir el patrón de acceso: SQLite local para el
  primer vertical slice de conversaciones y trazas, según ADR 0024.
- [x] Definir un principal mínimo y vincular las conversaciones autenticadas a su
  propiedad en memoria; no usar conversación, dispositivo o habitación como identidad
  implícita. Las conversaciones anónimas siguen siendo públicas y efímeras.
- [x] Introducir la identidad de instalación y un bootstrap de un solo propietario,
  invalidado tras la configuración inicial y sin credenciales predeterminadas.
- Sustituir la API key educativa por autenticación adecuada para las interfaces
  escritas cuando exista el primer dato privado persistente; no elegir proveedor de
  identidad antes de concretar el despliegue.
- Separar desde el modelo de datos memoria personal, compartida del hogar, de módulo,
  administrativa y efímera; aplicar autorización antes de recuperar contexto para el
  modelo.
- [x] Aprobar un modelo de privacidad de almacenamiento con propiedad, retención,
  borrado selectivo, control de acceso y auditoría (ADR 0025) antes de considerar
  completa la persistencia de información privada.
- [x] Implementar notas de memoria personal explícitas, separadas de conversaciones,
  con propiedad, scopes independientes, retención y borrado selectivo. No se
  recuperan para el modelo; memoria compartida, de módulo y administrativa siguen
  requiriendo sus propios contratos y autorización.
- Evaluar protección en reposo y consecuencias de backup y restauración según el
  almacenamiento y despliegue elegidos, sin fijar todavía un mecanismo de cifrado.
- [x] Persistir conversaciones autenticadas en SQLite, manteniendo las anónimas
  efímeras en memoria, con retención configurable y borrado selectivo por propietario.
  Las trazas durables siguen pendientes.
- [x] Resolver concurrencia de turnos sobre una misma conversación dentro de un
  proceso mediante un bloqueo por conversación; una espera cancelada no modifica el
  historial ni llama al proveedor. La coordinación entre procesos sigue pendiente.
- [x] Definir una fuente documental local mediante allowlist. La única raíz actual
  resuelve la carpeta Documentos configurada por el sistema operativo o una ruta
  absoluta existente configurada, sin hardcodear usuario ni explorar discos, perfil
  completo, `AppData`, sistema o repositorios.
- [x] Implementar como primer vertical slice búsqueda directa por nombre, extensión,
  ruta relativa y fechas o metadatos básicos, sin índice persistente. Requiere el
  scope `documents.search`, limita resultados y no devuelve contenido ni rutas
  absolutas.
- [x] Separar búsqueda de documentos y lectura de contenido como capacidades y
  permisos distintos; localizar un archivo por metadatos no requiere abrirlo completo.
- [x] Añadir lectura explícita de un documento seleccionado y extracción textual
  limitada a `.txt`, `.md`, `.json` y `.csv`, con el scope `documents.read`, referencia
  protegida de quince minutos y límite de 1 MiB. Los formatos no admitidos y archivos
  mayores fallan de forma explícita.
- [x] Incorporar búsqueda textual literal y acotada en los formatos textuales ya
  permitidos, con el scope independiente `documents.content.search`. Devuelve solo
  metadatos y no crea índice, embeddings ni retención de contenido.
- Evaluar índice local, embeddings locales y búsqueda semántica después de medir
  corpus, latencia y calidad; no introducir todavía base vectorial, watcher ni worker.
- Mantener la ingesta RAG como una decisión separada y explícita: buscar o leer un
  archivo no lo convertirá automáticamente en conocimiento persistente.
- Mantener repositorios y búsqueda de código fuera de las fuentes documentales; una
  futura capacidad `LocalCodeSource` tendrá requisitos propios.
- Conservar procedencia y frescura de evidencias externas sin retener páginas
  completas por defecto; decidir la caché después de medir el patrón de acceso.
- Protección frente a prompt injection procedente de documentos.

**Capacidades excluidas:** repositorios, escritura arbitraria, RAG automático, OCR,
watchers, base vectorial y acceso de módulos a carpetas no registradas.

**Criterio de finalización:** persistencia, concurrencia, aislamiento por principal,
retención y primer flujo documental están cubiertos por pruebas y documentación.

## Fase 5 — Home Assistant

**Resultado utilizable:** Jarvis consulta un conjunto explícito de entidades de Home
Assistant y mantiene preparadas las políticas para acciones confirmadas.

**Dependencias:** herramientas, identidad y autorización mínimas de la fase 3.

**Capacidades incluidas:**

- Integración inicial de solo lectura.
- Registro explícito de entidades y capacidades permitidas.
- Clasificar cada operación doméstica por capacidad y riesgo; los invitados estarán
  denegados y los menores limitados a acciones seguras por defecto.
- Acciones con cambio de estado únicamente tras disponer de confirmación verificable.
- Exigir identidad doméstica y autenticación reforzada antes de habilitar cerraduras,
  alarmas u otras acciones sensibles.
- MQTT solo cuando existan eventos o presencia que justifiquen desacoplamiento.
- Auditoría y separación de credenciales por conector.

**Capacidades excluidas:** automatizaciones autónomas, MQTT preventivo y control sin
confirmación de dispositivos con efectos relevantes.

**Criterio de finalización:** consulta permitida, denegación, fallo del conector y
auditoría funcionan en un vertical slice reproducible.

## Fase 6 — Pipeline de voz en un único dispositivo

**Resultado utilizable:** una conversación completa por voz funciona en un único
dispositivo con captura visible y cancelable.

**Dependencias:** núcleo conversacional estable y política de egreso de la fase 3.

**Capacidades incluidas:**

- Speech-to-text y text-to-speech intercambiables.
- Wake word configurable y local cuando sea razonable.
- Streaming o captura acotada con detección de inicio y final de turno.
- Estados visibles: inactivo, escuchando, capturando, procesando, respondiendo y error.
- Introducir el contexto mínimo de dispositivo o canal de origen con uso y tests
  reales, manteniendo sencilla la API HTTP.
- Tratar la sesión de voz como usuario desconocido o invitado mientras no exista una
  prueba suficiente; wake word y voz no concederán permisos personales.
- Someter cualquier STT o TTS externo futuro a la misma política de egreso, sin
  asumir que un proveedor de voz puede recibir conversación o memoria adicional.
- Interpretar órdenes naturales de profundidad de investigación, manteniendo la
  selección automática cuando el usuario no indique una preferencia.
- Indicador físico de captura y desactivación física del micrófono.

**Capacidades excluidas:** satélites, routing entre habitaciones y Nest Hub como
micrófono.

**Criterio de finalización:** captura, transcripción, respuesta, TTS, cancelación y
estados de error se validan en un solo equipo.

## Fase 7 — Primer satélite de habitación

**Resultado utilizable:** un satélite autenticado mantiene una conversación con el
núcleo desde una habitación registrada.

**Dependencias:** fase 6 y concepto mínimo de identidad de dispositivo.

**Capacidades incluidas:**

- Elegir una plataforma solo después de un prototipo comparativo: Home Assistant
  Assist, ESP32-S3, Raspberry Pi, Android u ordenador siguen siendo candidatos.
- Registrar un satélite y asociarlo a una habitación.
- Crear una identidad técnica revocable, distinta de cualquier cuenta humana, con
  tipo, capacidades, estado y relación con quien registró el dispositivo.
- Describir capacidades de entrada, salida, pantalla, botones, indicadores y wake
  word local.
- Autenticar el dispositivo y cifrar audio y control dentro de la red doméstica.
- Monitorizar conexión, versión, estado y errores.
- Mantener continuidad de conversación dentro de la habitación.

**Capacidades excluidas:** flota multidispositivo, transferencia de conversaciones y
selección avanzada de salidas.

**Criterio de finalización:** un prototipo elegido completa el flujo autenticado con
estado observable y recuperación básica de errores.

## Fase 8 — Nest Hub como salida de habitación

**Resultado utilizable:** una conversación originada en la cocina puede responder
por audio o pantalla en su Nest Hub registrado.

**Dependencias:** primer satélite y routing mínimo de salida de la fase 7.

**Capacidades incluidas:**

- Registrar cada Google Nest Hub exclusivamente como dispositivo de salida.
- Reproducir respuestas TTS mediante Google Cast.
- Mostrar paneles de Home Assistant, avisos o contexto adecuado para una pantalla
  compartida.
- Aplicar una política de salida que pueda reducir el contenido o enviarlo a un
  dispositivo personal cuando resulte privado para una habitación compartida.
- Seleccionar el Nest Hub de la habitación que originó el turno.
- No asumir acceso al micrófono, audio, wake word ni sustitución de Google Assistant.

**Capacidades excluidas:** captura desde Nest Hub, flota de habitaciones y contenido
privado no adecuado para pantallas compartidas.

**Criterio de finalización:** Cast y visualización funcionan con selección correcta
de habitación, fallback y controles de privacidad probados.

## Fase 9 — Varios satélites y routing de habitación

**Resultado utilizable:** varias habitaciones pueden conversar sin mezclar
identidades, sesiones ni dispositivos de entrada y salida.

**Dependencias:** fases 7 y 8.

**Capacidades incluidas:**

- Registrar varios dispositivos y habitaciones.
- Seleccionar automáticamente una salida compatible y disponible.
- Aplicar fallback si el destino preferido está desconectado o silenciado.
- Separar identidad de conversación, habitación, dispositivo de entrada y dispositivo
  de salida.
- Diagnóstico y actualizaciones controladas de satélites.

**Capacidades excluidas:** conversación continua avanzada y transferencia explícita
entre habitaciones.

**Criterio de finalización:** selección, fallback, aislamiento y diagnóstico se
validan con varios dispositivos registrados.

## Fase 10 — Conversación de voz natural

**Resultado utilizable:** la interacción por voz admite turnos naturales e
interrupción sin que el asistente procese su propia salida.

**Dependencias:** pipeline de voz y routing estable de las fases 6 a 9.

**Capacidades incluidas:**

- Cancelación de eco y prevención de que LocalAssistant escuche su propio TTS.
- Interrupción de una respuesta por parte del usuario (barge-in).
- Detección robusta de final de turno y conversación continua.
- Incorporar identificación probable de hablante solo como señal contextual y
  solicitar `step-up` para acciones cuyo riesgo supere la confianza disponible.
- Recuperación ante desconexión, error o cambio de dispositivo de salida.

**Capacidades excluidas:** usar identificación biométrica como autenticación
suficiente y transferencia implícita de sesiones privadas.

**Criterio de finalización:** eco, barge-in, final de turno y recuperación se validan
en escenarios reproducibles.

## Fase 11 — Transferencia entre habitaciones

**Resultado utilizable:** el usuario transfiere explícitamente una conversación a
otra habitación sin perder continuidad ni propiedad.

**Dependencias:** fases 9 y 10, más autorización por principal.

**Capacidades incluidas:**

- Transferencia explícita y opcional de una conversación activa.
- Política para elegir la nueva entrada y salida sin mezclar sesiones de usuarios.
- Privacidad ante presencia múltiple y pantallas compartidas.
- Auditoría y cancelación de transferencias erróneas.

**Capacidades excluidas:** seguimiento automático de personas y biometría de voz.

**Criterio de finalización:** transferencia, cancelación, aislamiento y selección de
salida están cubiertos por pruebas con varias habitaciones.

## Línea transversal — Household Identity, Authorization and Guest Access

Esta línea evoluciona la identidad local educativa hacia un hogar multiusuario sin
obligar a autenticarse para una consulta pública. No define aún proveedor de
identidad, catálogo definitivo de permisos, biometría ni interfaz de administración.
Cada hito se implementará solo cuando un vertical slice necesite su comportamiento.

### Hito 0 — Frontera mínima de herramientas (completado)

La API key local aporta un principal configurado y scopes de servidor. La política
filtra herramientas fuera del LLM, reevalúa antes de ejecutar y liga confirmaciones
al principal. No representa usuarios domésticos ni propiedad de conversaciones.

### Hito 1 — Instalación, propietario e interfaces escritas

**Resultado utilizable:** una instalación crea un único propietario mediante un
bootstrap de un solo uso y protege el primer dato privado escrito.

**Incluye:** identidad de instalación; alta inicial no reclamable desde la red;
autenticación escrita; recuperación sin puerta trasera; capacidades y decisiones de
autorización fuera del LLM; separación personal y compartida; exportación, borrado y
auditoría mínimas. Se coordina con la fase 4.

### Hito 2 — Miembros domésticos y administración básica

**Resultado utilizable:** propietario, adulto y menor usan datos y módulos según
capacidades y propiedad sin mezclar perfiles.

**Incluye:** roles provisionales; concesiones específicas por usuario; ciclo de vida
de invitación, activación, cambio de rol, suspensión, revocación y eliminación; reglas
apropiadas para menores; administración y recuperación del propietario. El catálogo
de capacidades crecerá con `BatchCooking`, tutor de inglés y Home Assistant.

### Hito 3 — Invitaciones y sesiones efímeras

**Resultado utilizable:** un propietario o adulto con `users.invite_guest` crea una
sesión temporal, revocable y aislada para texto o una habitación concreta.

**Incluye:** anfitrión, caducidad, capacidades, dispositivos o habitaciones, cuotas,
proveedor, presupuesto y persistencia; memoria efímera por defecto; revocación
inmediata; ausencia de autoalta y de propagación a otras habitaciones. Un menor no
podrá invitar.

### Hito 4 — Identidades de dispositivos y servicios

**Resultado utilizable:** satélites, workers y conectores se autentican con identidad
propia y privilegios mínimos sin suplantar personas.

**Incluye:** identificador, tipo, habitación, capacidades, credencial revocable,
registro, estado, última conexión, permisos y principal registrador. Se implementa
con el primer satélite y cada servicio real, no como directorio preventivo.

### Hito 5 — Voz, step-up y privacidad contextual

**Resultado utilizable:** Jarvis distingue usuario confirmado, probable, desconocido,
invitado o insuficientemente autenticado y evita revelar o ejecutar más de lo que la
confianza permite.

**Incluye:** reconocimiento de hablante solo como señal; autenticación reforzada
mediante aplicación, PIN no hablado, passkey, biometría personal, código temporal o
aprobación; políticas por habitación y salida compartida; derivación de resultados
sensibles a un dispositivo personal. Se coordina con las fases 6 a 11.

### Hito 6 — Auditoría y administración avanzada

**Resultado utilizable:** el hogar puede revisar cambios de identidad, permisos,
invitaciones, acciones sensibles, módulos y dispositivos sin convertir la auditoría
en una copia de las conversaciones.

**Incluye:** protección de registros frente a usuarios y módulos ordinarios; cambios
de rol y capacidad; inicios y cierres de sesión; rechazos; step-up; revocaciones;
recuperación; reglas de retención; y administración proporcional al riesgo.

La secuencia conceptual es: identidad mínima de instalación, autenticación escrita,
capacidades externas al LLM, separación personal/compartida, miembros adultos y
menores, administración, invitados, sesiones efímeras, identidades técnicas,
identificación probable por voz, `step-up`, privacidad de salidas y auditoría
avanzada. Acciones domésticas sensibles, repositorios y autoextensión dependerán del
nivel correspondiente, no solo de poseer el rol de administrador.

## Líneas posteriores o paralelas

### Conversational English Coach

El tutor escrito podrá empezar tras el núcleo conversacional y no dependerá de Home
Assistant, satélites ni voz. Será una capacidad funcional fuera del núcleo y
reutilizará identidad, persistencia y trabajos cuando sus incrementos los necesiten.

#### Hito 1 — Conversación escrita y role-play

**Resultado utilizable:** el usuario completa en texto una conversación libre o una
entrevista técnica en inglés y recibe un informe revisable.

**Dependencias:** fases 1 y 2; proveedor simulado para tests y modelo local opcional.

**Incluye:** objetivos y duración de sesión; conversación libre, entrevista técnica
y daily meeting como primeros modos; dificultad y velocidad conceptuales; corrección
inmediata, por turno, solo crítica o al final; clasificación de gramática,
vocabulario, naturalidad, claridad y estilo; puntos fuertes y ejercicios posteriores.
El incremento incorporará la actividad de práctica con identidad distinta de la
conversación, activación explícita o propuesta validada por servidor, enrutamiento
directo mientras esté activa y cierre explícito no ambiguo. Sus controles universales,
transiciones idempotentes y concurrencia se validarán sin fijar todavía endpoint,
tabla, protocolo ni worker.

**Excluye:** voz, pronunciación, perfil persistente, certificación oficial y todos
los modos de role-play previstos.

**Criterio de finalización:** escenarios deterministas demuestran que la política de
corrección no rompe el role-play y que el informe conserva evidencia de la sesión.

#### Hito 2 — Perfil temporal y repaso

**Resultado utilizable:** cada usuario retoma errores y ejercicios anteriores y
puede revisar, corregir, exportar o eliminar su historial.

**Dependencias:** identidad, propiedad, retención y persistencia de la fase 4.

**Incluye:** nivel orientativo, objetivos, intereses, vocabulario, dificultades,
fluidez, política preferida, historial y ejercicios; evidencias fechadas con frase,
contexto, corrección, tipo, repeticiones y evolución; reglas inspeccionables para
detectar tendencias sin confirmarlas como rasgos permanentes. Se añadirán de forma
incremental entrevistas de recursos humanos, refinamiento, presentaciones, clientes,
incidentes, negociación y vocabulario técnico.
También definirá retención y recuperación de actividades suspendidas, expiradas o
afectadas por un fallo de proveedor, manteniendo su propiedad y relación con la
conversación sin retener datos más allá de la política aplicable.

**Excluye:** puntuaciones equivalentes a certificaciones, grabaciones y adaptación
opaca del perfil.

**Criterio de finalización:** perfiles aislados explican por qué proponen un repaso,
una observación aislada no sobrescribe una tendencia y exportación o borrado respetan
la política de retención.

#### Hito 3 — Voz inglesa de baja latencia

**Resultado utilizable:** una sesión oral mantiene ritmo conversacional, admite
interrupciones y genera después el análisis no urgente.

**Dependencias:** pipeline de voz de la fase 6, conversación natural de la fase 10 y
perfil del hito 2.

**Incluye:** VAD, STT y TTS incrementales cuando el proveedor lo permita; final de
turno rápido, barge-in, eco, cancelación de respuestas obsoletas, recuperación de
transcripción y métricas por etapa; separación entre respuesta rápida, evaluador,
informe y actualización del perfil. El análisis diferido empezará en proceso y solo
usará trabajos duraderos si necesita sobrevivir a la petición.

**Excluye:** modelos definitivos, conservación de audio por defecto y análisis
fonético inferido únicamente desde la transcripción.

**Criterio de finalización:** una prueba medible muestra latencia por etapa,
cancelación efectiva y conversación continua mientras la evaluación se completa sin
bloquear el siguiente turno.

#### Hito 4 — Pronunciación, satélites y seguimiento

**Resultado utilizable:** el usuario practica pronunciación con evidencia acústica y
realiza sesiones programadas desde una habitación autorizada.

**Dependencias:** hito 3, satélites y routing de las fases 7 a 9, más una tecnología
fonética evaluada con métricas propias.

**Incluye:** pronunciación y prosodia separadas de STT, repaso de errores, sesiones
programadas, seguimiento temporal y routing de audio o informe al dispositivo
adecuado.

**Excluye:** biometría de voz, certificación oficial y uso de pantallas compartidas
para información profesional sensible sin confirmación.

**Criterio de finalización:** una evaluación reproducible distingue error acústico de
transcripción y una sesión conserva privacidad y continuidad entre dispositivos.

### Plataforma de módulos y BatchCooking

`BatchCooking` será el primer módulo doméstico de referencia y no formará parte del
núcleo. Esta línea comenzará tras disponer del mínimo necesario de herramientas,
identidad, autorización y persistencia de las fases 3 y 4. Puede avanzar por texto
en paralelo a Home Assistant y voz. Sus necesidades reales corregirán el contrato
antes de estabilizar un SDK, según el
[ADR 0007](adr/0007-use-batch-cooking-to-discover-module-contracts.md).

#### Hito 0 — Módulos mínimos y recursos controlados

**Resultado utilizable:** un módulo de prueba se registra, declara capacidades y
accede en lectura únicamente a recursos locales asignados a su ámbito.

**Dependencias:** identidad, autorización, confirmación y almacenamiento mínimos de
las fases 3 y 4.

**Incluye:** registro y descubrimiento mínimos; versión, configuración, activación,
permisos, persistencia aislada y un test de contrato; `Controlled Local Resources`
con carpetas registradas, formatos y tamaños limitados, hash, procedencia, acceso
revocable y lectura separada de escritura. La lectura inicial incorporará de forma
incremental texto, Markdown, JSON y CSV; Excel se añadirá con el caso real de la
plantilla, no como lector universal.

**Excluye:** SDK estable, carga arbitraria de extensiones, escritura de archivos,
watchers, ejecución de contenido y rutas propuestas libremente por el modelo.

**Criterio de finalización:** dos módulos de prueba no pueden acceder a recursos o
datos del otro y la política deniega rutas, formatos y tamaños fuera del ámbito.

#### Hito 1 — Preparación y migración trazable

**Resultado utilizable:** el usuario previsualiza y confirma la migración de los
recursos actuales a un estado estructurado de `BatchCooking` sin alterar originales.

**Dependencias:** hito 0 y corpus real inventariado por el usuario.

**Incluye:** separación del prompt en procedimiento, reglas, preferencias,
valoraciones, platos, recetas, inventario, menús, plantilla e instrucciones;
extracción, advertencias, supuestos y confirmación; soporte de los formatos realmente
presentes, incluida la plantilla Excel; procedencia, versión o hash y comparación de
cambios; historial temporal de preferencias, valoraciones y feedback. Alergias y
restricciones estables no perderán prioridad por antigüedad.

**Excluye:** relectura completa del archivo en cada semana, importación silenciosa,
sobrescritura de originales, OCR, imágenes, Word, PDF y aprendizaje opaco.

**Criterio de finalización:** una migración reproducible distingue importado,
ignorado, conflictivo y pendiente de confirmar, conserva la fuente y no duplica ni
elimina información al repetirse sobre la misma versión.

#### Hito 2 — BatchCooking MVP escrito

**Resultado utilizable:** un hogar prepara por texto una semana completa y obtiene
menú aprobado, plan ordenado y lista de compra revisable, con historial local.

**Dependencias:** hitos 0 y 1, principal doméstico y persistencia local.

**Incluye:** miembros; preferencias, rechazos y restricciones básicas; catálogo
sencillo de platos; valoraciones explícitas; inventario manual con certeza, cantidad,
caducidad y ubicación; personas y comidas cubiertas; tiempo, equipamiento y reglas
familiares; propuesta y ajustes de menú; preparaciones, dependencias, paralelismo,
conservación y descongelación; compra derivada del inventario confirmado; feedback e
historial básico. Las reglas serán inspeccionables y explicarán restricciones,
suposiciones y compromisos.

**Excluye:** voz, Nest Hub, APIs externas, consejo médico, nutrición detallada,
optimización matemática compleja, predicción automática, visión artificial y
generación del módulo por Jarvis.

**Criterio de finalización:** un escenario semanal determinista recorre recopilación,
inventario, menú, aprobación, preparación, compra y feedback; aprobar el menú no
consume inventario y toda modificación relevante queda trazada.

#### Hito 3 — Contrato de módulos y automatizaciones

**Resultado utilizable:** el contrato corregido con `BatchCooking` soporta un segundo
módulo pequeño y automatizaciones domésticas configurables.

**Dependencias:** uso real y revisión del MVP.

**Incluye:** estabilización incremental de manifiesto, compatibilidad, migraciones,
health checks, eventos, herramientas, UI opcional, activación y tests de contrato;
recordatorios de recopilación semanal, inventario, caducidad, descongelación, menú,
sesión de cocina, feedback y compra pendiente. Todas las automatizaciones serán
desactivables y respetarán horarios de silencio.

**Excluye:** marketplace, ejecución de módulos no confiables y autoextensión.

**Criterio de finalización:** el segundo módulo usa el contrato sin introducir su
dominio en el núcleo y cada automatización puede configurarse, auditarse y apagarse.

#### Hito 4 — Cocina por voz y dispositivos

**Resultado utilizable:** durante la preparación se consultan y actualizan manos
libres pasos, tareas, inventario y temporizadores, con salida visual en la cocina.

**Dependencias:** MVP, pipeline de voz, satélite y Nest Hub de las fases 6 a 9.

**Incluye:** siguiente paso, tareas completadas o canceladas, cantidades, ajustes del
plan, temporizadores, consumo de inventario, compra y descongelación; pantalla con
paso actual, cola, alertas, ingredientes y estado. Un satélite capturará voz y el
Nest Hub seguirá siendo únicamente salida Cast.

**Excluye:** micrófono del Nest Hub, confirmaciones sensibles basadas solo en voz y
exposición de datos privados en pantallas compartidas.

**Criterio de finalización:** una sesión interrumpible y ruidosa mantiene estado
coherente entre texto, voz y pantalla, con confirmaciones breves y recuperables.

#### Hito 5 — Capacidades avanzadas

**Resultado utilizable:** el módulo incorpora una capacidad avanzada cada vez sobre
datos trazables y métricas de utilidad reales.

**Dependencias:** MVP estable y necesidad medida.

**Incluye de forma incremental:** exportación Excel preservando plantilla y original;
formatos y comparación de versiones avanzados; importación de recetas; códigos de
barras, tickets e imágenes; presupuesto, nutrición informativa, sustituciones,
caducidades, temporada, calendarios, listas externas, supermercados, historial
avanzado e importación o exportación de datos.

**Excluye:** adoptar todos los proveedores o formatos a la vez y recomendaciones
médicas no supervisadas.

**Criterio de finalización:** cada vertical slice tiene proveedor sustituible cuando
corresponda, permisos mínimos, pruebas, métricas y posibilidad de desactivación.

### Enrutamiento híbrido

- Tratar la política de privacidad como restricción dura: dificultad, coste,
  latencia y disponibilidad solo eligen entre proveedores autorizados para las
  categorías del payload.
- Mantener local cualquier dato `DENY` aunque el modelo local carezca de capacidad;
  como máximo, usar una parte pública o saneada que siga siendo útil, continuar con
  capacidad reducida o comunicar la limitación sin decidir todavía la experiencia.
- Proveedores externos opcionales y desactivados por defecto.
- Validación del payload final por la política de egreso antes de llamar a un LLM,
  servicio de embeddings u otro proveedor externo.
- Evaluaciones para justificar cada decisión de routing.

Puede avanzar después del modelo local y la persistencia; no bloquea el primer
satélite, cuyo procesamiento podrá ser completamente local.

### Conocimiento externo e Internet

Esta línea comenzará tras establecer en la fase 3 las políticas de herramientas y
su primer contrato externo de solo lectura. Podrá crecer en paralelo a memoria y
voz, pero no habilitará acceso arbitrario a Internet para el modelo. Toda salida a
servicios externos atravesará un `Tools Gateway` controlado y las herramientas
seguirán registradas mediante allowlist.

#### Hito 0 — Clasificación y fronteras de confianza

- Definir categorías extensibles de datos y políticas como `DENY`,
  `ALLOW_WHEN_REQUIRED`, `ALLOW_SANITIZED` y `ALLOW`, con denegación predeterminada
  para categorías nuevas o desconocidas.
- Mantener en `DENY` la salida automática de código fuente, repositorios, archivos y
  documentos locales, RAG, bases de datos, memoria, historial de conversación,
  configuración privada, variables de entorno, credenciales y secretos.
- Permitir `LOCATION` solo cuando sea necesaria para la petición; permitir
  `SEARCH_QUERY` únicamente tras minimización y saneado, y `PUBLIC_DATA` según la
  política y el proveedor aplicables.
- Resolver localmente la ubicación mediante proveedores conceptuales de hogar,
  dispositivo móvil con permiso y ubicación explícita del usuario. Elegir la fuente
  mínima suficiente sin añadir contexto privado relacionado.
- Clasificar y validar el payload externo final, incluidas consultas, resúmenes,
  identificadores, nombres internos, hostnames y URLs derivados de datos protegidos.
- Aplicar la política a toda comunicación saliente: herramientas, LLM cloud, STT,
  TTS, embeddings, telemetría, analítica, informes de fallo, actualizaciones y SDKs
  de terceros.
- Diseñar una frontera técnica de egreso que los componentes internos no puedan
  omitir cuando la topología permita imponer red, contenedores o firewall; no
  implementar todavía esas restricciones.
- Aislar todo contenido recuperado como datos no confiables y transformarlo en
  evidencia antes del razonamiento local; nunca podrá modificar instrucciones,
  políticas, permisos ni selección de herramientas.
- Auditar localmente destino, proveedor, categorías autorizadas, propósito, tamaño,
  resultado y tiempos sin copiar payloads sensibles ni credenciales.
- Añadir pruebas de privacidad para minimización, payloads derivados, denegación,
  ubicación permitida, prompt injection indirecta y bypass de egreso. Cada futura
  herramienta externa deberá superar este conjunto antes de habilitarse.

#### Hito 1 — Pasarela y primer vertical slice

- Definir contratos independientes del proveedor para consultas y resultados
  externos, sin filtrar SDKs al núcleo conversacional.
- Centralizar selección de proveedor, credenciales, destinos permitidos, timeouts,
  rate limits, presupuestos de coste, caché, errores, auditoría y validación final
  de egreso.
- Implementar primero una búsqueda web de solo lectura con un proveedor sustituible
  y resultados que conserven URL, título, fragmento, fecha conocida y momento de
  recuperación.
- Evaluar calidad, latencia, coste y privacidad con un conjunto reproducible antes
  de añadir más proveedores.

#### Hito 2 — Lectura web y fuentes especializadas

- Añadir un lector de páginas limitado a HTTP/HTTPS, tipos y tamaños permitidos,
  con defensas frente a SSRF, redirecciones inseguras y contenido no confiable.
- Incorporar Wikipedia mediante un contrato especializado cuando aporte mejor
  estructura o trazabilidad que la búsqueda general.
- Añadir adaptadores sustituibles de mapas/routing, tiempo meteorológico y recetas
  como vertical slices separados, con ubicación y credenciales minimizadas.
- Validar que mapas, routing, navegación, meteorología y búsqueda de lugares reciben
  solo la ubicación necesaria: hogar resuelto localmente, posición móvil autorizada
  o ubicación explícita, sin conversación, memoria ni perfil no relacionados.
- No fijar todavía servicios comerciales concretos ni asumir que una única fuente
  resulta suficiente para todas las consultas.

#### Hito 3 — Consultas multifuente y evidencias

- Introducir un planificador de consultas que elija herramientas según intención,
  actualidad, ubicación, coste y evidencia ya disponible.
- Ejecutar en paralelo herramientas independientes con concurrencia, timeout y
  presupuesto totales acotados; conservar el orden cuando existan dependencias.
- Normalizar y deduplicar resultados mediante un agregador de evidencias que evalúe
  relevancia, calidad de fuente, frescura y posibles conflictos.
- Generar la respuesta a partir de evidencias agregadas y conservar trazabilidad
  suficiente para explicar fuentes, fechas y desacuerdos cuando se solicite.

#### Hito 4 — Profundidad adaptativa

- Soportar búsquedas rápidas, consultas multifuente normales e investigación
  profunda sin obligar al usuario a elegir un modo técnico.
- Inferir la profundidad inicial y escalar cuando falte evidencia, las fuentes
  discrepen, la consulta sea compleja o una verificación adicional cambie
  materialmente la confianza.
- Permitir que instrucciones naturales como «dímelo rápido», «míralo bien»,
  «compruébalo en varias fuentes» o «investígalo a fondo» prevalezcan sobre la
  selección automática, también desde voz.
- Aplicar presupuestos y límites explícitos incluso cuando se solicite investigación
  profunda; profundidad no significará acceso o coste ilimitados.

#### Hito 5 — Planificación compuesta avanzada

- Representar planes con pasos secuenciales y paralelos, dependencias, criterios de
  parada y resultados parciales, manteniendo cada ejecución dentro de herramientas
  registradas y políticas externas al modelo.
- Resolver peticiones que combinen búsqueda, selección, mapas, duración y hora de
  salida en una única respuesta respaldada por fuentes.
- Reutilizar confirmación, identidad, autorización e idempotencia antes de que un
  plan incluya herramientas que cambien estado o produzcan costes relevantes.
- Evaluar planes fallidos, fuentes contradictorias, resultados parciales y
  cancelación antes de delegar investigaciones largas a un posible worker.

### Conversational Project Lifecycle

Esta línea podrá empezar en texto cuando las fases 3 y 4 aporten autorización,
principal y persistencia suficientes para aislar proyectos y conservar decisiones.
No depende de la voz: ese canal se incorporará después de la fase 6 sobre el mismo
estado. Las notificaciones entre dispositivos llegarán cuando exista routing de
habitaciones. La separación estable entre definición, especificación, ejecución y
publicación se recoge en el
[ADR 0006](adr/0006-separate-project-definition-execution-and-publication.md).

#### Hito 1 — Definición escrita y estado estructurado

- Crear y reanudar varios proyectos aislados desde conversaciones escritas.
- Mantener objetivo, usuarios, alcance, requisitos, restricciones, riesgos,
  preguntas, alternativas, decisiones, criterios de aceptación y roadmap.
- Distinguir hechos confirmados, inferencias, preguntas abiertas y contenido
  sustituido, con historial y detección explícita de contradicciones.
- Separar transcripción, resumen operativo, estado estructurado y documentos
  derivados; definir estados revisables sin fijar todavía el esquema definitivo.
- Persistir el estado bajo propiedad, autorización, retención y borrado de la fase 4.

#### Hito 2 — Especificaciones incrementales y revisión

- Generar y actualizar documentos de visión, arquitectura, roadmap, seguridad y
  ADRs a partir del estado confirmado, sin presentar inferencias como decisiones.
- Permitir edición, comparación, revisión y confirmación incremental de cada
  documento; conservar versiones obsoletas o sustituidas con su motivo.
- Detectar divergencias entre conversación, estado y documentos antes de declarar
  una especificación lista para implementar.

#### Hito 3 — Asociación segura con repositorios

- Asociar explícitamente un proyecto a un repositorio o workspace permitido, sin
  incluir repositorios dentro de las fuentes documentales genéricas.
- Inspeccionar en modo de solo lectura estructura, reglas y estado necesarios para
  validar viabilidad, criterios de aceptación y primer incremento vertical.
- Exigir selección visible de repositorio y rama de trabajo; no crear ni modificar
  repositorios como efecto implícito de una conversación.

#### Hito 4 — Protocolo con agente simulado

- Definir una frontera independiente del proveedor para agentes locales, externos,
  simulados o especializados, sin SDKs de proveedor en el dominio.
- Tratar «impleméntalo» como inicio de comprobación, resumen, propuesta y aprobación,
  no como ejecución ni permiso ilimitado.
- Simular plan, progreso, cancelación, resultados, errores y artefactos para probar
  estados, políticas y experiencia antes de ejecutar código real.
- Mostrar agente, proveedor, herramientas, recursos, duración y coste estimado antes
  de solicitar una autorización acotada.

#### Hito 5 — Ejecución aislada en repositorios de prueba

- Ejecutar únicamente el alcance aprobado dentro de un workspace desechable con
  filesystem, procesos, red, tiempo y recursos limitados.
- Validar argumentos y allowlists de herramientas; el agente de programación no se
  convertirá en una shell genérica accesible al modelo conversacional.
- Entregar diff, build, tests, resumen, trazabilidad y artefactos para revisión;
  cancelar con seguridad y no crear commits por defecto.
- Empezar con repositorios de prueba y agentes locales antes de evaluar código real
  privado o cualquier proveedor externo.

#### Hito 6 — Git y publicación controlados

- Separar autorizaciones para editar, crear commit o rama local, publicar rama,
  abrir pull request, desplegar y borrar o sobrescribir recursos.
- Revisar el diff y los resultados de build y tests antes de integrar; favorecer
  operaciones recuperables y ofrecer cancelación o reversión donde sea posible.
- Añadir integración real con un host Git únicamente mediante un vertical slice
  autorizado, auditable y probado; no asumir GitHub como contrato del dominio.

#### Hito 7 — Trabajos largos, voz y continuidad entre dispositivos

- Introducir trabajos duraderos y un posible worker solo cuando una ejecución deba
  sobrevivir a una petición o reinicio; no elegir broker de forma preventiva.
- Persistir estado, progreso y artefactos con timeout, cancelación, reintentos
  acotados y recuperación tras reinicio.
- Permitir preguntar por el estado y recibir notificaciones en un canal autorizado,
  sin exponer código, secretos o resultados sensibles en dispositivos compartidos.
- Continuar por texto o voz sobre el mismo proyecto; exigir confirmación reforzada
  en un canal autenticado para publicar, desplegar o ejecutar acciones irreversibles.
- Aplicar la política de egreso al agente y al repositorio: falta de capacidad local
  nunca permitirá enviar `SOURCE_CODE` o `REPOSITORY_DATA` marcados `DENY`.

### Controlled Self-Extension

Esta línea avanzada no comenzará hasta estabilizar el contrato de módulos con
`BatchCooking` y completar agente simulado, ejecución aislada, revisión de diffs y
publicación controlada. Reutilizará `Conversational Project Lifecycle`; no creará un
segundo protocolo de generación de código. El
[ADR 0011](adr/0011-forbid-self-approval-and-active-instance-mutation.md) prohíbe
autoaprobación y cambios directos sobre la instancia activa.

#### Hito 1 — Clasificación y ciclo simulado

**Resultado utilizable:** Jarvis convierte una petición de ampliación en una
especificación y simula su ciclo completo sin generar ni instalar código.

**Dependencias:** contrato de módulos validado por un segundo módulo y protocolo con
agente simulado de `Conversational Project Lifecycle`.

**Incluye:** clasificación entre skill, tool, connector, module, capacidad de
satélite y cambio del núcleo; elección del mecanismo mínimo; manifiesto conceptual,
permisos y recursos; estados de propuesta, build, tests, revisión, aprobación,
instalación, activación, suspensión y retirada; autorizaciones independientes y
artefactos simulados.

**Excluye:** ejecución de comandos, código real, instalación, activación y cambios en
el núcleo o instancia activa.

**Criterio de finalización:** pruebas deterministas demuestran clasificación,
transiciones permitidas y rechazo de saltos, autoaprobación o elevación de permisos.

#### Hito 2 — Primera skill o tool generada

**Resultado utilizable:** una skill o tool pequeña y reversible se genera en un
repositorio de prueba y queda como diff revisable, sin instalarse en producción.

**Dependencias:** ciclo simulado, sandbox, agente intercambiable, trabajos, build,
tests y Git controlado de los hitos 4 a 7 de `Conversational Project Lifecycle`.

**Incluye:** requisitos, riesgos, rama y alcance; dependencias, filesystem, red,
secretos, tiempo y coste declarados; build, tests independientes, análisis de
seguridad y supply-chain, revisión de permisos, diff y aprobaciones de integración.

**Excluye:** connector con credenciales reales, módulo completo, modificación del
núcleo, autoaprobación, publicación o activación implícitas.

**Criterio de finalización:** el artefacto puede rechazarse o integrarse en un host de
prueba y ninguna capacidad posterior ocurre sin su autorización verificable.

#### Hito 3 — Instalación, activación y rollback controlados

**Resultado utilizable:** una extensión revisada se instala y activa en un entorno
no activo, se monitoriza y puede suspenderse o revertirse.

**Dependencias:** hito 2, artefactos versionados, compatibilidad, health checks y
estrategias de migración y rollback probadas.

**Incluye:** aprobaciones separadas de integración, instalación, activación y
despliegue; health checks, monitorización, desactivación automática por fallos,
rollback de código y tratamiento explícito del estado migrado.

**Excluye:** mutación en caliente de la instancia activa, políticas modificables por
la extensión y despliegue sin dispositivo autenticado.

**Criterio de finalización:** un fallo inducido suspende la extensión y restaura una
versión conocida sin permitir que la extensión altere autorización o auditoría.

#### Hito 4 — Ampliaciones de módulos y conectores

**Resultado utilizable:** Jarvis propone una ampliación pequeña y reversible de un
módulo existente, como un exportador o control de presupuesto, y la entrega mediante
el flujo completo.

**Dependencias:** hitos 1 a 3 y métricas de uso de un módulo estable.

**Incluye progresivamente:** skills, tools, exportadores y ampliaciones acotadas de
`BatchCooking`; después, connectors, capacidades de satélite y módulos completos con
políticas específicas.

**Excluye inicialmente:** generación de un módulo funcional completo, propuestas
proactivas y cambios del núcleo. Estos últimos seguirán el desarrollo humano normal
y permanecerán exploratorios aunque Jarvis ayude a especificarlos.

**Criterio de finalización:** cada tipo demuestra permisos, compatibilidad, revisión,
activación y rollback propios antes de habilitar el siguiente nivel de riesgo.

### Herramientas técnicas

- Conectores API y MCP con allowlists y permisos mínimos.
- Asistente de programación local limitado a workspaces aprobados.
- Planificación, vista previa y confirmación antes de cambios.

### Calidad y publicación

- [x] Mostrar cobertura automatizada en CI y en la portada, inicialmente sin umbral.
- Añadir badge de última release cuando exista la primera versión SemVer publicada.
- Añadir badge de análisis de seguridad cuando haya un workflow estable y accionable.
- Añadir versiones de paquete o imagen solo si el proyecto publica esos artefactos.
- Mantener fuera de la portada métricas manuales o badges sin una fuente automatizada.

## Decisiones pospuestas

- Modelo definitivo de autorización de herramientas y combinación de impacto,
  sensibilidad, identidad, alcance, egreso, confirmación y coste; evolucionará con
  vertical slices reales sin anticipar RBAC o ABAC completos.
- Proveedor de identidad, esquema de usuarios y permisos, recuperación tras borrar o
  perder el estado de instalación, experiencia administrativa y tecnología de
  identificación de hablante; se elegirán con el despliegue y los canales reales.
- Tecnología concreta de protección en reposo y política de backups; se elegirán
  junto con el almacenamiento y modelo de despliegue.
- Formato definitivo del manifiesto, mecanismo de carga, packaging, aislamiento de
  procesos y SDK de módulos; `BatchCooking` y un segundo módulo aportarán evidencia
  antes de estabilizarlos.
- API y almacenamiento concretos de `Controlled Local Resources`, librerías para
  texto, datos tabulares y Excel, y política de sincronización tras cambios; cada
  formato se elegirá mediante un vertical slice y archivos de prueba hostiles.
- Esquema de inventario, preferencias temporales y procedencia, ponderación de
  tendencias y estrategia de migración física; se validarán contra una copia
  saneada del sistema actual sin convertir el prompt en lógica permanente.
- Proveedores de recetas, calendarios, listas, supermercados, nutrición, OCR,
  códigos de barras e imágenes; quedan fuera del MVP de `BatchCooking`.
- Forma final del tutor de inglés dentro del sistema de extensiones, modelos de
  conversación y evaluación, objetivo de latencia, tecnología de pronunciación y
  política opcional de grabaciones; se decidirán con el incremento escrito y pruebas
  de voz medibles.
- Formato final de manifiesto y estados de confianza para autoextensión, firma y
  procedencia de artefactos, host de prueba, promoción entre entornos y herramientas
  de análisis de supply-chain; se elegirán antes del primer artefacto instalable.
- Formatos documentales definitivos, librerías de extracción, estrategia de búsqueda
  textual, índice, almacén y modelo local de embeddings; se decidirán con un corpus
  real y métricas. OCR, watchers e indexación de código permanecen fuera del primer
  vertical slice.
- Taxonomía definitiva de categorías, reglas de herencia, duración del permiso de
  ubicación y experiencia de consentimiento; el modelo inicial debe poder crecer
  sin rediseñar la frontera.
- Mecanismo concreto para impedir bypass de egreso —proxy, políticas de firewall,
  aislamiento por proceso o contenedor— hasta conocer la topología desplegada.
- Proveedores y precisión de `HomeLocation` y `MobileCurrentLocation`, tratamiento
  de ubicación obsoleta y persistencia; la ubicación explícita seguirá disponible
  sin requerir estas integraciones.
- Proveedores concretos de búsqueda, lectura, Wikipedia, mapas, meteorología y
  recetas; se elegirán por calidad, privacidad, coste, límites y condiciones de uso.
- Forma física del `Tools Gateway`: comenzará como límite modular y solo se separará
  en proceso si aparecen necesidades medibles de aislamiento o escalado.
- Estrategia de caché, almacenamiento de evidencias, ranking y umbrales de calidad;
  requieren consultas y métricas reales.
- Representación interna del plan multiherramienta y criterios exactos de profundidad;
  se decidirán con los primeros vertical slices, sin delegar las políticas al LLM.
- Esquema y almacenamiento definitivos del estado de proyecto, documentos y
  trabajos, incluidos nombres finales de estados y estrategia de versionado.
- Agentes de programación, proveedores, protocolo del gateway, tecnología de
  sandbox y estimación de costes; se elegirán con evaluaciones y repositorios de
  prueba, no desde la documentación inicial.
- Integración con proveedor Git, experiencia exacta de revisión y confirmación por
  voz, y política de excepciones de egreso por repositorio.
- `LocalAssistant.Worker`: aparecerá con la primera tarea larga que necesite
  sobrevivir a una petición HTTP.
- Broker de mensajes: solo si API, worker o satélites necesitan desacoplamiento
  duradero; el streaming de audio no lo justifica automáticamente.
- Base vectorial: se elegirá con un corpus y métricas reales.
- Hardware de satélite: se decidirá después del pipeline de voz y un prototipo.
- Protocolo de satélite: no se elige aún entre APIs, WebSocket, Home Assistant,
  MQTT u otra opción.
- Open WebUI: posible canal externo, no parte del núcleo.
- OpenTelemetry: se añadirá con un backend o necesidad de diagnóstico concreta.
- Dependabot: se reconsiderará al estabilizar la política de actualizaciones.
