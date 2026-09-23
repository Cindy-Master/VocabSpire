# -*- coding: utf-8 -*-
"""
从 STS2 手机版 apk 里提取托管程序集，供「老游戏版本构建」当引用用。

背景：手机版是电脑版包一层 Android 壳，Godot 的 Android 导出里 C# 仍是**纯 IL 托管 dll**
（不是 AOT native），所以 apk 里的 sts2.dll 可以直接拿来当引用程序集编译 mod ——
这是电脑版旧安装包找不到时，拿到老版本 API 的唯一可靠途径。

用法：
    python tools/extract_apk_assemblies.py <手机版.apk> <输出目录>
    # 例：
    python tools/extract_apk_assemblies.py "D:/v0.99.1.apk" ./refs/v0.99.1

然后用它做老版构建：
    dotnet build VocabSpire/VocabSpire.csproj -c Release -p:LegacyGame=true -p:STS2GamePath=<输出目录>
产物在 VocabSpire/.godot/mono/temp/bin-legacy/Release/（与主线的 bin/Release 分开）。

拿到 dll 后先跑一遍 API 对比，看这个版本差了什么：
    dotnet run --project tools/apicheck -- <输出目录>/sts2.dll <当前游戏>/sts2.dll

要点：
  1. 程序集在 assets/.godot/mono/publish/<arch>/ 下，arch 可能是 arm64 / arm32 / x86_64，
     所以不写死路径、按后缀搜。
  2. 必须连 GodotSharp.dll 一起取：不然引用解析不到 Control/TextureRect 这些 Godot 类型，
     编译和 apicheck 都会报 FileNotFoundException。
  3. apk 里的 assets/release_info.json 带着确切版本号与 commit，顺手抄出来存档。
  4. apk 里那几千个 .cs **每个只有 1 字节** —— Godot 导出时把源码换成了占位文件，
     别指望能读到游戏源码，API 只能从 dll 元数据挖。
"""
import argparse
import json
import os
import posixpath
import sys
import zipfile

# 编译老版 mod 至少需要这三个；多取无害，少取就解析不出类型
WANTED = ("sts2.dll", "0Harmony.dll", "GodotSharp.dll")


def main():
    ap = argparse.ArgumentParser(description="从 STS2 手机版 apk 提取托管程序集")
    ap.add_argument("apk")
    ap.add_argument("out_dir")
    ap.add_argument("--all", action="store_true",
                    help="提取 publish 目录下所有 dll（默认只取编译必需的三个）")
    args = ap.parse_args()

    if not zipfile.is_zipfile(args.apk):
        sys.exit(f"不是有效的 zip/apk：{args.apk}")

    os.makedirs(args.out_dir, exist_ok=True)
    with zipfile.ZipFile(args.apk) as z:
        names = z.namelist()

        # 定位 publish 目录（arch 名不固定）
        pubs = sorted({posixpath.dirname(n) for n in names
                       if "/mono/publish/" in n and n.endswith(".dll")})
        if not pubs:
            sys.exit("apk 里找不到 .godot/mono/publish/*/ 下的托管程序集 —— "
                     "这个包可能不是 Godot .NET 导出，或是 AOT 编译的")
        if len(pubs) > 1:
            print(f"发现多个架构目录，取第一个：{pubs}")
        pub = pubs[0]
        print(f"程序集目录: {pub}")

        wanted = None if args.all else {w.lower() for w in WANTED}
        got, missing = [], []
        for n in names:
            if posixpath.dirname(n) != pub or not n.endswith(".dll"):
                continue
            base = posixpath.basename(n)
            if wanted is not None and base.lower() not in wanted:
                continue
            dst = os.path.join(args.out_dir, base)
            with open(dst, "wb") as f:
                f.write(z.read(n))
            got.append((base, os.path.getsize(dst)))

        have = {g[0].lower() for g in got}
        for w in WANTED:
            if w.lower() not in have:
                missing.append(w)

        # 版本信息：抄一份出来，省得以后认不出这堆 dll 是哪个版本的
        info = next((n for n in names if n.endswith("release_info.json")), None)
        if info:
            raw = z.read(info).decode("utf-8", "replace")
            with open(os.path.join(args.out_dir, "release_info.json"), "w",
                      encoding="utf-8", newline="\n") as f:
                f.write(raw)
            try:
                d = json.loads(raw)
                print(f"版本: {d.get('version')}  commit: {d.get('commit')}  日期: {d.get('date')}")
            except json.JSONDecodeError:
                print("release_info.json 解析失败，已原样保存")
        else:
            print("警告：apk 里没有 release_info.json，无法确认版本号")

    print("\n--- 提取结果 ---")
    for base, size in sorted(got):
        print(f"  {base:<28} {size:>10,} B")
    if missing:
        print("\n警告：以下编译必需的程序集没找到，老版构建大概会失败：")
        for m in missing:
            print("  ✗ " + m)
    print(f"\n输出目录: {os.path.abspath(args.out_dir)}")


if __name__ == "__main__":
    main()
