using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace Mutsumi.Desktop
{
    internal sealed class Notebook
    {
        public string name { get; set; }
        public string[] fields { get; set; }
        public int count { get; set; }
    }

    internal sealed class MemoryRead
    {
        public string book { get; set; }
        public string contains { get; set; }
        public long before { get; set; }
    }

    internal sealed class MemoryValue
    {
        public string key { get; set; }
        public string value { get; set; }
    }

    internal sealed class MemoryChange
    {
        public string op { get; set; }
        public string book { get; set; }
        public long id { get; set; }
        public string[] fields { get; set; }
        public MemoryValue[] values { get; set; }
    }

    // One snapshot per user turn. Only requested pages leave the local database.
    internal sealed class PetMemory
    {
        private readonly string path = Path.Combine(PetState.Folder, "memory.sqlite");
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        private readonly HashSet<long> seen = new HashSet<long>();
        private readonly HashSet<string> readBooks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private const int PageSize = 60;
        private readonly long revision;
        public Notebook[] Books { get; } = Array.Empty<Notebook>();

        public PetMemory()
        {
            if (!File.Exists(path)) return;
            using (var db = new MemoryDatabase(path))
            {
                db.Execute("BEGIN");
                // A rolled-back first write can leave an empty SQLite file.
                if (db.Execute("SELECT name FROM sqlite_master WHERE type='table' AND name='memory_meta'").Count == 0) return;
                revision = Revision(db);
                Books = db.Execute("SELECT b.name,b.fields,count(e.id) FROM books b LEFT JOIN entries e ON b.name=e.book GROUP BY b.name ORDER BY b.name")
                    .Select(r => new Notebook { name = r[0], fields = json.Deserialize<string[]>(r[1]), count = int.Parse(r[2]) }).ToArray();
                db.Execute("COMMIT");
            }
        }

        private static void EnsureSchema(MemoryDatabase db)
        {
            db.Execute("CREATE TABLE IF NOT EXISTS memory_meta (id INTEGER PRIMARY KEY CHECK(id=1), revision INTEGER NOT NULL)");
            db.Execute("CREATE TABLE IF NOT EXISTS books (name TEXT PRIMARY KEY COLLATE NOCASE NOT NULL, fields TEXT NOT NULL)");
            db.Execute("CREATE TABLE IF NOT EXISTS entries (id INTEGER PRIMARY KEY AUTOINCREMENT, book TEXT NOT NULL REFERENCES books(name) ON DELETE CASCADE, recorded TEXT NOT NULL, data TEXT NOT NULL)");
            db.Execute("CREATE INDEX IF NOT EXISTS entries_book_id ON entries(book,id)");
            db.Execute("INSERT OR IGNORE INTO memory_meta VALUES (1,0)");
        }

        private static long Revision(MemoryDatabase db) => long.Parse(db.Execute("SELECT revision FROM memory_meta WHERE id=1")[0][0]);
        private void CheckRevision(MemoryDatabase db)
        {
            if (Revision(db) != revision) throw new InvalidOperationException("记忆库刚有更新，右键重试即可按最新内容处理。");
        }

        public MemoryRead[] MissingReads(MemoryChange[] changes)
        {
            // Before writing into an existing notebook, give the model a chance to find a matching entry.
            return Books.Where(b => !readBooks.Contains(b.name) && changes.Any(c => c != null &&
                    string.Equals(b.name, c.book?.Trim(), StringComparison.OrdinalIgnoreCase) &&
                    c.op != "book" && (b.count > 0 || c.op == "forget_book")))
                .Select(b => new MemoryRead { book = b.name, contains = "" }).ToArray();
        }

        public object Read(MemoryRead query)
        {
            if (query == null || query.before < 0) throw InvalidChange();
            var book = Books.FirstOrDefault(b => string.Equals(b.name, query.book?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (book == null) return new { query, missing = true, entries = Array.Empty<object>(), nextBefore = 0 };
            using (var db = new MemoryDatabase(path))
            {
                db.Execute("BEGIN");
                CheckRevision(db);
                var rows = db.Execute("SELECT id,recorded,data FROM entries WHERE book=?1 AND (?2='0' OR id<CAST(?2 AS INTEGER)) " +
                    "AND instr(lower(recorded || ' ' || data),lower(?3))>0 ORDER BY id DESC LIMIT " + (PageSize + 1),
                    book.name, query.before.ToString(CultureInfo.InvariantCulture), query.contains ?? "");
                var entries = rows.Take(PageSize).Select(r => new {
                    id = long.Parse(r[0]), recorded = r[1], data = json.Deserialize<Dictionary<string,string>>(r[2])
                }).ToArray();
                db.Execute("COMMIT");
                foreach (var row in entries) seen.Add(row.id);
                readBooks.Add(book.name);
                return new { query, entries, nextBefore = rows.Count > PageSize ? entries.Last().id : 0 };
            }
        }

        // Commit only after the UI accepts the still-current reply. Discarded requests never write.
        public void Apply(MemoryChange[] changes)
        {
            if (changes == null || changes.Length == 0) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var db = new MemoryDatabase(path))
            {
                db.Execute("BEGIN IMMEDIATE");
                EnsureSchema(db);
                CheckRevision(db);
                foreach (var change in changes) ApplyOne(db, change);
                db.Execute("UPDATE memory_meta SET revision=revision+1 WHERE id=1");
                db.Execute("COMMIT"); // Closing an uncommitted connection rolls the entire batch back.
            }
        }

        private static string Name(string value)
        {
            string name = value?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 100 || name.Any(char.IsControl))
                throw InvalidChange();
            return name;
        }

        private static InvalidOperationException InvalidChange() =>
            new InvalidOperationException("这次记忆变更没有保存。请说明要修改的记录，或右键重试。");

        private void ApplyOne(MemoryDatabase db, MemoryChange change)
        {
            if (change == null) throw InvalidChange();
            string book = Name(change.book);
            var notebook = db.Execute("SELECT name,fields FROM books WHERE name=? COLLATE NOCASE", book);
            if (notebook.Count > 0) book = notebook[0][0];
            string[] fields = notebook.Count == 0 ? Array.Empty<string>() : json.Deserialize<string[]>(notebook[0][1]);
            if (change.op == "book")
            {
                var merged = fields.Concat((change.fields ?? Array.Empty<string>()).Select(Name)).Distinct().ToArray();
                if (merged.Length == 0) throw InvalidChange();
                if (notebook.Count == 0) db.Execute("INSERT INTO books(name,fields) VALUES(?,?)", book, json.Serialize(merged));
                else db.Execute("UPDATE books SET fields=? WHERE name=?", json.Serialize(merged), book);
                return;
            }
            if (notebook.Count == 0) throw InvalidChange();
            if (change.op == "forget_book")
            {
                if (!readBooks.Contains(book)) throw InvalidChange();
                db.Execute("DELETE FROM books WHERE name=?", book);
                return;
            }
            var data = new Dictionary<string,string>();
            string id = change.id.ToString(CultureInfo.InvariantCulture);
            if (change.op == "edit" || change.op == "forget")
            {
                if (!seen.Contains(change.id)) throw InvalidChange();
                var entry = db.Execute("SELECT data FROM entries WHERE book=? AND id=?", book, id);
                if (entry.Count != 1) throw InvalidChange();
                if (change.op == "forget") { db.Execute("DELETE FROM entries WHERE book=? AND id=?", book, id); return; }
                data = json.Deserialize<Dictionary<string,string>>(entry[0][0]);
            }
            else if (change.op != "add") throw InvalidChange();
            if (change.values == null || change.values.Length == 0) throw InvalidChange();
            foreach (var pair in change.values)
            {
                if (pair == null || !fields.Contains(pair.key) || pair.value == null || pair.value.IndexOf('\0') >= 0) throw InvalidChange();
                data[pair.key] = pair.value;
            }
            if (change.op == "edit") db.Execute("UPDATE entries SET data=? WHERE book=? AND id=?", json.Serialize(data), book, id);
            else db.Execute("INSERT INTO entries(book,recorded,data) VALUES(?,?,?)", book,
                DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture), json.Serialize(data));
        }
    }
}
