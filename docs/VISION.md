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

## Resultado a largo plazo

Un núcleo .NET coordinará modelos locales y externos bajo una política de privacidad
que prevalece sobre cualquier decisión de routing. Coste, dificultad, latencia y
recursos solo decidirán entre proveedores que puedan recibir el payload autorizado.
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

El acceso por voz podrá distribuirse mediante satélites de habitación pequeños y
especializados. Una conversación no quedará ligada a un único aparato: el
micrófono que inicia el turno, el dispositivo que reproduce la respuesta y la
pantalla que muestra contexto podrán ser distintos y asociarse por habitación.
Los Google Nest Hub existentes se reutilizarán únicamente como salidas mediante
Google Cast y como pantallas de Home Assistant; no se presupone acceso a sus
micrófonos ni sustitución de Google Assistant.

El nombre mostrado y la palabra de activación serán configurables. `LocalAssistant` es el
nombre elegido para el repositorio y la solución.
