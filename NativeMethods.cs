using System;
using System.Runtime.InteropServices;

namespace RawPrint
{
    // ============================================================================================
    //  NativeMethods.cs — La "frontera" entre C# y Windows
    // --------------------------------------------------------------------------------------------
    //  Aquí vive todo el interop con el SPOOLER de impresión de Windows. El spooler vive en la DLL
    //  del sistema "winspool.drv", y exponemos sus funciones a .NET mediante P/Invoke (Platform
    //  Invocation Services): la firma [DllImport] le dice al runtime "esta función NO está en .NET,
    //  está en esa DLL nativa; cuando la llame, haz el puente (marshalling) de los argumentos".
    //
    //  Fijaos en el patrón didáctico de TODO el ejemplo: hablamos con la impresora a pelo, sin
    //  pasar por GDI ni por el driver. Le mandamos el flujo de bytes CRUDO (RAW) que la impresora
    //  ya entiende (PCL, ZPL, EPL, ESC/POS...). Por eso "RawPrint": imprimir en bruto.
    //
    //  Conceptos .NET que conviene tener claros al leer este fichero:
    //   - struct con [StructLayout(LayoutKind.Sequential)]: obliga a que los campos queden en
    //     memoria EN EL MISMO ORDEN que la struct equivalente en C, para que el marshaller copie
    //     campo a campo sin sorpresas.
    //   - CharSet.Unicode: las cadenas se pasan como UTF-16 (las funciones "...W" de Win32). Es lo
    //     que usa Windows internamente, así evitamos conversiones.
    //   - SetLastError = true: tras la llamada, .NET guarda el GetLastError() de Windows para que
    //     podamos recuperarlo con Marshal.GetLastWin32Error() (o lanzando un Win32Exception).
    // ============================================================================================

    // ReSharper disable InconsistentNaming  (mantenemos los nombres TAL CUAL la API de Win32: así
    //                                         comparar con la documentación de Microsoft es directo)
    // ReSharper disable FieldCanBeMadeReadOnly.Local

    /// <summary>
    /// Permisos con los que abrimos la impresora (parámetro de <c>OpenPrinter</c>).
    /// Para imprimir nos basta con <see cref="PRINTER_ACCESS_USE"/>; el resto son privilegios
    /// administrativos que aquí no necesitamos. Es un enum [Flags] porque en Win32 estos valores
    /// se combinan con OR de bits.
    /// </summary>
    [Flags]
    internal enum PRINTER_ACCESS_MASK : uint
    {
        PRINTER_ACCESS_ADMINISTER = 0x00000004,
        PRINTER_ACCESS_USE = 0x00000008,           // el único que usamos: "déjame mandar trabajos"
        PRINTER_ACCESS_MANAGE_LIMITED = 0x00000040,
        PRINTER_ALL_ACCESS = 0x000F000C,
    }

    /// <summary>
    /// Equivalente de la struct <c>PRINTER_DEFAULTS</c> de Win32. Le decimos a
    /// <c>OpenPrinter</c> con qué acceso queremos abrir la cola de impresión.
    /// </summary>
    // Importante: el ORDEN de los campos debe coincidir con el de la struct nativa. No los reordenéis.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct PRINTER_DEFAULTS
    {
        public string pDatatype;

        // pDevMode (configuración de página) no nos hace falta para RAW: lo dejamos a NULL.
        // Es privado y nunca lo asignamos, así que viaja como IntPtr.Zero.
        private IntPtr pDevMode;

        public PRINTER_ACCESS_MASK DesiredPrinterAccess;
    }

    /// <summary>
    /// Equivalente de <c>DOC_INFO_1</c>: describe el documento que vamos a enviar al spooler
    /// (nombre visible en la cola, fichero de salida y tipo de datos).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DOC_INFO_1
    {
        public string pDocName;     // nombre que veréis en la cola de impresión de Windows

        public string pOutputFile;  // null = a la impresora; si pusiéramos ruta, "imprimiría a fichero"

        public string pDataType;    // "RAW" (bytes crudos) o "XPS_PASS" para drivers XPS. Ver IsXPSDriver.
    }

