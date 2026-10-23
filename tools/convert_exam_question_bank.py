#!/usr/bin/env python3
"""将网校导出的计量师题库转换为 VocabSpire 固定选择题格式。

特点：
- 以 options[].is_right 为答案真值，并与原 answer 字段交叉校验；
- 支持单选和多选，输出答案统一为 0-based 索引数组；
- 清洗 HTML，同时保留段落、表格分隔和原题图片 URL；
- 写入解析与章节路径，供作答后展示；
- 可用“章节核对.json”校验 chapter_id / section_id。
"""

from __future__ import annotations

import argparse
import hashlib
import html
import json
import mimetypes
import re
import urllib.request
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path
from typing import Any
from urllib.parse import urlparse


IMG_RE = re.compile(r"<img\b[^>]*>", re.IGNORECASE)
ATTR_RE = re.compile(r"([\w:-]+)\s*=\s*([\"'])(.*?)\2", re.DOTALL)
SCRIPT_STYLE_RE = re.compile(r"<(script|style)\b[^>]*>.*?</\1>", re.IGNORECASE | re.DOTALL)
TAG_RE = re.compile(r"<[^>]+>", re.DOTALL)


def _image_marker(match: re.Match[str]) -> str:
    attrs = {key.lower(): value for key, _, value in ATTR_RE.findall(match.group(0))}
    src = html.unescape(attrs.get("src", "")).strip()
    alt = html.unescape(attrs.get("alt", attrs.get("title", "题图"))).strip() or "题图"
    return f"\n【图片：{alt}；原图链接：{src}】\n" if src else f"\n【图片：{alt}】\n"


def html_to_text(value: Any) -> str:
    """把题干/选项/解析 HTML 转成适合 Godot 文本控件显示的纯文本。"""
    text = "" if value is None else str(value)
    text = SCRIPT_STYLE_RE.sub("", text)
    text = IMG_RE.sub(_image_marker, text)
    replacements = (
        (r"<br\s*/?>", "\n"),
        (r"</(?:p|div|section|ul|ol|table|tr|h[1-6])\s*>", "\n"),
        (r"<(?:li)\b[^>]*>", "• "),
        (r"</(?:td|th)\s*>", " | "),
    )
    for pattern, replacement in replacements:
        text = re.sub(pattern, replacement, text, flags=re.IGNORECASE)
    text = TAG_RE.sub("", text)
    text = html.unescape(text).replace("\xa0", " ").replace("\u3000", " ")
    lines = [re.sub(r"[ \t]+", " ", line).strip(" |") for line in text.splitlines()]
    compact: list[str] = []
    for line in lines:
        if line:
            compact.append(line)
        elif compact and compact[-1] != "":
            compact.append("")
    return "\n".join(compact).strip()


def image_urls(value: Any) -> list[str]:
    urls: list[str] = []
    for match in IMG_RE.finditer("" if value is None else str(value)):
        attrs = {key.lower(): val for key, _, val in ATTR_RE.findall(match.group(0))}
        src = html.unescape(attrs.get("src", "")).strip()
        if src:
            urls.append(src)
    return urls


def _download_one(url: str, image_dir: Path) -> tuple[str, Path | None, str | None]:
    candidates = [url]
    if url.startswith("http://"):
        candidates.insert(0, "https://" + url[len("http://"):])
    last_error = "未知错误"
    for candidate in candidates:
        try:
            request = urllib.request.Request(candidate, headers={"User-Agent": "Mozilla/5.0 VocabSpire/1.0"})
            with urllib.request.urlopen(request, timeout=20) as response:
                data = response.read(12 * 1024 * 1024 + 1)
                if len(data) > 12 * 1024 * 1024:
                    raise ValueError("单张图片超过 12 MiB")
                content_type = response.headers.get_content_type()
            suffix = Path(urlparse(candidate).path).suffix.lower()
            if suffix not in {".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp"}:
                suffix = mimetypes.guess_extension(content_type) or ".img"
                if suffix == ".jpe":
                    suffix = ".jpg"
            filename = hashlib.sha1(url.encode("utf-8")).hexdigest() + suffix
            destination = image_dir / filename
            if not destination.exists():
                destination.write_bytes(data)
            return url, destination, None
        except Exception as exc:  # noqa: BLE001 - 需要记录每个失效的原站图片
            last_error = f"{type(exc).__name__}: {exc}"
    return url, None, last_error


def download_images(urls: set[str], image_dir: Path) -> tuple[dict[str, Path], dict[str, str]]:
    image_dir.mkdir(parents=True, exist_ok=True)
    downloaded: dict[str, Path] = {}
    failed: dict[str, str] = {}
    with ThreadPoolExecutor(max_workers=12) as pool:
        futures = [pool.submit(_download_one, url, image_dir) for url in sorted(urls)]
        for future in as_completed(futures):
            url, path, error = future.result()
            if path is not None:
                downloaded[url] = path
            else:
                failed[url] = error or "未知错误"
    return downloaded, failed


def answer_letters(indices: list[int]) -> str:
    return ",".join(chr(ord("A") + index) for index in indices)


def category_text(paths: Any) -> str:
    valid_paths: list[str] = []
    for path in paths or []:
        parts = [str(part).strip() for part in path if str(part).strip()]
        if parts:
            valid_paths.append(" > ".join(parts))
    return " / ".join(valid_paths)


