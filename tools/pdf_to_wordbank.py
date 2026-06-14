# -*- coding: utf-8 -*-
"""
PDF 词书 → VocabSpire JSON 词库转换工具（固定流程）

适用版式：「扫码听单词 / 纸上默写」系列双栏 PDF 词书
（如：专升本英语、雅思词汇真经……同一排版模板的所有词书）。

版式特征：
  - 每页左栏 Word（序号 + 单词）、右栏 Meaning（序号 + 按词性分行的释义）
  - 每页固定 20 词，序号全局连续递增
  - 汉字是康熙部首/兼容区字符（需 NFKC 还原），标点为全角中文标点
  - 页脚含书名、「共 N 词 / X/Y 页」、「扫码听单词」「纸上默写…」

用法：
    python tools/pdf_to_wordbank.py <input.pdf> <out_id> --name "显示名" [--desc "描述"]
    # 例：
    python tools/pdf_to_wordbank.py "D:/雅思词汇真经.pdf" ielts_zhenjing --name "雅思词汇真经"

输出：VocabSpire/Resources/wordbanks/<out_id>.json（与内置词库同格式）
依赖：pymupdf(fitz)。音标自动从 ECDICT 回填（首次运行下载 ~63MB 到系统临时目录缓存）。

处理要点（踩过的坑，勿删）：
  1. 逐字 NFKC 还原部首字（⽓→气），但保留全角中文标点（；，（）不被转半角）。
  2. 书名/页眉页脚不硬编码 —— 自动检测「在大多数页重复出现的短行」过滤，换书无需改代码。
  3. 单词可能跨行（carbon\\ndioxide / absent-\\nminded）—— 续行拼回上一个单词。
  4. 释义按单词区的序号序列切块，避免页脚行污染最后一个词。
"""
import fitz, unicodedata, re, json, os, csv, argparse, tempfile, urllib.request
from collections import Counter

# 保留这些全角中文标点（NFKC 默认会把它们转半角，逐字处理时排除以保留排版美观）
_KEEP = set('；，。、！？：·…—～（）【】〔〕《》〈〉「」『』“”‘’%')


def fix_text(s):
    """逐字 NFKC：康熙部首/兼容汉字 → 正常汉字，但保留全角中文标点；压缩多余空白。"""
    out = [ch if ch in _KEEP else unicodedata.normalize('NFKC', ch) for ch in s]
    return re.sub(r'[ \t]+', ' ', ''.join(out))


_FOOTER_RE = re.compile(r'^(共\s*\d+\s*词|\d+\s*/\s*\d+\s*页)')


def _is_footer_anchor(l):
    """与具体书名无关的页脚锚点。"""
    return l == '' or '扫码听单词' in l or '纸上默写' in l or bool(_FOOTER_RE.match(l))


def detect_repeated(doc, ratio=0.5):
    """检测大多数页重复出现的短行（书名/页眉/广告语）作为过滤集；排除结构锚点 Word/Meaning。"""
    cnt = Counter()
    for page in doc:
        seen = set()
        for raw in page.get_text().split('\n'):
            l = fix_text(raw).strip()
            if l in ('Word', 'Meaning') or l.isdigit():
                continue
            if 0 < len(l) <= 18 and l not in seen:
                cnt[l] += 1
                seen.add(l)
    n = doc.page_count
    return {line for line, c in cnt.items() if c >= max(2, n * ratio)}


def parse_page(text, repeated):
    lines = [fix_text(x).strip() for x in text.split('\n')]
    lines = [x for x in lines if not _is_footer_anchor(x) and x not in repeated]
    wm = [i for i, x in enumerate(lines) if x == 'Word']
    if len(wm) < 2:
        return []
    words_zone = lines[wm[0] + 2: wm[1]]
    mean_zone = lines[wm[1] + 2:]

    # 单词区：兼容「序号 单词」同行 / 「序号」「单词」分行；换行截断词拼回上一个
    ordered = []
    pending = None
    for x in words_zone:
        m = re.match(r'^(\d+)\s+(\S.*)$', x)
        if m:
            ordered.append((int(m.group(1)), m.group(2).strip()))
            pending = None
            continue
        m = re.match(r'^(\d+)$', x)
        if m:
            pending = int(m.group(1))
            continue
        if pending is not None:
            ordered.append((pending, x.strip()))
            pending = None
        elif ordered:
            n_, w_ = ordered[-1]
            sep = '' if w_.endswith('-') else ' '
            ordered[-1] = (n_, w_ + sep + x.strip())
    expected = [n for n, _ in ordered]

    # 释义区：按单词区的序号序列依次切块，兼容同行/分行
    means = {}
    idx = 0
    cur = None
    for x in mean_zone:
        m = re.match(r'^(\d+)(?:\s+(.*))?$', x)
        if m and idx < len(expected) and int(m.group(1)) == expected[idx]:
            cur = expected[idx]
            idx += 1
            means[cur] = []
            rest = (m.group(2) or '').strip()
            if rest:
                means[cur].append(rest)
        elif cur is not None:
            means[cur].append(x.strip())
    return [(n, w, means.get(n, [])) for n, w in ordered]


