# Visión

LocalAssistant busca ser un asistente doméstico y técnico que priorice la ejecución local,
la privacidad y la capacidad de entender cada pieza del sistema.

El objetivo no es ensamblar productos opacos, sino aprender y mantener explícitos
los límites entre conversación, inferencia, herramientas, memoria, conectores,
seguridad y operación.

## Principios

1. **Privacidad antes que routing:** los datos protegidos por la política permanecen
   locales aunque falten recursos o capacidad. Dificultad, latencia y coste solo
   permiten elegir entre proveedores autorizados para las categorías implicadas.
2. **Proveedores intercambiables:** el dominio no depende del SDK de un proveedor.
3. **Capacidades explícitas:** el modelo solicita herramientas; nunca obtiene
   acceso arbitrario al sistema.
4. **Autorización y confirmación proporcionales al riesgo:** lectura, modificación y
   ejecución no determinan por sí solas el riesgo. También importan sensibilidad,
   identidad, alcance, exposición externa, coste y otros efectos significativos.
5. **Trazabilidad sin vigilancia:** registrar decisiones técnicas y tiempos sin
   copiar indiscriminadamente conversaciones privadas.
6. **Crecimiento justificado:** un nuevo proceso, almacén o broker debe resolver
   una necesidad observable.
7. **Presencia ubicua y visible:** el asistente podrá estar disponible en varias
   habitaciones, pero toda captura de audio deberá ser perceptible, controlable y
   desactivable físicamente.
8. **Conocimiento externo controlado:** Internet y los servicios de terceros serán
   herramientas explícitas, acotadas y trazables; el modelo no tendrá acceso de red
   irrestricto ni presentará todas las fuentes como igualmente fiables.
9. **Control humano por transiciones:** una intención conversacional puede preparar
   trabajo, pero editar, publicar, desplegar o realizar otra acción sensible exige
   alcance visible y autorización adecuada para esa transición concreta.
10. **Identidad doméstica explícita:** rol, capacidades, propiedad, contexto y
    confianza de autenticación se combinan fuera del modelo. Una voz, habitación,
    conversación o dispositivo compartido no identifican por sí solos a una persona.

## Resultado a largo plazo

Un núcleo .NET coordinará modelos locales y externos bajo una política de privacidad
que prevalece sobre cualquier decisión de routing. Complejidad, razonamiento,
compatibilidad con herramientas, latencia, coste, hardware disponible, preferencias
del usuario y confidencialidad solo decidirán entre proveedores que puedan recibir
el payload autorizado.
Un modelo externo podrá procesar una petición pública o saneada, pero no recibirá
automáticamente conversación, memoria, documentos, RAG, código, repositorio ni
configuración privada cuando el modelo local resulte insuficiente. Servicios
especializados podrán ocuparse de inferencia, voz, automatización doméstica, eventos
y búsqueda semántica. Los canales de texto, voz y programación compartirán políticas
y capacidades, pero podrán adaptar su experiencia.

Para preguntas que dependan de conocimiento externo o reciente, el núcleo podrá
combinar búsqueda general, lectura web y fuentes especializadas. Seleccionará una o
varias herramientas según la consulta, agregará evidencias atendiendo a procedencia,
calidad, frescura y conflictos, y podrá explicar las fuentes utilizadas. La
profundidad se adaptará automáticamente, con instrucciones naturales del usuario
como criterio prioritario y con límites explícitos de privacidad, tiempo y coste.

La política de privacidad distinguirá categorías de datos. El contexto privado
podrá influir en decisiones locales, pero no pasará implícitamente a una petición
externa. La ubicación será una excepción explícita: podrá transmitirse con la
precisión mínima cuando routing, navegación, lugares cercanos, búsqueda local o
meteorología la necesiten y la política de egreso lo autorice. Ese permiso no se
extenderá a conversación, memoria, documentos, código ni perfil adicional.

El asistente podrá descubrir y leer documentos del usuario dentro de fuentes locales
configuradas, empezando por su carpeta Documentos. Búsqueda, lectura e incorporación
a memoria o RAG serán decisiones separadas. El modelo no obtendrá acceso arbitrario
al sistema de archivos y el procesamiento documental permanecerá local por defecto;
repositorios y búsqueda de código constituirán una capacidad diferente.

