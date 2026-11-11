# Guía de RaspOdex

## Primera prueba

1. Prepara una carpeta con **copias** de algunas fotos.
2. Pulsa **Añadir archivos** o **Añadir carpeta**. Cargar la lista no cambia los archivos.
3. Elige **Automático · fecha y nombre**. La opción de evitar repetidos propone un sufijo cuando el nombre ya está ocupado.
4. Revisa los resultados y errores en **Vista previa**. Solo al pulsar **Renombrar** se aplica el lote.
5. Para revertir una tanda usa **Deshacer** en la misma ventana, mientras los archivos no hayan cambiado. Al cerrar, el historial se pierde. **Vaciar lista** solo quita las filas de la pantalla.

## Fotos que puedes encontrar por su nombre

Usa **Añadir campo explicado** y selecciona los componentes en el orden deseado. Puedes combinar texto fijo y campos. Por ejemplo `{dia_semana}_{fecha:yyyyMMdd}_{nombre}` puede dar `viernes_20250912_foto.jpg`. Una galería puede encontrar ese texto si permite buscar por nombre; RaspOdex no escribe etiquetas internas.

`{fecha}`, `{hora}` y `{dia_semana}` dependen de la fecha de captura guardada en la foto. `{creado}` y `{modificado}` son fechas del sistema de archivos y pueden cambiar al copiar. El botón automático usa la modificación cuando falta la captura. La hora EXIF no se convierte automáticamente entre zonas horarias.

Para país y ciudad sin Internet, usa el menú **Ubicación** y resuelve cada foto con GeoNames. El resultado es una localidad poblada cercana, no una dirección exacta. Si falta GPS, puedes utilizar un valor manual; si falta un dato exigido por una plantilla, la vista previa mostrará el error.

## Carpeta de salida

Vacía significa que cada archivo permanece en su carpeta. Si eliges una carpeta de salida, el archivo se **mueve** allí al renombrarlo. En esta versión, origen y destino deben estar en la misma unidad. La vista previa detecta conflictos; mantén una copia de tus fotos importantes.

## Búsqueda

Una búsqueda normal reemplaza texto literal. **Expresión regular** habilita patrones; el menú junto a Buscar incluye ejemplos insertables. **Todas las veces** aplica el cambio a todas las coincidencias, **Distinguir mayús.** diferencia mayúsculas y minúsculas, y **Editar extensión** permite cambiar también la extensión. Cambiar `.jpg` por `.png` no convierte el contenido.

## Problemas habituales

- **No se leen metadatos:** comprueba que `tools/exiftool/exiftool.exe` y `exiftool_files` están junto al programa.
- **No hay búsqueda local:** comprueba que existe `data/GeoNames/geonames.sqlite`.
- **No se guarda una plantilla:** extrae el ZIP en una carpeta donde puedas escribir. Se guarda en `Renombra.Plantillas.json` junto al ejecutable.
- **Una foto no indica ciudad:** puede no tener GPS o no haber un lugar poblado próximo; utiliza un valor manual.
- **No funciona Deshacer:** solo está disponible para las tandas de la ventana actual y puede fallar si los archivos se han movido, editado o sustituido.

Esta versión está orientada y probada con fotografías. Para eliminar la edición portátil, borra su carpeta y accesos directos cuando no los necesites.
