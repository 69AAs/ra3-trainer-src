import os, shutil, json

BS = chr(92)  # backslash
SRC = "D:/Games/CoronaLauncher/CoronaResources/NewWorldBuilder/日冕地图lua例子/使用原生api （新手推荐）/Example_origin_05_top_button"
NAME = "hs_luatest"
HERE = "D:/work/ra3-trainer-refine/tools/map-lib/inject-map-script"
OUT = os.path.join(HERE, "out", "install_" + NAME)
LIB = os.path.join(OUT, NAME)

if os.path.isdir(OUT):
    shutil.rmtree(OUT)
os.makedirs(LIB)

# 1) our patched .map
shutil.copy(os.path.join(HERE, "out/Example05.hs-inplace.map"), os.path.join(OUT, NAME + ".map"))

# 2) resource files copied from the official example
for f in ("Example_origin_05_top_button.tga", "map_meta.json", "overrides.xml", "map.str"):
    p = os.path.join(SRC, f)
    if os.path.exists(p):
        dst = NAME + ".tga" if f.endswith(".tga") else f
        shutil.copy(p, os.path.join(OUT, dst))

# 3) map.xml: only id / display name / FileName / Description change; script-group name kept
x = open(os.path.join(SRC, "map.xml"), encoding="utf-8").read()
x = x.replace('id="Example_origin_05_top_button">', 'id="%s">' % NAME)
x = x.replace('Description="Map:Example_origin_05_top_button/Desc"', 'Description="HS Lua 等长原地补丁测试图"')
x = x.replace('DisplayName="Example_origin_05_top_button"', 'DisplayName="HS Lua 补丁测试图"')
x = x.replace('FileName="data' + BS + 'maps' + BS + 'example_origin_05_top_button' + BS + 'example_origin_05_top_button.map"',
              'FileName="data' + BS + 'maps' + BS + NAME + BS + NAME + '.map"')
open(os.path.join(OUT, "map.xml"), "w", encoding="utf-8", newline="").write(x)

# 4) lib folder: lua source + import manifest
shutil.copy(os.path.join(HERE, "out/inplace.lua"), os.path.join(LIB, "init.lua"))
libpath = os.path.abspath(LIB).replace("/", BS)
meta = {
    "FileName": NAME, "FilePath": libpath, "FileType": "dir",
    "IsEnabled": True, "IsIncluded": True, "RunOnce": False, "IsEvaluateEachFrame": True,
    "EvaluationInterval": 1, "ActiveInEasy": True, "ActiveInMedium": True, "ActiveInHard": True,
    "OrderNum": 0,
    "Children": [{
        "FileName": "init.lua", "FilePath": BS + NAME + BS + "init.lua", "FileType": "lua",
        "IsEnabled": True, "IsIncluded": True, "RunOnce": False, "IsEvaluateEachFrame": True,
        "EvaluationInterval": 1, "ActiveInEasy": True, "ActiveInMedium": True, "ActiveInHard": True,
        "OrderNum": 0, "Children": [], "LibPath": libpath}],
    "LibPath": libpath,
}
open(os.path.join(LIB, ".lib_meta.json"), "w", encoding="utf-8", newline="").write(
    json.dumps(meta, ensure_ascii=False, indent=2))

for r, ds, fs in os.walk(OUT):
    for f in sorted(fs):
        print(" ", os.path.join(r, f).replace(BS, "/"))
print("map.xml id 检查:", NAME in open(os.path.join(OUT, "map.xml"), encoding="utf-8").read())
