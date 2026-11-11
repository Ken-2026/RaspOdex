# Contribuir a Renombra

## Compilar

Necesitas Windows x64 y el [SDK de .NET 10](https://dotnet.microsoft.com/download/dotnet/10.0). Ejecuta estos comandos desde la raíz del repositorio.

```powershell
dotnet publish .\src\Renombra\Renombra.csproj -c Release -p:UseAppHost=false --self-contained false -o .\artifacts\Renombra
dotnet .\artifacts\Renombra\Renombra.dll
```

Para probar los metadatos, descarga el [paquete oficial de ExifTool 13.63 para Windows x64](https://sourceforge.net/projects/exiftool/files/exiftool-13.63_64.zip/download). Coloca `exiftool.exe` y la carpeta `exiftool_files` completa en `tools/exiftool` antes de compilar. El proyecto copia esa carpeta al resultado de la publicación. [Publicación dependiente de .NET](https://learn.microsoft.com/dotnet/core/deploying/) · [Instalación de ExifTool](https://exiftool.org/install.html)

## Probar un cambio

Usa una carpeta temporal con archivos de prueba. Para informar de un error, indica la versión de Windows y Renombra, la plantilla usada y un ejemplo del nombre esperado. Los nombres ficticios permiten reproducir el problema sin publicar rutas o metadatos personales.

- Comprueba la lectura y la vista previa con una foto que tenga EXIF y otra a la que le falte el campo elegido.
- Prueba nombres con tildes, numeración y una colisión con un archivo existente.
- En un lote válido, aplica el cambio y usa **Deshacer**. Puedes usar `Get-FileHash` para comprobar que el contenido sigue siendo idéntico.
- Si el cambio afecta a la importación o a ExifTool, prueba también un lote de varios cientos de archivos y describe el tiempo de respuesta y el uso de memoria que observaste.

## Proponer mejoras

Abre una incidencia para describir qué quieres mejorar y cómo debería funcionar. Si ya tienes un cambio preparado, abre una solicitud de incorporación con los pasos de prueba y, cuando afecte a la interfaz, una captura con archivos de ejemplo.

Estas mejoras encajan con el flujo actual.

- Guardar un registro de cada lote para poder recuperar sus nombres tras un cierre inesperado.
- Filtrar la vista previa por extensión y estado. Volver a comprobar colisiones al aplicar a una selección.
- Mostrar la etiqueta de origen de las fechas y permitir elegir cómo tratar una foto sin fecha de captura.
- Mostrar el progreso de la importación en carpetas grandes y permitir cancelarla.

La interfaz está en `MainWindow.xaml` y `MainWindow.xaml.cs`. En `Core`, el lector consulta ExifTool, el motor genera y valida los nombres y el almacén conserva las plantillas. Si añades un campo de metadatos, revisa su lectura, su nombre en la interfaz y su comportamiento con valores ausentes o con caracteres que Windows no admite en un nombre.

## Añadir un campo EXIF

1. Añade la etiqueta que consulta ExifTool y su lectura en `Core/ExifMetadataReader.cs`. Conserva un valor vacío cuando el archivo carezca de esa etiqueta.
2. Registra el dato en `FileMetadata` y su nombre y valor visible en `ExifFields.All`, en `Core/RenameEngine.cs`. El panel muestra los campos de ese catálogo.
3. Comprueba cómo se convierte el valor en un nombre de Windows. Por ejemplo, un tiempo de exposición de `1/250` se escribe como `1-250`.
4. Añade el campo a la tabla del README y prueba con una foto que tenga ese dato y otra que carezca de él.
