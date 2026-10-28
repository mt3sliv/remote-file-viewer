# Excel DAO COM bridge (C#)

A small Windows **.NET Framework 4.8** COM DLL exposing `ExcelDaoBridge.Factory` for Excel VBA's `CreateObject`. Unlike a wrapper returning ADO.NET data, it returns the **actual open `DAO.Database` COM instance**, backed by a local Access `.accdb` file.

## Prerequisites
- Windows, Visual Studio 2022 with .NET desktop development and .NET Framework 4.8 targeting pack.
- Desktop Microsoft Excel with VBA.
- Microsoft Access Database Engine (ACE/DAO) installed, typically via Access or Microsoft Access Runtime, with compatible **Office bitness** (x86 or x64). Not every Office configuration permits separate engine installation.
- Administrator permissions to register the COM DLL machine-wide.

## Build and register
1. Open `src/ExcelDaoBridge/ExcelDaoBridge.csproj` in Visual Studio and build **Release**. Or run `dotnet build src/ExcelDaoBridge/ExcelDaoBridge.csproj -c Release` on Windows with the targeting pack present.
2. Open **PowerShell as Administrator** in this folder and run `powershell -ExecutionPolicy Bypass -File .\scripts\Register.ps1 -OfficeBitness x64` (substitute `x86` for 32-bit Excel).
3. Keep the compiled DLL in the same directory after registration; `/codebase` records its full path.
4. Excel: Alt+F11 > Insert > Module, import `excel/ExcelExample.bas` (or paste its code), and run `QueryDaoIntoExcel`.
5. To unregister, run `powershell -ExecutionPolicy Bypass -File .\scripts\Unregister.ps1 -OfficeBitness x64`.

## COM interface
- `CreateObject("ExcelDaoBridge.Factory")`
- `bridge.DefaultPath`: path to `%LOCALAPPDATA%\ExcelDaoBridge\sample.accdb`
- `Set db = bridge.OpenOrCreateSample()`: lazily creates/initializes a local sample database and returns open DAO.Database
- `Set db = bridge.OpenDatabase("C:\Data\mydata.accdb")`: opens an existing Access file and returns open DAO.Database
- `Set rs = db.OpenRecordset("SELECT * FROM [People]")`: **DAO** recordset (not ADODB)
- `rs.Close` and `db.Close`: caller owns cleanup

## Important trade-offs
- DAO.Database is an object, not an ADO `Connection`. This matches the requirement for DAO. SQLite does **not** natively provide a DAO.Database COM object.
- The bridge and Access engine run in Excel's process; the database stays on disk (not embedded in the DLL).
- Works in Windows Excel, not Excel for the web, and cannot be registered on macOS/Linux.
- `.accdb` creation uses DAO `dbVersion120` and an ACE locale; if ACE is absent, creating/opening will fail.
- Class uses `ClassInterfaceType.AutoDual` for a compact late-binding demo. For a production binary-compatible COM API, switch to an explicit versioned `IDispatch` interface (InterfaceIsIDispatch) and stable DISPIDs.
- Do not publish the example with untrusted SQL interpolated into `OpenRecordset`; use DAO QueryDefs and parameters for user-provided values.
- `/codebase` without a strong name can cause RegAsm warning; for production, sign the assembly, package with an installer, and avoid moving the DLL after registration.
