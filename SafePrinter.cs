using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RawPrint
{
    // ============================================================================================
    //  SafePrinter.cs — Un handle de impresora que se cierra SOLO (RAII al estilo .NET)
    // --------------------------------------------------------------------------------------------
    //  Cuando abrimos una impresora con OpenPrinter, Windows nos da un "handle" (un puntero opaco a
    //  un recurso del sistema). Ese handle HAY que cerrarlo con ClosePrinter sí o sí; si se nos
    //  olvida, fuga de recursos. ¿Cómo garantizamos el cierre aunque salte una excepción a mitad?
    //
    //  Heredando de SafeHandle (aquí SafeHandleZeroOrMinusOneIsInvalid, la variante para handles
    //  donde 0 e -1 significan "inválido"). Es la forma canónica en .NET de envolver un recurso
    //  no gestionado: el Garbage Collector y la sentencia `using` se encargan de llamar a
    //  ReleaseHandle() por nosotros. Fijaos: NO escribimos un destructor ni un Dispose a mano,
    //  SafeHandle ya trae toda esa fontanería (y además es seguro frente a abortos de hilo).
    //
    //  Resumen para quien venga de PowerBuilder: es como una clase que en su destructor cierra el
    //  recurso, pero hecho a prueba de balas por el propio framework.
    // ============================================================================================
    internal class SafePrinter : SafeHandleZeroOrMinusOneIsInvalid
    {
        // Constructor PRIVADO a propósito: solo se crea un SafePrinter a través de OpenPrinter (más
        // abajo), nunca con "new" desde fuera. Así no hay forma de tener un handle sin abrir bien.
        // El `true` que pasamos a base() = "este handle es nuestro, encárgate de liberarlo".
        private SafePrinter(IntPtr hPrinter)
            : base(true)
        {
            handle = hPrinter;
        }

        /// <summary>
        /// Lo llama el framework (vía Dispose/finalizador) para devolver el handle a Windows.
        /// Nosotros NO lo invocamos a mano: basta con un <c>using</c> sobre el SafePrinter.
        /// </summary>
        protected override bool ReleaseHandle()
        {
            if (IsInvalid)
            {
                return false;
            }

            // ClosePrinter devuelve != 0 si fue bien. Anulamos el handle para no cerrarlo dos veces.
            var result = NativeMethods.ClosePrinter(handle) != 0;
            handle = IntPtr.Zero;

            return result;
        }

        /// <summary>
        /// Abre un documento en la cola de impresión y devuelve su identificador de trabajo (Job ID).
        /// Es el primer paso del ciclo: StartDoc -> StartPage -> WritePrinter -> EndPage -> EndDoc.
        /// </summary>
        public uint StartDocPrinter(DOC_INFO_1 di1)
        {
            var id = NativeMethods.StartDocPrinterW(handle, 1, ref di1);
            if (id == 0)
            {
                // 1804 = ERROR_INVALID_DATATYPE. El típico fallo cuando el driver no admite "RAW".
                // Damos un mensaje útil en vez de un código de error a secas. El Win32Exception
                // interior conserva el error original de Windows como InnerException.
                if (Marshal.GetLastWin32Error() == 1804)
                {
                    throw new Exception("The specified datatype is invalid, try setting 'Enable advanced printing features' in printer properties.", new Win32Exception());
                }
                // Win32Exception() sin argumentos lee automáticamente Marshal.GetLastWin32Error()
                // y lo traduce a un mensaje legible. Por eso usamos SetLastError=true en los DllImport.
                throw new Win32Exception();
            }

            return id;
        }

        /// <summary>Cierra el documento abierto con <see cref="StartDocPrinter"/>.</summary>
        public void EndDocPrinter()
        {
            if (NativeMethods.EndDocPrinter(handle) == 0)
            {
                throw new Win32Exception();
            }
        }

        /// <summary>Marca el inicio de una página dentro del documento.</summary>
        public void StartPagePrinter()
        {
            if (NativeMethods.StartPagePrinter(handle) == 0)
            {
                throw new Win32Exception();
            }
        }

        /// <summary>Marca el fin de la página actual.</summary>
        public void EndPagePrinter()
        {
            if (NativeMethods.EndPagePrinter(handle) == 0)
            {
                throw new Win32Exception();
            }
        }

        /// <summary>
        /// Vuelca <paramref name="size"/> bytes del <paramref name="buffer"/> a la impresora.
        /// Aquí es donde realmente "salen" los datos crudos hacia el dispositivo.
        /// </summary>
        public void WritePrinter(byte[] buffer, int size)
        {
            int written = 0;
            if (NativeMethods.WritePrinter(handle, buffer, size, ref written) == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        /// <summary>
        /// Devuelve la lista de ficheros de los que depende el driver de esta impresora.
        /// Lo usamos para detectar drivers XPS (ver <c>RawPrint.IsXPSDriver</c>).
        /// </summary>
        /// <remarks>
        /// Aquí está lo jugoso del interop. Patrón clásico de Win32 en DOS pasos:
        ///   1) Llamamos a GetPrinterDriver con buffer 0 esperando que FALLE con
        ///      ERROR_INSUFFICIENT_BUFFER (122), pero rellenando bufferSize con los bytes necesarios.
        ///   2) Reservamos esa memoria NO GESTIONADA con Marshal.AllocHGlobal (vive fuera del GC, en
        ///      el heap nativo) y repetimos la llamada, ahora sí, con sitio donde escribir.
        /// El try/finally garantiza que SIEMPRE liberamos esa memoria con FreeHGlobal: en interop,
        /// quien reserva, libera. El GC no lo hará por nosotros porque no es memoria gestionada.
        /// </remarks>
        public IEnumerable<string> GetPrinterDriverDependentFiles()
        {
            int bufferSize = 0;

            // Paso 1: sondeo de tamaño. Esperamos fallo CON error 122; cualquier otra cosa es un fallo real.
            if (NativeMethods.GetPrinterDriver(handle, null, 3, IntPtr.Zero, 0, ref bufferSize) != 0 || Marshal.GetLastWin32Error() != 122) // 122 = ERROR_INSUFFICIENT_BUFFER
            {
                throw new Win32Exception();
            }

            // Reservamos memoria nativa del tamaño exacto que nos pidió Windows.
            var ptr = Marshal.AllocHGlobal(bufferSize);

            try
            {
                // Paso 2: ahora sí, con buffer. Windows rellena ptr con un DRIVER_INFO_3.
                if (NativeMethods.GetPrinterDriver(handle, null, 3, ptr, bufferSize, ref bufferSize) == 0)
                {
                    throw new Win32Exception();
                }

                // Marshal.PtrToStructure<T>() (genérico, C# moderno) "moldea" los bytes crudos del
                // puntero a nuestra struct C#, copiando campo a campo según el StructLayout.
                var di3 = Marshal.PtrToStructure<DRIVER_INFO_3>(ptr);

                // ¡OJO con la materialización! ReadMultiSz es un iterador perezoso que lee del puntero;
                // .ToList() lo FUERZA a leer YA, mientras la memoria sigue viva. Si devolviéramos el
                // iterador sin más, el finally de abajo liberaría 'ptr' antes de recorrerlo -> crash.
                return ReadMultiSz(di3.pDependentFiles).ToList(); // We need a list because FreeHGlobal will be called on return
            }
            finally
            {
                // Liberamos la memoria nativa. Imprescindible: en interop, quien reserva, libera.
                Marshal.FreeHGlobal(ptr);
            }
        }

        /// <summary>
        /// Recorre un bloque MULTI_SZ y va devolviendo cada cadena. Un MULTI_SZ es el formato de
        /// Win32 para "lista de cadenas": cadenas pegadas separadas por '\0', y un '\0' extra al final.
        /// </summary>
        // Lo recorremos a mano leyendo carácter a carácter (Unicode = 2 bytes, de ahí los "pos += 2"
        // y Marshal.ReadInt16). Es un iterador (yield return): produce las cadenas bajo demanda.
        private static IEnumerable<string> ReadMultiSz(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero)
            {
                yield break;
            }

            var builder = new StringBuilder();
            var pos = ptr;

            while (true)
            {
                var c = (char)Marshal.ReadInt16(pos); // leemos un char UTF-16 del heap nativo

                if (c == '\0')
                {
                    // Dos '\0' seguidos (cadena vacía) = fin del MULTI_SZ -> paramos.
                    if (builder.Length == 0)
                    {
                        break;
                    }

                    // Un solo '\0' = fin de UNA cadena -> la devolvemos y arrancamos la siguiente.
                    yield return builder.ToString();
                    builder = new StringBuilder();
                }
                else
                {
                    builder.Append(c);
                }

                pos += 2; // avanzamos 2 bytes = 1 carácter UTF-16
            }
        }

        /// <summary>
        /// Abre la impresora indicada y devuelve un <see cref="SafePrinter"/> que la cierra sola.
        /// Es el ÚNICO punto de entrada para crear un SafePrinter (constructor privado). Úsalo con
        /// <c>using</c> para garantizar el cierre.
        /// </summary>
        /// <param name="printerName">Nombre de la impresora tal y como aparece en Windows.</param>
        /// <param name="defaults">Permisos de apertura (normalmente PRINTER_ACCESS_USE).</param>
        public static SafePrinter OpenPrinter(string printerName, ref PRINTER_DEFAULTS defaults)
        {
            IntPtr hPrinter;

            if (NativeMethods.OpenPrinterW(printerName, out hPrinter, ref defaults) == 0)
            {
                throw new Win32Exception();
            }

            return new SafePrinter(hPrinter);
        }

    }
}
