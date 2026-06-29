using System;
using System.IO;
using System.Linq;


// ================================================================================================
//  RawPrint.cs — La cara pública de la librería (lo que llamaréis desde PowerBuilder)
// ------------------------------------------------------------------------------------------------
//  ¿Qué resuelve esta clase? Mandar un fichero o un stream de bytes TAL CUAL a una impresora, sin
//  que Windows lo reinterprete: nada de GDI, nada de driver pintando. Es justo lo que necesitáis
//  para etiquetas (ZPL/EPL de Zebra), tickets (ESC/POS) o ficheros ya "rasterizados" en PCL/PostScript.
//
//  Cómo se usa desde PowerBuilder (vía COM/.NET Assembly o el puente que uséis):
//      RawPrint lp
//      lp = create RawPrint
//      lp.PrintRawFile("Zebra ZD220", "C:\etiqueta.zpl", false)   // false = no pausar el trabajo
//
//  Crédito: este es un PORT a .NET moderno del proyecto original "RawPrint" de
//  Frogmore Computer Services Ltd (https://github.com/frogmorecs/RawPrint). Todo el mérito del
//  diseño original es suyo; aquí lo comentamos y adaptamos con fines didácticos.
// ================================================================================================
namespace RawPrint
{
    /// <summary>
    /// Punto de entrada para imprimir datos en bruto (RAW) en una impresora de Windows.
    /// Pensada para consumirse desde PowerBuilder: instánciala y llama a uno de los métodos
    /// <c>PrintRawFile</c> / <c>PrintRawStream</c>.
    /// </summary>
    public class RawPrint
    {
        // Argumentos del evento "se ha creado un trabajo": id del trabajo en la cola + impresora.
        internal class JobCreatedEventArgs : EventArgs
        {
            public uint Id { get; set; }
            public string PrinterName { get; set; } = "";
        }

        private delegate void JobCreatedHandler(object sender, JobCreatedEventArgs e);

        // Evento opcional que se dispara cuando el spooler asigna el Job ID. Es '?' (anulable) porque
        // puede no tener suscriptores; por eso más abajo se invoca con OnJobCreated?.Invoke(...).
        private event JobCreatedHandler? OnJobCreated;

        /// <summary>
        /// Imprime un fichero en bruto usando su propia ruta como nombre de documento en la cola.
        /// </summary>
        /// <param name="printer">Nombre de la impresora en Windows.</param>
        /// <param name="path">Ruta del fichero a enviar tal cual a la impresora.</param>
        /// <param name="paused">Si es <c>true</c>, el trabajo entra PAUSADO en la cola.</param>
        public void PrintRawFile(string printer, string path, bool paused)
        {
            PrintRawFile(printer, path, path, paused);
        }

        /// <summary>
        /// Imprime un fichero en bruto, permitiendo elegir el nombre que se verá en la cola.
        /// </summary>
        /// <param name="printer">Nombre de la impresora en Windows.</param>
        /// <param name="path">Ruta del fichero a enviar.</param>
        /// <param name="documentName">Nombre del documento que aparecerá en la cola de impresión.</param>
        /// <param name="paused">Si es <c>true</c>, el trabajo entra pausado.</param>
        public void PrintRawFile(string printer, string path, string documentName, bool paused)
        {
            // 'using' cierra y libera el stream al salir, incluso si salta una excepción.
            using (var stream = File.OpenRead(path))
            {
                PrintRawStream(printer, stream, documentName, paused);
                stream.Close(); // redundante (el 'using' ya lo cierra), pero lo dejamos por claridad
            }

        }

        /// <summary>
        /// Imprime el contenido de un <see cref="Stream"/> en bruto (documento de una sola página).
        /// </summary>
        public void PrintRawStream(string printer, Stream stream, string documentName, bool paused)
        {
            PrintRawStream(printer, stream, documentName, paused, 1);
        }

