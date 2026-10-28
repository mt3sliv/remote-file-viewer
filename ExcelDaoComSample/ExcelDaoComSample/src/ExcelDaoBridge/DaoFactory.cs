using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ExcelDaoBridge
{
    // Only this class is COM-creatable. It exposes late-bound IDispatch to VBA.
    [ComVisible(true)]
    [Guid("851B210C-EE64-4F4D-85FD-19BC1C480D85")]
    [ProgId("ExcelDaoBridge.Factory")]
    [ClassInterface(ClassInterfaceType.AutoDual)]
    public sealed class DaoFactory
    {
        private const int DbVersion120 = 128;
        private const int DbFailOnError = 128;

        public string DefaultPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ExcelDaoBridge", "sample.accdb");

        // Returns the real Access DAO.Database COM object, NOT a .NET wrapper.
        // Caller must Close it after closing all Recordsets.
        public object OpenDatabase(string absolutePath)
        {
            if (string.IsNullOrWhiteSpace(absolutePath))
                throw new ArgumentException("Database path must not be empty.", nameof(absolutePath));
            string path = Path.GetFullPath(absolutePath);
            if (!File.Exists(path))
                throw new FileNotFoundException("DAO database not found.", path);

            dynamic engine = NewEngine();
            return engine.OpenDatabase(path, false, false, "");
        }

        // Creates a sample local .accdb if absent; then returns it OPEN as DAO.Database.
        public object OpenOrCreateSample()
        {
            string path = DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (!File.Exists(path))
            {
                dynamic engine = NewEngine();
                dynamic db = null;
                try
                {
                    db = engine.CreateDatabase(path, ";LANGID=0x0409;CP=1252;COUNTRY=0", DbVersion120);
                    db.Execute("CREATE TABLE [People] ([Id] COUNTER CONSTRAINT [PK_People] PRIMARY KEY, " +
                               "[FullName] TEXT(100), [City] TEXT(100))", DbFailOnError);
                    db.Execute("INSERT INTO [People] ([FullName],[City]) VALUES ('Ada Lovelace','London')", DbFailOnError);
                    db.Execute("INSERT INTO [People] ([FullName],[City]) VALUES ('Grace Hopper','New York')", DbFailOnError);
                    db.Execute("INSERT INTO [People] ([FullName],[City]) VALUES ('Alan Turing','Manchester')", DbFailOnError);
                }
                finally
                {
                    if (db != null) db.Close();
                }
            }
            return OpenDatabase(path);
        }

        private static object NewEngine()
        {
            // Office / Access Database Engine installs the DAO runtime.
            // Try current ACE first, with older ACE fallback.
            foreach (string progId in new[] { "DAO.DBEngine.160", "DAO.DBEngine.120" })
            {
                Type type = Type.GetTypeFromProgID(progId, throwOnError: false);
                if (type != null)
                    return Activator.CreateInstance(type);
            }
            throw new COMException("DAO/ACE runtime not found. Install Microsoft Access Runtime " +
                "or Microsoft Access Database Engine matching Excel bitness.");
        }
    }
}
