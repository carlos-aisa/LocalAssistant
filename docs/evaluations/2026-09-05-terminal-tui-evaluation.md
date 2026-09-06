# Evaluación de TUI terminal — 2026-09-05

## Decisión

Se adopta un renderizador propio mínimo para el cliente `net8.0`. Conserva el modo
textual como referencia y fallback. No se añade dependencia de TUI en este incremento.

## Evidencia

| Alternativa | Resultado |
| --- | --- |
| Spectre.Console | Descartada. Su documentación de Live Display no admite combinar de forma segura el renderizado vivo con componentes interactivos; el cliente necesita ambas cosas. |
| Terminal.Gui v2 | Descartada para esta versión. Un prototipo `net8.0` con Terminal.Gui 2.1.0 falló en restore: el paquete actualmente publicado exige `net10.0`. La estable actual 2.4.17 también exige .NET 10. No se acepta una versión sin listar ni se eleva el framework sin autorización explícita. |
| Renderizador propio mínimo | Elegida. Un bucle dedicado procesa teclado no bloqueante, resize y snapshots mientras `TerminalClientApplication` espera entrada en su worker. No hay ANSI en redirección porque esta ruta no se inicia fuera de una terminal interactiva. |

## Trade-offs

El renderizador propio tiene menor coste de dependencia y mantiene `net8.0`, pero ofrece
solo el layout, transcript y scroll mínimos de este incremento. No sustituye un toolkit
general. La capa de entrada y snapshots queda separada para poder reevaluar una TUI con
dependencia cuando el framework del cliente cambie con una decisión explícita.

## Comprobación manual pendiente de cierre

Las pruebas automáticas del driver falso cubren cancelación, EOF, resize, viewport,
scroll, frames prioritarios y normalización. Sigue pendiente la comprobación manual en
Windows Terminal + PowerShell, con una API local configurada y una credencial de
prueba:

1. Ejecutar el cliente sin `--plain`, reducir y ampliar la ventana, y comprobar que el
   estado, transcript e input siguen siendo utilizables.
2. Pegar una frase con `ñ`, tildes y signos de apertura; verificar que llega como una
   línea completa y que el transcript conserva los caracteres.
3. Solicitar una tool que requiera confirmación y comprobar que solo `approve` o
   `reject` escritos la resuelven.
4. Abrir pairing, rotación o revocación y comprobar que el valor secreto aparece
   enmascarado y no queda en el transcript tras enviar o cancelar.
5. Pulsar `Ctrl+C`, y en otra ejecución usar `/exit`; comprobar que se restaura el
   cursor y la consola acepta el siguiente comando de PowerShell.
6. Ejecutar con `--plain` y con salida redirigida; comprobar que se conserva el flujo
   textual y que el archivo resultante no contiene secuencias ANSI.
