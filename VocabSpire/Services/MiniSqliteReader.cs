using System.Text;

namespace VocabSpire.Services;

/// <summary>
/// 纯托管的 SQLite 文件「只读」reader —— 不依赖任何 native 库（避开 Godot 动态加载 mod
/// 带 e_sqlite3.dll 的风险），足够解析 Anki .apkg 内的 collection 数据库。
///
/// 实现 SQLite 文件格式的最小子集：
///   · 数据库头（page_size / 文本编码）
///   · table b-tree 遍历（interior 类型 5 递归 → leaf 类型 13）
///   · record 解析（varint 头 + serial types）
///   · overflow page 链（长字段如 notes.flds 会跨页）
///   · 从 sqlite_master 的 CREATE SQL 解析列名 → 按列名返回行（适应 schema 变化）
///
/// 参考：https://www.sqlite.org/fileformat.html
/// </summary>
public sealed class MiniSqliteReader
{
    private readonly byte[] _d;
    private readonly int _pageSize;
    private readonly int _usable;
    private readonly Encoding _textEnc;

    private const int LeafTable = 13;
    private const int InteriorTable = 5;

    public MiniSqliteReader(byte[] dbBytes)
    {
        _d = dbBytes ?? throw new ArgumentNullException(nameof(dbBytes));
        if (_d.Length < 100 || Encoding.ASCII.GetString(_d, 0, 16) != "SQLite format 3\0")
            throw new InvalidDataException("不是合法的 SQLite 数据库（magic 头不匹配）。");

        int ps = U16(16);
        _pageSize = ps == 1 ? 65536 : ps;
        int reserved = _d[20];
        _usable = _pageSize - reserved;

        _textEnc = U32(56) switch
        {
            2 => Encoding.Unicode,        // UTF-16le
            3 => Encoding.BigEndianUnicode, // UTF-16be
            _ => new UTF8Encoding(false)    // 1 或默认 = UTF-8（Anki 用这个）
        };
    }

    /// <summary>读取整张表，每行是「列名 → 值」字典。表不存在返回空列表。</summary>
    public List<Dictionary<string, object?>> ReadTable(string tableName)
    {
        // 1) 遍历 page 1 = sqlite_master，列固定：type,name,tbl_name,rootpage,sql
        var master = new List<(long rowid, object?[] vals)>();
        WalkTableBtree(1, master);

        int rootPage = -1;
        string? createSql = null;
        foreach (var (_, r) in master)
        {
            if (r.Length >= 5 && (r[0] as string) == "table" && (r[1] as string) == tableName)
            {
                rootPage = (int)ToLong(r[3]);
                createSql = r[4] as string;
                break;
            }
        }
        if (rootPage <= 0) return new List<Dictionary<string, object?>>();

        var (columns, rowidAlias) = ParseColumns(createSql ?? "");

        // 2) 遍历目标表 b-tree
        var rawRows = new List<(long rowid, object?[] vals)>();
        WalkTableBtree(rootPage, rawRows);

        // 3) 按列名组装。INTEGER PRIMARY KEY 列是 rowid 别名：record 里存为 NULL，用 cell 的 rowid 回填
        var result = new List<Dictionary<string, object?>>(rawRows.Count);
        foreach (var (rowid, row) in rawRows)
        {
            var dict = new Dictionary<string, object?>(columns.Count, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < columns.Count; i++)
            {
                object? v = i < row.Length ? row[i] : null;
                if (v is null && i == rowidAlias) v = rowid;
                dict[columns[i]] = v;
            }
            result.Add(dict);
        }
        return result;
    }

    // ── B-tree 遍历 ──────────────────────────────────────────────────────────