def parse_pdf(path):
    doc = fitz.open(path)
    repeated = detect_repeated(doc)
    rows, bad = [], []
    for i in range(doc.page_count):
        r = parse_page(doc[i].get_text(), repeated)
        if not r:
            bad.append(i)
        rows.extend(r)
    nums = [n for n, _, _ in rows]
    dup = sorted({n for n in nums if nums.count(n) > 1}) if len(nums) != len(set(nums)) else []
    missing = sorted(set(range(1, max(nums) + 1)) - set(nums)) if nums else []
    stats = dict(total=len(rows), rng=((min(nums), max(nums)) if nums else None),
                 dup=dup, missing=missing[:20], bad_pages=bad,
                 empty_word=[(n, w) for n, w, m in rows if not w][:10],
                 empty_mean=[(n, w) for n, w, m in rows if not m][:10],
                 detected_footer=sorted(repeated))
    return rows, stats


def load_ecdict():
    """下载/缓存 ECDICT 完整 csv，返回 {word_lower: phonetic}。"""
    cache = os.path.join(tempfile.gettempdir(), 'vocabspire_ecdict.csv')
    if not os.path.exists(cache) or os.path.getsize(cache) < 1_000_000:
        url = 'https://raw.githubusercontent.com/skywind3000/ECDICT/master/ecdict.csv'
        print(f'下载 ECDICT 音标词典 → {cache} ...')
        urllib.request.urlretrieve(url, cache)
    csv.field_size_limit(10 ** 7)
    phon = {}
    with open(cache, encoding='utf-8', newline='') as f:
        r = csv.reader(f)
        header = next(r)
        wi, pi = header.index('word'), header.index('phonetic')
        for row in r:
            if len(row) <= pi:
                continue
            w = row[wi].strip().lower()
            if w and w not in phon:
                p = row[pi].strip()
                if p:
                    phon[w] = p
    return phon


def main():
    ap = argparse.ArgumentParser(description='「扫码听单词」系列双栏 PDF 词书 → VocabSpire 词库 json')
    ap.add_argument('pdf', help='输入 PDF 路径')
    ap.add_argument('out_id', help='输出词库 id（文件名，无扩展名）')
    ap.add_argument('--name', default=None, help='词库显示名')
    ap.add_argument('--desc', default=None, help='词库描述')
    ap.add_argument('--out-dir', default=None, help='输出目录（默认 VocabSpire/Resources/wordbanks）')
    ap.add_argument('--no-phonetic', action='store_true', help='跳过 ECDICT 音标回填')
    a = ap.parse_args()

    rows, st = parse_pdf(a.pdf)
    print('解析统计:', json.dumps(st, ensure_ascii=False))
    if st['dup'] or st['missing'] or st['empty_word'] or st['empty_mean']:
        print('⚠ 校验异常，请核对上面统计后再用')

    phon = {} if a.no_phonetic else load_ecdict()
    words, hit = [], 0
    for n, w, m in sorted(rows, key=lambda r: r[0]):
        e = w.strip().lower()
        ph = ''
        if e in phon:
            ph = f"/{phon[e]}/"
            hit += 1
        words.append(dict(english=w, chinese=m, phonetic=ph))
    cov = round(hit / len(words) * 100) if words else 0
    print(f'音标覆盖: {hit}/{len(words)} = {cov}%')

    out_dir = a.out_dir or os.path.join(
        os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
        'VocabSpire', 'Resources', 'wordbanks')
    out = os.path.join(out_dir, a.out_id + '.json')
    name = a.name or a.out_id
    bank = dict(
        name=name,
        description=a.desc or f'{name}（{len(words)} 词，PDF 转换，按词性分组释义，ECDICT 音标覆盖约 {cov}%）。',
        words=words)
    with open(out, 'w', encoding='utf-8') as f:
        json.dump(bank, f, ensure_ascii=False, indent=1)
    print('✅ 写出:', out, '| 词数:', len(words))


if __name__ == '__main__':
    main()
