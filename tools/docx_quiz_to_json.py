# -*- coding: utf-8 -*-
"""
Word 选择题题库(.docx) → VocabSpire 固定选择题词库 json 转换工具（线下）

适用版式（《题库.docx》护理学题库这类）：
  - 章节标题《X》；大题说明「一、…五个备选答案…」「二、…案例…」
  - 题干「N．题干------（   ）」；选项「A．x  B．y」（多项挤一行 / 一行一项 / 题干段内嵌选项均可）
  - 正确选项用红色字体（FF0000 等）标注
  - 案例题：「（1～2题共用题干）」+ 共用题干段 + 子题；共用题干自动拼进各子题题干

输出词条格式（游戏 2.7.21+ 识别）：
  { "english": "题干", "chinese": "正确答案文本", "options": [...], "answer": 正确索引 }

用法：
  python tools/docx_quiz_to_json.py <input.docx> <out_id> --name "显示名" [--desc "描述"]
"""
import zipfile, re, json, os, argparse
from xml.etree import ElementTree as ET

W = '{http://schemas.openxmlformats.org/wordprocessingml/2006/main}'
RED = {'FF0000', 'C00000', 'E36C0A', 'FF3300', 'CC0000', 'RED'}

QNO = re.compile(r'^(\d+)[．.、]\s*(.*)$', re.S)
OPTPRE = re.compile(r'^([A-E])[．.、]\s*')
OPTSPLIT = re.compile(r'(?=[A-E][．.、])')
CASEMK = re.compile(r'[（(]\s*\d+\s*[～~\-—]\s*\d+\s*题?共用题干\s*[）)]')
SECT = re.compile(r'^《(.+)》$')
BIGHDR = re.compile(r'^[一二三四五六七八九十]、')
TAILCLEAN = re.compile(r'[-—－]{2,}\s*[（(]\s*[）)]\s*$|[（(]\s*[）)]\s*$|[-—－]{2,}\s*$')
# 题干段内嵌选项：题干…（ ）A．xxx —— 从第一个「A．」处切开（前提是括号/虚线在其前）
INLINE_OPT = re.compile(r'^(.*?[（(]\s*[）)]|.*?[-—－]{2,})\s*(A[．.、].*)$', re.S)


def extract_paragraphs(path):
    z = zipfile.ZipFile(path)
    root = ET.fromstring(z.read('word/document.xml').decode('utf-8'))
    out = []
    for p in root.iter(W + 'p'):
        full, red = '', ''
        for r in p.findall(W + 'r'):
            txt = ''.join(t.text or '' for t in r.findall(W + 't'))
            if not txt:
                continue
            full += txt
            rpr = r.find(W + 'rPr')
            col = None
            if rpr is not None:
                c = rpr.find(W + 'color')
                if c is not None:
                    col = c.get(W + 'val')
            if col and col.upper() in RED:
                red += txt
        full = full.strip()
        if full:
            out.append((full, red))
    return out


def parse(paras):
    qs, bad = [], []
    cur = None
    casetext = ''
    collecting = False

    def commit():
        nonlocal cur
        if cur:
            opts = cur['opts']
            if len([o for o in opts if o]) >= 2 and 0 <= cur['ans'] < len(opts) and opts[cur['ans']]:
                qs.append(cur)
            else:
                bad.append(cur)
        cur = None

    def feed_options(text, red):
        """把一段选项文本（可能含多项）灌进当前题；红色文本用于判答案。"""
        for part in [s for s in OPTSPLIT.split(text) if s.strip()]:
            pm = OPTPRE.match(part.strip())
            if not pm:
                continue
            idx = ord(pm.group(1)) - 65
            t = OPTPRE.sub('', part.strip()).strip()
            while len(cur['opts']) <= idx:
                cur['opts'].append('')
            if t:
                cur['opts'][idx] = t
        if red.strip():
            rm = re.search(r'([A-E])[．.、]', red.strip())
            if rm:
                cur['ans'] = ord(rm.group(1)) - 65

    for full, red in paras:
        if SECT.match(full) or BIGHDR.match(full):
            commit(); casetext = ''; collecting = False; continue
        if CASEMK.search(full):
            commit(); casetext = ''; collecting = True; continue
        m = QNO.match(full)
        if m:
            commit(); collecting = False
            body = m.group(2)
            cur = {'stem': '', 'opts': [], 'ans': -1}
            # 题干段内嵌选项：拆成 题干 + 选项部分（红色随段，答案字母照常匹配）
            im = INLINE_OPT.match(body)
            if im:
                stem = TAILCLEAN.sub('', im.group(1)).strip()
                cur['stem'] = ('【案例】' + casetext + '\n' + stem) if casetext else stem
                feed_options(im.group(2), red)
            else:
                stem = TAILCLEAN.sub('', body).strip()
                cur['stem'] = ('【案例】' + casetext + '\n' + stem) if casetext else stem
            continue
        if collecting:
            casetext += full
            continue
        if cur is None:
            continue
        if OPTPRE.match(full):
            feed_options(full, red)
        else:
            if not cur['opts']:
                cur['stem'] += full
            elif cur['opts'][-1]:
                cur['opts'][-1] += full
    commit()

    # 压实空洞选项并修正答案索引
    for q in qs:
        if '' in q['opts']:
            ans_text = q['opts'][q['ans']]
            q['opts'] = [o for o in q['opts'] if o]
            q['ans'] = q['opts'].index(ans_text)
    return qs, bad


def main():
    ap = argparse.ArgumentParser(description='Word 选择题题库 → VocabSpire 固定选择题 json')
    ap.add_argument('docx'); ap.add_argument('out_id')
    ap.add_argument('--name', default=None); ap.add_argument('--desc', default=None)
    ap.add_argument('--out-dir', default=None)
    a = ap.parse_args()

    paras = extract_paragraphs(a.docx)
    qs, bad = parse(paras)
    print(f'解析成功: {len(qs)} 题 | 失败: {len(bad)} 题')
    for b in bad:
        print('  失败:', b['stem'][:50], f"(选项{len([o for o in b['opts'] if o])}个/答案{b['ans']})")
    dist = {}
    for q in qs:
        dist[chr(65 + q['ans'])] = dist.get(chr(65 + q['ans']), 0) + 1
    print('答案分布:', dict(sorted(dist.items())))

    dto = {
        'name': a.name or a.out_id,
        'description': a.desc or f'选择题题库（{len(qs)} 题）',
        'words': [
            {'english': q['stem'], 'chinese': q['opts'][q['ans']],
             'options': q['opts'], 'answer': q['ans']}
            for q in qs
        ],
    }
    out_dir = a.out_dir or os.path.join(
        os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
        'VocabSpire', 'Resources', 'wordbanks')
    out = os.path.join(out_dir, a.out_id + '.json')
    with open(out, 'w', encoding='utf-8') as f:
        json.dump(dto, f, ensure_ascii=False, indent=2)
    print('✅ 写出:', out, '| 题数:', len(qs))


if __name__ == '__main__':
    main()
