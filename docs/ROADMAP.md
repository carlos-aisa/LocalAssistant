# Roadmap

El orden expresa dependencias técnicas, no fechas. Cada fase debe aportar un flujo
vertical comprobable antes de introducir infraestructura adicional. En particular,
la voz se valida primero en un único dispositivo; solo después se distribuye por
habitaciones.

## Fase 1 — Protocolo mínimo (completada)

- API de conversación.
- Proveedor fake secuencial.
- Bucle explícito de herramientas.
- Herramienta de hora, políticas iniciales, logging y tests.
- Conversaciones en memoria.

## Fase 2 — Primer modelo local (completada)

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

## Fase 3 — Herramientas y permisos

- Ampliar el catálogo con una segunda herramienta de solo lectura útil.
- Definir clasificación de datos y políticas de egreso por categoría antes del
  primer acceso a un servicio externo.
- Definir el primer contrato de herramienta externa y una pasarela controlada
  (`Tools Gateway`) que mantenga proveedores, credenciales y acceso de red fuera
  del núcleo conversacional.
- Confirmación vinculada a una solicitud concreta, no aprobación global por nombre.
- Tratar el riesgo como multidimensional: impacto de operación, sensibilidad del
  dato, identidad, alcance solicitado, exposición externa, coste y otros efectos;
  una lectura no será de bajo riesgo por definición.
- Introducir identidad, autorización y confirmación de forma incremental según las
  necesidades del siguiente vertical slice, antes de leer datos privados o ejecutar
  operaciones con efectos relevantes; no diseñar todavía un sistema completo.
- Auditoría, idempotencia y políticas de exposición de resultados.

## Fase 4 — Persistencia y memoria de conversación

- Elegir almacenamiento tras medir el patrón de acceso.
- Definir un concepto mínimo de `User` o `Principal` y el alcance de propiedad y
  acceso antes de persistir memoria personal; no usar conversación, dispositivo o
  habitación como identidad implícita.
- Aprobar un modelo de privacidad de almacenamiento con propiedad, retención,
  borrado selectivo, control de acceso y auditoría antes de considerar completa la
  persistencia de información privada.
- Evaluar protección en reposo y consecuencias de backup y restauración según el
  almacenamiento y despliegue elegidos, sin fijar todavía un mecanismo de cifrado.
- Persistir conversaciones y trazas con retención configurable.
- Resolver concurrencia de turnos sobre una misma conversación.
- Definir fuentes documentales locales mediante allowlist. La primera resolverá la
  carpeta Documentos configurada por el sistema operativo, sin hardcodear usuario o
  ruta y sin explorar discos, perfil completo, `AppData`, sistema o repositorios.
- Implementar como primer vertical slice búsqueda directa por nombre, extensión,
  ruta relativa y fechas o metadatos básicos, sin índice persistente si el corpus y
  rendimiento no lo justifican.
- Separar búsqueda de documentos y lectura de contenido como capacidades y permisos
  distintos; localizar un archivo por metadatos no requerirá abrirlo completo.
- Añadir después lectura explícita de un documento seleccionado y extracción textual
  limitada a formatos soportados, tamaños y recursos acotados. Los formatos no
  admitidos fallarán de forma explícita.
- Incorporar búsqueda textual en contenido solo cuando aporte valor y evaluar índice
  local, embeddings locales y búsqueda semántica después de medir corpus, latencia y
  calidad; no introducir todavía base vectorial, watcher ni worker.
- Mantener la ingesta RAG como una decisión separada y explícita: buscar o leer un
  archivo no lo convertirá automáticamente en conocimiento persistente.
- Mantener repositorios y búsqueda de código fuera de las fuentes documentales; una
  futura capacidad `LocalCodeSource` tendrá requisitos propios.
- Conservar procedencia y frescura de evidencias externas sin retener páginas
  completas por defecto; decidir la caché después de medir el patrón de acceso.
- Protección frente a prompt injection procedente de documentos.

## Fase 5 — Home Assistant

- Integración inicial de solo lectura.
- Registro explícito de entidades y capacidades permitidas.
- Acciones con cambio de estado únicamente tras disponer de confirmación verificable.
- MQTT solo cuando existan eventos o presencia que justifiquen desacoplamiento.
- Auditoría y separación de credenciales por conector.

## Fase 6 — Pipeline de voz en un único dispositivo

- Speech-to-text y text-to-speech intercambiables.
- Wake word configurable y local cuando sea razonable.
- Streaming o captura acotada con detección de inicio y final de turno.
- Estados visibles: inactivo, escuchando, capturando, procesando, respondiendo y error.
- Introducir el contexto mínimo de dispositivo o canal de origen con uso y tests
  reales, manteniendo sencilla la API HTTP.
- Someter cualquier STT o TTS externo futuro a la misma política de egreso, sin
  asumir que un proveedor de voz puede recibir conversación o memoria adicional.
- Interpretar órdenes naturales de profundidad de investigación, manteniendo la
  selección automática cuando el usuario no indique una preferencia.
- Indicador físico de captura y desactivación física del micrófono.

## Fase 7 — Primer satélite de habitación

- Elegir una plataforma solo después de un prototipo comparativo: Home Assistant
  Assist, ESP32-S3, Raspberry Pi, Android u ordenador siguen siendo candidatos.
- Registrar un satélite y asociarlo a una habitación.
- Describir capacidades de entrada, salida, pantalla, botones, indicadores y wake
  word local.
- Autenticar el dispositivo y cifrar audio y control dentro de la red doméstica.
- Monitorizar conexión, versión, estado y errores.
- Mantener continuidad de conversación dentro de la habitación.

## Fase 8 — Nest Hub como salida de habitación

- Registrar cada Google Nest Hub exclusivamente como dispositivo de salida.
- Reproducir respuestas TTS mediante Google Cast.
- Mostrar paneles de Home Assistant, avisos o contexto adecuado para una pantalla
  compartida.
- Seleccionar el Nest Hub de la habitación que originó el turno.
- No asumir acceso al micrófono, audio, wake word ni sustitución de Google Assistant.

## Fase 9 — Varios satélites y routing de habitación

- Registrar varios dispositivos y habitaciones.
- Seleccionar automáticamente una salida compatible y disponible.
- Aplicar fallback si el destino preferido está desconectado o silenciado.
- Separar identidad de conversación, habitación, dispositivo de entrada y dispositivo
  de salida.
- Diagnóstico y actualizaciones controladas de satélites.

## Fase 10 — Conversación de voz natural

- Cancelación de eco y prevención de que LocalAssistant escuche su propio TTS.
- Interrupción de una respuesta por parte del usuario (barge-in).
- Detección robusta de final de turno y conversación continua.
- Recuperación ante desconexión, error o cambio de dispositivo de salida.

## Fase 11 — Transferencia entre habitaciones

- Transferencia explícita y opcional de una conversación activa.
- Política para elegir la nueva entrada y salida sin mezclar sesiones de usuarios.
- Privacidad ante presencia múltiple y pantallas compartidas.
- Auditoría y cancelación de transferencias erróneas.

## Líneas posteriores o paralelas

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
- Tecnología concreta de protección en reposo y política de backups; se elegirán
  junto con el almacenamiento y modelo de despliegue.
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
