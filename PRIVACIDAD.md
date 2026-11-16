# Privacidad

RaspOdex lee en el equipo los archivos y metadatos para preparar la vista previa. Renombrar cambia el nombre o la ruta de los archivos seleccionados; elegir carpeta de salida los mueve dentro de la misma unidad. La búsqueda local consulta un índice GeoNames incluido y no necesita Internet.

La opción de búsqueda de ubicación **en línea** es voluntaria y solicita confirmación. Envía al servicio público Nominatim las coordenadas de la foto seleccionada, no la foto ni su nombre. Las coordenadas pueden revelar dónde fue tomada la foto. El servicio externo puede recibir información técnica habitual de una conexión, como la dirección IP. Consulta [Nominatim](https://nominatim.org/) y [OpenStreetMap](https://www.openstreetmap.org/copyright).

Las plantillas se guardan localmente junto al programa en `Renombra.Plantillas.json`. El historial de deshacer existe solo durante la sesión abierta.

## Funciones experimentales de esta rama

El reconocimiento visual local envía una miniatura al servicio Ollama configurado en `127.0.0.1`. El análisis local de documentos envía al mismo servicio texto extraído del archivo. Instalar Ollama o descargar modelos requiere Internet. Si se activa el OCR, el texto visible puede acabar en el nombre del archivo; revisa la propuesta antes de aplicarla.

Las casillas **Usar Gemini en línea** son opcionales. Antes de cada lote, RaspOdex solicita una clave y muestra una confirmación: enviará a Google una miniatura JPEG por imagen o hasta 12.000 caracteres de texto extraído por documento. La clave se envía en la cabecera de la solicitud y esta función no la guarda en disco. El modo Gemini no cambia automáticamente al modo local si falla. Estas funciones no forman parte de la versión descargable 1.4.0.
