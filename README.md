# 🖨️ RawPrint

![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?style=flat-square&logo=dotnet&logoColor=white)
![C#](https://img.shields.io/badge/C%23-239120?style=flat-square&logo=csharp&logoColor=white)
![Windows](https://img.shields.io/badge/Windows-spooler-0078D6?style=flat-square&logo=windows&logoColor=white)
![Blog](https://img.shields.io/badge/blog-rsrsystem-FF5722?style=flat-square&logo=blogger&logoColor=white)

> Librería **.NET 10** para enviar archivos **directamente a la impresora** (RAW, sin diálogos) desde PowerBuilder.

## 📋 ¿Qué es esto?

A veces no quieres abrir visores ni diálogos: quieres mandar un fichero **tal cual** a la cola de
impresión (un `.prn`, un PDF ya rasterizado, etiquetas ZPL…). Esta librería habla directamente con el
**spooler de Windows** (`winspool.drv`) para hacer justo eso, y se consume cómodamente desde
PowerBuilder como un `dotnetobject`.

## 🛠️ Requisitos

- **.NET SDK 10.0** o superior
- **Windows** (usa el spooler vía P/Invoke)

## 🚀 Compilar

```bat
dotnet build RawPrint.csproj -c Release
```

La DLL queda en `bin\Release\net10.0\`.

## 🔗 Proyecto Visual Studio / PowerBuilder

👉 **RawPrint** — https://github.com/rasanfe/RawPrint

## 🙌 Créditos

Basado en el proyecto original **RawPrint** de **Frogmore Computer Services Ltd**:
👉 https://github.com/frogmorecs/RawPrint

---

📨 **Blog:** <https://rsrsystem.blogspot.com/>

> ¡Nos vemos en el próximo artículo! Y recuerda: en PowerBuilder, los límites solo están en nuestra imaginación. 🚀
