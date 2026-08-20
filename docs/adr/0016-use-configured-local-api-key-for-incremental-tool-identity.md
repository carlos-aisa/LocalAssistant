# ADR 0016: Usar una API key local configurada para identidad incremental de herramientas

## Contexto

La política de riesgo ya diferencia autenticación y scopes, pero el contexto siempre
era anónimo. Antes de habilitar herramientas privadas o con efectos relevantes se
necesita un principal verificable y confirmaciones que no pueda resolver otro actor.
No existe aún una necesidad que justifique usuarios persistentes, roles, tokens de
terceros ni una base de datos de identidad.

## Decisión

La API admite de forma opcional una única API key local. La configuración contiene
su hash SHA-256, un identificador de principal y sus scopes; la clave original queda
fuera del repositorio y de `appsettings.json`. El adaptador HTTP crea el principal y
los scopes desde esa configuración. Si no se presenta la clave, el contexto es
anónimo y las herramientas públicas siguen disponibles. Una clave presentada pero
inválida se rechaza antes del orquestador.

Cada confirmación pendiente conserva el principal que originó la llamada. Antes de
consumirla, el orquestador exige el mismo principal. El cliente y el modelo no pueden
aportar, elevar ni sustituir scopes o el principal.

## Consecuencias

Las herramientas con scopes ya pueden probarse con un principal autenticado y las
confirmaciones autenticadas no se comparten entre principales. La API key no protege
por sí sola el transporte: un despliegue fuera del equipo local deberá usar HTTPS y
gestión de secretos adecuada. Tampoco crea propiedad de conversaciones ni habilita
datos privados persistentes; esas responsabilidades pertenecen a la fase 4. La futura
identidad multiusuario sustituirá este adaptador sin trasladar SDKs de autenticación al
núcleo.
