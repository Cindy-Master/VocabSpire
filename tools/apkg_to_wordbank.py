# -*- coding: utf-8 -*-
"""
Anki .apkg → VocabSpire JSON 词库 离线转换工具

为什么是离线工具而不是做进 mod：
    apkg 只规定了「notetype 有哪些字段」，字段叫什么、内容怎么排全凭牌组作者高兴，
    模板组合近乎无穷。mod 内的 ApkgImporter 只做通用启发式（够用就好），遇到作者
    自造的模板必然认不出。与其把越来越多的特例塞进 mod，不如在这里离线转一次，
    产出标准词库 JSON —— mod 侧零改动，转换逻辑出错也只影响这一个词库。

用法：
    python tools/apkg_to_wordbank.py <input.apkg> --out-dir <目录> [--prefix 前缀] [--name 显示名]
    # 例：
    python tools/apkg_to_wordbank.py "D:/生物.apkg" --out-dir ./out --prefix bio --name "高中生物"

输出（每类题型各一册 + 一个合并册，按需取用）：
    <prefix>_choice.json   固定选择题（english=题干 + options + answer）
    <prefix>_cloze.json    填空挖空题（english=挖空句，chinese=答案）
    <prefix>_qa.json       问答题    （english=问题，chinese=答案）
    <prefix>_all.json      以上三者合并

识别策略（按 notetype 字段名 + 内容签名，不认死 notetype 名）：
    choice  字段名同时含 question / options / answer  → 固定选择题
    cloze   主字段内容含 {{c1::…}}                    → 每个 cN 编号生成一张挖空卡
    qa      字段名含 question 且有 answer* 系列        → 问答对

处理要点（踩过的坑，勿删）：
  1. answer 输出 **0-based**（FileParser.ParseJsonAnswer 规范）。Anki 侧多是 1-based，要减 1。
  2. 普通条目的 chinese 一律写成**数组**：写成字符串会被 FileParser.SplitDefinitions
     按「;／；」硬拆成多义项，而中文答案里分号极常见（"抽搐；肌无力"会被拆成两条）。
  3. cloze 正则要匹配大小写 c（实测有作者写 {{C1::…}}），且非贪婪 —— 遇到 "}}}}"
     这类手滑会留下孤立括号，最后统一清残留。
  4. <sup> 转 ^ 再去标签，否则 H<sub>2</sub><sup>18</sup>O 会糊成 H218O。
  5. 多选题（answer 形如 "2||3||4"）直接丢弃 —— mod 没有多选 UI，勉强收下只会判错。
  6. 判断题模板常把**题号**塞在 Answer-1、真答案在同题干的另一条 note 里。
     规则：Answer-1 为纯数字 + 其余答案位全空 + 题干以「。」结尾 → 判定为题号占位，丢弃该 note。
     必须带「题干以句号结尾」这一条，否则会误杀 "血红蛋白中Fe几价？→ 2" 这种真数字答案。
"""
import argparse
import html
import json
import os
import re
import sqlite3
import sys
import tempfile
import zipfile
from collections import Counter, defaultdict

FIELD_SEP = "\x1f"          # Anki 用 0x1F 分隔 note 的各字段
BLANK = "____"              # 挖空占位符

