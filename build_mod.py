# -*- coding: utf-8 -*-
"""「手机操作优化」Mod —— 编译 → 打包 → 装机 一条龙。

★ 本包只含 ①滑动误选撤销 / ②战斗中误触取消；
  原来的 ③按压反馈 / ④自动拾取开关 已拆到独立包「点击反馈与自动拾取」
  （`mod/MobileTapFeedback/`），那边有自己的 build_mod.py，改完记得分别打包。

用法：
    python build_mod.py             # 只编译 + 打包
    python build_mod.py --install   # 继续装机（覆盖 Mods/*.pmod + 清解包缓存）

硬护栏（照技能文档）：
  · mod.json 必须在【根】且【唯一】
  · Runtime/ 下只允许 ModAssembly.dll（多一个可执行文件 → 整包被拒）
  · 包内绝不允许出现 .cs（ModLoader.IsExecutablePackageFile 白名单拒收）
"""
import json
import os
import shutil
import subprocess
import sys
import zipfile
import hashlib

ROOT = r"C:\Users\txgcs\WorkBuddy\zjb"
BASE = os.path.join(ROOT, "mod", "MobileUXFixes")
SRC = os.path.join(BASE, "runtime_src")
DIST = os.path.join(ROOT, "mod", "dist", "MobileUXFixes.pmod")
DOTNET = os.path.join(ROOT, "tools", "dotnet9", "dotnet.exe")

UD = r"C:\Users\txgcs\AppData\Roaming\Godot\app_userdata\植物大战僵尸杂交版"
MODS = os.path.join(UD, "Mods")
CACHE = os.path.join(UD, "ModsCache")

MOD_ID = "mobileuxfixes"
BINSRC = os.path.join(SRC, "bin", "Release", "JTYMobileUXFixes.dll")

out = []


def log(s):
    out.append(str(s))
    try:
        with open(os.path.join(ROOT, "mod", "_mobileuxfixes_build.log"), "w", encoding="utf-8") as f:
            f.write("\n".join(out))
    except Exception:
        pass


def md5(path):
    h = hashlib.md5()
    with open(path, "rb") as f:
        for c in iter(lambda: f.read(65536), b""):
            h.update(c)
    return h.hexdigest()


