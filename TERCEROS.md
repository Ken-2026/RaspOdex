# Componentes y atribuciones

- **RaspOdex:** código del proyecto, licencia MIT; véase `LICENSE`.
- **GeoNames:** `geonames.sqlite` es un índice derivado del volcado mundial `allCountries.zip`, filtrado a localidades pobladas y adaptado a SQLite mediante `tools/Build-GeoNamesIndex.py`. Datos © [GeoNames](https://www.geonames.org/), [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/). La base conserva su atribución en la tabla `metadata`. Fecha exacta del volcado original: no registrada. Los datos se suministran sin garantía de exactitud o actualidad.
- **ExifTool:** herramienta de Phil Harvey, bajo los mismos términos que Perl. [ExifTool y licencia](https://exiftool.org/). La distribución de Windows incluye Perl y el lanzador; sus avisos y licencias se conservan en `tools/exiftool/exiftool_files`, incluido `LICENSE` y `Licenses_Strawberry_Perl.zip`.
- **.NET / WPF:** runtime de Microsoft incluido en la publicación independiente. Los avisos correspondientes acompañan los archivos del runtime; consulta [.NET](https://github.com/dotnet/runtime).
- **Microsoft.Data.Sqlite / SQLite:** se usan para consultar el índice local. [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) y [SQLite](https://www.sqlite.org/copyright.html).
- **OpenStreetMap y Nominatim:** se usan solo al solicitar una consulta en línea. Datos © [OpenStreetMap contributors](https://www.openstreetmap.org/copyright).

Las licencias de terceros no se sustituyen por la licencia MIT del código de RaspOdex.