# 挖空体内禁止再出现 {{ 或 }}：作者把 }} 手滑写成 } 时（实测 3 处），朴素的 (.*?) 会一路
# 吃到下一个挖空的 }}，把两个空并成一个、答案里混进别人的文本。加上这个否定环视就只是匹配
# 失败（该条随后被 finalize 的残留检查丢掉），不会产出错答案。
CLOZE = re.compile(r"\{\{[cC](\d+)::((?:(?!\{\{|\}\}).)*?)\}\}", re.S)
SOUND = re.compile(r"\[sound:[^\]]*\]")
SUP = re.compile(r"<sup[^>]*>(.*?)</sup>", re.S | re.I)
BR = re.compile(r"<br\s*/?>", re.I)
BLOCK_END = re.compile(r"</(div|p|li|tr|h[1-6])\s*>", re.I)
ANY_TAG = re.compile(r"<[^>]+>")
PURE_INT = re.compile(r"^\d{1,4}$")
# 「有效内容」判定：至少含一个 CJK 汉字 / 假名 / 拉丁字母数字，否则是 ";" "——" 这类残渣。
# 末尾那串判断符号（✓ × √ 等）必须留着：判断题的答案就是孤零零一个符号，只按汉字字母过滤
# 会把整批判断题误杀（实测这一条救回 46 张填空判断卡）。
MEANINGFUL = re.compile(r"[\u4e00-\u9fff\u3040-\u30ffA-Za-z0-9\u2713\u2714\u221a\u00d7\u2717\u2718\u25cb\u25cf\u25ef\u2b55\u274c]")


# ── 文本清洗 ──────────────────────────────────────────────────────────────

def clean_html(s):
    """HTML 富文本 → 单行纯文本。块级标签转空格，上标转 ^，实体还原。"""
    if not s:
        return ""
    s = SOUND.sub("", s)
    s = SUP.sub(lambda m: "^" + ANY_TAG.sub("", m.group(1)), s)   # H<sup>18</sup>O → H^18O
    s = BR.sub("\n", s)
    s = BLOCK_END.sub("\n", s)
    s = ANY_TAG.sub("", s)
    s = html.unescape(s)
    s = s.replace("\u00a0", " ")
    lines = [ln.strip() for ln in s.split("\n")]
    return " ".join(ln for ln in lines if ln).strip()


def strip_cloze_markup(s):
    """把 {{cN::文本}} 还原成「文本」（丢掉标记本身），并清掉孤立残留括号。"""
    s = CLOZE.sub(lambda m: m.group(2).split("::", 1)[0], s)
    return s.replace("{{", "").replace("}}", "")


def meaningful(s, min_len=2):
    return len(s) >= min_len and MEANINGFUL.search(s) is not None


# ── cloze 拆卡 ────────────────────────────────────────────────────────────

def split_cloze_cards(raw):
    """
    一条 cloze note → 若干 (挖空题干, 答案) —— 与 Anki 同语义：每个 cN 编号一张卡。
    目标编号的所有出现都挖空（带 ::hint 时空里保留提示），其余编号填回答案文本。
    """
    hits = CLOZE.findall(raw)
    if not hits:
        return []

    order, seen = [], set()
    for num, _ in hits:
        if num not in seen:
            seen.add(num)
            order.append(num)

    cards = []
    for target in order:
        answers = []

        def repl(m, _t=target, _acc=answers):
            body = m.group(2)
            ans, _, hint = body.partition("::")
            ans = clean_html(ans).strip()
            if m.group(1) != _t:
                return ans                      # 别的空：填回答案，作为上下文
            _acc.append(ans)
            hint = clean_html(hint).strip()
            return BLANK + "（" + hint + "）" if hint else BLANK

        stem = clean_html(CLOZE.sub(repl, raw)).replace("{{", "").replace("}}", "")
        answer = "，".join(a for a in answers if a)
        if stem and answer:
            cards.append((stem, answer))
    return cards


# ── notetype 识别 ─────────────────────────────────────────────────────────

def name_index(fnames, keys, exclude=()):
    """返回首个「名字含 keys 之一且不含 exclude」的字段下标，没有则 -1。"""
    for i, f in enumerate(fnames):
        low = f.lower()
        if any(k in low for k in keys) and not any(x in low for x in exclude):
            return i
    return -1


