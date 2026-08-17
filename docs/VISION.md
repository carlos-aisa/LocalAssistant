# Visión

LocalAssistant busca ser un asistente doméstico y técnico que priorice la ejecución local,
la privacidad y la capacidad de entender cada pieza del sistema.

El objetivo no es ensamblar productos opacos, sino aprender y mantener explícitos
los límites entre conversación, inferencia, herramientas, memoria, conectores,
seguridad y operación.

## Principios

1. **Local primero:** la información privada debe poder procesarse sin salir del
   equipo siempre que los recursos y la dificultad lo permitan.
2. **Proveedores intercambiables:** el dominio no depende del SDK de un proveedor.
3. **Capacidades explícitas:** el modelo solicita herramientas; nunca obtiene
   acceso arbitrario al sistema.
4. **Confirmación proporcional al riesgo:** leer, modificar estado y realizar una
   acción sensible no deben tratarse igual.
5. **Trazabilidad sin vigilancia:** registrar decisiones técnicas y tiempos sin
   copiar indiscriminadamente conversaciones privadas.
6. **Crecimiento justificado:** un nuevo proceso, almacén o broker debe resolver
   una necesidad observable.
7. **Presencia ubicua y visible:** el asistente podrá estar disponible en varias
   habitaciones, pero toda captura de audio deberá ser perceptible, controlable y
   desactivable físicamente.

## Resultado a largo plazo

Un núcleo .NET coordinará modelos locales y externos según privacidad, coste,
dificultad y recursos. Servicios especializados podrán ocuparse de inferencia,
voz, automatización doméstica, eventos y búsqueda semántica. Los canales de texto,
voz y programación compartirán políticas y capacidades, pero podrán adaptar su
experiencia.

El acceso por voz podrá distribuirse mediante satélites de habitación pequeños y
especializados. Una conversación no quedará ligada a un único aparato: el
micrófono que inicia el turno, el dispositivo que reproduce la respuesta y la
pantalla que muestra contexto podrán ser distintos y asociarse por habitación.
Los Google Nest Hub existentes se reutilizarán únicamente como salidas mediante
Google Cast y como pantallas de Home Assistant; no se presupone acceso a sus
micrófonos ni sustitución de Google Assistant.

El nombre mostrado y la palabra de activación serán configurables. `LocalAssistant` es el
nombre elegido para el repositorio y la solución.
