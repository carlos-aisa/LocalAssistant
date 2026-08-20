# ADR 0013: Política de egreso extensible y denegada por defecto

## Contexto

Antes de conectar búsquedas, mapas, meteorología u otros proveedores, Jarvis debe
decidir qué información puede abandonar el límite local. Una lista global de datos
privados no expresa la necesidad de cada campo, ni impide que una categoría nueva o
un dato derivado salga por accidente.

## Decisión

El núcleo define categorías extensibles de datos y una política pura que evalúa
descriptores de campos clasificados. Las categorías protegidas y las desconocidas se
deniegan. `LOCATION` solo se permite cuando el campo es necesario para el propósito;
`SEARCH_QUERY` requiere saneado; `PUBLIC_DATA` se permite. Un campo con más de una
categoría recibe el resultado más restrictivo.

La política no recibe valores de payload ni realiza red, logging, saneado o selección
de proveedor. El futuro `Tools Gateway` deberá asociar cada descriptor permitido con
el payload final, comprobar procedencia y aplicar la decisión inmediatamente antes
del egreso.

## Consecuencias

El comportamiento es determinista y comprobable sin proveedor externo. La marca de
saneado es una precondición de contrato, no una prueba de que el texto sea seguro;
un sanitizador confiable y la validación final del gateway son trabajo posterior. No
existe todavía una ruta de red que esta política pueda proteger por sí sola.