def classify(fnames, rows):
    """判定这个 notetype 走哪条转换策略。"""
    qi = name_index(fnames, ("question", "题干", "题目", "问题"))
    oi = name_index(fnames, ("options", "选项", "choices"))
    ai = name_index(fnames, ("answer", "答案"))

    if qi >= 0 and oi >= 0 and ai >= 0:
        return "choice", (qi, oi, ai)

    # cloze：看内容而非字段名 —— 作者可以把主字段叫 "+" "正面" 甚至 "《》"
    probe = rows[:200]
    width = max((len(r) for r in probe), default=0)
    for ci in range(width):
        hit = sum(1 for r in probe if ci < len(r) and CLOZE.search(r[ci]))
        if hit >= max(5, len(probe) * 0.3):
            return "cloze", (ci,)

    if qi >= 0 and ai >= 0:
        answer_cols = [i for i, f in enumerate(fnames)
                       if any(k in f.lower() for k in ("answer", "答案", "背面"))]
        if answer_cols:
            return "qa", (qi, tuple(answer_cols))

    return None, ()


# ── 三种策略 ──────────────────────────────────────────────────────────────

def parse_answer_index(raw, n):
    """Anki 侧的答案 → 0-based 索引。字母 A-H 按 0-based，数字优先按 1-based。"""
    raw = raw.strip()
    if len(raw) == 1 and raw.upper().isalpha():
        i = ord(raw.upper()) - ord("A")
        return i if 0 <= i < n else -1
    if PURE_INT.match(raw):
        num = int(raw)
        if 1 <= num <= n:
            return num - 1
        if 0 <= num < n:
            return num
    return -1


def from_choice(rows, idx, stats):
    qi, oi, ai = idx
    out = []
    for parts in rows:
        if max(qi, oi, ai) >= len(parts):
            stats["choice_字段不足"] += 1
            continue
        stem = clean_html(strip_cloze_markup(parts[qi]))
        raw_opts = clean_html(strip_cloze_markup(parts[oi]))
        raw_ans = clean_html(parts[ai]).strip()
        if not stem or not raw_opts or not raw_ans:
            stats["choice_空字段"] += 1
            continue
        if "||" in raw_ans:
            stats["choice_多选题跳过"] += 1        # mod 无多选 UI
            continue

        sep = "||" if "||" in raw_opts else "\n"
        opts = [o.strip() for o in raw_opts.split(sep) if o.strip()]
        if len(opts) < 2:
            stats["choice_选项不足2个"] += 1
            continue

        ans = parse_answer_index(raw_ans, len(opts))
        if ans < 0:
            stats["choice_答案无法解析"] += 1
            continue

        out.append({"english": stem, "chinese": opts[ans], "options": opts, "answer": ans})
    return out


def from_cloze(rows, idx, stats):
    (ci,) = idx
    out = []
    for parts in rows:
        if ci >= len(parts):
            stats["cloze_字段不足"] += 1
            continue
        cards = split_cloze_cards(parts[ci])
        if not cards:
            stats["cloze_无挖空"] += 1
            continue
        for stem, ans in cards:
            out.append({"english": stem, "chinese": [ans]})
    return out


def from_qa(rows, idx, stats):
    qi, answer_cols = idx
    out = []
    for parts in rows:
        if qi >= len(parts):
            stats["qa_字段不足"] += 1
            continue
        q = clean_html(strip_cloze_markup(parts[qi]))
        answers = [clean_html(strip_cloze_markup(parts[c])).strip()
                   for c in answer_cols if c < len(parts)]
        answers = [a for a in answers if a]
        if not q or not answers:
            stats["qa_空题干或空答案"] += 1
            continue
        # 题号占位：Answer-1 是纯数字 + 没有别的答案 + 题干是「。」结尾的陈述句
        if len(answers) == 1 and PURE_INT.match(answers[0]) and q.endswith("。"):
            stats["qa_题号占位丢弃"] += 1
            continue
        out.append({"english": q, "chinese": answers})
    return out


# ── apkg 读取 ─────────────────────────────────────────────────────────────

