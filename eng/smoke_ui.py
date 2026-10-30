from pathlib import Path
import json
import os
import subprocess
import time
import uuid
from PIL import ImageGrab

root = Path(__file__).resolve().parents[1]
exe = Path(os.environ.get("KBEA_EXE", str(root / "artifacts" / "KBEA-Portable" / "KBEA.exe")))
appdata = root / "artifacts" / "smoke-appdata"
appdata.mkdir(parents=True, exist_ok=True)
mount = root / "artifacts" / "smoke-mount"
(mount / "文档").mkdir(parents=True, exist_ok=True)
(mount / "文档" / "欢迎使用.md").write_text("# 知识库自进化助手\n\n挂载文件夹、文件浏览与预览运行正常。", encoding="utf-8")
(mount / "项目说明.txt").write_text("这是一个用于启动验证的挂载文件夹。", encoding="utf-8")
workspace_id = str(uuid.uuid4())
mount_id = str(uuid.uuid4())
categories = [{"Id": str(uuid.uuid4()), "Name": name, "Children": []} for name in ("项目", "专业", "阶段")]
configuration = {
    "AuthorName": "smoke-test", "AuthorEmail": "smoke@example.com", "Theme": "Light",
    "RsyncExecutable": "tools/rsync/rsync.exe", "ArchiveRoot": "", "AutoDetect": True,
    "Workspaces": [{"Id": workspace_id, "Name": "验证工作空间", "Categories": categories,
                    "Mounts": [{"Id": mount_id, "Name": "示例资料库", "Path": str(mount), "RemoteUrl": "", "AutoSync": False}],
                    "Favorites": []}]
}
config = appdata / "KBEA" / "workspaces.json"
config.parent.mkdir(parents=True, exist_ok=True)
config.write_text(json.dumps(configuration, ensure_ascii=False, indent=2), encoding="utf-8")
env = os.environ.copy()
env["APPDATA"] = str(appdata)
env["LOCALAPPDATA"] = str(appdata)
dll = os.environ.get("KBEA_DLL")
command = [str(root / ".dotnet" / "dotnet.exe"), dll] if dll else [str(exe)]
process = subprocess.Popen(command + ["--config", str(config)], cwd=str(exe.parent), env=env)
try:
    time.sleep(12)
    code = process.poll()
    if code is not None:
        print(f"APP_EXITED:{code}")
        raise SystemExit(1)
    screenshot = root / "artifacts" / "ui-preview.png"
    ImageGrab.grab().save(screenshot)
    print(f"APP_RUNNING:PID={process.pid}")
    print(f"SCREENSHOT:{screenshot}")
finally:
    if process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)