def load_node_ids(path: Path | None) -> set[str]:
    if path is None:
        return set()
    rows = json.loads(path.read_text(encoding="utf-8"))
    return {str(row.get("node_id", "")) for row in rows if row.get("node_id")}


def convert(
    source: Path,
    destination: Path,
    node_ids: set[str],
    image_dir: Path | None = None,
    image_prefix: str = "",
) -> dict[str, int]:
    rows = json.loads(source.read_text(encoding="utf-8"))
    if not isinstance(rows, list) or not rows:
        raise ValueError(f"{source} 不是非空题目数组")

    all_image_urls: set[str] = set()
    for row in rows:
        all_image_urls.update(image_urls(row.get("content_html")))
        all_image_urls.update(image_urls(row.get("analysis_html")))
        for option in row.get("options") or []:
            all_image_urls.update(image_urls(option.get("content_html")))
    downloaded, failed_downloads = download_images(all_image_urls, image_dir) if image_dir else ({}, {})

    def local_images(value: Any) -> list[str]:
        result: list[str] = []
        for url in image_urls(value):
            path = downloaded.get(url)
            if path is not None:
                result.append((Path(image_prefix) / path.name).as_posix())
        return result

    words: list[dict[str, Any]] = []
    multi_count = 0
    image_question_count = 0
    image_analysis_count = 0
    fallback_analysis_count = 0
    missing_node_count = 0

    for position, row in enumerate(rows, start=1):
        options_raw = row.get("options") or []
        if len(options_raw) < 2:
            raise ValueError(f"第 {position} 题选项不足")

        options = [html_to_text(option.get("content_html", "")) for option in options_raw]
        if any(not option for option in options):
            raise ValueError(f"第 {position} 题存在空选项")

        correct = [index for index, option in enumerate(options_raw) if bool(option.get("is_right"))]
        if not correct:
            raise ValueError(f"第 {position} 题没有正确答案")

        declared = re.findall(r"[A-H]", str(row.get("answer", "")).upper())
        if declared and declared != [chr(ord("A") + index) for index in correct]:
            raise ValueError(
                f"第 {position} 题答案冲突：answer={declared}，is_right={answer_letters(correct)}"
            )

        content_html = str(row.get("content_html", ""))
        analysis_html = str(row.get("analysis_html", ""))
        question = html_to_text(content_html)
        explanation = html_to_text(analysis_html)
        if not question:
            raise ValueError(f"第 {position} 题题干为空")
        if not explanation:
            explanation = "本题暂无文字解析。"
            fallback_analysis_count += 1

        if IMG_RE.search(content_html):
            image_question_count += 1
        if IMG_RE.search(analysis_html):
            image_analysis_count += 1
        if len(correct) > 1:
            multi_count += 1

        if node_ids:
            for key in ("chapter_id", "section_id"):
                raw_node_id = row.get(key)
                node_id = str(raw_node_id) if raw_node_id else ""
                if node_id and node_id not in node_ids:
                    missing_node_count += 1

        item: dict[str, Any] = {
            "english": question,
            "chinese": " | ".join(options[index] for index in correct),
            "options": options,
            "answer": correct,
            "explanation": explanation,
            "category": category_text(row.get("paths")),
            "source_id": str(row.get("id", "")),
            "source_number": row.get("number", 0),
        }
        question_images = local_images(content_html)
        option_images = [local_images(option.get("content_html", "")) for option in options_raw]
        explanation_images = local_images(analysis_html)
        if question_images:
            item["question_images"] = question_images
        if any(option_images):
            item["option_images"] = option_images
        if explanation_images:
            item["explanation_images"] = explanation_images
        words.append(item)

    subject = str(rows[0].get("subject", source.stem)).strip()
    subject_name = str(rows[0].get("subject_name", subject)).strip()
    bank = {
        "name": f"一级计量师·{subject.removeprefix('一级')}题库",
        "description": (
            f"{subject_name}；共 {len(words)} 题（单选 {len(words) - multi_count} 题，"
            f"多选 {multi_count} 题）。作答后显示解析与章节。"
        ),
        "words": words,
    }
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(json.dumps(bank, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return {
        "total": len(words),
        "single": len(words) - multi_count,
        "multi": multi_count,
        "fallback_analysis": fallback_analysis_count,
        "image_questions": image_question_count,
        "image_analyses": image_analysis_count,
        "missing_nodes": missing_node_count,
        "image_urls": len(all_image_urls),
        "images_downloaded": len(downloaded),
        "images_failed": len(failed_downloads),
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path, help="原始题库 JSON")
    parser.add_argument("destination", type=Path, help="输出 VocabSpire JSON")
    parser.add_argument("--chapter-check", type=Path, help="章节核对 JSON（可选）")
    parser.add_argument("--image-dir", type=Path, help="离线图片输出目录（可选）")
    parser.add_argument("--image-prefix", default="", help="写入题库 JSON 的图片相对路径前缀")
    args = parser.parse_args()

    stats = convert(
        args.source,
        args.destination,
        load_node_ids(args.chapter_check),
        args.image_dir,
        args.image_prefix,
    )
    print(json.dumps({"output": str(args.destination), **stats}, ensure_ascii=False))


if __name__ == "__main__":
    main()
