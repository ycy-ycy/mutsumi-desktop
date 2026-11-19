using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Mutsumi.Desktop
{
    // The SQLite library included with Windows uses the Windows calling convention.
    internal sealed class MemoryDatabase : IDisposable
    {
        private const string Library = "winsqlite3.dll";
        private IntPtr db;

        [DllImport(Library, CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int sqlite3_open16(string filename, out IntPtr handle);
        [DllImport(Library)] private static extern int sqlite3_close(IntPtr handle);
        [DllImport(Library)] private static extern int sqlite3_busy_timeout(IntPtr handle, int milliseconds);
        [DllImport(Library)] private static extern IntPtr sqlite3_errmsg16(IntPtr handle);
        [DllImport(Library, CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int sqlite3_prepare16_v2(IntPtr handle, string sql, int bytes, out IntPtr statement, IntPtr tail);
        [DllImport(Library, CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int sqlite3_bind_text16(IntPtr statement, int index, string value, int bytes, IntPtr destructor);
        [DllImport(Library)] private static extern int sqlite3_step(IntPtr statement);
        [DllImport(Library)] private static extern int sqlite3_finalize(IntPtr statement);
        [DllImport(Library)] private static extern int sqlite3_column_count(IntPtr statement);
        [DllImport(Library)] private static extern IntPtr sqlite3_column_text16(IntPtr statement, int index);

        public MemoryDatabase(string path)
        {
            int result = sqlite3_open16(path, out db);
            try
            {
                Check(result);
                Check(sqlite3_busy_timeout(db, 600));
                Execute("PRAGMA foreign_keys=ON");
                Execute("PRAGMA secure_delete=ON");
            }
            catch { Dispose(); throw; }
        }

        public List<string[]> Execute(string sql, params string[] parameters)
        {
            IntPtr statement;
            Check(sqlite3_prepare16_v2(db, sql, -1, out statement, IntPtr.Zero));
            try
            {
                for (int i = 0; i < parameters.Length; i++)
                    Check(sqlite3_bind_text16(statement, i + 1, parameters[i], -1, new IntPtr(-1)));
                var rows = new List<string[]>();
                int result;
                while ((result = sqlite3_step(statement)) == 100) // SQLITE_ROW
                {
                    var row = new string[sqlite3_column_count(statement)];
                    for (int i = 0; i < row.Length; i++)
                        row[i] = Marshal.PtrToStringUni(sqlite3_column_text16(statement, i));
                    rows.Add(row);
                }
                if (result != 101) Check(result); // SQLITE_DONE
                return rows;
            }
            finally { sqlite3_finalize(statement); }
        }

        private void Check(int result)
        {
            if (result != 0)
                throw new InvalidOperationException("无法访问桌宠记忆库：" +
                    (db == IntPtr.Zero ? result.ToString() : Marshal.PtrToStringUni(sqlite3_errmsg16(db))));
        }

        public void Dispose()
        {
            if (db != IntPtr.Zero) { sqlite3_close(db); db = IntPtr.Zero; }
        }
    }
}
