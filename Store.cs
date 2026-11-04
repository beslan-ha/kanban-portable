using System.Globalization;
using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Kanban;

public sealed class AppException : Exception
{
    public int StatusCode { get; }
    public AppException(int statusCode, string message) : base(message) => StatusCode = statusCode;
}

public sealed class BoardDto
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public int Position { get; set; }
}

public sealed class ColumnDto
{
    public long Id { get; set; }
    public long BoardId { get; set; }
    public string Name { get; set; } = "";
    public int Position { get; set; }
    public int? WipLimit { get; set; }
}

public sealed class ChecklistItemDto
{
    public long Id { get; set; }
    public string Text { get; set; } = "";
    public bool Done { get; set; }
    public int Position { get; set; }
}

public sealed class AttachmentDto
{
    public long Id { get; set; }
    public long CardId { get; set; }
    /// <summary>"link" или "file".</summary>
    public string Kind { get; set; } = "link";
    public string Title { get; set; } = "";
    public string? Url { get; set; }
    public string? OriginalName { get; set; }
    /// <summary>Имя файла внутри папки files (только для kind = "file").</summary>
    public string? FileName { get; set; }
    public long Size { get; set; }
    public string CreatedAt { get; set; } = "";
    public string? DownloadUrl { get; set; }
}

public sealed class CardDto
{
    public long Id { get; set; }
    public long BoardId { get; set; }
    public long ColumnId { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public int Priority { get; set; } = 1;
    public string? DueDate { get; set; }
    public string Tags { get; set; } = "";
    public int Position { get; set; }
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public string? ArchivedAt { get; set; }
    public List<ChecklistItemDto> Checklist { get; set; } = new();
    public List<AttachmentDto> Attachments { get; set; } = new();
}

public sealed class StateDto
{
    public List<BoardDto> Boards { get; set; } = new();
    public List<ColumnDto> Columns { get; set; } = new();
    public List<CardDto> Cards { get; set; } = new();
}

public sealed class Store
{
    private readonly string _connectionString;

    /// <summary>Сколько последних резервных копий хранить (задаётся из kanban.config.json).</summary>
    public int BackupsToKeep { get; set; } = 30;

    public string DataDir { get; }
    public string DbPath { get; }
    public string BackupDir { get; }
    public string FilesDir { get; }

    public Store(string dataDir)
    {
        DataDir = dataDir;
        DbPath = Path.Combine(dataDir, "kanban.db");
        BackupDir = Path.Combine(dataDir, "backups");
        FilesDir = Path.Combine(dataDir, "files");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            ForeignKeys = true,
        }.ToString();
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var p = c.CreateCommand();
        // synchronous=FULL + journal_mode=DELETE: медленнее WAL, но безопаснее на флешке.
        p.CommandText = "PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;";
        p.ExecuteNonQuery();
        return c;
    }

    public void Init()
    {
        Directory.CreateDirectory(BackupDir);
        Directory.CreateDirectory(FilesDir);
        using (var c = Open())
        {
            Schema.Ensure(c);
        }
        // Подчищаем файлы, оставшиеся от неудачных импортов/удалений.
        CleanupOrphanFiles();
    }

