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
3. Escribe el texto fijo y los separadores que quieras conservar. Por ejemplo, `Viaje_{fecha:yyyyMMdd}_{camara}_{n}` puede generar `Viaje_20250510_Nikon D750_001.jpg`. El número sigue el orden de la lista.
4. Revisa la vista previa. Si falta un dato requerido, ajusta la plantilla o quita de la lista el archivo que muestra el error. Pulsa **Renombrar** cuando esté listo.

Pulsa **Guardar…** junto al selector de plantillas para conservar la combinación. Las plantillas propias se guardan en un archivo JSON junto a la aplicación. Puedes deshacer los lotes durante la sesión. ExifTool lee los metadatos en el equipo. Renombrar modifica los nombres de los archivos.

| Campo | Valor |
| --- | --- |
| `{fecha}` | Fecha de captura disponible. Admite formatos como `{fecha:yyyyMMdd}`. |
| `{fabricante}`, `{camara}` | Fabricante y modelo de cámara. |
| `{objetivo}`, `{iso}` | Objetivo y sensibilidad ISO. |
| `{focal}`, `{apertura}`, `{exposicion}` | Distancia focal, apertura y tiempo de exposición. |
| `{ancho}`, `{alto}` | Dimensiones en píxeles. |
| `{nombre}`, `{ext}`, `{n}` | Nombre base, extensión original sin punto y número. |
| `{creado}`, `{modificado}` | Fechas de creación y modificación del archivo. |

## Compilar y contribuir

La aplicación está escrita en C# y WPF. Consulta [cómo compilar y proponer mejoras](CONTRIBUTING.md).

El código de Renombra se publica bajo la [licencia MIT](LICENSE). [ExifTool](https://github.com/exiftool/exiftool/blob/master/README), [Strawberry Perl](https://strawberryperl.com/) y el [lanzador de ExifTool para Windows](https://oliverbetz.de/pages/Artikel/ExifTool-for-Windows) se distribuyen por separado bajo sus propias licencias.
