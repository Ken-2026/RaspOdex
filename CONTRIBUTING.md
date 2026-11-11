# Compilar y contribuir

Se requiere Windows x64 y el SDK de .NET 10 con soporte WPF. El código está en `src/Renombra`. El archivo del proyecto conserva el nombre histórico `Renombra.csproj` y genera `RaspOdex.exe`.

```powershell
dotnet restore .\src\Renombra\Renombra.csproj -r win-x64
dotnet build .\src\Renombra\Renombra.csproj -c Release
dotnet publish .\src\Renombra\Renombra.csproj -c Release -r win-x64 --self-contained true -o .\salida
```

Para incluir ExifTool sitúa el paquete oficial de Windows x64 en `tools/exiftool`: el ejecutable debe llamarse `exiftool.exe` y estar junto a `exiftool_files`. El proyecto lo copia al publicar. Sin él, las funciones EXIF no están disponibles.

Para la búsqueda local coloca el índice en `salida/data/GeoNames/geonames.sqlite`. Puede generarse desde el volcado oficial `allCountries.zip` con Python 3:

```powershell
python .\tools\Build-GeoNamesIndex.py .\allCountries.zip .\salida\data\GeoNames\geonames.sqlite
```

El volcado y el índice son grandes y no se guardan en Git. Respeta las atribuciones de [TERCEROS.md](TERCEROS.md). Prueba los cambios con archivos temporales y no incluyas fotografías privadas, rutas personales ni archivos generados en las contribuciones.
