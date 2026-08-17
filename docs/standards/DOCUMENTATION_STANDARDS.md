# Estándares de documentación

## Idioma y audiencia

- La documentación narrativa del repositorio se mantiene actualmente en español.
- Identificadores, contratos JSON, esquemas y términos técnicos consolidados pueden
  permanecer en inglés.
- Si el proyecto adopta una audiencia internacional, el cambio de idioma se hará de
  forma coherente y no archivo por archivo sin plan.

## Actualización obligatoria

Antes de completar un cambio:

1. Revisar el diff y clasificar comportamiento, API, configuración, arquitectura y
   seguridad afectados.
2. Actualizar README, documentos temáticos, OpenAPI o ADR que correspondan.
3. Comprobar que lo documentado existe realmente y que el roadmap no parece código
   ya implementado.
4. Informar qué documentación cambió y por qué.

## Reglas de calidad

- Escribir de forma clara, explícita, consistente y verificable.
- No asumir conocimiento del repositorio para instrucciones de instalación o prueba.
- Mantener comandos reproducibles desde la raíz.
- Diferenciar arquitectura actual y evolución prevista.
- Añadir ADR solo para decisiones tomadas, con contexto, decisión y consecuencias.
- No copiar secretos, rutas personales, tokens o resultados sensibles.

La documentación forma parte del entregable y debe evolucionar en el mismo commit
que el comportamiento correspondiente.