Jarvis crecerá mediante módulos funcionales que consuman capacidades generales de
conversación, identidad, memoria, herramientas, persistencia, automatización,
confirmación, dispositivos y observabilidad. El núcleo no incorporará conceptos de
cada dominio. `BatchCooking` será el primer módulo doméstico de referencia: permitirá
descubrir con un caso real el contrato mínimo de extensibilidad antes de estabilizar
un SDK y, mucho después, antes de permitir que Jarvis proponga o implemente nuevas
capacidades de forma controlada.

`BatchCooking` acompañará el ciclo semanal de inventario, menú, preparación, compra
y feedback sin presentar suposiciones como existencias confirmadas ni sustituir
consejo sanitario profesional. El conocimiento doméstico existente se migrará de
forma gradual y trazable; reglas, preferencias, valoraciones, recetas, inventarios,
menús y plantillas no se convertirán en un prompt monolítico ni se reconstruirán
desde cero.

Una capacidad general futura de `Controlled Local Resources` permitirá a cada módulo
usar únicamente carpetas y recursos registrados, con lectura y escritura separadas,
previsualización y confirmación antes de importar o modificar. Los archivos serán
fuentes no confiables, no instrucciones. Las restricciones estables y las
preferencias variables conservarán procedencia y vigencia: una valoración nueva no
borrará el historial ni reducirá silenciosamente la prioridad de una alergia.

## Household Identity, Authorization and Guest Access

LocalAssistant comenzará en un único hogar, pero distinguirá al propietario o
administrador, miembros adultos, miembros infantiles, invitados e identidades no
humanas de dispositivos y servicios. Los nombres reales y la composición familiar
pertenecerán a la configuración de cada instalación y nunca al repositorio.

Los roles serán perfiles iniciales, no autorizaciones absolutas. La decisión efectiva
combinará capacidades específicas, propiedad y ámbito del dato, dispositivo y
habitación, riesgo de la acción, contexto de uso, confianza del método de
autenticación y confirmación adicional. Administrar el sistema no eliminará límites
destructivos, protección de secretos, privacidad de salidas compartidas, auditoría ni
la separación entre proponer, integrar, activar y desplegar.

Los miembros adultos usarán las funciones domésticas y personales que tengan
concedidas. Los menores dispondrán de capacidades apropiadas y supervisión
proporcionada y transparente, sin administración, invitados, compras, secretos ni
acciones sensibles por defecto. Los invitados operarán mediante sesiones temporales,
revocables y aisladas, sin memoria familiar, herramientas privadas ni persistencia
predeterminada. Un adulto solo podrá invitar si posee la capacidad correspondiente;
un menor no podrá hacerlo.

Satélites, workers y conectores tendrán identidad propia, credenciales revocables y
privilegios mínimos. Autenticar un dispositivo demostrará qué dispositivo participa,
no quién habla delante de él. La wake word y el reconocimiento de hablante serán
señales contextuales; una acción sensible exigirá autenticación reforzada mediante un
canal personal adecuado.

Memoria personal, memoria compartida del hogar, estado de módulos, administración y
sesiones efímeras serán ámbitos diferentes. La autorización se aplicará antes de
recuperar contenido para RAG o entregarlo al LLM y también antes de seleccionar una
salida: un altavoz o una pantalla compartidos no revelarán información sensible solo
porque el principal tenga permiso para consultarla.

La primera instalación tendrá un bootstrap de propietario de un solo uso, limitado a
configuración inicial y resistente a reclamaciones desde la red. Recuperar al
administrador será un proceso explícito y auditable, no una credencial predeterminada
ni una puerta trasera. La API key local actual es un peldaño educativo y no representa
este modelo doméstico completo.

## Conversational English Coach

Jarvis podrá actuar como tutor personal de inglés para conversación cotidiana y
profesional, entrevistas, reuniones, presentaciones, incidencias, clientes y
vocabulario de .NET, sistemas distribuidos, AMR e IA. Una primera experiencia escrita
ofrecerá role-play, política de corrección configurable e informe de sesión antes de
depender de la infraestructura de voz doméstica.