    public void Checkpoint()
    {
        try
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA optimize;";
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    // ---------- чтение ----------

    public StateDto GetState()
    {
        var state = new StateDto();
        using var c = Open();

        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT id, name, position FROM boards ORDER BY position, id;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                state.Boards.Add(new BoardDto { Id = r.GetInt64(0), Name = r.GetString(1), Position = r.GetInt32(2) });
        }

        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT id, board_id, name, position, wip_limit FROM board_columns ORDER BY position, id;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                state.Columns.Add(new ColumnDto
                {
                    Id = r.GetInt64(0),
                    BoardId = r.GetInt64(1),
                    Name = r.GetString(2),
                    Position = r.GetInt32(3),
                    WipLimit = r.IsDBNull(4) ? null : r.GetInt32(4),
                });
            }
        }

        var byId = new Dictionary<long, CardDto>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                SELECT id, board_id, column_id, title, description, priority, due_date, tags, position,
                       created_at, updated_at, archived_at
                FROM cards ORDER BY position, id;
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var card = new CardDto
                {
                    Id = r.GetInt64(0),
                    BoardId = r.GetInt64(1),
                    ColumnId = r.GetInt64(2),
                    Title = r.GetString(3),
                    Description = r.GetString(4),
                    Priority = r.GetInt32(5),
                    DueDate = r.IsDBNull(6) ? null : r.GetString(6),
                    Tags = r.GetString(7),
                    Position = r.GetInt32(8),
                    CreatedAt = r.GetString(9),
                    UpdatedAt = r.GetString(10),
                    ArchivedAt = r.IsDBNull(11) ? null : r.GetString(11),
                };
                state.Cards.Add(card);
                byId[card.Id] = card;
            }
        }

        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT id, card_id, text, done, position FROM card_checklist ORDER BY position, id;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (!byId.TryGetValue(r.GetInt64(1), out var card)) continue;
                card.Checklist.Add(new ChecklistItemDto
                {
                    Id = r.GetInt64(0),
                    Text = r.GetString(2),
                    Done = r.GetInt64(3) != 0,
                    Position = r.GetInt32(4),
                });
            }
        }

        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                SELECT id, card_id, kind, title, url, original_name, file_name, size, created_at
                FROM attachments ORDER BY id;
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (!byId.TryGetValue(r.GetInt64(1), out var card)) continue;
                string kind = r.GetString(2);
                long attId = r.GetInt64(0);
                card.Attachments.Add(new AttachmentDto
                {
                    Id = attId,
                    CardId = card.Id,
                    Kind = kind,
                    Title = r.GetString(3),
                    Url = r.IsDBNull(4) ? null : r.GetString(4),
                    OriginalName = r.IsDBNull(5) ? null : r.GetString(5),
                    FileName = r.IsDBNull(6) ? null : r.GetString(6),
                    Size = r.IsDBNull(7) ? 0 : r.GetInt64(7),
                    CreatedAt = r.GetString(8),
                    DownloadUrl = kind == "file" ? $"/api/attachments/{attId}/download" : null,
                });
            }
        }

        return state;
    }

    // ---------- доски ----------

    public BoardDto CreateBoard(string name)
    {
        name = Clean(name, 120, "Новая доска");
        using var c = Open();
        using var tx = c.BeginTransaction();

        int pos = ScalarInt(c, tx, "SELECT COALESCE(MAX(position), -1) + 1 FROM boards;");
        long id = Insert(c, tx, "INSERT INTO boards(name, position, created_at) VALUES ($n, $p, $t);",
            ("$n", name), ("$p", pos), ("$t", Now()));

        // Новая доска сразу получает три колонки — иначе на ней нечего делать.
        string[] defaults = { "Бэклог", "В работе", "Готово" };
        for (int i = 0; i < defaults.Length; i++)
        {
            Insert(c, tx, "INSERT INTO board_columns(board_id, name, position, wip_limit) VALUES ($b, $n, $p, NULL);",
                ("$b", id), ("$n", defaults[i]), ("$p", i));
        }

        tx.Commit();
        return new BoardDto { Id = id, Name = name, Position = pos };
    }

    public void UpdateBoard(long id, string? name, int? position)
    {
        using var c = Open();
        if (!string.IsNullOrWhiteSpace(name))
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE boards SET name = $n WHERE id = $id;";
            cmd.Parameters.AddWithValue("$n", Clean(name, 120, "Доска"));
            cmd.Parameters.AddWithValue("$id", id);
            if (cmd.ExecuteNonQuery() == 0) throw new AppException(404, "Доска не найдена");
        }

        if (position is int p)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE boards SET position = $p WHERE id = $id;";
            cmd.Parameters.AddWithValue("$p", p);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public void DeleteBoard(long id)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();

        int count = ScalarInt(c, tx, "SELECT COUNT(*) FROM boards;");
        if (count <= 1) throw new AppException(400, "Нельзя удалить единственную доску");

        var files = CollectFiles(c, tx, "SELECT a.file_name FROM attachments a JOIN cards k ON k.id = a.card_id WHERE k.board_id = $x AND a.file_name IS NOT NULL;", id);

        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM boards WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            if (cmd.ExecuteNonQuery() == 0) throw new AppException(404, "Доска не найдена");
        }

        NormalizeBoardPositions(c, tx);
        tx.Commit();
        DeleteFiles(files);
    }

    // ---------- колонки ----------

    public ColumnDto CreateColumn(long boardId, string? name)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();

        if (ScalarLong(c, tx, "SELECT COUNT(*) FROM boards WHERE id = $id;", ("$id", boardId)) == 0)
            throw new AppException(404, "Доска не найдена");

        int pos = ScalarInt(c, tx, "SELECT COALESCE(MAX(position), -1) + 1 FROM board_columns WHERE board_id = $b;", ("$b", boardId));
        string clean = Clean(name, 80, "Колонка");
        long id = Insert(c, tx, "INSERT INTO board_columns(board_id, name, position, wip_limit) VALUES ($b, $n, $p, NULL);",
            ("$b", boardId), ("$n", clean), ("$p", pos));

        tx.Commit();
        return new ColumnDto { Id = id, BoardId = boardId, Name = clean, Position = pos };
    }

    public void UpdateColumn(long id, string? name, int? position, int? wipLimit, bool clearWipLimit)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();

        if (ScalarLong(c, tx, "SELECT COUNT(*) FROM board_columns WHERE id = $id;", ("$id", id)) == 0)
            throw new AppException(404, "Колонка не найдена");

        if (!string.IsNullOrWhiteSpace(name))
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE board_columns SET name = $n WHERE id = $id;";
            cmd.Parameters.AddWithValue("$n", Clean(name, 80, "Колонка"));
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }

        if (clearWipLimit || wipLimit is not null)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE board_columns SET wip_limit = $w WHERE id = $id;";
            cmd.Parameters.AddWithValue("$w", clearWipLimit || wipLimit is null or <= 0 ? (object)DBNull.Value : (object)wipLimit.Value);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }

        if (position is int p) MoveColumn(c, tx, id, p);

        tx.Commit();
    }

    public void DeleteColumn(long id)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();

        long boardId = ScalarLong(c, tx, "SELECT board_id FROM board_columns WHERE id = $id;", ("$id", id));
        int count = ScalarInt(c, tx, "SELECT COUNT(*) FROM board_columns WHERE board_id = $b;", ("$b", boardId));
        if (count <= 1) throw new AppException(400, "Нельзя удалить последнюю колонку доски");

        var files = CollectFiles(c, tx, "SELECT a.file_name FROM attachments a JOIN cards k ON k.id = a.card_id WHERE k.column_id = $x AND a.file_name IS NOT NULL;", id);

        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM board_columns WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }

        NormalizeColumnPositions(c, tx, boardId);
        tx.Commit();
        DeleteFiles(files);
    }

    public void ReorderColumns(long boardId, long[] ids)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        ApplyColumnOrder(c, tx, boardId, ids.ToList());
        tx.Commit();
    }

    // ---------- карточки ----------

    public CardDto CreateCard(long columnId, string? title, string? description, int? priority, string? dueDate, string? tags)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();

        long boardId = ScalarLong(c, tx, "SELECT board_id FROM board_columns WHERE id = $id;", ("$id", columnId));
        int pos = ScalarInt(c, tx, "SELECT COALESCE(MAX(position), -1) + 1 FROM cards WHERE column_id = $c;", ("$c", columnId));
        string now = Now();
        string t = Clean(title, 300, "Новая карточка");

        long id = Insert(c, tx, """
            INSERT INTO cards(board_id, column_id, title, description, priority, due_date, tags, position, created_at, updated_at, archived_at)
            VALUES ($b, $c, $t, $d, $pr, $due, $tags, $pos, $now, $now, NULL);
            """,
            ("$b", boardId), ("$c", columnId), ("$t", t),
            ("$d", description ?? ""), ("$pr", Math.Clamp(priority ?? 1, 0, 3)),
            ("$due", (object?)NormalizeDate(dueDate) ?? DBNull.Value),
            ("$tags", NormalizeTags(tags)), ("$pos", pos), ("$now", now));

        tx.Commit();
        return new CardDto
        {
            Id = id, BoardId = boardId, ColumnId = columnId, Title = t,
            Description = description ?? "", Priority = Math.Clamp(priority ?? 1, 0, 3),
            DueDate = NormalizeDate(dueDate), Tags = NormalizeTags(tags), Position = pos,
            CreatedAt = now, UpdatedAt = now,
        };
    }

    public void UpdateCard(long id, string? title, string? description, int? priority,
        string? dueDate, bool clearDueDate, string? tags)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();

        if (ScalarLong(c, tx, "SELECT COUNT(*) FROM cards WHERE id = $id;", ("$id", id)) == 0)
            throw new AppException(404, "Карточка не найдена");

        var sets = new List<string> { "updated_at = $now" };

        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.Parameters.AddWithValue("$now", Now());
        cmd.Parameters.AddWithValue("$id", id);

        if (title is not null) { sets.Add("title = $title"); cmd.Parameters.AddWithValue("$title", Clean(title, 300, "Без названия")); }
        if (description is not null) { sets.Add("description = $desc"); cmd.Parameters.AddWithValue("$desc", description); }
        if (priority is int pr) { sets.Add("priority = $pr"); cmd.Parameters.AddWithValue("$pr", Math.Clamp(pr, 0, 3)); }
        if (tags is not null) { sets.Add("tags = $tags"); cmd.Parameters.AddWithValue("$tags", NormalizeTags(tags)); }
        if (clearDueDate) { sets.Add("due_date = NULL"); }
        else if (dueDate is not null)
        {
            string? norm = NormalizeDate(dueDate);
            if (norm is null) sets.Add("due_date = NULL");
            else { sets.Add("due_date = $due"); cmd.Parameters.AddWithValue("$due", norm); }
        }

        cmd.CommandText = $"UPDATE cards SET {string.Join(", ", sets)} WHERE id = $id;";
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    public void SetArchived(long id, bool archived)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE cards SET archived_at = $a, updated_at = $now WHERE id = $id;";
        cmd.Parameters.AddWithValue("$a", archived ? (object)Now() : DBNull.Value);
        cmd.Parameters.AddWithValue("$now", Now());
        cmd.Parameters.AddWithValue("$id", id);
        if (cmd.ExecuteNonQuery() == 0) throw new AppException(404, "Карточка не найдена");
    }

    public void DeleteCard(long id)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();

        long columnId = ScalarLong(c, tx, "SELECT column_id FROM cards WHERE id = $id;", ("$id", id));
        var files = CollectFiles(c, tx, "SELECT file_name FROM attachments WHERE card_id = $x AND file_name IS NOT NULL;", id);

        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM cards WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            if (cmd.ExecuteNonQuery() == 0) throw new AppException(404, "Карточка не найдена");
        }

        NormalizeCardPositions(c, tx, columnId);
        tx.Commit();
        DeleteFiles(files);
    }

    public void MoveCard(long cardId, long targetColumnId, int index)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();

        long sourceColumnId, boardId;
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT column_id, board_id FROM cards WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", cardId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) throw new AppException(404, "Карточка не найдена");
            sourceColumnId = r.GetInt64(0);
            boardId = r.GetInt64(1);
        }

        long targetBoard = ScalarLong(c, tx, "SELECT board_id FROM board_columns WHERE id = $id;", ("$id", targetColumnId));
        if (targetBoard != boardId) throw new AppException(400, "Колонка принадлежит другой доске");

        if (sourceColumnId == targetColumnId)
        {
            var ids = OrderedCardIds(c, tx, sourceColumnId, cardId);
            index = Math.Clamp(index, 0, ids.Count);
            ids.Insert(index, cardId);
            ApplyCardOrder(c, tx, sourceColumnId, ids);
        }
        else
        {
            var sourceIds = OrderedCardIds(c, tx, sourceColumnId, cardId);
            var targetIds = OrderedCardIds(c, tx, targetColumnId, cardId);
            index = Math.Clamp(index, 0, targetIds.Count);

            using (var cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE cards SET column_id = $col, updated_at = $now WHERE id = $id;";
                cmd.Parameters.AddWithValue("$col", targetColumnId);
                cmd.Parameters.AddWithValue("$now", Now());
                cmd.Parameters.AddWithValue("$id", cardId);
                cmd.ExecuteNonQuery();
            }

            targetIds.Insert(index, cardId);
            ApplyCardOrder(c, tx, sourceColumnId, sourceIds);
            ApplyCardOrder(c, tx, targetColumnId, targetIds);
        }

        tx.Commit();
    }

    // ---------- чек-лист ----------

    public void ReplaceChecklist(long cardId, IEnumerable<ChecklistItemDto> items)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();

        if (ScalarLong(c, tx, "SELECT COUNT(*) FROM cards WHERE id = $id;", ("$id", cardId)) == 0)
            throw new AppException(404, "Карточка не найдена");

        using (var del = c.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM card_checklist WHERE card_id = $c;";
            del.Parameters.AddWithValue("$c", cardId);
            del.ExecuteNonQuery();
        }

        int position = 0;
        foreach (var item in items.Take(200))
        {
            string text = Clean(item.Text, 300, "");
            if (text.Length == 0) continue;
            Insert(c, tx, "INSERT INTO card_checklist(card_id, text, done, position) VALUES ($c, $t, $d, $p);",
                ("$c", cardId), ("$t", text), ("$d", item.Done ? 1 : 0), ("$p", position++));
        }

        Touch(c, tx, cardId);
        tx.Commit();
    }

    // ---------- вложения ----------

    public AttachmentDto AddLink(long cardId, string? title, string? url)
    {
        string link = (url ?? "").Trim();
        if (link.Length == 0) throw new AppException(400, "Пустая ссылка");
        if (link.Length > 2000) link = link[..2000];
        if (!link.Contains("://", StringComparison.Ordinal) && !link.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            link = "https://" + link;

        using var c = Open();
        using var tx = c.BeginTransaction();
        if (ScalarLong(c, tx, "SELECT COUNT(*) FROM cards WHERE id = $id;", ("$id", cardId)) == 0)
            throw new AppException(404, "Карточка не найдена");

        string now = Now();
        string text = Clean(title, 200, HostOf(link));
        long id = Insert(c, tx, """
            INSERT INTO attachments(card_id, kind, title, url, file_name, original_name, size, created_at)
            VALUES ($c, 'link', $t, $u, NULL, NULL, 0, $n);
            """,
            ("$c", cardId), ("$t", text), ("$u", link), ("$n", now));

        Touch(c, tx, cardId);
        tx.Commit();

        return new AttachmentDto { Id = id, CardId = cardId, Kind = "link", Title = text, Url = link, CreatedAt = now };
    }

    public AttachmentDto AddFile(long cardId, string? originalName, byte[] bytes)
    {
        if (bytes.Length == 0) throw new AppException(400, "Пустой файл");

        string name = Path.GetFileName((originalName ?? "").Replace('\\', '/')).Trim();
        if (name.Length == 0) name = "файл";
        if (name.Length > 200) name = name[..200];

        string ext = Path.GetExtension(name);
        if (ext.Length > 12) ext = "";
        string stored = Guid.NewGuid().ToString("N") + ext;

        Directory.CreateDirectory(FilesDir);
        string path = Path.Combine(FilesDir, stored);

        using var c = Open();
        using var tx = c.BeginTransaction();
        if (ScalarLong(c, tx, "SELECT COUNT(*) FROM cards WHERE id = $id;", ("$id", cardId)) == 0)
            throw new AppException(404, "Карточка не найдена");

        File.WriteAllBytes(path, bytes);
        try
        {
            string now = Now();
            long id = Insert(c, tx, """
                INSERT INTO attachments(card_id, kind, title, url, file_name, original_name, size, created_at)
                VALUES ($c, 'file', $t, NULL, $f, $o, $s, $n);
                """,
                ("$c", cardId), ("$t", name), ("$f", stored), ("$o", name), ("$s", bytes.Length), ("$n", now));

            Touch(c, tx, cardId);
            tx.Commit();

            return new AttachmentDto
            {
                Id = id, CardId = cardId, Kind = "file", Title = name, OriginalName = name,
                Size = bytes.Length, CreatedAt = now, DownloadUrl = $"/api/attachments/{id}/download",
            };
        }
        catch
        {
            try { File.Delete(path); } catch { }
            throw;
        }
    }

    public (AttachmentDto Dto, string? StoredName) GetAttachment(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id, card_id, kind, title, url, file_name, original_name, size, created_at
            FROM attachments WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new AppException(404, "Вложение не найдено");

        string kind = r.GetString(2);
        string? stored = r.IsDBNull(5) ? null : r.GetString(5);
        return (new AttachmentDto
        {
            Id = r.GetInt64(0),
            CardId = r.GetInt64(1),
            Kind = kind,
            Title = r.GetString(3),
            Url = r.IsDBNull(4) ? null : r.GetString(4),
            OriginalName = r.IsDBNull(6) ? null : r.GetString(6),
            FileName = stored,
            Size = r.IsDBNull(7) ? 0 : r.GetInt64(7),
            CreatedAt = r.GetString(8),
            DownloadUrl = kind == "file" ? $"/api/attachments/{id}/download" : null,
        }, stored);
    }

    public void DeleteAttachment(long id)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();

        long cardId;
        string? stored = null;
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT card_id, file_name FROM attachments WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) throw new AppException(404, "Вложение не найдено");
            cardId = r.GetInt64(0);
            stored = r.IsDBNull(1) ? null : r.GetString(1);
        }

        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM attachments WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }

        Touch(c, tx, cardId);
        tx.Commit();

        if (stored is not null) DeleteFiles(new[] { stored });
    }

    // ---------- резервные копии и перенос ----------

    public string Backup()
    {
        Directory.CreateDirectory(BackupDir);
        string name = $"kanban-{DateTime.Now:yyyyMMdd-HHmmss}.db";
        string path = Path.Combine(BackupDir, name);
        if (File.Exists(path)) path = Path.Combine(BackupDir, $"kanban-{DateTime.Now:yyyyMMdd-HHmmss-fff}.db");

        using (var src = Open())
        using (var dst = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false,
        }.ToString()))
        {
            dst.Open();
            src.BackupDatabase(dst);
        }

        PruneBackups(BackupsToKeep);
        return Path.GetFileName(path);
    }

    public void BackupIfOlderThan(TimeSpan age)
    {
        Directory.CreateDirectory(BackupDir);
        var files = new DirectoryInfo(BackupDir).GetFiles("kanban-*.db");
        var newest = files.OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        if (newest is null || DateTime.UtcNow - newest.LastWriteTimeUtc >= age) Backup();
        else PruneBackups(BackupsToKeep);
    }

    public void PruneBackups(int keep)
    {
        try
        {
            var files = new DirectoryInfo(BackupDir).GetFiles("kanban-*.db")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(keep)
                .ToList();
            foreach (var f in files) f.Delete();
        }
        catch (Exception ex) { AppLog.Error("Очистка старых копий", ex); }
    }

    public string ExportJson()
    {
        var state = GetState();
        // JSON-экспорт не содержит двоичных файлов — для полного переноса есть .zip
        foreach (var card in state.Cards)
            card.Attachments.RemoveAll(a => a.Kind == "file");

        return System.Text.Json.JsonSerializer.Serialize(state, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    public byte[] ExportZip()
    {
        var state = GetState();

        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(state, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var entry = zip.CreateEntry("data.json", CompressionLevel.Optimal).Open())
                entry.Write(json, 0, json.Length);

            var storedNames = new List<string>();
            using (var c = Open())
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT file_name FROM attachments WHERE kind = 'file' AND file_name IS NOT NULL;";
                using var r = cmd.ExecuteReader();
                while (r.Read()) storedNames.Add(r.GetString(0));
            }

            foreach (string name in storedNames)
            {
                string path = Path.Combine(FilesDir, name);
                if (!File.Exists(path)) continue;
                zip.CreateEntryFromFile(path, "files/" + name, CompressionLevel.Optimal);
            }
        }

        return ms.ToArray();
    }

    public string ExportCsv()
    {
        var state = GetState();
        var boards = state.Boards.ToDictionary(b => b.Id, b => b.Name);
        var columns = state.Columns.ToDictionary(c => c.Id, c => c.Name);

        var sb = new StringBuilder();
        sb.Append("Доска;Колонка;Заголовок;Описание;Приоритет;Срок;Теги;Чек-лист;Вложения;Создана;Изменена;В архиве\r\n");

        foreach (var card in state.Cards.OrderBy(c => c.BoardId).ThenBy(c => c.ColumnId).ThenBy(c => c.Position))
        {
            int total = card.Checklist.Count;
            int done = card.Checklist.Count(i => i.Done);
            string due = card.DueDate is null ? "" : FormatDate(card.DueDate);

            sb.Append(Csv(boards.GetValueOrDefault(card.BoardId, ""))).Append(';')
              .Append(Csv(columns.GetValueOrDefault(card.ColumnId, ""))).Append(';')
              .Append(Csv(card.Title)).Append(';')
              .Append(Csv(card.Description)).Append(';')
              .Append(Csv(PriorityName(card.Priority))).Append(';')
              .Append(Csv(due)).Append(';')
              .Append(Csv(card.Tags)).Append(';')
              .Append(Csv(total > 0 ? $"{done}/{total}" : "")).Append(';')
              .Append(Csv(card.Attachments.Count.ToString(CultureInfo.InvariantCulture))).Append(';')
              .Append(Csv(FormatDateTime(card.CreatedAt))).Append(';')
              .Append(Csv(FormatDateTime(card.UpdatedAt))).Append(';')
              .Append(Csv(card.ArchivedAt is null ? "" : "да"))
              .Append("\r\n");
        }

        // BOM нужен, чтобы Excel открыл файл в UTF-8 и не сломал кириллицу.
        return "\uFEFF" + sb;
    }

    public void ImportJson(StateDto state, bool replaceFiles = false, string? sourceFilesDir = null,
        bool preserveFileNames = false)
    {
        state.Boards ??= new();
        state.Columns ??= new();
        state.Cards ??= new();

        if (state.Boards.Count == 0) throw new AppException(400, "В файле нет ни одной доски");

        foreach (var card in state.Cards)
        {
            card.Checklist ??= new();
            card.Attachments ??= new();
        }

        using var c = Open();
        using var tx = c.BeginTransaction();

        Exec(c, tx, "DELETE FROM attachments;");
        Exec(c, tx, "DELETE FROM card_checklist;");
        Exec(c, tx, "DELETE FROM cards;");
        Exec(c, tx, "DELETE FROM board_columns;");
        Exec(c, tx, "DELETE FROM boards;");

        string now = Now();
        foreach (var b in state.Boards)
        {
            Insert(c, tx, "INSERT INTO boards(id, name, position, created_at) VALUES ($id, $n, $p, $t);",
                ("$id", b.Id), ("$n", Clean(b.Name, 120, "Доска")), ("$p", b.Position), ("$t", now));
        }

        foreach (var col in state.Columns)
        {
            if (state.Boards.All(b => b.Id != col.BoardId)) continue;
            Insert(c, tx, "INSERT INTO board_columns(id, board_id, name, position, wip_limit) VALUES ($id, $b, $n, $p, $w);",
                ("$id", col.Id), ("$b", col.BoardId), ("$n", Clean(col.Name, 80, "Колонка")), ("$p", col.Position),
                ("$w", (object?)col.WipLimit ?? DBNull.Value));
        }

        foreach (var card in state.Cards)
        {
            var col = state.Columns.FirstOrDefault(x => x.Id == card.ColumnId);
            if (col is null) continue;
            Insert(c, tx, """
                INSERT INTO cards(id, board_id, column_id, title, description, priority, due_date, tags, position, created_at, updated_at, archived_at)
                VALUES ($id, $b, $c, $t, $d, $pr, $due, $tags, $pos, $cr, $up, $ar);
                """,
                ("$id", card.Id), ("$b", col.BoardId), ("$c", card.ColumnId),
                ("$t", Clean(card.Title, 300, "Без названия")), ("$d", card.Description ?? ""),
                ("$pr", Math.Clamp(card.Priority, 0, 3)),
                ("$due", (object?)NormalizeDate(card.DueDate) ?? DBNull.Value),
                ("$tags", NormalizeTags(card.Tags)), ("$pos", card.Position),
                ("$cr", string.IsNullOrWhiteSpace(card.CreatedAt) ? now : card.CreatedAt),
                ("$up", string.IsNullOrWhiteSpace(card.UpdatedAt) ? now : card.UpdatedAt),
                ("$ar", (object?)card.ArchivedAt ?? DBNull.Value));

            int position = 0;
            foreach (var item in card.Checklist.Take(200))
            {
                string text = Clean(item.Text, 300, "");
                if (text.Length == 0) continue;
                Insert(c, tx, "INSERT INTO card_checklist(card_id, text, done, position) VALUES ($c, $t, $d, $p);",
                    ("$c", card.Id), ("$t", text), ("$d", item.Done ? 1 : 0), ("$p", position++));
            }
        }

        // Вложения импортируем после карточек: ссылки всегда, файлы — только если они есть в источнике.
        foreach (var card in state.Cards)
        {
            foreach (var att in card.Attachments)
            {
                string? stored = null;
                long size = 0;
                if (att.Kind == "file")
                {
                    string? source = null;
                    if (!string.IsNullOrEmpty(sourceFilesDir) && !string.IsNullOrEmpty(att.FileName))
                    {
                        string candidate = Path.Combine(sourceFilesDir, Path.GetFileName(att.FileName));
                        if (File.Exists(candidate)) source = candidate;
                    }
                    // Файла нет в архиве — вложение пропускаем, чтобы не оставлять «битые» ссылки.
                    if (source is null) continue;

                    if (preserveFileNames && IsSafeStoredName(att.FileName))
                    {
                        // Синхронизация: имя файла на сервере и локально должно совпадать,
                        // иначе каждая синхронизация заново качала бы все вложения.
                        stored = Path.GetFileName(att.FileName!);
                        string target = Path.Combine(FilesDir, stored);
                        Directory.CreateDirectory(FilesDir);
                        if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                            File.Copy(source, target, true);
                        size = new FileInfo(target).Length;
                    }
                    else
                    {
                        string ext = Path.GetExtension(source);
                        if (ext.Length > 12) ext = "";
                        stored = Guid.NewGuid().ToString("N") + ext;
                        Directory.CreateDirectory(FilesDir);
                        string target = Path.Combine(FilesDir, stored);
                        File.Copy(source, target, true);
                        size = new FileInfo(target).Length;
                    }
                }

                Insert(c, tx, """
                    INSERT INTO attachments(id, card_id, kind, title, url, file_name, original_name, size, created_at)
                    VALUES ($id, $c, $k, $t, $u, $f, $o, $s, $n);
                    """,
                    ("$id", att.Id), ("$c", card.Id), ("$k", att.Kind == "file" ? "file" : "link"),
                    ("$t", Clean(att.Title, 200, "Вложение")),
                    ("$u", (object?)att.Url ?? DBNull.Value),
                    ("$f", (object?)stored ?? DBNull.Value),
                    ("$o", (object?)att.OriginalName ?? DBNull.Value),
                    ("$s", size),
                    ("$n", string.IsNullOrWhiteSpace(att.CreatedAt) ? now : att.CreatedAt));
            }
        }

        // Сбрасываем AUTOINCREMENT, чтобы новые записи не конфликтовали с импортированными id.
        Exec(c, tx, "DELETE FROM sqlite_sequence WHERE name IN ('boards','board_columns','cards','card_checklist','attachments');");
        Exec(c, tx, "INSERT INTO sqlite_sequence(name, seq) SELECT 'boards', COALESCE(MAX(id), 0) FROM boards;");
        Exec(c, tx, "INSERT INTO sqlite_sequence(name, seq) SELECT 'board_columns', COALESCE(MAX(id), 0) FROM board_columns;");
        Exec(c, tx, "INSERT INTO sqlite_sequence(name, seq) SELECT 'cards', COALESCE(MAX(id), 0) FROM cards;");
        Exec(c, tx, "INSERT INTO sqlite_sequence(name, seq) SELECT 'card_checklist', COALESCE(MAX(id), 0) FROM card_checklist;");
        Exec(c, tx, "INSERT INTO sqlite_sequence(name, seq) SELECT 'attachments', COALESCE(MAX(id), 0) FROM attachments;");

        tx.Commit();

        // Старые файлы не удалялись заранее: если импорт сорвался бы, вложения текущей базы
        // остались бы рабочими. Теперь можно убрать файлы, на которые никто не ссылается.
        if (replaceFiles) CleanupOrphanFiles();
    }

    /// <summary>Удаляет из папки files файлы, на которые не ссылается база (например, после импорта).</summary>
    public void CleanupOrphanFiles()
    {
        try
        {
            if (!Directory.Exists(FilesDir)) return;

            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var c = Open())
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT file_name FROM attachments WHERE file_name IS NOT NULL;";
                using var r = cmd.ExecuteReader();
                while (r.Read()) referenced.Add(r.GetString(0));
            }

            foreach (string path in Directory.GetFiles(FilesDir))
            {
                if (referenced.Contains(Path.GetFileName(path))) continue;
                // Свежие файлы не трогаем: их мог только что записать параллельный запрос загрузки.
                if (File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddHours(-1)) continue;
                try { File.Delete(path); }
                catch (Exception ex) { AppLog.Error("Удаление неиспользуемого файла", ex); }
            }
        }
        catch (Exception ex) { AppLog.Error("Очистка папки files", ex); }
    }

    /// <summary>
    /// Распаковывает архив переноса (data.json + files/) во временную папку.
    /// Файлы извлекаются в отдельную папку, чтобы импорт мог сначала очистить рабочую files.
    /// </summary>
    public static (string DataPath, string FilesDir) UnpackZip(byte[] zipBytes, string tempRoot)
    {
        Directory.CreateDirectory(tempRoot);
        string tempZip = Path.Combine(tempRoot, "import.zip");
        File.WriteAllBytes(tempZip, zipBytes);

        string filesDir = Path.Combine(tempRoot, "files");
        Directory.CreateDirectory(filesDir);

        using (var zip = ZipFile.OpenRead(tempZip))
        {
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;

                if (entry.FullName == "data.json")
                {
                    entry.ExtractToFile(Path.Combine(tempRoot, "data.json"), true);
                    continue;
                }

                string normalized = entry.FullName.Replace('\\', '/');
                if (!normalized.StartsWith("files/", StringComparison.OrdinalIgnoreCase)) continue;

                string name = Path.GetFileName(normalized);
                if (name.Length == 0) continue;
                entry.ExtractToFile(Path.Combine(filesDir, name), true);
            }
        }

        string dataPath = Path.Combine(tempRoot, "data.json");
        if (!File.Exists(dataPath)) throw new AppException(400, "В архиве нет data.json");
        return (dataPath, filesDir);
    }

    // ---------- служебное ----------

    /// <summary>Имя файла, которое безопасно использовать внутри папки files.</summary>
    private static bool IsSafeStoredName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 120) return false;
        if (name is "." or "..") return false;
        if (name.Contains('/') || name.Contains('\\') || name.Contains(':')) return false;
        return name.All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-');
    }

    private static string Now() => DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private static string Clean(string? value, int max, string fallback)
    {
        string s = (value ?? "").Trim();
        if (s.Length == 0) return fallback;
        return s.Length <= max ? s : s[..max];
    }

    private static string? NormalizeDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : null;
    }

    private static string NormalizeTags(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags)) return "";
        var parts = tags.Split(new[] { ',', ';', '#' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.Length > 0)
            .Select(p => p.Length > 40 ? p[..40] : p)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20);
        return string.Join(",", parts);
    }

    private static string HostOf(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "Ссылка";
    }

    private static string PriorityName(int priority) => priority switch
    {
        0 => "Низкий",
        2 => "Высокий",
        3 => "Срочный",
        _ => "Обычный",
    };

    private static string FormatDate(string iso) =>
        DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)
            : iso;

    private static string FormatDateTime(string iso) =>
        DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d)
            ? d.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)
            : iso;

    private static string Csv(string value)
    {
        value ??= "";
        bool needsQuotes = value.Contains(';') || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
        string escaped = value.Replace("\"", "\"\"");
        return needsQuotes ? $"\"{escaped}\"" : escaped;
    }

    private static void Exec(SqliteConnection c, SqliteTransaction tx, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Insert(SqliteConnection c, SqliteTransaction tx, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql + " SELECT last_insert_rowid();";
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }

    private static long ScalarLong(SqliteConnection c, SqliteTransaction? tx, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = c.CreateCommand();
        if (tx is not null) cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        object? result = cmd.ExecuteScalar();
        if (result is null || result is DBNull) throw new AppException(404, "Запись не найдена");
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static int ScalarInt(SqliteConnection c, SqliteTransaction? tx, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = c.CreateCommand();
        if (tx is not null) cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        object? result = cmd.ExecuteScalar();
        return result is null or DBNull ? 0 : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static void Touch(SqliteConnection c, SqliteTransaction tx, long cardId)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE cards SET updated_at = $n WHERE id = $id;";
        cmd.Parameters.AddWithValue("$n", Now());
        cmd.Parameters.AddWithValue("$id", cardId);
        cmd.ExecuteNonQuery();
    }

    private List<string> CollectFiles(SqliteConnection c, SqliteTransaction tx, string sql, long id)
    {
        var files = new List<string>();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$x", id);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (!r.IsDBNull(0)) files.Add(r.GetString(0));
        return files;
    }

    private void DeleteFiles(IEnumerable<string> names)
    {
        foreach (string name in names)
        {
            try
            {
                string path = Path.Combine(FilesDir, Path.GetFileName(name));
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) { AppLog.Error("Удаление файла вложения", ex); }
        }
    }

    private static List<long> OrderedCardIds(SqliteConnection c, SqliteTransaction tx, long columnId, long? exclude)
    {
        var ids = new List<long>();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT id FROM cards WHERE column_id = $c AND ($x IS NULL OR id <> $x) ORDER BY position, id;";
        cmd.Parameters.AddWithValue("$c", columnId);
        cmd.Parameters.AddWithValue("$x", (object?)exclude ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        while (r.Read()) ids.Add(r.GetInt64(0));
        return ids;
    }

    private static void ApplyCardOrder(SqliteConnection c, SqliteTransaction tx, long columnId, List<long> ids)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE cards SET position = $p WHERE id = $id AND column_id = $c;";
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        var pPos = cmd.Parameters.Add("$p", SqliteType.Integer);
        var pCol = cmd.Parameters.Add("$c", SqliteType.Integer);
        pCol.Value = columnId;
        for (int i = 0; i < ids.Count; i++)
        {
            pId.Value = ids[i];
            pPos.Value = i;
            cmd.ExecuteNonQuery();
        }
    }

    private static void NormalizeCardPositions(SqliteConnection c, SqliteTransaction tx, long columnId)
    {
        var ids = OrderedCardIds(c, tx, columnId, null);
        ApplyCardOrder(c, tx, columnId, ids);
    }

    private static void MoveColumn(SqliteConnection c, SqliteTransaction tx, long columnId, int position)
    {
        long boardId = ScalarLong(c, tx, "SELECT board_id FROM board_columns WHERE id = $id;", ("$id", columnId));
        var ids = new List<long>();
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT id FROM board_columns WHERE board_id = $b AND id <> $id ORDER BY position, id;";
            cmd.Parameters.AddWithValue("$b", boardId);
            cmd.Parameters.AddWithValue("$id", columnId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add(r.GetInt64(0));
        }

        position = Math.Clamp(position, 0, ids.Count);
        ids.Insert(position, columnId);
        ApplyColumnOrder(c, tx, boardId, ids);
    }

    private static void ApplyColumnOrder(SqliteConnection c, SqliteTransaction tx, long boardId, List<long> ids)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE board_columns SET position = $p WHERE id = $id AND board_id = $b;";
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        var pPos = cmd.Parameters.Add("$p", SqliteType.Integer);
        var pBoard = cmd.Parameters.Add("$b", SqliteType.Integer);
        pBoard.Value = boardId;
        for (int i = 0; i < ids.Count; i++)
        {
            pId.Value = ids[i];
            pPos.Value = i;
            cmd.ExecuteNonQuery();
        }
    }

    private static void NormalizeColumnPositions(SqliteConnection c, SqliteTransaction tx, long boardId)
    {
        var ids = new List<long>();
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT id FROM board_columns WHERE board_id = $b ORDER BY position, id;";
            cmd.Parameters.AddWithValue("$b", boardId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add(r.GetInt64(0));
        }
        ApplyColumnOrder(c, tx, boardId, ids);
    }

    private static void NormalizeBoardPositions(SqliteConnection c, SqliteTransaction tx)
    {
        var ids = new List<long>();
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT id FROM boards ORDER BY position, id;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add(r.GetInt64(0));
        }

        using var upd = c.CreateCommand();
        upd.Transaction = tx;
        upd.CommandText = "UPDATE boards SET position = $p WHERE id = $id;";
        var pId = upd.Parameters.Add("$id", SqliteType.Integer);
        var pPos = upd.Parameters.Add("$p", SqliteType.Integer);
        for (int i = 0; i < ids.Count; i++)
        {
            pId.Value = ids[i];
            pPos.Value = i;
            upd.ExecuteNonQuery();
        }
    }
}