def extract_collection(apkg_path, workdir):
    """从 apkg 里取出 collection 数据库文件路径。anki21b 需要 zstd 解压。"""
    with zipfile.ZipFile(apkg_path) as z:
        names = set(z.namelist())
        for cand in ("collection.anki21b", "collection.anki21", "collection.anki2"):
            if cand not in names:
                continue
            blob = z.read(cand)
            if cand.endswith("b"):
                try:
                    import zstandard
                except ImportError:
                    sys.exit("collection.anki21b 需要 zstd 解压：pip install zstandard")
                blob = zstandard.ZstdDecompressor().decompress(blob, max_output_size=1 << 30)
            out = os.path.join(workdir, "collection.sqlite")
            with open(out, "wb") as f:
                f.write(blob)
            return out, cand
    sys.exit("apkg 内没有 collection 数据库（anki2 / anki21 / anki21b 都没找到）")


def read_models(conn):
    """notetype id → (名称, 字段名列表)。旧版在 col.models(JSON)，新版在 notetypes+fields 表。"""
    try:
        raw = conn.execute("select models from col").fetchone()
        if raw and raw[0] and len(raw[0]) > 2:
            models = json.loads(raw[0])
            out = {}
            for mid, m in models.items():
                flds = sorted(m.get("flds", []), key=lambda f: f.get("ord", 0))
                out[str(mid)] = (m.get("name", ""), [f.get("name", "") for f in flds])
            if out:
                return out
    except (sqlite3.Error, json.JSONDecodeError, TypeError):
        pass

    out = {}
    try:
        names = {str(r[0]): r[1] for r in conn.execute("select id, name from notetypes")}
        by_nt = defaultdict(list)
        for ntid, ordv, nm in conn.execute("select ntid, ord, name from fields"):
            by_nt[str(ntid)].append((ordv, nm))
        for ntid, lst in by_nt.items():
            lst.sort()
            out[ntid] = (names.get(ntid, ""), [nm for _, nm in lst])
    except sqlite3.Error:
        pass
    return out


# ── 词条后处理 ────────────────────────────────────────────────────────────

def finalize(entries, stats, tag, max_stem):
    """过滤垃圾 + 按题干去重（答案合并）。保持首次出现顺序。"""
    order, merged = [], {}
    for e in entries:
        stem = e["english"].strip()
        if not meaningful(stem, 3):
            stats[tag + "_题干太短或无实义"] += 1
            continue
        # 残留的 {{ }} 只会来自原始数据里写坏的挖空标记，这种条目答案不可信，直接扔
        texts = [stem] + ([e["chinese"]] if isinstance(e["chinese"], str) else list(e["chinese"]))
        if any("{{" in t or "}}" in t for t in texts):
            stats[tag + "_挖空标记损坏丢弃"] += 1
            continue
        # QuizPanel 的题干是固定 30 号字、无滚动条的 Label，题干太长会把选项按钮挤出屏幕。
        # 320 这个上限对齐内置医考题库的实测极值（最长 310 字，p99=210），不会误伤正常题。
        if max_stem > 0 and len(stem) > max_stem:
            stats[tag + "_题干超长丢弃"] += 1
            continue

        if "options" in e:
            opts = [o for o in e["options"] if o.strip()]
            if len(opts) < 2 or not (0 <= e["answer"] < len(opts)):
                stats[tag + "_选项或答案损坏"] += 1
                continue
            key = ("C", stem)
            if key in merged:
                stats[tag + "_重复题干合并"] += 1
                continue
            merged[key] = e
            order.append(key)
            continue

        answers = [a.strip() for a in e["chinese"] if meaningful(a.strip(), 1)]
        answers = [a for a in answers if a != stem]
        if not answers:
            stats[tag + "_无有效答案"] += 1
            continue
        key = ("P", stem)
        if key in merged:
            stats[tag + "_重复题干合并"] += 1
            for a in answers:
                if a not in merged[key]["chinese"]:
                    merged[key]["chinese"].append(a)
            continue
        merged[key] = {"english": stem, "chinese": answers}
        order.append(key)

    return [merged[k] for k in order]


def write_bank(path, name, desc, words):
    payload = {"name": name, "description": desc, "words": words}
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(payload, f, ensure_ascii=False, indent=2)
        f.write("\n")
    return os.path.getsize(path)


