# Renombra

Renombra es una aplicación de Windows para cambiar el nombre de varios archivos a la vez. Permite ver los datos EXIF de una foto y utilizarlos en el nombre. La vista previa muestra el resultado de cada regla antes de aplicarla.

## Empezar

1. Instala el [runtime de escritorio de .NET 10 para Windows x64](https://dotnet.microsoft.com/download/dotnet/10.0). Busca **.NET Desktop Runtime**.
2. Descarga el ZIP de Renombra desde [Releases](https://github.com/Ken-2026/Renombra/releases) y extrae todos sus archivos en una carpeta.
3. Para usar las funciones EXIF, descarga el [ZIP oficial de ExifTool 13.63 para Windows x64](https://sourceforge.net/projects/exiftool/files/exiftool-13.63_64.zip/download). Dentro de la carpeta de Renombra, crea `tools/exiftool` y copia allí el ejecutable y la carpeta `exiftool_files` completa. Cambia el nombre del ejecutable de `exiftool(-k).exe` a `exiftool.exe`.
4. Abre PowerShell en la carpeta extraída y ejecuta este comando.

   ```powershell
   dotnet .\Renombra.dll
   ```

La búsqueda, el reemplazo, el cambio de mayúsculas y minúsculas y la numeración funcionan también con ExifTool sin instalar. El paquete portátil de ExifTool incluye su propio Perl. [Instrucciones oficiales de ExifTool](https://exiftool.org/install.html)

Windows puede bloquear la ejecución de compilaciones sin firma según la política del equipo. [Información sobre Control inteligente de aplicaciones](https://support.microsoft.com/es-es/windows/security/threat-malware-protection/smart-app-control-frequently-asked-questions)

## Usar metadatos en los nombres

1. Añade archivos o arrástralos a Renombra.
2. Selecciona una foto y abre **Datos EXIF**. Pulsa un campo para incorporarlo a la plantilla.
3. Escribe el texto fijo y los separadores que quieras conservar. Para un viaje completo, escribe `Indonesia` en **Ubicación para este lote** y usa `{lugar}_{fecha:yyyyMMdd}_{nombre}`. Por ejemplo, puede generar `Indonesia_20250510_DSC_0001.jpg`. El número sigue el orden de la lista.
4. Si la foto tiene GPS, usa **Ubicación** en el menú superior para consultar país y ciudad. Renombra pide confirmación antes de enviar las coordenadas al servicio de OpenStreetMap y completa los campos del lote. Puedes revisar o cambiar sus valores antes de renombrar.
5. Revisa la vista previa. Si falta un dato requerido, ajusta la plantilla o quita de la lista el archivo que muestra el error. Pulsa **Renombrar** cuando esté listo.

Pulsa **Guardar…** junto al selector de plantillas para conservar la combinación. Las plantillas propias se guardan en un archivo JSON junto a la aplicación. Puedes deshacer los lotes durante la sesión. ExifTool lee los metadatos en el equipo. Renombrar modifica los nombres de los archivos.

| Campo | Valor |
| --- | --- |
| `{fecha}` | Fecha de captura disponible. Admite formatos como `{fecha:yyyyMMdd}`. |
| `{lugar}` | Lugar fijo escrito para el lote, como `Indonesia`, `Bali` o `IND`. |
| `{pais}`, `{ciudad}`, `{codigo_pais}` | Datos de ubicación del lote. El menú puede completarlos a partir del GPS de la foto seleccionada. El código consultado sigue ISO de dos letras, como `ES` o `ID`. |
| `{indicador}` | Abreviatura personal del lote. Por ejemplo, escribe `IND` si ésa es tu convención para Indonesia. Es independiente del código ISO que se obtiene automáticamente. |
| `{fabricante}`, `{camara}` | Fabricante y modelo de cámara. |
| `{objetivo}`, `{iso}` | Objetivo y sensibilidad ISO. |
| `{focal}`, `{apertura}`, `{exposicion}` | Distancia focal, apertura y tiempo de exposición. |
| `{latitud}`, `{longitud}` | Coordenadas GPS guardadas dentro de la foto. |
| `{ancho}`, `{alto}` | Dimensiones en píxeles. |
| `{nombre}`, `{ext}`, `{n}` | Nombre base, extensión original sin punto y número. |
| `{creado}`, `{modificado}` | Fechas de creación y modificación del archivo. |

### Privacidad de las consultas GPS

Al elegir **Ubicación > Buscar país y ciudad desde el GPS de la foto seleccionada**, Renombra pide permiso y envía únicamente las dos coordenadas de esa foto al servicio público de geocodificación inversa de OpenStreetMap, de una en una. No sube la foto, su nombre ni otros metadatos. La consulta es opcional: también puedes escribir país, ciudad, código e indicador a mano. Datos de ubicación © [OpenStreetMap contributors](https://www.openstreetmap.org/copyright), mediante [Nominatim](https://nominatim.org/).

## Búsqueda local sin Internet con GeoNames

La versión 1.3 puede resolver la localidad directamente en el equipo mediante un índice SQLite generado a partir de `allCountries.zip`. La base completa no se incluye en GitHub porque ocupa cientos de megabytes.

1. Descarga el volcado mundial `allCountries.zip` desde [GeoNames](https://www.geonames.org/export/) y guárdalo en una unidad con espacio suficiente.
2. Ejecuta el constructor incluido, apuntando al ZIP y al destino del índice. Por ejemplo, en PowerShell:

   ```powershell
   python .\tools\Build-GeoNamesIndex.py `
     K:\Renombra\data\GeoNames\allCountries.zip `
     K:\Renombra\data\GeoNames\geonames.sqlite
   ```

3. Coloca `geonames.sqlite` en `data/GeoNames` junto a la carpeta de la aplicación o en la carpeta `data/GeoNames` hermana de la aplicación.
4. En Renombra, selecciona una foto con GPS y elige **Ubicación > Buscar sin Internet con GeoNames**.

El índice contiene sólo localidades pobladas y se consulta por proximidad, sin cargar los 1,7 GB de texto en memoria. El archivo generado incluye los metadatos de atribución **GeoNames, CC BY 4.0**. GeoNames se ofrece sin garantía de exactitud, actualidad o exhaustividad, por lo que los campos obtenidos se pueden corregir antes de renombrar.

## Compilar y contribuir

La aplicación está escrita en C# y WPF. Consulta [cómo compilar y proponer mejoras](CONTRIBUTING.md).

El código de Renombra se publica bajo la [licencia MIT](LICENSE). [ExifTool](https://github.com/exiftool/exiftool/blob/master/README), [Strawberry Perl](https://strawberryperl.com/) y el [lanzador de ExifTool para Windows](https://oliverbetz.de/pages/Artikel/ExifTool-for-Windows) se distribuyen por separado bajo sus propias licencias.