internal static class Schema
{
    public const int Version = 2;

    public static void Ensure(SqliteConnection c)
    {
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=DELETE;";
            cmd.ExecuteScalar();
        }

        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS boards (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    name        TEXT    NOT NULL,
                    position    INTEGER NOT NULL DEFAULT 0,
                    created_at  TEXT    NOT NULL
                );

                CREATE TABLE IF NOT EXISTS board_columns (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    board_id    INTEGER NOT NULL REFERENCES boards(id) ON DELETE CASCADE,
                    name        TEXT    NOT NULL,
                    position    INTEGER NOT NULL DEFAULT 0,
                    wip_limit   INTEGER NULL
                );

                CREATE TABLE IF NOT EXISTS cards (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    board_id    INTEGER NOT NULL REFERENCES boards(id) ON DELETE CASCADE,
                    column_id   INTEGER NOT NULL REFERENCES board_columns(id) ON DELETE CASCADE,
                    title       TEXT    NOT NULL,
                    description TEXT    NOT NULL DEFAULT '',
                    priority    INTEGER NOT NULL DEFAULT 1,
                    due_date    TEXT    NULL,
                    tags        TEXT    NOT NULL DEFAULT '',
                    position    INTEGER NOT NULL DEFAULT 0,
                    created_at  TEXT    NOT NULL,
                    updated_at  TEXT    NOT NULL,
                    archived_at TEXT    NULL
                );