    private void WalkTableBtree(int pageNum, List<(long rowid, object?[] vals)> rows)
    {
        int baseOff = (pageNum - 1) * _pageSize;
        int hdr = baseOff + (pageNum == 1 ? 100 : 0); // page 1 前 100 字节是 db header
        byte type = _d[hdr];
        int numCells = U16(hdr + 3);

        if (type == LeafTable)
        {
            int cellPtr = hdr + 8;
            for (int i = 0; i < numCells; i++)
            {
                int cellOff = baseOff + U16(cellPtr + i * 2);
                rows.Add(ParseTableLeafCell(cellOff));
            }
        }
        else if (type == InteriorTable)
        {
            int cellPtr = hdr + 12;
            for (int i = 0; i < numCells; i++)
            {
                int cellOff = baseOff + U16(cellPtr + i * 2);
                int child = (int)U32(cellOff); // 左孩子页号
                WalkTableBtree(child, rows);
            }
            int rightMost = (int)U32(hdr + 8);
            WalkTableBtree(rightMost, rows);
        }
        // 其它类型（index b-tree）我们不需要，忽略
    }

    private (long rowid, object?[] vals) ParseTableLeafCell(int cellOff)
    {
        int p = cellOff;
        long payloadLen = ReadVarint(_d, ref p);
        long rowid = ReadVarint(_d, ref p); // rowid（INTEGER PRIMARY KEY 列的真实值在这里）
        byte[] payload = ReadPayload(p, (int)payloadLen);
        return (rowid, ParseRecord(payload));
    }

    /// <summary>读取 cell payload，处理 overflow page 链。</summary>
    private byte[] ReadPayload(int localOff, int payloadLen)
    {
        int maxLocal = _usable - 35;
        if (payloadLen <= maxLocal)
        {
            var only = new byte[payloadLen];
            Array.Copy(_d, localOff, only, 0, payloadLen);
            return only;
        }

        int minLocal = (_usable - 12) * 32 / 255 - 23;
        int k = minLocal + (payloadLen - minLocal) % (_usable - 4);
        int localSize = k <= maxLocal ? k : minLocal;

        var buf = new byte[payloadLen];
        Array.Copy(_d, localOff, buf, 0, localSize);
        int got = localSize;
        int overflowPage = (int)U32(localOff + localSize);

        while (overflowPage != 0 && got < payloadLen)
        {
            int pOff = (overflowPage - 1) * _pageSize;
            int next = (int)U32(pOff); // overflow page 前 4 字节 = 下一页号（0 结束）
            int chunk = Math.Min(_usable - 4, payloadLen - got);
            Array.Copy(_d, pOff + 4, buf, got, chunk);
            got += chunk;
            overflowPage = next;
        }
        return buf;
    }

    // ── Record 解析 ─────────────────────────────────────────────────────────

    private object?[] ParseRecord(byte[] rec)
    {
        int p = 0;
        long headerLen = ReadVarint(rec, ref p);
        int headerEnd = (int)headerLen;

        var serials = new List<long>();
        while (p < headerEnd) serials.Add(ReadVarint(rec, ref p));

        var vals = new object?[serials.Count];
        int body = headerEnd;
        for (int i = 0; i < serials.Count; i++)
        {
            var (val, next) = ReadValue(rec, body, serials[i]);
            vals[i] = val;
            body = next;
        }
        return vals;
    }

    private (object?, int) ReadValue(byte[] rec, int off, long st)
    {
        switch (st)
        {
            case 0: return (null, off);
            case 1: return (ReadIntBE(rec, off, 1), off + 1);
            case 2: return (ReadIntBE(rec, off, 2), off + 2);
            case 3: return (ReadIntBE(rec, off, 3), off + 3);
            case 4: return (ReadIntBE(rec, off, 4), off + 4);
            case 5: return (ReadIntBE(rec, off, 6), off + 6);
            case 6: return (ReadIntBE(rec, off, 8), off + 8);
            case 7:
            {
                ulong u = 0;
                for (int i = 0; i < 8; i++) u = (u << 8) | rec[off + i];
                return (BitConverter.Int64BitsToDouble((long)u), off + 8);
            }
            case 8: return (0L, off);
            case 9: return (1L, off);
            default:
                if (st >= 12)
                {
                    bool isText = (st % 2) == 1;
                    int len = (int)((st - (isText ? 13 : 12)) / 2);
                    if (isText)
                    {
                        string s = _textEnc.GetString(rec, off, len);
                        return (s, off + len);
                    }
                    var blob = new byte[len];
                    Array.Copy(rec, off, blob, 0, len);
                    return (blob, off + len);
                }
                return (null, off); // 10/11 保留，不会出现
        }
    }