def compile_asm():
    home = os.path.join(ROOT, "tools", "dotnet_home")
    tmpd = os.path.join(home, "tmp")
    nuget = os.path.join(ROOT, "tools", "nuget")
    for d in (home, tmpd, nuget):
        os.makedirs(d, exist_ok=True)
    env = dict(os.environ)
    env["DOTNET_ROOT"] = os.path.join(ROOT, "tools", "dotnet9")
    env["DOTNET_CLI_HOME"] = home
    env["TEMP"] = tmpd
    env["TMP"] = tmpd
    env["TMPDIR"] = tmpd
    env["NUGET_PACKAGES"] = nuget
    env["DOTNET_NOLOGO"] = "1"
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    env["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"
    env["MSBUILDDISABLENODEREUSE"] = "1"

    assets = os.path.join(SRC, "obj", "project.assets.json")
    logs = []

    def run(args, timeout):
        p = subprocess.run([DOTNET] + args, cwd=SRC, env=env, capture_output=True,
                           text=True, encoding="utf-8", errors="replace", timeout=timeout)
        logs.append("$ dotnet " + " ".join(args) + "  -> RC=%d" % p.returncode)
        if p.stdout:
            logs.append(p.stdout.strip())
        if p.stderr:
            logs.append(p.stderr.strip())
        return p.returncode

    try:
        subprocess.run(["taskkill", "/F", "/IM", "dotnet.exe"], capture_output=True, timeout=30)
    except Exception:
        pass

    rc = 0
    if not os.path.isfile(assets):
        log("[compile] 首次：先 restore")
        rc = run(["restore"], 900)
    if rc == 0:
        rc = run(["build", "-c", "Release", "--no-restore",
                  "-p:UseSharedCompilation=false", "-m:1", "-nodeReuse:false",
                  "-v:q", "-nologo"], 600)
    with open(os.path.join(SRC, "build.log"), "w", encoding="utf-8") as f:
        f.write("\n".join(logs))
    log("[compile] RC=%d" % rc)
    return rc == 0


def package():
    if not os.path.isfile(BINSRC):
        log("[package] 缺编译产物 " + BINSRC)
        return False
    runtime_dir = os.path.join(BASE, "Runtime")
    os.makedirs(runtime_dir, exist_ok=True)
    dll = os.path.join(runtime_dir, "ModAssembly.dll")
    shutil.copyfile(BINSRC, dll)
    log("[package] DLL md5=" + md5(dll))

    man = json.load(open(os.path.join(BASE, "mod.json"), encoding="utf-8"))
    names = sorted(os.listdir(runtime_dir))
    bad = [n for n in names if n != "ModAssembly.dll"]
    if bad:
        log("[package] ❌ Runtime/ 有非法文件（会整包拒收）: %s" % bad)
        return False
    actual = sorted(["Runtime/ModAssembly.dll"], key=str.lower)
    declared = sorted(man.get("resources", []), key=str.lower)
    if actual != declared:
        log("[package] ❌ resources 声明与实际不一致: %s vs %s" % (declared, actual))
        return False
    if man.get("runtimeAssembly") != "Runtime/ModAssembly.dll" or man.get("runtimeApiVersion") != 1:
        log("[package] ❌ runtime 字段不对")
        return False

    os.makedirs(os.path.dirname(DIST), exist_ok=True)
    with zipfile.ZipFile(DIST, "w", zipfile.ZIP_DEFLATED) as z:
        z.write(os.path.join(BASE, "mod.json"), "mod.json")
        z.write(dll, "Runtime/ModAssembly.dll")
    with zipfile.ZipFile(DIST) as z:
        nl = z.namelist()
    log("[package] %s  %d B  内容=%s" % (DIST, os.path.getsize(DIST), nl))
    if nl.count("mod.json") != 1 or nl[0] != "mod.json":
        log("[package] ❌ mod.json 不在根或不止一个")
        return False
    log("[package] ✅ 护栏全过")
    return True


def install():
    dst = os.path.join(MODS, "MobileUXFixes.pmod")
    shutil.copyfile(DIST, dst)
    log("[install] %s (%d B)" % (dst, os.path.getsize(dst)))
    if os.path.isdir(CACHE):
        import time
        ts = time.strftime("%H%M%S")
        src = os.path.join(CACHE, "MobileUXFixes")
        if os.path.isdir(src):
            dstc = src + ".bak_" + ts
            i = 1
            while os.path.exists(dstc):
                i += 1
                dstc = os.path.join(CACHE, "MobileUXFixes.bak_%s_%d" % (ts, i))
            try:
                r = subprocess.run(["cmd", "/c", "move", src, dstc],
                                   capture_output=True, errors="replace",
                                   encoding="gbk", timeout=60)
                log("[cache] move rc=%d" % r.returncode)
            except Exception as ex:
                log("[cache] move 失败（已忽略）：%r" % ex)
    en = os.path.join(MODS, "enabled_mods.json")
    try:
        ids = []
        if os.path.isfile(en):
            with open(en, encoding="utf-8") as f:
                ids = json.load(f)
        if MOD_ID not in ids:
            ids.append(MOD_ID)
            with open(en, "w", encoding="utf-8") as f:
                json.dump(ids, f, ensure_ascii=False, indent=2)
            log("[install] enabled_mods.json += " + MOD_ID)
        else:
            log("[install] enabled_mods.json 已含 " + MOD_ID)
    except Exception as e:
        log("[install] enabled_mods.json 合并失败: %r" % e)
    return True


def main():
    if not compile_asm():
        log("❌ 编译失败，终止")
        return 1
    if not package():
        return 1
    if "--install" in sys.argv:
        install()
    return 0


if __name__ == "__main__":
    rc = 0
    try:
        rc = main()
    except Exception as e:
        import traceback
        log("EXC: %r" % e)
        log(traceback.format_exc())
        rc = 1
    txt = "\n".join(out)
    with open(os.path.join(ROOT, "mod", "_mobileuxfixes_build.log"), "w", encoding="utf-8") as f:
        f.write(txt)
    print(txt)
    sys.exit(rc)