                CREATE TABLE IF NOT EXISTS card_checklist (
                    id       INTEGER PRIMARY KEY AUTOINCREMENT,
                    card_id  INTEGER NOT NULL REFERENCES cards(id) ON DELETE CASCADE,
                    text     TEXT    NOT NULL,
                    done     INTEGER NOT NULL DEFAULT 0,
                    position INTEGER NOT NULL DEFAULT 0
                );

                CREATE TABLE IF NOT EXISTS attachments (
                    id            INTEGER PRIMARY KEY AUTOINCREMENT,
                    card_id       INTEGER NOT NULL REFERENCES cards(id) ON DELETE CASCADE,
                    kind          TEXT    NOT NULL DEFAULT 'link',
                    title         TEXT    NOT NULL DEFAULT '',
                    url           TEXT    NULL,
                    file_name     TEXT    NULL,
                    original_name TEXT    NULL,
                    size          INTEGER NOT NULL DEFAULT 0,
                    created_at    TEXT    NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_cards_column ON cards(column_id, position);
                CREATE INDEX IF NOT EXISTS ix_columns_board ON board_columns(board_id, position);
                CREATE INDEX IF NOT EXISTS ix_checklist_card ON card_checklist(card_id, position);
                CREATE INDEX IF NOT EXISTS ix_attachments_card ON attachments(card_id);
                """;
            cmd.ExecuteNonQuery();
        }

        // Миграция базы версии 1: в cards не было archived_at.
        if (!HasColumn(c, "cards", "archived_at"))
        {
            using var alter = c.CreateCommand();
            alter.CommandText = "ALTER TABLE cards ADD COLUMN archived_at TEXT NULL;";
            alter.ExecuteNonQuery();
        }

        int version;
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version;";
            version = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }

        if (version == 0)
        {
            SeedIfEmpty(c);
        }

        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA user_version={Version};";
            cmd.ExecuteNonQuery();
        }
    }

    private static bool HasColumn(SqliteConnection c, string table, string column)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static void SeedIfEmpty(SqliteConnection c)
    {
        using var check = c.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM boards;";
        if (Convert.ToInt32(check.ExecuteScalar() ?? 0) > 0) return;

        string now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        using var tx = c.BeginTransaction();

        long boardId;
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO boards(name, position, created_at) VALUES ('Моя доска', 0, $t); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$t", now);
            boardId = Convert.ToInt64(cmd.ExecuteScalar());
        }

        long MakeColumn(string name, int pos)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO board_columns(board_id, name, position, wip_limit) VALUES ($b, $n, $p, NULL); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$b", boardId);
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$p", pos);
            return Convert.ToInt64(cmd.ExecuteScalar());
        }

        long backlog = MakeColumn("Бэклог", 0);
        long progress = MakeColumn("В работе", 1);
        MakeColumn("Готово", 2);

        long MakeCard(long column, string title, string desc, int priority, string? due, string tags, int pos)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO cards(board_id, column_id, title, description, priority, due_date, tags, position, created_at, updated_at, archived_at)
                VALUES ($b, $c, $t, $d, $pr, $due, $tags, $p, $now, $now, NULL);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$b", boardId);
            cmd.Parameters.AddWithValue("$c", column);
            cmd.Parameters.AddWithValue("$t", title);
            cmd.Parameters.AddWithValue("$d", desc);
            cmd.Parameters.AddWithValue("$pr", priority);
            cmd.Parameters.AddWithValue("$due", (object?)due ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$tags", tags);
            cmd.Parameters.AddWithValue("$p", pos);
            cmd.Parameters.AddWithValue("$now", now);
            return Convert.ToInt64(cmd.ExecuteScalar());
        }

        long card1 = MakeCard(backlog, "Так выглядит карточка",
            "Кликните по карточке: там чек-лист, вложения, срок и теги. Перетаскивайте карточки между колонками.",
            1, null, "пример", 0);
        MakeCard(backlog, "Все данные — в папке программы",
            "Файл kanban.db лежит рядом с Kanban.exe. Скопируйте папку на другой ПК или флешку — доска поедет вместе с ней.",
            2, null, "портативно", 1);
        MakeCard(progress, "Проверить перенос на другой ПК",
            "Скопируйте всю папку, запустите Kanban.exe на другом компьютере.", 1, null, "", 0);

        void MakeCheck(long card, string text, bool done, int pos)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO card_checklist(card_id, text, done, position) VALUES ($c, $t, $d, $p);";
            cmd.Parameters.AddWithValue("$c", card);
            cmd.Parameters.AddWithValue("$t", text);
            cmd.Parameters.AddWithValue("$d", done ? 1 : 0);
            cmd.Parameters.AddWithValue("$p", pos);
            cmd.ExecuteNonQuery();
        }

        MakeCheck(card1, "Открыть карточку", true, 0);
        MakeCheck(card1, "Добавить пункт чек-листа", false, 1);
        MakeCheck(card1, "Перетащить карточку в другую колонку", false, 2);

        tx.Commit();
    }
}