    /// <summary>
    /// Equivalente de <c>DRIVER_INFO_3</c>: información del driver de la impresora. Solo lo usamos
    /// para mirar <see cref="pDependentFiles"/> y averiguar si el driver es XPS.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DRIVER_INFO_3
    {
        public uint cVersion;
        public string pName;
        public string pEnvironment;
        public string pDriverPath;
        public string pDataFile;
        public string pConfigFile;
        public string pHelpFile;
        // OJO: pDependentFiles es un MULTI_SZ (lista de cadenas pegadas, cada una con su \0 y un \0
        // final extra). No es una string normal, por eso lo recibimos como IntPtr (puntero crudo) y
        // lo recorremos a mano en SafePrinter.ReadMultiSz.
        public IntPtr pDependentFiles;
        public string pMonitorName;
        public string pDefaultDataType;
    }

    /// <summary>
    /// Comandos de control de un trabajo de impresión para <c>SetJob</c>. Aquí solo usamos
    /// <see cref="Pause"/> (cuando se pide imprimir "en pausa"), el resto se incluye por completitud.
    /// </summary>
    internal enum JobControl
    {
        Pause = 0x01,
        Resume = 0x02,
        Cancel = 0x03,
        Restart = 0x04,
        Delete = 0x05,
        Retain = 0x08,
        Release = 0x09,
    }

    /// <summary>
    /// Declaraciones P/Invoke de las funciones del spooler de Windows (winspool.drv).
    /// Cada método es el "espejo" en C# de una función nativa: el runtime hace el puente.
    /// </summary>
    internal class NativeMethods
    {
        // Cierra el handle de impresora devuelto por OpenPrinter. Lo llama SafePrinter.ReleaseHandle.
        [DllImport("winspool.drv", SetLastError = true)]
        public static extern int ClosePrinter(IntPtr hPrinter);

        // Devuelve datos del driver (DRIVER_INFO_*). Truco clásico de Win32: se llama DOS veces,
        // la primera con buffer 0 para que nos diga cuánta memoria necesita (pcbNeeded), y la
        // segunda ya con el buffer reservado. Ver GetPrinterDriverDependentFiles.
        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int GetPrinterDriver(IntPtr hPrinter, string? pEnvironment, int Level, IntPtr pDriverInfo, int cbBuf, ref int pcbNeeded);

        // Abre un documento en la cola y devuelve su Job ID (>0 si va bien, 0 si falla).
        // Sufijo W = versión Unicode de la API.
        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint StartDocPrinterW(IntPtr hPrinter, uint level, [MarshalAs(UnmanagedType.Struct)] ref DOC_INFO_1 di1);

        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int EndDocPrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int StartPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int EndPagePrinter(IntPtr hPrinter);

        // El corazón del ejemplo: vuelca un bloque de bytes directamente a la impresora.
        // [In, Out] sobre el byte[] indica al marshaller que copie el array en ambos sentidos.
        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int WritePrinter(IntPtr hPrinter, [In, Out] byte[] pBuf, int cbBuf, ref int pcWritten);

        // Abre la impresora por nombre y nos da su handle (out phPrinter). Puerta de entrada a todo.
        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int OpenPrinterW(string pPrinterName, out IntPtr phPrinter, ref PRINTER_DEFAULTS pDefault);

        // Pausa/reanuda/cancela un trabajo. Usamos EntryPoint = "SetJobA" (versión ANSI) porque
        // aquí pasamos pJob = IntPtr.Zero (sin struct de job), así que el juego de caracteres da igual.
        [DllImport("winspool.drv", EntryPoint = "SetJobA", SetLastError = true)]
        public static extern int SetJob(IntPtr hPrinter, uint JobId, uint Level, IntPtr pJob, uint Command_Renamed);
    }


    // ReSharper restore FieldCanBeMadeReadOnly.Local
    // ReSharper restore InconsistentNaming
}
