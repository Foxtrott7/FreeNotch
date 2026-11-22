using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace WindowsNotch.Services.Database
{
    public enum ClipboardItemKind
    {
        Text,
        Link,
        Image
    }

    public sealed class FileTrayRecord
    {
        public string Id { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public bool IsDirectory { get; set; }
        public DateTime AddedAt { get; set; }
        public bool IsPinned { get; set; }
    }

    public sealed class ClipboardRecord
    {
        public string Id { get; set; } = string.Empty;
        public ClipboardItemKind Kind { get; set; }
        public string? TextContent { get; set; }
        public string? ImagePath { get; set; }
        public string PreviewText { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public bool IsPinned { get; set; }
        public string ContentHash { get; set; } = string.Empty;
    }

    public sealed class TodoRecord
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public bool IsDone { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// Lokale SQLite-Datenbank (%AppData%\WindowsNotch\notch.db) für File Tray und Zwischenablage-Verlauf.
    /// </summary>
    public sealed class NotchDatabase
    {
        private static readonly Lazy<NotchDatabase> _instance = new(() => new NotchDatabase());
        public static NotchDatabase Instance => _instance.Value;

        private readonly object _sync = new();
        private readonly string _connectionString;
        private bool _initialized;

        public string DataDirectory { get; }
        public string DatabasePath { get; }
        public string ClipboardImagesDirectory { get; }
        public string FaviconCacheDirectory { get; }

        private NotchDatabase()
        {
            DataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WindowsNotch");
            DatabasePath = Path.Combine(DataDirectory, "notch.db");
            ClipboardImagesDirectory = Path.Combine(DataDirectory, "clipboard_images");
            FaviconCacheDirectory = Path.Combine(DataDirectory, "favicons");
            _connectionString = $"Data Source={DatabasePath};Cache=Shared";
        }

        private SqliteConnection OpenConnection()
        {
            EnsureInitialized();
            var conn = new SqliteConnection(_connectionString);
            conn.Open();
            return conn;
        }

        public void EnsureInitialized()
        {
            if (_initialized) return;

            lock (_sync)
            {
                if (_initialized) return;

                Directory.CreateDirectory(DataDirectory);
                Directory.CreateDirectory(ClipboardImagesDirectory);
                Directory.CreateDirectory(FaviconCacheDirectory);

                using var conn = new SqliteConnection(_connectionString);
                conn.Open();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
PRAGMA journal_mode = WAL;

CREATE TABLE IF NOT EXISTS file_tray_items (
    id TEXT PRIMARY KEY,
    file_path TEXT NOT NULL UNIQUE,
    file_name TEXT NOT NULL,
    file_size INTEGER NOT NULL,
    is_directory INTEGER NOT NULL DEFAULT 0,
    added_at TEXT NOT NULL,
    is_pinned INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS idx_file_tray_order
    ON file_tray_items (is_pinned DESC, added_at DESC);

CREATE TABLE IF NOT EXISTS clipboard_items (
    id TEXT PRIMARY KEY,
    kind TEXT NOT NULL,
    text_content TEXT,
    image_path TEXT,
    preview_text TEXT NOT NULL,
    created_at TEXT NOT NULL,
    is_pinned INTEGER NOT NULL DEFAULT 0,
    content_hash TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_clipboard_order
    ON clipboard_items (is_pinned DESC, created_at DESC);

CREATE INDEX IF NOT EXISTS idx_clipboard_hash
    ON clipboard_items (content_hash);

CREATE TABLE IF NOT EXISTS dashboard_todos (
    id TEXT PRIMARY KEY,
    title TEXT NOT NULL,
    is_done INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_todos_order
    ON dashboard_todos (is_done ASC, created_at DESC);

CREATE TABLE IF NOT EXISTS dashboard_notes (
    id TEXT PRIMARY KEY,
    content TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
";
                cmd.ExecuteNonQuery();
                _initialized = true;
            }
        }

        // ==================== File Tray ====================

        public List<FileTrayRecord> GetFileTrayItems()
        {
            lock (_sync)
            {
                var result = new List<FileTrayRecord>();
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
SELECT id, file_path, file_name, file_size, is_directory, added_at, is_pinned
FROM file_tray_items
ORDER BY is_pinned DESC, added_at ASC;";

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(new FileTrayRecord
                    {
                        Id = reader.GetString(0),
                        FilePath = reader.GetString(1),
                        FileName = reader.GetString(2),
                        FileSize = reader.GetInt64(3),
                        IsDirectory = reader.GetInt32(4) != 0,
                        AddedAt = ParseDate(reader.GetString(5)),
                        IsPinned = reader.GetInt32(6) != 0
                    });
                }
                return result;
            }
        }

        public void UpsertFileTrayItem(FileTrayRecord item)
        {
            lock (_sync)
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
INSERT INTO file_tray_items (id, file_path, file_name, file_size, is_directory, added_at, is_pinned)
VALUES ($id, $path, $name, $size, $dir, $added, $pinned)
ON CONFLICT(file_path) DO UPDATE SET
    file_name = excluded.file_name,
    file_size = excluded.file_size,
    is_directory = excluded.is_directory,
    added_at = excluded.added_at;";

                cmd.Parameters.AddWithValue("$id", item.Id);
                cmd.Parameters.AddWithValue("$path", item.FilePath);
                cmd.Parameters.AddWithValue("$name", item.FileName);
                cmd.Parameters.AddWithValue("$size", item.FileSize);
                cmd.Parameters.AddWithValue("$dir", item.IsDirectory ? 1 : 0);
                cmd.Parameters.AddWithValue("$added", item.AddedAt.ToUniversalTime().ToString("O"));
                cmd.Parameters.AddWithValue("$pinned", item.IsPinned ? 1 : 0);
                cmd.ExecuteNonQuery();
            }
        }

        public void SetFileTrayPinned(string id, bool pinned)
        {
            lock (_sync)
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE file_tray_items SET is_pinned = $pinned WHERE id = $id;";
                cmd.Parameters.AddWithValue("$pinned", pinned ? 1 : 0);
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
        }

        public void DeleteFileTrayItem(string id)
        {
            lock (_sync)
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM file_tray_items WHERE id = $id;";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
        }

        public void ClearFileTray(bool includePinned = false)
        {
            lock (_sync)
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = includePinned
                    ? "DELETE FROM file_tray_items;"
                    : "DELETE FROM file_tray_items WHERE is_pinned = 0;";
                cmd.ExecuteNonQuery();
            }
        }

        // ==================== Zwischenablage ====================

        public List<ClipboardRecord> GetClipboardItems(int limit = 150)
        {
            lock (_sync)
            {
                var result = new List<ClipboardRecord>();
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
SELECT id, kind, text_content, image_path, preview_text, created_at, is_pinned, content_hash
FROM clipboard_items
ORDER BY is_pinned DESC, created_at DESC
LIMIT $limit;";
                cmd.Parameters.AddWithValue("$limit", limit);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(new ClipboardRecord
                    {
                        Id = reader.GetString(0),
                        Kind = ParseKind(reader.GetString(1)),
                        TextContent = reader.IsDBNull(2) ? null : reader.GetString(2),
                        ImagePath = reader.IsDBNull(3) ? null : reader.GetString(3),
                        PreviewText = reader.GetString(4),
                        CreatedAt = ParseDate(reader.GetString(5)),
                        IsPinned = reader.GetInt32(6) != 0,
                        ContentHash = reader.GetString(7)
                    });
                }
                return result;
            }
        }

        public void AddOrBumpClipboardItem(ClipboardRecord item, int maxUnpinned = 100)
        {
            lock (_sync)
            {
                using var conn = OpenConnection();
                string? existingId = null;
                string? existingImagePath = null;

                using (var findCmd = conn.CreateCommand())
                {
                    findCmd.CommandText = "SELECT id, image_path FROM clipboard_items WHERE content_hash = $hash LIMIT 1;";
                    findCmd.Parameters.AddWithValue("$hash", item.ContentHash);
                    using var reader = findCmd.ExecuteReader();
                    if (reader.Read())
                    {
                        existingId = reader.GetString(0);
                        existingImagePath = reader.IsDBNull(1) ? null : reader.GetString(1);
                    }
                }

                string nowStr = item.CreatedAt.ToUniversalTime().ToString("O");

                if (existingId != null)
                {
                    // Duplikat: Zeitstempel nach oben ziehen und ggf. überflüssige neue Bilddatei entfernen
                    if (!string.IsNullOrEmpty(item.ImagePath) &&
                        !string.Equals(item.ImagePath, existingImagePath, StringComparison.OrdinalIgnoreCase) &&
                        File.Exists(item.ImagePath))
                    {
                        TryDeleteFile(item.ImagePath);
                    }

                    using var updateCmd = conn.CreateCommand();
                    updateCmd.CommandText = "UPDATE clipboard_items SET created_at = $now WHERE id = $id;";
                    updateCmd.Parameters.AddWithValue("$now", nowStr);
                    updateCmd.Parameters.AddWithValue("$id", existingId);
                    updateCmd.ExecuteNonQuery();
                }
                else
                {
                    using var insertCmd = conn.CreateCommand();
                    insertCmd.CommandText = @"
INSERT INTO clipboard_items (id, kind, text_content, image_path, preview_text, created_at, is_pinned, content_hash)
VALUES ($id, $kind, $text, $img, $preview, $created, $pinned, $hash);";
                    insertCmd.Parameters.AddWithValue("$id", item.Id);
                    insertCmd.Parameters.AddWithValue("$kind", item.Kind.ToString().ToLowerInvariant());
                    insertCmd.Parameters.AddWithValue("$text", (object?)item.TextContent ?? DBNull.Value);
                    insertCmd.Parameters.AddWithValue("$img", (object?)item.ImagePath ?? DBNull.Value);
                    insertCmd.Parameters.AddWithValue("$preview", item.PreviewText);
                    insertCmd.Parameters.AddWithValue("$created", nowStr);
                    insertCmd.Parameters.AddWithValue("$pinned", item.IsPinned ? 1 : 0);
                    insertCmd.Parameters.AddWithValue("$hash", item.ContentHash);
                    insertCmd.ExecuteNonQuery();
                }

                TrimClipboardHistory(conn, maxUnpinned);
            }
        }

        private static void TrimClipboardHistory(SqliteConnection conn, int maxUnpinned)
        {
            var toDeleteImages = new List<string>();
            using (var selCmd = conn.CreateCommand())
            {
                selCmd.CommandText = @"
SELECT image_path FROM clipboard_items
WHERE is_pinned = 0 AND id NOT IN (
    SELECT id FROM clipboard_items WHERE is_pinned = 0 ORDER BY created_at DESC LIMIT $limit
);";
                selCmd.Parameters.AddWithValue("$limit", maxUnpinned);
                using var reader = selCmd.ExecuteReader();
                while (reader.Read())
                {
                    if (!reader.IsDBNull(0))
                        toDeleteImages.Add(reader.GetString(0));
                }
            }

            using (var delCmd = conn.CreateCommand())
            {
                delCmd.CommandText = @"
DELETE FROM clipboard_items
WHERE is_pinned = 0 AND id NOT IN (
    SELECT id FROM clipboard_items WHERE is_pinned = 0 ORDER BY created_at DESC LIMIT $limit
);";
                delCmd.Parameters.AddWithValue("$limit", maxUnpinned);
                delCmd.ExecuteNonQuery();
            }

            foreach (var img in toDeleteImages)
                TryDeleteFile(img);
        }

        public void SetClipboardPinned(string id, bool pinned)
        {
            lock (_sync)
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE clipboard_items SET is_pinned = $pinned WHERE id = $id;";
                cmd.Parameters.AddWithValue("$pinned", pinned ? 1 : 0);
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
        }

        public void DeleteClipboardItem(string id)
        {
            lock (_sync)
            {
                using var conn = OpenConnection();
                string? imgPath = null;
                using (var selCmd = conn.CreateCommand())
                {
                    selCmd.CommandText = "SELECT image_path FROM clipboard_items WHERE id = $id;";
                    selCmd.Parameters.AddWithValue("$id", id);
                    var val = selCmd.ExecuteScalar();
                    if (val is string s) imgPath = s;
                }

                using (var delCmd = conn.CreateCommand())
                {
                    delCmd.CommandText = "DELETE FROM clipboard_items WHERE id = $id;";
                    delCmd.Parameters.AddWithValue("$id", id);
                    delCmd.ExecuteNonQuery();
                }

                if (!string.IsNullOrEmpty(imgPath))
                    TryDeleteFile(imgPath);
            }
        }

        public void ClearClipboard(bool includePinned = false)
        {
            lock (_sync)
            {
                using var conn = OpenConnection();
                var images = new List<string>();
                using (var selCmd = conn.CreateCommand())
                {
                    selCmd.CommandText = includePinned
                        ? "SELECT image_path FROM clipboard_items WHERE image_path IS NOT NULL;"
                        : "SELECT image_path FROM clipboard_items WHERE is_pinned = 0 AND image_path IS NOT NULL;";
                    using var reader = selCmd.ExecuteReader();
                    while (reader.Read())
                        images.Add(reader.GetString(0));
                }

                using (var delCmd = conn.CreateCommand())
                {
                    delCmd.CommandText = includePinned
                        ? "DELETE FROM clipboard_items;"
                        : "DELETE FROM clipboard_items WHERE is_pinned = 0;";
                    delCmd.ExecuteNonQuery();
                }

                foreach (var img in images)
                    TryDeleteFile(img);
            }
        }

        // ==================== Dashboard: Aufgaben & Notizen ====================

        public List<TodoRecord> GetTodos()
        {
            lock (_sync)
            {
                var result = new List<TodoRecord>();
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
SELECT id, title, is_done, created_at
FROM dashboard_todos
ORDER BY is_done ASC, created_at DESC;";

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(new TodoRecord
                    {
                        Id = reader.GetString(0),
                        Title = reader.GetString(1),
                        IsDone = reader.GetInt32(2) != 0,
                        CreatedAt = ParseDate(reader.GetString(3))
                    });
                }
                return result;
            }
        }

        public TodoRecord AddTodo(string title)
        {
            var item = new TodoRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = title.Trim(),
                IsDone = false,
                CreatedAt = DateTime.Now
            };

            lock (_sync)
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
INSERT INTO dashboard_todos (id, title, is_done, created_at)
VALUES ($id, $title, 0, $created);";
                cmd.Parameters.AddWithValue("$id", item.Id);
                cmd.Parameters.AddWithValue("$title", item.Title);
                cmd.Parameters.AddWithValue("$created", item.CreatedAt.ToUniversalTime().ToString("O"));
                cmd.ExecuteNonQuery();
            }

            return item;
        }

        public void SetTodoDone(string id, bool isDone)
        {
            lock (_sync)
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE dashboard_todos SET is_done = $done WHERE id = $id;";
                cmd.Parameters.AddWithValue("$done", isDone ? 1 : 0);
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
        }

        public void DeleteTodo(string id)
        {
            lock (_sync)
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM dashboard_todos WHERE id = $id;";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
        }

        public void ClearCompletedTodos()
        {
            lock (_sync)
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM dashboard_todos WHERE is_done = 1;";
                cmd.ExecuteNonQuery();
            }
        }

        public string GetNote(string id = "default")
        {
            lock (_sync)
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT content FROM dashboard_notes WHERE id = $id LIMIT 1;";
                cmd.Parameters.AddWithValue("$id", id);
                return cmd.ExecuteScalar() as string ?? string.Empty;
            }
        }

        public void SaveNote(string content, string id = "default")
        {
            lock (_sync)
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
INSERT INTO dashboard_notes (id, content, updated_at)
VALUES ($id, $content, $updated)
ON CONFLICT(id) DO UPDATE SET
    content = excluded.content,
    updated_at = excluded.updated_at;";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$content", content ?? string.Empty);
                cmd.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
                cmd.ExecuteNonQuery();
            }
        }

        private static ClipboardItemKind ParseKind(string value) => value switch
        {
            "link" => ClipboardItemKind.Link,
            "image" => ClipboardItemKind.Image,
            _ => ClipboardItemKind.Text
        };

        private static DateTime ParseDate(string iso)
            => DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
                ? dt.ToLocalTime()
                : DateTime.Now;

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Ignorieren, falls Datei gesperrt ist
            }
        }
    }
}