# ── 主流程 ────────────────────────────────────────────────────────────────

def main():
    ap = argparse.ArgumentParser(description="Anki .apkg → VocabSpire JSON 词库")
    ap.add_argument("apkg")
    ap.add_argument("--out-dir", required=True)
    ap.add_argument("--prefix", default=None, help="输出文件名前缀，默认取 apkg 文件名")
    ap.add_argument("--name", default=None, help="词库显示名，默认同 prefix")
    ap.add_argument("--source", default="", help="写进 description 的出处说明")
    ap.add_argument("--max-stem", type=int, default=320,
                    help="题干字数上限，超过的整条丢弃（0=不限）。默认 320，见 finalize 里的说明")
    args = ap.parse_args()

    prefix = args.prefix or re.sub(r"[^\w\u4e00-\u9fff-]", "_",
                                   os.path.splitext(os.path.basename(args.apkg))[0])
    display = args.name or prefix
    os.makedirs(args.out_dir, exist_ok=True)

    with tempfile.TemporaryDirectory() as tmp:
        db_path, which = extract_collection(args.apkg, tmp)
        conn = sqlite3.connect(db_path)
        models = read_models(conn)
        rows_by_mid = defaultdict(list)
        for mid, flds in conn.execute("select mid, flds from notes"):
            rows_by_mid[str(mid)].append(flds.split(FIELD_SEP))
        conn.close()

    total_notes = sum(len(v) for v in rows_by_mid.values())
    print("=== " + os.path.basename(args.apkg) + " (" + which + ") ===")
    print("notetype 数=" + str(len(models)) + "  notes 总数=" + str(total_notes) + "\n")

    buckets = {"choice": [], "cloze": [], "qa": []}
    stats = Counter()
    handlers = {"choice": from_choice, "cloze": from_cloze, "qa": from_qa}
    for mid, rows in sorted(rows_by_mid.items(), key=lambda kv: -len(kv[1])):
        mname, fnames = models.get(mid, ("(未知)", []))
        if not fnames:
            fnames = ["#%d" % i for i in range(len(rows[0]) if rows else 0)]
        kind, idx = classify(fnames, rows)
        print("[%s] notes=%d 字段=%s" % (mname, len(rows), fnames))
        if kind is None:
            print("   → 无法识别，跳过\n")
            stats["整个notetype跳过"] += len(rows)
            continue
        produced = handlers[kind](rows, idx, stats)
        buckets[kind].extend(produced)
        print("   → 策略=%s  产出 %d 条（去重前）\n" % (kind, len(produced)))

    label = {"choice": "选择题", "cloze": "填空题", "qa": "问答题"}
    written, all_words = [], []
    for kind in ("choice", "cloze", "qa"):
        final = finalize(buckets[kind], stats, kind, args.max_stem)
        if not final:
            continue
        all_words.extend(final)
        path = os.path.join(args.out_dir, prefix + "_" + kind + ".json")
        desc = "%d 题（%s）。由 %s 转换。" % (len(final), label[kind], os.path.basename(args.apkg))
        if args.source:
            desc += " 来源：" + args.source
        size = write_bank(path, display + "·" + label[kind], desc, final)
        written.append((path, len(final), size))

    if len(written) > 1:
        path = os.path.join(args.out_dir, prefix + "_all.json")
        desc = "%d 题（选择题 + 填空题 + 问答题 合并）。由 %s 转换。" % (
            len(all_words), os.path.basename(args.apkg))
        if args.source:
            desc += " 来源：" + args.source
        size = write_bank(path, display, desc, all_words)
        written.append((path, len(all_words), size))

    print("--- 丢弃明细 ---")
    for k in sorted(stats):
        print("  %s: %d" % (k, stats[k]))

    print("\n--- 产出 ---")
    for path, n, size in written:
        print("  %s: %d 条, %.0f KB" % (os.path.basename(path), n, size / 1024))


if __name__ == "__main__":
    main()