        /// <summary>
        /// Imprime el contenido de un <see cref="Stream"/> en bruto, indicando cuántas páginas
        /// debe contabilizar el trabajo en la cola.
        /// </summary>
        /// <param name="printer">Nombre de la impresora en Windows.</param>
        /// <param name="stream">Flujo de bytes a enviar tal cual.</param>
        /// <param name="documentName">Nombre visible del documento en la cola.</param>
        /// <param name="paused">Si es <c>true</c>, el trabajo entra pausado.</param>
        /// <param name="pagecount">Número de páginas a reflejar en la cola (ver <c>PagePrinter</c>).</param>
        public void PrintRawStream(string printer, Stream stream, string documentName, bool paused, int pagecount)
        {
            // Abrimos solo con permiso de USO: no necesitamos administrar la impresora, solo imprimir.
            var defaults = new PRINTER_DEFAULTS
            {
                DesiredPrinterAccess = PRINTER_ACCESS_MASK.PRINTER_ACCESS_USE
            };

            // 'using' sobre el SafePrinter: al salir del bloque se llama a ClosePrinter solo.
            using (var safePrinter = SafePrinter.OpenPrinter(printer, ref defaults))
            {
                // Si el driver es XPS hay que enviar como "XPS_PASS"; en cualquier otro caso, "RAW".
                DocPrinter(safePrinter, documentName, IsXPSDriver(safePrinter) ? "XPS_PASS" : "RAW", stream, paused, pagecount, printer);
            }
        }

        // Detecta si el driver es de tipo XPS mirando si entre sus ficheros dependientes aparece
        // "pipelineconfig.xml" (señal inequívoca del pipeline de impresión XPS). En ese caso el
        // datatype RAW no vale y hay que usar "XPS_PASS".
        private static bool IsXPSDriver(SafePrinter printer)
        {
            var files = printer.GetPrinterDriverDependentFiles();

            return files.Any(f => f.EndsWith("pipelineconfig.xml", StringComparison.InvariantCultureIgnoreCase));
        }

        // Orquesta el ciclo completo de un documento: StartDoc -> (pausa opcional) -> páginas -> EndDoc.
        // El try/finally asegura que EndDocPrinter se llama SIEMPRE, aunque la escritura falle: si no,
        // el documento se quedaría "abierto" en el spooler.
        private void DocPrinter(SafePrinter printer, string documentName, string dataType, Stream stream, bool paused, int pagecount, string printerName)
        {
            var di1 = new DOC_INFO_1
            {
                pDataType = dataType,
                pDocName = documentName,
            };

            var id = printer.StartDocPrinter(di1);

            // Si se pidió pausar, lo hacemos NADA MÁS crear el trabajo (antes de mandar los bytes),
            // así el operario puede revisarlo en la cola antes de que salga por la impresora.
            if (paused)
            {
                // DangerousGetHandle: saca el IntPtr "pelado" del SafePrinter. Se llama "peligroso"
                // porque el GC podría liberar el SafePrinter mientras usamos el puntero; aquí es
                // seguro porque seguimos dentro del 'using' de PrintRawStream (el objeto sigue vivo).
                NativeMethods.SetJob(printer.DangerousGetHandle(), id, 0, IntPtr.Zero, (int)JobControl.Pause);
            }

            // Avisamos a quien esté suscrito de que ya hay Job ID. El '?.' evita NullReference si no hay nadie.
            OnJobCreated?.Invoke(this, new JobCreatedEventArgs { Id = id, PrinterName = printerName });

            try
            {
                PagePrinter(printer, stream, pagecount);
            }
            finally
            {
                printer.EndDocPrinter();
            }
        }

        // Envía los bytes dentro de UNA página real y luego "rellena" el contador de páginas.
        private static void PagePrinter(SafePrinter printer, Stream stream, int pagecount)
        {
            printer.StartPagePrinter();

            try
            {
                WritePrinter(printer, stream);
            }
            finally
            {
                printer.EndPagePrinter(); // cierra la página aunque WritePrinter falle a media escritura
            }

            // Truco de presentación: con RAW, Windows no sabe cuántas páginas lleva el documento (los
            // saltos de página los entiende la impresora, no el spooler). Para que la cola muestre el
            // recuento correcto, abrimos y cerramos páginas VACÍAS hasta cuadrar 'pagecount'.
            for (int i = 1; i < pagecount; i++)
            {
                printer.StartPagePrinter();
                printer.EndPagePrinter();
            }
        }

        // Lee el stream por bloques y los va volcando a la impresora. Bloques de 1 MB para no cargar
        // ficheros enormes en memoria de golpe: leemos un trozo, lo escribimos, repetimos.
        private static void WritePrinter(SafePrinter printer, Stream stream)
        {
            stream.Seek(0, SeekOrigin.Begin); // por si el stream venía "usado", empezamos desde el principio

            const int bufferSize = 1048576;  // 1 MB (1024 * 1024)
            var buffer = new byte[bufferSize];

            int read;
            // Read devuelve cuántos bytes ha leído de verdad (puede ser < bufferSize); 0 = fin del stream.
            while ((read = stream.Read(buffer, 0, bufferSize)) != 0)
            {
                printer.WritePrinter(buffer, read);
            }
        }

    }
}