    // ── 基础读取 ────────────────────────────────────────────────────────────

    private int U16(int off) => (_d[off] << 8) | _d[off + 1];

    private long U32(int off) =>
        ((long)_d[off] << 24) | ((long)_d[off + 1] << 16) | ((long)_d[off + 2] << 8) | _d[off + 3];

    /// <summary>读 n 字节大端有符号整数（serial types 1-6 是补码大端）。</summary>
    private static long ReadIntBE(byte[] d, int off, int n)
    {
        long v = 0;
        for (int i = 0; i < n; i++) v = (v << 8) | d[off + i];
        if (n < 8 && (d[off] & 0x80) != 0) // 负数：符号扩展
            v |= -1L << (n * 8);
        return v;
    }

    /// <summary>SQLite 变长整数：最多 9 字节，前 8 字节每字节高位是续位标志，第 9 字节用满 8 位。</summary>
    private static long ReadVarint(byte[] d, ref int p)
    {
        long result = 0;
        for (int i = 0; i < 9; i++)
        {
            byte b = d[p++];
            if (i == 8) { result = (result << 8) | b; }
            else
            {
                result = (result << 7) | (long)(b & 0x7F);
                if ((b & 0x80) == 0) break;
            }
        }
        return result;
    }

    private static long ToLong(object? o) => o switch
    {
        long l => l,
        int i => i,
        null => 0,
        _ => Convert.ToInt64(o)
    };

    // ── 从 CREATE TABLE SQL 解析列名 ────────────────────────────────────────

    private static (List<string> cols, int rowidAlias) ParseColumns(string createSql)
    {
        var cols = new List<string>();
        int rowidAlias = -1;
        int lp = createSql.IndexOf('(');
        int rp = createSql.LastIndexOf(')');
        if (lp < 0 || rp <= lp) return (cols, rowidAlias);

        string inner = createSql.Substring(lp + 1, rp - lp - 1);

        // 按顶层逗号切分（忽略括号内的逗号，如 DECIMAL(10,2) 或 primary key(a,b)）
        var segs = new List<string>();
        var sb = new StringBuilder();
        int depth = 0;
        foreach (char c in inner)
        {
            if (c == '(') depth++;
            else if (c == ')') depth--;
            if (c == ',' && depth == 0) { segs.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        if (sb.Length > 0) segs.Add(sb.ToString());

        foreach (var seg in segs)
        {
            string t = seg.Trim();
            if (t.Length == 0) continue;
            string up = t.ToUpperInvariant();
            // 跳过表级约束行
            if (up.StartsWith("PRIMARY ") || up.StartsWith("UNIQUE") || up.StartsWith("CHECK") ||
                up.StartsWith("FOREIGN") || up.StartsWith("CONSTRAINT"))
                continue;
            // 单列 INTEGER PRIMARY KEY = rowid 别名（record 里存为 NULL，需用 rowid 回填）
            if (up.Contains("INTEGER") && up.Contains("PRIMARY KEY"))
                rowidAlias = cols.Count;
            cols.Add(FirstToken(t));
        }
        return (cols, rowidAlias);
    }

    /// <summary>取列定义的第一个 token = 列名，去掉 "..." / `...` / [...] / '...' 包裹。</summary>
    private static string FirstToken(string def)
    {
        def = def.TrimStart();
        if (def.Length == 0) return def;

        char q = def[0];
        char close = q switch { '"' => '"', '`' => '`', '[' => ']', '\'' => '\'', _ => '\0' };
        if (close != '\0')
        {
            int end = def.IndexOf(close, 1);
            return end > 0 ? def.Substring(1, end - 1) : def.Substring(1);
        }

        int i = 0;
        while (i < def.Length && !char.IsWhiteSpace(def[i]) && def[i] != '(') i++;
        return def.Substring(0, i);
    }
}