Conversar, evaluar y actualizar el perfil de aprendizaje serán responsabilidades
distintas. El camino interactivo priorizará naturalidad y baja latencia; correcciones
no bloqueantes, análisis pedagógico e informes podrán completarse después. Nivel,
objetivos, errores, vocabulario, fluidez y ejercicios conservarán evidencias
temporales por usuario: una inferencia o una mala sesión no se convertirá
silenciosamente en una característica permanente.

La evolución por voz permitirá ajustar velocidad, interrupciones y momento de las
correcciones, pero no confundirá transcripción con pronunciación. El análisis
fonético preciso requerirá evidencia de audio y tecnología específica todavía no
seleccionada. Grabaciones, transcripciones y contenido profesional serán privados;
el audio no se conservará por defecto ni saldrá a un proveedor externo sin
autorización.

## Conversational Project Lifecycle

Jarvis podrá convertir progresivamente una conversación natural en un proyecto
diseñado, implementado, probado y revisable, siempre mediante transiciones
autorizadas. Ayudará a aclarar objetivo, usuarios, alcance, requisitos, restricciones,
riesgos, seguridad, alternativas, decisiones y criterios de aceptación; conservará
un estado estructurado y generará especificaciones y roadmaps que el usuario pueda
inspeccionar y corregir.

Texto y voz serán canales intercambiables, no almacenes del proyecto. Una sesión
podrá comenzar hablando, continuar en una interfaz escrita, reanudarse días después
y mantener varios proyectos aislados. Las afirmaciones relevantes distinguirán
confirmaciones del usuario, inferencias, preguntas abiertas y decisiones sustituidas,
con historial suficiente para detectar contradicciones.

«Impleméntalo» será una intención de transición, no permiso ilimitado para ejecutar.
Jarvis resumirá el alcance, señalará decisiones bloqueantes, propondrá un primer
incremento, identificará repositorio, agente, herramientas, proveedor y posible
coste, y solicitará confirmación antes de preparar un entorno aislado. El resultado
será un diff con build, tests y trazabilidad; commits, ramas remotas, pull requests,
despliegues y acciones irreversibles requerirán autorizaciones independientes.

Los agentes de programación podrán ser locales, externos, simulados o especializados
y se conectarán mediante una frontera intercambiable. Privacidad y confidencialidad
del repositorio seguirán precediendo al routing: un agente externo potente no podrá
recibir código o contexto `DENY` solo porque el agente local resulte insuficiente.

## Controlled Self-Extension

Después de validar manualmente la extensibilidad con `BatchCooking`, Jarvis podrá
proponer e implementar ampliaciones para su propio ecosistema mediante el ciclo
conversacional de proyectos. Elegirá el mecanismo mínimo suficiente —skill, tool,
connector, module o capacidad de satélite— y tratará un cambio del núcleo como una
excepción de mayor riesgo sujeta al desarrollo normal del producto.

Ninguna extensión se autoaprobará ni modificará o desplegará directamente la
instancia activa. Requisitos, código, integración, instalación, activación y
despliegue tendrán revisiones y autorizaciones separadas. El trabajo ocurrirá en
repositorio, rama y entorno aislados y producirá especificación, análisis de
permisos, build, tests, revisión de seguridad, diff y artefactos antes de poder
integrarse. La activación será monitorizable, desactivable y reversible.

El acceso por voz podrá distribuirse mediante satélites de habitación pequeños y
especializados. Una conversación no quedará ligada a un único aparato: el
micrófono que inicia el turno, el dispositivo que reproduce la respuesta y la
pantalla que muestra contexto podrán ser distintos y asociarse por habitación.
Los Google Nest Hub existentes se reutilizarán únicamente como salidas mediante
Google Cast y como pantallas de Home Assistant; no se presupone acceso a sus
micrófonos ni sustitución de Google Assistant.

El nombre mostrado y la palabra de activación serán configurables. `LocalAssistant` es el
nombre elegido para el repositorio y la solución.
