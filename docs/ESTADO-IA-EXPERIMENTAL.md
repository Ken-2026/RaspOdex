# Estado del análisis visual y de documentos

Esta rama contiene una **implementación experimental** encima de RaspOdex 1.4.0. No sustituye la versión estable ni ofrece instalador o binarios descargables.

## Funciones presentes en el código

- Dos modos separados en la interfaz: fotografías y documentos PDF/DOCX.
- Análisis visual local con Ollama y propuesta de nombre `{imagen}`. OCR de texto visible opcional con Tesseract.
- Extracción local de texto y campos para PDF/DOCX, con propuesta de nombre `{contenido}`. PDF escaneados requieren Tesseract y `pdftoppm`.
- Uso opcional de Gemini para miniaturas de fotos o texto de documentos, tras una confirmación por lote. Consulta [privacidad](../PRIVACIDAD.md).
- Vista previa antes de renombrar y verificación de que el archivo no cambió desde el análisis.

## Comprobado el 16 de noviembre de 2026

- La copia exacta del código de esta rama compila con .NET 10 para Windows, sin advertencias ni errores, usando una sola tarea de compilación.
- El motor de renombrado de la versión 1.4.0 pasó 19 comprobaciones con archivos temporales: vista previa, intercambio de nombres, deshacer, colisiones y detección de cambios externos.
- El analizador de documentos de esta rama leyó un PDF con texto y un DOCX ficticios y propuso `Factura` en ambos. Rechazó un PDF averiado con un error controlado.
- Se revisó el conjunto de archivos para evitar incluir fotografías, documentos, modelos, claves o paquetes generados.

## Pendiente para una versión pública

- Pruebas completas de los botones de fotos y documentos, OCR, instalación de Ollama y modelos, y respuestas reales de Gemini.
- Pruebas de renombrado y deshacer con propuestas visuales y documentales en una copia aislada de archivos.
- Empaquetado reproducible, revisión de licencias de PdfPig, Poppler, Tesseract y modelos, y solución para la política de Control de aplicaciones de Windows. Este PC bloqueó el instalador sin firma con eventos 3033 y 3077.
- La prueba del PDF escaneado con OCR y el flujo completo de la interfaz siguen pendientes. Una nueva DLL de pruebas y un arnés WPF fueron bloqueados por la misma política; la lectura de los dos documentos anteriores sí se ejecutó desde una DLL de consola.

No utilices documentos personales para completar estas pruebas. La [versión 1.4.0](https://github.com/Ken-2026/RaspOdex/releases/tag/v1.4.0) sigue siendo la opción publicada para fotografías.
